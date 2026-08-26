using System;
using System.IO;
using System.Text.Json;
using Godot;
using OpenSwos.Menu;

namespace OpenSwos.Competition;

// ============================================================================
// CompetitionStore — competition state persisted as pretty-printed JSON.
//
// Two tiers:
//   * AUTOSAVE slot — the legacy single-slot API (Save/Load/Delete/Exists) at
//     user://competition.json. Behaviour unchanged.
//   * Named slots — SaveAs/LoadSlot/DeleteSlot at user://saves/<SLOT>.json.
//     Slot names are sanitized to A-Z 0-9 '-' '_' (max 16 chars). The slot
//     name "AUTOSAVE" is reserved and aliases the legacy slot.
//   * ListSlots() enumerates everything (AUTOSAVE first when present) with a
//     human-readable label built from the loaded state.
//
// All methods are best-effort: writes/deletes log and swallow I/O errors,
// loads return null on a missing or corrupt file so the menu can fall back to
// "no competition in progress".
// ============================================================================

public static class CompetitionStore
{
    private const string SavePath = "user://competition.json";
    private const string SlotsDir = "user://saves";
    private const string AutosaveSlot = "AUTOSAVE";

    // The FALLBACK writer/parser (see ToBytes/FromText); the normal path is the
    // source-generated one in CompetitionJson.cs, which carries the measurements.
    // Not indented: a career save is the whole world and pretty-printing alone
    // doubled it, 38.4 MB against 17.3 MB, for a file no human reads. Reading is
    // unaffected either way — System.Text.Json parses both, so saves written by
    // earlier builds still load.
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static string FilePath() => ProjectSettings.GlobalizePath(SavePath);

    /// <summary>
    /// The save as UTF-8 bytes, through the source-generated writer
    /// (CompetitionJson.cs) — see there for why, and for the measurements.
    /// SerializeToUtf8Bytes rather than Serialize + WriteAllText: the string
    /// form allocates the whole save twice, once UTF-16 and once UTF-8.
    ///
    /// Falls back to reflection if the generated context ever refuses a type:
    /// a slow save beats a lost career.
    /// </summary>
    private static byte[] ToBytes(CompetitionState state)
    {
        try { return JsonSerializer.SerializeToUtf8Bytes(state, CompetitionJsonContext.Default.CompetitionState); }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore: generated serializer refused the state ({ex.Message}); using reflection");
            return JsonSerializer.SerializeToUtf8Bytes(state, JsonOpts);
        }
    }

    /// <summary>The reading counterpart of <see cref="ToBytes"/>, same fallback.</summary>
    private static CompetitionState? FromText(string json)
    {
        try { return JsonSerializer.Deserialize(json, CompetitionJsonContext.Default.CompetitionState); }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore: generated parser refused the file ({ex.Message}); using reflection");
            return JsonSerializer.Deserialize<CompetitionState>(json, JsonOpts);
        }
    }

    public static void Save(CompetitionState state)
    {
        try
        {
            string path = FilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, ToBytes(state));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.Save failed: {ex.Message}");
        }
    }

    public static CompetitionState? Load()
    {
        try
        {
            string path = FilePath();
            if (!File.Exists(path)) return null;
            return FromText(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.Load failed: {ex.Message}");
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            string path = FilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.Delete failed: {ex.Message}");
        }
    }

    public static bool Exists()
    {
        try { return File.Exists(FilePath()); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ slots

    // Reduce an arbitrary user string to a safe slot name: uppercase ASCII,
    // keep only A-Z 0-9 '-' '_', cap at 16 chars; empty result -> "SAVE".
    public static string SanitizeSlotName(string slotName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char raw in (slotName ?? "").ToUpperInvariant())
        {
            char c = raw;
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
            {
                sb.Append(c);
                if (sb.Length >= 16) break;
            }
        }
        return sb.Length == 0 ? "SAVE" : sb.ToString();
    }

    private static string SlotFilePath(string slotName) =>
        Path.Combine(ProjectSettings.GlobalizePath(SlotsDir), slotName + ".json");

    public static void SaveAs(CompetitionState state, string slotName)
    {
        string slot = SanitizeSlotName(slotName);
        if (slot == AutosaveSlot) { Save(state); return; }
        try
        {
            string path = SlotFilePath(slot);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, ToBytes(state));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.SaveAs('{slot}') failed: {ex.Message}");
        }
    }

    public static CompetitionState? LoadSlot(string slotName)
    {
        string slot = SanitizeSlotName(slotName);
        if (slot == AutosaveSlot) return Load();
        try
        {
            string path = SlotFilePath(slot);
            if (!File.Exists(path)) return null;
            return FromText(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.LoadSlot('{slot}') failed: {ex.Message}");
            return null;
        }
    }

    public static void DeleteSlot(string slotName)
    {
        string slot = SanitizeSlotName(slotName);
        if (slot == AutosaveSlot) { Delete(); return; }
        try
        {
            string path = SlotFilePath(slot);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.DeleteSlot('{slot}') failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Is there ANY named save? A directory listing, nothing more.
    ///
    /// It exists because the menu only wants to know whether to show a LOAD
    /// GAME button, and asking <see cref="ListSlots"/> for that used to read
    /// every save file on disk while BUILDING A SCREEN. See the note on
    /// ListSlots for what that cost.
    /// </summary>
    public static bool AnySlotExists()
    {
        try
        {
            if (Exists()) return true;
            string dir = ProjectSettings.GlobalizePath(SlotsDir);
            return Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length > 0;
        }
        catch { return false; }
    }

    // (slotName, label) pairs for every existing save, AUTOSAVE first if
    // present. Corrupt files are skipped.
    //
    // THIS USED TO FULLY DESERIALIZE EVERY SAVE, and the comment here said it
    // was "cheap by design ... there will only ever be a handful of slots".
    // That was wrong in a way nobody noticed until the user reported the game
    // freezing (2026-08-24): a career save is the whole world, so five slots is
    // ~130 MB of JSON and ~150 000 objects — many SECONDS — and the desktop
    // menu was doing it while building a screen, for no reason beyond deciding
    // whether to show a LOAD GAME button.
    //
    // A label needs the competition name, the round, and two team names. None
    // of that is inside the career world, so read only what the label uses and
    // SKIP the rest without materializing it.
    public static System.Collections.Generic.List<(string slot, string label)> ListSlots()
    {
        var result = new System.Collections.Generic.List<(string slot, string label)>();

        try
        {
            string autoPath = FilePath();
            if (File.Exists(autoPath))
            {
                string? label = ReadSlotLabel(autoPath);
                if (label is not null) result.Add((AutosaveSlot, label));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.ListSlots: autosave unreadable, skipped: {ex.Message}");
        }

        try
        {
            string dir = ProjectSettings.GlobalizePath(SlotsDir);
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    string slot = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                    if (slot == AutosaveSlot) continue;   // reserved alias, never a named slot
                    try
                    {
                        string? label = ReadSlotLabel(file);
                        if (label is null) { GD.PrintErr($"CompetitionStore.ListSlots: '{slot}' is empty, skipped"); continue; }
                        result.Add((slot, label));
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"CompetitionStore.ListSlots: '{slot}' corrupt, skipped: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.ListSlots failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// The save-slot label read straight off the file with a streaming reader:
    /// the six top-level scalars the label uses, plus the NAMES of the entrant
    /// teams. Every other property — above all <c>Career</c>, which holds the
    /// ~29 000-player world — is skipped without being turned into objects.
    ///
    /// Falls back to a full parse if the streaming read fails for any reason,
    /// so an unexpected layout costs speed and never a missing save.
    /// </summary>
    private static string? ReadSlotLabel(string path)
    {
        try
        {
            // Read a PREFIX first. Everything the label needs is written before
            // `Career` (property order follows the class), and `Career` is the
            // ~29 MB of world, so a few hundred KB is almost always the whole
            // answer — and this runs once per save file every time a slot list
            // is shown. Falls through to the full file if the prefix is short.
            byte[]? prefix = ReadPrefix(path, 512 * 1024);
            if (prefix is not null)
            {
                string? quick = LabelFromBytes(prefix, isFinalBlock: false);
                if (quick is not null) return quick;
            }
            return LabelFromBytes(File.ReadAllBytes(path), isFinalBlock: true);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CompetitionStore.ReadSlotLabel('{Path.GetFileName(path)}') fell back to a full parse: {ex.Message}");
            try
            {
                var state = FromText(File.ReadAllText(path));
                return state is null ? null : SlotLabel(state);
            }
            catch { return null; }
        }
    }

    /// <summary>The first <paramref name="max"/> bytes of a file, or null.</summary>
    private static byte[]? ReadPrefix(string path, int max)
    {
        try
        {
            // Fully qualified: Godot also defines a FileAccess, and `using Godot`
            // makes the bare name ambiguous.
            using var fs = new FileStream(path, FileMode.Open,
                System.IO.FileAccess.Read, FileShare.ReadWrite);
            int want = (int)Math.Min(max, fs.Length);
            var buf = new byte[want];
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(buf, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got == 0) return null;
            if (got != want) Array.Resize(ref buf, got);
            return buf;
        }
        catch { return null; }
    }

    /// <summary>
    /// Scans a save (or the front of one) for the label fields, skipping every
    /// other property without materializing it. Returns null when the buffer
    /// ran out before all the fields were seen, which tells the caller to read
    /// the whole file.
    /// </summary>
    private static string? LabelFromBytes(byte[] bytes, bool isFinalBlock)
    {
        try
        {
            string name = "";
            bool finished = false;
            int champion = -1, currentRound = 0, totalRounds = 0, playerTeam = -1;
            var teamNames = new System.Collections.Generic.List<string>();

            const int Wanted = 0x7F;   // the seven fields below, one bit each
            int seen = 0;

            var reader = new Utf8JsonReader(bytes, isFinalBlock, default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (seen == Wanted) break;                 // everything the label needs
                if (reader.TokenType != JsonTokenType.PropertyName) return null;
                string prop = reader.GetString() ?? "";
                if (!reader.Read()) return null;

                switch (prop)
                {
                    case "Name": name = reader.GetString() ?? ""; seen |= 1; break;
                    case "Finished": finished = reader.GetBoolean(); seen |= 2; break;
                    case "Champion": champion = reader.GetInt32(); seen |= 4; break;
                    case "CurrentRound": currentRound = reader.GetInt32(); seen |= 8; break;
                    case "TotalRounds": totalRounds = reader.GetInt32(); seen |= 16; break;
                    case "PlayerTeam": playerTeam = reader.GetInt32(); seen |= 32; break;
                    case "Teams":
                        seen |= 64;
                        // [{ MasterIndex, GlobalId, Name, Strength }, ...] — the
                        // entrant list, a couple of dozen rows, not the world.
                        if (reader.TokenType != JsonTokenType.StartArray) { if (!reader.TrySkip()) return null; break; }
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject) { if (!reader.TrySkip()) return null; continue; }
                            string teamName = "";
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) break;
                                bool isName = reader.ValueTextEquals("Name");
                                if (!reader.Read()) break;
                                if (isName) teamName = reader.GetString() ?? "";
                                else if (!reader.TrySkip()) return null;
                            }
                            teamNames.Add(teamName);
                        }
                        break;
                    default:
                        // Career (the world), Fixtures, Scorers... TrySkip so a
                        // truncated prefix reports "need more" instead of throwing.
                        if (!reader.TrySkip()) return null;
                        break;
                }
            }
            if (seen != Wanted) return null;               // prefix ran out

            string Team(int i) => i >= 0 && i < teamNames.Count ? teamNames[i] : "";
            return BuildSlotLabel(name, finished, Team(champion), champion >= 0 && champion < teamNames.Count,
                                  currentRound, totalRounds, Team(playerTeam),
                                  playerTeam >= 0 && playerTeam < teamNames.Count);
        }
        catch (JsonException)
        {
            return null;    // truncated prefix, or a layout we do not recognise
        }
    }

    // "<NAME> - <round/champion summary> - <team you manage>". Derived from
    // the state alone — deliberately no CompetitionEngine dependency.
    private static string SlotLabel(CompetitionState state)
        => BuildSlotLabel(state.Name, state.Finished,
                          state.Champion >= 0 && state.Champion < state.Teams.Count ? state.Teams[state.Champion].Name : "",
                          state.Champion >= 0 && state.Champion < state.Teams.Count,
                          state.CurrentRound, state.TotalRounds,
                          state.PlayerTeam >= 0 && state.PlayerTeam < state.Teams.Count ? state.Teams[state.PlayerTeam].Name : "",
                          state.PlayerTeam >= 0 && state.PlayerTeam < state.Teams.Count);

    /// <summary>
    /// The one place the label wording lives, so the streaming reader and the
    /// full-parse fallback cannot drift apart.
    /// </summary>
    private static string BuildSlotLabel(string name, bool finished, string championName, bool hasChampion,
                                         int currentRound, int totalRounds, string teamName, bool hasTeam)
    {
        string progress = finished
            ? (hasChampion
                ? string.Format(Loc.Tr("comp.slot_winner", "WINNER {0}"), championName)
                : Loc.Tr("comp.slot_finished", "FINISHED"))
            : string.Format(Loc.Tr("comp.slot_round", "ROUND {0}/{1}"), currentRound + 1, totalRounds);
        string team = hasTeam ? teamName : Loc.Tr("comp.slot_no_team", "NO TEAM");
        return $"{name} - {progress} - {team}";
    }
}
