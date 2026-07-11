using OpenSwos.Sim.Port;
using OpenSwos.SwosVm;
using Xunit;
using Xunit.Abstractions;

namespace OpenSwos.Tests;

// Golden-master / differential tests for BallUpdate (the SwosVm port).
//
// **How it works**:
//   1. Load a CSV trace produced by external/swos-port-modified.
//   2. Walk the trace in pairs: (updateBall_enter_row, updateBall_exit_row).
//   3. For each pair:
//      a. Memory.Init() — clean slate.
//      b. TraceComparer.RestoreFrom(enter_row) — set SwosVm state to reference's
//         entry state for this tick.
//      c. BallUpdate.Tick() — invoke the port.
//      d. TraceComparer.DiffAgainst(exit_row) — should produce no diff if port matches.
//   4. First differing row = first concrete port bug. Test reports field-level diff.
//
// **Status 2026-05-23**: Golden CSVs not yet captured (B4.8 step 5 — user runs
// external/swos-port-modified/bin/x64/swos-port-x64-Release.exe to produce one).
// Tests SKIP gracefully when no CSV exists — they will turn red the moment a
// trace is dropped into tests/OpenSwos.Tests/golden/.
public class BallUpdateGoldenTests
{
    private readonly ITestOutputHelper _output;
    public BallUpdateGoldenTests(ITestOutputHelper output) { _output = output; }

    private static string GoldenDir
    {
        get
        {
            // CSV files copied to output via <Content Include> in csproj.
            string asm = AppContext.BaseDirectory;
            return Path.Combine(asm, "golden");
        }
    }

    [Theory]
    [InlineData("kickoff.csv")]
    [InlineData("free_kick.csv")]
    [InlineData("corner.csv")]
    [InlineData("goal_kick.csv")]
    public void Replay_UpdateBall_MatchesReference(string traceFile)
    {
        string path = Path.Combine(GoldenDir, traceFile);
        if (!File.Exists(path))
        {
            // Skip rather than fail — golden CSVs are user-produced. They will
            // exist once external/swos-port-modified is run through the scenario.
            // To convert "skip" into "fail", drop a trace file here.
            _output.WriteLine($"SKIP: no golden trace at {path}. " +
                              $"Run external/swos-port-modified/bin/x64/swos-port-x64-Release.exe, " +
                              $"play '{traceFile}' scenario, copy resulting ball_trace.csv here.");
            return;
        }

        var rows = CsvTraceReader.Load(path);
        _output.WriteLine($"Loaded {rows.Count} trace rows from {path}");

        // Pair enter→exit rows by scanning sequentially. We don't assume strict
        // pairing because applyBallAfterTouch can interleave with updateBall.
        // Focus on updateBall pairs for now.
        var enters = new Dictionary<int, TraceRow>();
        var failures = new List<string>();

        Memory.Init(pcMode: true);
        int comparedPairs = 0;

        foreach (var row in rows)
        {
            switch (row.Phase)
            {
                case "updateBall_enter":
                    enters[row.Frame] = row;
                    break;

                case "updateBall_exit":
                    if (!enters.TryGetValue(row.Frame, out var enter))
                    {
                        failures.Add($"frame {row.Frame}: exit without enter");
                        continue;
                    }
                    enters.Remove(row.Frame);

                    // Reset to enter state, run Tick, compare to exit state.
                    Memory.Init(pcMode: true);
                    TraceComparer.RestoreFrom(enter);

                    try
                    {
                        BallUpdate.Tick();
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"frame {row.Frame}: Tick() threw — {ex.GetType().Name}: {ex.Message}");
                        if (failures.Count >= 5) goto report;  // stop after 5 failures
                        continue;
                    }

                    var diff = TraceComparer.DiffAgainst(row, $"BallUpdate.Tick output mismatch");
                    if (diff != null)
                    {
                        failures.Add(diff);
                        if (failures.Count >= 5) goto report;
                    }
                    comparedPairs++;
                    break;
            }
        }

        report:
        _output.WriteLine($"Compared {comparedPairs} enter→exit pairs.");
        if (failures.Count > 0)
        {
            Assert.Fail("Found " + failures.Count + " mismatch(es):\n\n" +
                        string.Join("\n\n", failures.Take(5)) +
                        (failures.Count > 5 ? $"\n\n... and {failures.Count - 5} more" : ""));
        }
    }

    // ---- Smoke tests that don't need golden traces -------------------------

    [Fact]
    public void Tick_RunsWithoutThrowingOnFreshMemory()
    {
        Memory.Init(pcMode: true);
        // Default state: ball at (0,0,0), no team has ball, gameStatePl=100 (in progress).
        // Section3 will go down "keeper doesn't hold ball" branch.
        BallUpdate.Tick();
        // No assertion — just verifying no NotImplementedException, no NRE.
    }

    [Fact]
    public void CalculateDeltaXAndY_OneOClockKick_HasPositiveXPositiveY()
    {
        // Speed 2560 (normal kick), heading from origin toward NE.
        Memory.Init(pcMode: true);
        var result = SpriteUpdate.CalculateDeltaXAndY(
            speed: 2560, x: 100, y: 100, destX: 200, destY: 200);

        Assert.True(result.Direction >= 0, "Direction should be valid (>= 0)");
        Assert.True(result.DeltaX > 0, $"DeltaX should be positive for NE motion, got {result.DeltaX}");
        Assert.True(result.DeltaY > 0, $"DeltaY should be positive for SE motion, got {result.DeltaY}");
    }

    [Fact]
    public void CalculateDeltaXAndY_StationaryReturnsMinusOne()
    {
        Memory.Init(pcMode: true);
        var result = SpriteUpdate.CalculateDeltaXAndY(
            speed: 0, x: 100, y: 100, destX: 100, destY: 100);
        Assert.Equal(-1, result.Direction);
        Assert.Equal(0, result.DeltaX);
        Assert.Equal(0, result.DeltaY);
    }
}
