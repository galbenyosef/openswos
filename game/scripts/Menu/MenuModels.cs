using Godot;

namespace OpenSwos.Menu;

// One row on a menu screen. Three kinds:
//   Button — a label the player activates with FIRE (start match, open a screen).
//   Option — a label plus a value box with ‹ › arrows; LEFT/RIGHT change the value.
//   Label  — a static, non-selectable caption / spacer.
public enum EntryKind { Button, Option, Label }

public sealed class MenuEntry
{
    public string Id = "";
    public EntryKind Kind = EntryKind.Button;
    public MenuTheme.Style Style = MenuTheme.Style.Tool;
    public bool Big = true;

    public System.Func<string> Label = () => "";
    public System.Func<string>? Value;        // Option only
    public System.Action? OnActivate;         // Button only
    public System.Action<int>? OnStep;        // Option only — delta already scaled by hold-repeat
    public bool FastScroll;                   // Option only — held ‹ › accelerates (team lists)

    // Explicit geometry (screen fills these during layout).
    public int X, Y, W, H;

    public bool Selectable => Kind != EntryKind.Label;

    // Live Godot nodes (created lazily by the screen; disposed on rebuild).
    public Sprite2D? Bg, LabelSpr, ValueBg, ValueSpr, ArrowL, ArrowR;
}

// Everything a menu screen needs from the game. Implemented by Main (see
// Main.MenuHost.cs) so the menu module stays decoupled from gameplay internals.
public interface IMenuHost
{
    // Match-setup state (each Step* wraps, de-dupes home!=away, and re-applies
    // the live preview underneath the menu).
    int  TeamCount { get; }
    string TeamTag(int idx);                  // compact "NAME  AVGn" for value boxes
    string TeamName(int idx);                 // bare team name (uppercased)
    int  HomeIndex { get; }
    int  AwayIndex { get; }
    void StepHome(int delta);
    void StepAway(int delta);
    // Direct selection by master-list index (country-filtered pickers).
    void SetHomeTeam(int masterIndex);
    void SetAwayTeam(int masterIndex);
    void SwapTeams();

    string PitchLabel { get; }
    void StepPitch(int delta);

    string OpponentLabel { get; }
    void StepOpponent(int delta);

    string LengthLabel { get; }
    void StepLength(int delta);

    string SpeedLabel { get; }
    void StepSpeed(int delta);

    // Actions.
    void StartMatch();
    void QuitGame();

    // Team-config data (squad, kit, tactics).
    OpenSwos.Assets.TeamRecord Team(int idx);
    // Approximate RGB for a SWOS kit colour byte, for kit swatches.
    Color KitSwatch(byte colorByte);

    // --- competition support ---
    // Launches a sim match with the HUMAN always on the first (home) slot.
    void StartCompetitionMatch(int playerMasterIndex, int opponentMasterIndex);
    // Non-null exactly once after a competition match reaches full time:
    // (playerGoals, opponentGoals). Cleared by the call.
    (int player, int opponent)? TakeLastCompetitionResult();
    int TeamStrength(int idx);          // avg per-stat skill 1..7
    int TeamDivision(int idx);
    int TeamNation(int idx);
    ushort TeamGlobalId(int idx);
    // Master-list indices of teams in a nation+division (division -1 = any),
    // capped at max, EXCLUDING none (caller filters). Ordered as in the master list.
    System.Collections.Generic.List<int> TeamsByNationDivision(int nation, int division, int max);
    // count random distinct master indices, always including mustInclude (if >=0).
    System.Collections.Generic.List<int> RandomTeams(int count, int mustInclude);
    // Local 2-player: set opponent mode to PLAYER 2 (WASD) and start a match
    // with the current home/away selection.
    void StartLocalMultiplayerMatch();

    // --- gameplay options ---
    bool SkillScalingEnabled { get; }
    void ToggleSkillScaling();

    // --- gameplay options wave 2 ---
    bool AllPlayerTeamsEqual { get; }
    void ToggleAllPlayerTeamsEqual();
    bool ShowPreMatchMenus { get; }
    void ToggleShowPreMatchMenus();

    // --- display / fullscreen ---
    // Mirrors the F11 cycle: Windowed / Fullscreen Fill / Fullscreen Integer.
    string DisplayModeLabel { get; }
    void CycleDisplayMode();
    // True exactly once after a NON-competition match returns to the menu.
    bool TakeFriendlyJustEnded();
}
