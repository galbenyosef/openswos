namespace OpenSwos.Tests;

// Parses ball_trace.csv produced by external/swos-port-modified instrumentation.
//
// CSV layout (one row per call entry/exit, ~2 rows per game frame):
//   frame,phase,gameState,gameStatePl,
//   ballX,ballY,ballZ,ballDx,ballDy,ballDz,ballDestX,ballDestY,ballSpeed,ballDir,ballImg,
//   topPlHas,botPlHas,topSpin,botSpin,topAllowDir,botAllowDir,
//   topLSpin,topRSpin,botLSpin,botRSpin
//
// Phase values: "updateBall_enter", "updateBall_exit",
//               "applyBallAfterTouch_enter", "applyBallAfterTouch_exit"
public sealed record TraceRow(
    int    Frame,
    string Phase,
    short  GameState,
    short  GameStatePl,
    int    BallX,    // Q16.16
    int    BallY,    // Q16.16
    int    BallZ,    // Q16.16
    int    BallDx,   // Q16.16
    int    BallDy,   // Q16.16
    int    BallDz,   // Q16.16
    short  BallDestX,
    short  BallDestY,
    short  BallSpeed,
    short  BallDir,
    short  BallImg,
    short  TopPlHas,
    short  BotPlHas,
    short  TopSpin,
    short  BotSpin,
    short  TopAllowDir,
    short  BotAllowDir,
    short  TopLSpin,
    short  TopRSpin,
    short  BotLSpin,
    short  BotRSpin);

public static class CsvTraceReader
{
    public static IReadOnlyList<TraceRow> Load(string path)
    {
        var rows = new List<TraceRow>();
        using var sr = new StreamReader(path);
        string? header = sr.ReadLine();
        if (header is null)
            throw new InvalidDataException($"Empty trace file: {path}");

        // We don't validate header strictly — but require it starts with "frame,phase".
        if (!header.StartsWith("frame,phase,"))
            throw new InvalidDataException(
                $"Unexpected header in {path}: {header[..Math.Min(50, header.Length)]}");

        string? line;
        int lineNo = 1;
        while ((line = sr.ReadLine()) != null)
        {
            lineNo++;
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length != 25)
                throw new InvalidDataException(
                    $"{path}:{lineNo} — expected 25 columns, got {parts.Length}: {line}");

            rows.Add(new TraceRow(
                Frame:        int.Parse(parts[0]),
                Phase:        parts[1],
                GameState:    short.Parse(parts[2]),
                GameStatePl:  short.Parse(parts[3]),
                BallX:        int.Parse(parts[4]),
                BallY:        int.Parse(parts[5]),
                BallZ:        int.Parse(parts[6]),
                BallDx:       int.Parse(parts[7]),
                BallDy:       int.Parse(parts[8]),
                BallDz:       int.Parse(parts[9]),
                BallDestX:    short.Parse(parts[10]),
                BallDestY:    short.Parse(parts[11]),
                BallSpeed:    short.Parse(parts[12]),
                BallDir:      short.Parse(parts[13]),
                BallImg:      short.Parse(parts[14]),
                TopPlHas:     short.Parse(parts[15]),
                BotPlHas:     short.Parse(parts[16]),
                TopSpin:      short.Parse(parts[17]),
                BotSpin:      short.Parse(parts[18]),
                TopAllowDir:  short.Parse(parts[19]),
                BotAllowDir:  short.Parse(parts[20]),
                TopLSpin:     short.Parse(parts[21]),
                TopRSpin:     short.Parse(parts[22]),
                BotLSpin:     short.Parse(parts[23]),
                BotRSpin:     short.Parse(parts[24])));
        }
        return rows;
    }
}
