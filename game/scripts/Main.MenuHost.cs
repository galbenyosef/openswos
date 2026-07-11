using Godot;
using OpenSwos.Sim;

namespace OpenSwos;

// Bridge between the SWOS-styled front-end (game/scripts/Menu/) and the game.
// Kept in its own partial so Main.cs stays lean and the menu module never
// reaches into gameplay internals directly — it only sees this interface.
//
// Every Step* here mirrors the exact wrap / de-dupe / re-apply logic the old
// plain-text TickMenu used, so the live preview underneath the menu stays
// correct.
public partial class Main : OpenSwos.Menu.IMenuHost
{
    // SWOS team-kit palette (kTeamPalette, swos-port color.cpp:9-13) — used for
    // the squad-screen kit swatches. Copied constant (licence: constants OK).
    private static readonly Color[] kTeamPaletteSwatch =
    {
        new Color(60/255f,184/255f,0), new Color(152/255f,152/255f,152/255f), new Color(1,1,1), new Color(0,0,0),
        new Color(100/255f,32/255f,0), new Color(184/255f,68/255f,0), new Color(252/255f,100/255f,0), new Color(140/255f,184/255f,0),
        new Color(0,32/255f,0), new Color(60/255f,184/255f,0), new Color(1,0,0), new Color(0,0,1),
        new Color(100/255f,0,32/255f), new Color(152/255f,152/255f,152/255f), new Color(0,220/255f,0), new Color(1,1,0),
    };

    // ---- teams ---------------------------------------------------------------
    int OpenSwos.Menu.IMenuHost.TeamCount => _allTeams.Count;
    int OpenSwos.Menu.IMenuHost.HomeIndex => _homeTeamIndex;
    int OpenSwos.Menu.IMenuHost.AwayIndex => _awayTeamIndex;
    // Compact, ASCII-only team label for the value box (the SWOS charset has no
    // '·'/'&', and the box is narrow): "NAME  AVGn". Full detail lives on the
    // TEAM SETUP squad screen.
    string OpenSwos.Menu.IMenuHost.TeamTag(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return "?";
        var t = _allTeams[idx];
        int n = t.Players.Count, sum = 0;
        foreach (var p in t.Players)
            sum += p.Passing + p.Shooting + p.Heading + p.Tackling + p.Control + p.Speed + p.Finishing;
        int avg = (n > 0) ? sum / (n * 7) : 0;
        string name = (t.Name ?? "").Trim();
        if (name.Length > 18) name = name.Substring(0, 18);
        return $"{name}  AVG{avg}";
    }

    string OpenSwos.Menu.IMenuHost.TeamName(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return "?";
        return (_allTeams[idx].Name ?? "").Trim();
    }

    void OpenSwos.Menu.IMenuHost.StepHome(int delta)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _homeTeamIndex = (_homeTeamIndex + delta + n) % n;
        if (_homeTeamIndex == _awayTeamIndex)
            _homeTeamIndex = (_homeTeamIndex + System.Math.Sign(delta == 0 ? 1 : delta) + n) % n;
        ApplyMatchSetup();
    }

    void OpenSwos.Menu.IMenuHost.StepAway(int delta)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _awayTeamIndex = (_awayTeamIndex + delta + n) % n;
        if (_awayTeamIndex == _homeTeamIndex)
            _awayTeamIndex = (_awayTeamIndex + System.Math.Sign(delta == 0 ? 1 : delta) + n) % n;
        ApplyMatchSetup();
    }

    void OpenSwos.Menu.IMenuHost.SwapTeams()
    {
        (_homeTeamIndex, _awayTeamIndex) = (_awayTeamIndex, _homeTeamIndex);
        ApplyMatchSetup();
    }

    // Direct team selection (nation-filtered pickers). De-dupes like the
    // steppers: colliding with the other side nudges that side by one.
    void OpenSwos.Menu.IMenuHost.SetHomeTeam(int masterIndex)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _homeTeamIndex = System.Math.Clamp(masterIndex, 0, n - 1);
        if (_awayTeamIndex == _homeTeamIndex) _awayTeamIndex = (_awayTeamIndex + 1) % n;
        ApplyMatchSetup();
    }

    void OpenSwos.Menu.IMenuHost.SetAwayTeam(int masterIndex)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _awayTeamIndex = System.Math.Clamp(masterIndex, 0, n - 1);
        if (_homeTeamIndex == _awayTeamIndex) _homeTeamIndex = (_homeTeamIndex + 1) % n;
        ApplyMatchSetup();
    }

    // ---- pitch ---------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.PitchLabel =>
        _availablePitches.Count > 0 ? $"SWCPICH{PitchId}  ({_pitchSlot + 1}/{_availablePitches.Count})" : "(NONE)";

    void OpenSwos.Menu.IMenuHost.StepPitch(int delta)
    {
        if (_availablePitches.Count == 0) return;
        int d = System.Math.Sign(delta);
        _pitchSlot = (_pitchSlot + d + _availablePitches.Count) % _availablePitches.Count;
        LoadPitchVariant(PitchId);
    }

    // ---- opponent ------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.OpponentLabel => _opponentMode switch
    {
        OpponentMode.Ai => "AI",
        OpponentMode.Player2 => "PLAYER 2  (WASD)",
        OpponentMode.Demo => "DEMO  (AI V AI)",
        _ => "?",
    };

    void OpenSwos.Menu.IMenuHost.StepOpponent(int delta) =>
        _opponentMode = (OpponentMode)(((int)_opponentMode + System.Math.Sign(delta == 0 ? 1 : delta) + 3) % 3);

    // ---- length --------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.LengthLabel =>
        SecondsPerHalf < 60 ? $"{SecondsPerHalf}S REAL  (45 MIN)" : $"{SecondsPerHalf / 60} MIN REAL  (45 MIN)";

    void OpenSwos.Menu.IMenuHost.StepLength(int delta)
    {
        int d = System.Math.Sign(delta);
        _matchLengthSlot = (_matchLengthSlot + d + MatchLengthPresets.Length) % MatchLengthPresets.Length;
    }

    // ---- speed ---------------------------------------------------------------
    string OpenSwos.Menu.IMenuHost.SpeedLabel =>
        $"x{_gameSpeedScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";

    void OpenSwos.Menu.IMenuHost.StepSpeed(int delta)
    {
        _gameSpeedScale = System.Math.Round(
            System.Math.Clamp(_gameSpeedScale + System.Math.Sign(delta == 0 ? 1 : delta) * SpeedScaleStep,
                              SpeedScaleMin, SpeedScaleMax), 2);
        SaveSettings();
    }

    // ---- actions -------------------------------------------------------------
    void OpenSwos.Menu.IMenuHost.StartMatch()
    {
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _appState = AppState.Match;
    }

    void OpenSwos.Menu.IMenuHost.QuitGame() => GetTree().Quit();

    // ---- competition support ---------------------------------------------------
    // A competition match is a normal sim match with the HUMAN always on the
    // first (home) slot; the menu maps the (player, opponent) score back onto
    // the fixture's home/away orientation itself. The result is captured at the
    // FullTime -> Menu transition in _PhysicsProcess (see _competitionMatchPending
    // consumers in Main.cs) and handed over exactly once via
    // TakeLastCompetitionResult.
    private bool _competitionMatchPending;
    private (int player, int opponent)? _lastCompetitionResult;

    void OpenSwos.Menu.IMenuHost.StartCompetitionMatch(int playerMasterIndex, int opponentMasterIndex)
    {
        int n = _allTeams.Count; if (n == 0) return;
        _homeTeamIndex = System.Math.Clamp(playerMasterIndex, 0, n - 1);
        _awayTeamIndex = System.Math.Clamp(opponentMasterIndex, 0, n - 1);
        if (_awayTeamIndex == _homeTeamIndex) _awayTeamIndex = (_awayTeamIndex + 1) % n;
        _opponentMode = OpponentMode.Ai;   // competition fixtures are vs CPU
        ApplyMatchSetup();
        _competitionMatchPending = true;
        _lastCompetitionResult = null;
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _appState = AppState.Match;
    }

    (int player, int opponent)? OpenSwos.Menu.IMenuHost.TakeLastCompetitionResult()
    {
        var r = _lastCompetitionResult;
        _lastCompetitionResult = null;
        return r;
    }

    int OpenSwos.Menu.IMenuHost.TeamStrength(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return 3;
        var t = _allTeams[idx];
        int n = t.Players.Count, sum = 0;
        foreach (var p in t.Players)
            sum += p.Passing + p.Shooting + p.Heading + p.Tackling + p.Control + p.Speed + p.Finishing;
        return (n > 0) ? System.Math.Clamp(sum / (n * 7), 1, 7) : 3;
    }

    int OpenSwos.Menu.IMenuHost.TeamDivision(int idx) =>
        (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx].Division : 0;

    int OpenSwos.Menu.IMenuHost.TeamNation(int idx) =>
        (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx].Nation : 0;

    ushort OpenSwos.Menu.IMenuHost.TeamGlobalId(int idx) =>
        (idx >= 0 && idx < _allTeams.Count) ? _allTeams[idx].GlobalId : (ushort)0;

    System.Collections.Generic.List<int> OpenSwos.Menu.IMenuHost.TeamsByNationDivision(int nation, int division, int max)
    {
        var result = new System.Collections.Generic.List<int>();
        for (int i = 0; i < _allTeams.Count && result.Count < max; i++)
        {
            var t = _allTeams[i];
            if (t.Nation != nation) continue;
            if (division >= 0 && t.Division != division) continue;
            result.Add(i);
        }
        return result;
    }

    System.Collections.Generic.List<int> OpenSwos.Menu.IMenuHost.RandomTeams(int count, int mustInclude)
    {
        // Deterministic per (count, mustInclude): entrant draws must not depend
        // on wall-clock so a re-created competition with the same picks gets the
        // same field (lockstep-friendly; see docs/decisions/01-netcode-model.md).
        var result = new System.Collections.Generic.List<int>();
        int n = _allTeams.Count; if (n == 0) return result;
        if (mustInclude >= 0 && mustInclude < n) result.Add(mustInclude);
        uint rng = (uint)(mustInclude * 2654435761 + count * 40503 + 0x1234567);
        int guard = 0;
        while (result.Count < System.Math.Min(count, n) && guard++ < 10000)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;   // xorshift32
            int pick = (int)(rng % (uint)n);
            if (!result.Contains(pick)) result.Add(pick);
        }
        return result;
    }

    void OpenSwos.Menu.IMenuHost.StartLocalMultiplayerMatch()
    {
        _opponentMode = OpponentMode.Player2;
        ApplyMatchSetup();
        _match = MatchState.NewMatch();
        EnterPreKickoff();
        _appState = AppState.Match;
    }

    // ---- gameplay options ------------------------------------------------------
    // Faithful ScaleSkill price/team-value feedback (task #196) — ON by default,
    // toggleable from OPTIONS page 2. Takes effect on the NEXT match load
    // (TeamDataLoader consults SkillScaling.Enabled when writing skills).
    bool OpenSwos.Menu.IMenuHost.SkillScalingEnabled => OpenSwos.Sim.Port.SkillScaling.Enabled;

    void OpenSwos.Menu.IMenuHost.ToggleSkillScaling()
    {
        OpenSwos.Sim.Port.SkillScaling.Enabled = !OpenSwos.Sim.Port.SkillScaling.Enabled;
        SaveSettings();
        ApplyMatchSetup();   // re-seed the preview/next match with the new mode
    }

    // ALL TEAMS EQUAL — the original's "all player teams equal" gameplay option:
    // ScaleSkill's teamAppPercent equalizer only activates for player-coached
    // teams when this is on (swos.asm:35761-35825; SkillScaling.TeamAppPercent).
    bool OpenSwos.Menu.IMenuHost.AllPlayerTeamsEqual => OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual;

    void OpenSwos.Menu.IMenuHost.ToggleAllPlayerTeamsEqual()
    {
        OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual = !OpenSwos.Sim.Port.SkillScaling.AllPlayerTeamsEqual;
        SaveSettings();
        ApplyMatchSetup();
    }

    // PRE MATCH MENUS — gates the versus + stadium-lineups screens (the
    // original's "show pre-match menus" gameplay option). Menu-side gate; the
    // host only persists the preference.
    private bool _showPreMatchMenus = true;
    bool OpenSwos.Menu.IMenuHost.ShowPreMatchMenus => _showPreMatchMenus;

    void OpenSwos.Menu.IMenuHost.ToggleShowPreMatchMenus()
    {
        _showPreMatchMenus = !_showPreMatchMenus;
        SaveSettings();
    }

    // True exactly once after a NON-competition match returns to the menu —
    // drives the post-friendly PLAY AGAIN / EXIT screen (replayExit.mnu).
    private bool _friendlyJustEnded;
    bool OpenSwos.Menu.IMenuHost.TakeFriendlyJustEnded()
    {
        bool v = _friendlyJustEnded;
        _friendlyJustEnded = false;
        return v;
    }

    // ---- display / fullscreen --------------------------------------------------
    // OPTIONS "DISPLAY" row — mirrors the F11 hotkey exactly (cycle Windowed ->
    // Fullscreen Fill -> Fullscreen Integer). The label uses the same uppercase
    // ASCII text the hotkey path uses (SWOS charset has no lowercase).
    string OpenSwos.Menu.IMenuHost.DisplayModeLabel => DisplayModeToLabel(_displayMode);

    void OpenSwos.Menu.IMenuHost.CycleDisplayMode() => CycleDisplayModeInternal();

    // ---- team-config data ----------------------------------------------------
    OpenSwos.Assets.TeamRecord OpenSwos.Menu.IMenuHost.Team(int idx)
    {
        if (idx < 0 || idx >= _allTeams.Count) return new OpenSwos.Assets.TeamRecord();
        return _allTeams[idx];
    }

    Color OpenSwos.Menu.IMenuHost.KitSwatch(byte colorByte) => kTeamPaletteSwatch[colorByte & 15];
}
