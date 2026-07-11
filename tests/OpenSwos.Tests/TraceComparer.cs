using OpenSwos.SwosVm;

namespace OpenSwos.Tests;

// Reads current state from SwosVm (BallSprite + TeamData + Memory globals) into a TraceRow
// shape so we can field-diff against the reference CSV row.
public static class TraceComparer
{
    public static TraceRow CaptureCurrent(int frame, string phase)
    {
        return new TraceRow(
            Frame:        frame,
            Phase:        phase,
            GameState:    Memory.ReadSignedWord(Memory.Addr.gameState),
            GameStatePl:  Memory.ReadSignedWord(Memory.Addr.gameStatePl),
            BallX:        BallSprite.X,
            BallY:        BallSprite.Y,
            BallZ:        BallSprite.Z,
            BallDx:       BallSprite.DeltaX,
            BallDy:       BallSprite.DeltaY,
            BallDz:       BallSprite.DeltaZ,
            BallDestX:    BallSprite.DestX,
            BallDestY:    BallSprite.DestY,
            BallSpeed:    BallSprite.Speed,
            BallDir:      BallSprite.Direction,
            BallImg:      BallSprite.ImageIndex,
            TopPlHas:     TeamData.PlayerHasBall(top: true),
            BotPlHas:     TeamData.PlayerHasBall(top: false),
            TopSpin:      TeamData.GetSpinTimer(top: true),
            BotSpin:      TeamData.GetSpinTimer(top: false),
            TopAllowDir:  TeamData.CurrentAllowedDirection(top: true),
            BotAllowDir:  TeamData.CurrentAllowedDirection(top: false),
            TopLSpin:     TeamData.LeftSpin(top: true),
            TopRSpin:     TeamData.RightSpin(top: true),
            BotLSpin:     TeamData.LeftSpin(top: false),
            BotRSpin:     TeamData.RightSpin(top: false));
    }

    // Loads current state INTO SwosVm — used to set up before calling BallUpdate.Tick().
    public static void RestoreFrom(TraceRow row)
    {
        Memory.WriteWord(Memory.Addr.gameState,   row.GameState);
        Memory.WriteWord(Memory.Addr.gameStatePl, row.GameStatePl);
        BallSprite.X         = row.BallX;
        BallSprite.Y         = row.BallY;
        BallSprite.Z         = row.BallZ;
        BallSprite.DeltaX    = row.BallDx;
        BallSprite.DeltaY    = row.BallDy;
        BallSprite.DeltaZ    = row.BallDz;
        BallSprite.DestX     = row.BallDestX;
        BallSprite.DestY     = row.BallDestY;
        BallSprite.Speed     = row.BallSpeed;
        BallSprite.Direction = row.BallDir;
        BallSprite.ImageIndex = row.BallImg;
        TeamData.SetPlayerHasBall(true,  row.TopPlHas);
        TeamData.SetPlayerHasBall(false, row.BotPlHas);
        TeamData.SetSpinTimer(true,  row.TopSpin);
        TeamData.SetSpinTimer(false, row.BotSpin);
        TeamData.SetCurrentAllowedDirection(true,  row.TopAllowDir);
        TeamData.SetCurrentAllowedDirection(false, row.BotAllowDir);
        TeamData.SetLeftSpin(true,   row.TopLSpin);
        TeamData.SetRightSpin(true,  row.TopRSpin);
        TeamData.SetLeftSpin(false,  row.BotLSpin);
        TeamData.SetRightSpin(false, row.BotRSpin);
    }

    // Diffs current SwosVm state against expected row. Returns null on full match,
    // else a multi-line message listing each differing field with (expected, actual).
    //
    // NOTE: BallImg is INTENTIONALLY skipped — the sprite frame index depends on
    // stateful animation timers (cycleFramesTimer, frameIndex, frameDelay,
    // frameIndicesTable) that are NOT in our CSV trace. Per-tick `RestoreFrom`
    // resets them to 0 every call, so the port can't reproduce the swos-port
    // animation phase without that state. This isn't a port bug — it's a test
    // scope limitation. To actually fidelity-test animation, we need to either:
    //   (a) extend the CSV to capture those 4 animation fields, OR
    //   (b) test sequentially without per-tick RestoreFrom (state evolves naturally).
    // Tracked in B4.8 follow-up (after we see other diffs surface).
    public static string? DiffAgainst(TraceRow expected, string contextLabel)
    {
        var actual = CaptureCurrent(expected.Frame, expected.Phase);
        var diffs = new List<string>();

        void check(string name, object e, object a)
        {
            if (!Equals(e, a)) diffs.Add($"  {name}: expected={e}, actual={a}");
        }

        check("GameState",   expected.GameState,   actual.GameState);
        check("GameStatePl", expected.GameStatePl, actual.GameStatePl);
        check("BallX",       expected.BallX,       actual.BallX);
        check("BallY",       expected.BallY,       actual.BallY);
        check("BallZ",       expected.BallZ,       actual.BallZ);
        check("BallDx",      expected.BallDx,      actual.BallDx);
        check("BallDy",      expected.BallDy,      actual.BallDy);
        check("BallDz",      expected.BallDz,      actual.BallDz);
        check("BallDestX",   expected.BallDestX,   actual.BallDestX);
        check("BallDestY",   expected.BallDestY,   actual.BallDestY);
        check("BallSpeed",   expected.BallSpeed,   actual.BallSpeed);
        check("BallDir",     expected.BallDir,     actual.BallDir);
        // BallImg skipped — see comment above.
        check("TopPlHas",    expected.TopPlHas,    actual.TopPlHas);
        check("BotPlHas",    expected.BotPlHas,    actual.BotPlHas);
        check("TopSpin",     expected.TopSpin,     actual.TopSpin);
        check("BotSpin",     expected.BotSpin,     actual.BotSpin);
        check("TopAllowDir", expected.TopAllowDir, actual.TopAllowDir);
        check("BotAllowDir", expected.BotAllowDir, actual.BotAllowDir);
        check("TopLSpin",    expected.TopLSpin,    actual.TopLSpin);
        check("TopRSpin",    expected.TopRSpin,    actual.TopRSpin);
        check("BotLSpin",    expected.BotLSpin,    actual.BotLSpin);
        check("BotRSpin",    expected.BotRSpin,    actual.BotRSpin);

        if (diffs.Count == 0) return null;
        return $"{contextLabel} (frame {expected.Frame}, phase {expected.Phase}):\n"
             + string.Join("\n", diffs);
    }
}
