using Godot;
using OpenSwos.Competition;
using OpenSwos.Competition.Career;

namespace OpenSwos.Menu;

// ============================================================================
// Career screens added on 2026-08-26:
//   TRAINING       — the weekly session (TrainingModel), user directive
//   TRAINING REPORT— how it went, player by player
//   CHRONICLE      — the season's own history (career depth plan feature #7)
//   YOUTH INTAKE   — the academy's new players (feature #6)
//   CLUB LEGENDS   — appearances and goals for this club (feature #8)
//
// All five are VIEWS. Every rule they show lives in game/scripts/Competition/,
// so the browser client gets the identical feature from the same code — the
// thin-client rule from 02-match-streaming-and-multiplayer.md.
// ============================================================================
public sealed partial class MenuClient
{
    private int _trainPage;
    private int _trainSelectedIndex;
    private string? _trainNotice;
    private int _chronSeasonView;      // 0 = all seasons, else index into the season list
    private int _chronPage;
    private int _youthSelectedIndex;
    private string? _youthNotice;
    private int _legendPage;

    // ------------------------------------------------------------------
    // TRAINING
    // ------------------------------------------------------------------

    private MenuScreen BuildTrainingScreen()
    {
        var c = LoadedComp();
        _trainPage = 0;
        _trainSelectedIndex = 0;
        _trainNotice = null;

        var s = new MenuScreen { Title = Loc.Tr("train.title", "TRAINING"), BodyReserve = 96 };
        if (c?.Career is null || CurrentCareerClub() is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = TrainingStatusLabel });
            s.Entries.Add(new MenuEntry
            {
                Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("train.drill", "DRILL"),
                Value = TrainingDrillLabel,
                OnStep = delta =>
                {
                    int n = TrainingModel.Drills.Length;
                    c.Career.TrainingDrill = ((c.Career.TrainingDrill + delta) % n + n) % n;
                    RebuildCurrent();
                },
            });
            s.Entries.Add(new MenuEntry
            {
                Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("train.intensity", "INTENSITY"),
                Value = TrainingIntensityLabel,
                OnStep = delta =>
                {
                    c.Career.TrainingIntensity = System.Math.Clamp(c.Career.TrainingIntensity + delta, 0, 2);
                    RebuildCurrent();
                },
            });
            var playerField = new MenuEntry
            {
                Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.player", "PLAYER"),
                Value = TrainingSelectedLabel,
                OnActivate = EnterTableSelectCurrent,
            };
            s.Entries.Add(playerField);
            s.TableSelect = new MenuTableSelect
            {
                Field = playerField,
                Count = () => SquadPlayers().Count,
                GetIndex = () => _trainSelectedIndex,
                SetIndex = idx => { _trainSelectedIndex = idx; _trainPage = idx / TrainingRowsPerPage(); },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("train.toggle", "ADD / REMOVE"), OnActivate = ToggleTrainingGroup });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("train.auto", "AUTO PICK"), OnActivate = AutoPickTrainingGroup });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlaySecondary, Big = false,
                Label = TrainingRunLabel, OnActivate = RunTrainingSession });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("train.report", "LAST REPORT"), OnActivate = () => Push(BuildTrainingReportScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.next_page", "NEXT PAGE"), OnActivate = () => StepTrainingPage(+1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _trainNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawTrainingBody(s));
        return s;
    }

    /// <summary>Flashes a "!" while this week's session has not been run.</summary>
    private string TrainingEntryLabel()
    {
        string label = Loc.Tr("dash.training", "TRAINING");
        var c = LoadedComp();
        bool waiting = c is not null && TrainingModel.CanTrain(c);
        return waiting && (Godot.Time.GetTicksMsec() / 400) % 2 == 0 ? "! " + label : label;
    }

    private string TrainingStatusLabel()
    {
        var c = LoadedComp();
        if (c is null) return "";
        if (TrainingModel.AlreadyTrained(c))
            return Loc.Tr("train.done_week", "TRAINED THIS WEEK - NEXT SESSION AFTER THE MATCH");
        if (!TrainingModel.CanTrain(c))
            return Loc.Tr("train.closed", "NO TRAINING BETWEEN SEASONS");
        var club = CurrentCareerClub();
        int coach = TrainingModel.BestCoachQuality(club, TrainingModel.DrillAt(c.Career!.TrainingDrill));
        return Loc.Tr("train.ready", "SESSION READY") + "   "
             + Loc.Tr("train.coach", "COACH") + " " + coach + "/7";
    }

    private string TrainingDrillLabel()
    {
        var c = LoadedComp();
        var drill = TrainingModel.DrillAt(c?.Career?.TrainingDrill ?? 0);
        return FitText(Loc.Tr(drill.Key, drill.Name), false, 150);
    }

    private string TrainingIntensityLabel()
    {
        int i = System.Math.Clamp(LoadedComp()?.Career?.TrainingIntensity ?? 1, 0, 2);
        return Loc.Tr("train.int_" + i, TrainingModel.IntensityNames[i]);
    }

    private string TrainingRunLabel()
    {
        var c = LoadedComp();
        int n = c?.Career?.TrainingGroup?.Count ?? 0;
        return Loc.Tr("train.run", "RUN SESSION") + (n > 0 ? " (" + n + ")" : "");
    }

    private string TrainingSelectedLabel()
    {
        var players = SquadPlayers();
        if (players.Count == 0) return Loc.Tr("common.none", "NONE");
        _trainSelectedIndex = System.Math.Clamp(_trainSelectedIndex, 0, players.Count - 1);
        return FitText(AsciiText(players[_trainSelectedIndex].Name), false, 132);
    }

    private int TrainingRowsPerPage()
    {
        int panelY = TablePanelY;
        int panelH = TableVh - panelY - 21;
        return System.Math.Max(1, (panelH - 29) / 8);
    }

    private void StepTrainingPage(int delta)
    {
        int rows = TrainingRowsPerPage();
        int count = SquadPlayers().Count;
        int pages = System.Math.Max(1, (count + rows - 1) / rows);
        _trainPage = ((_trainPage + delta) % pages + pages) % pages;
        _trainSelectedIndex = System.Math.Min(_trainPage * rows, System.Math.Max(0, count - 1));
        RebuildCurrent();
    }

    private void ToggleTrainingGroup()
    {
        var c = LoadedComp();
        var players = SquadPlayers();
        if (c?.Career is null || players.Count == 0) { _trainNotice = Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE"); RebuildCurrent(); return; }
        _trainSelectedIndex = System.Math.Clamp(_trainSelectedIndex, 0, players.Count - 1);
        var player = players[_trainSelectedIndex];
        if (TrainingModel.ToggleGroup(c, player.Id, out string refusal))
        {
            CompetitionStore.Save(c);
            _trainNotice = null;
        }
        else _trainNotice = Loc.Tr("train.group_full", "GROUP FULL - MAX") + " " + TrainingModel.MaxGroup;
        RebuildCurrent();
    }

    private void AutoPickTrainingGroup()
    {
        var c = LoadedComp();
        var club = CurrentCareerClub();
        if (c?.Career is null || club is null) return;
        c.Career.TrainingGroup = TrainingModel.AutoGroup(club, c.Career.TrainingDrill);
        CompetitionStore.Save(c);
        _trainNotice = Loc.Tr("train.auto_done", "GROUP PICKED BY POTENTIAL");
        RebuildCurrent();
    }

    private void RunTrainingSession()
    {
        var c = LoadedComp();
        if (c is null) return;
        // A session touches at most 22 players, so it is fast — but it does
        // write the save, which is the expensive part (see "THE CLIENT MUST
        // NEVER FREEZE SILENTLY" in CLAUDE.md), so it goes behind PLEASE WAIT.
        RunBusy(Loc.Tr("busy.training", "TRAINING"), () =>
        {
            if (TrainingModel.RunSession(c, out string refusal))
            {
                CompetitionStore.Save(c);
                ReplaceTop(BuildTrainingReportScreen());
            }
            else
            {
                _trainNotice = refusal;
                RebuildCurrent();
            }
        });
    }

    private void DrawTrainingBody(MenuScreen s)
    {
        var c = LoadedComp();
        var club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        var dim = new Color(0.62f, 0.68f, 0.82f);
        if (c?.Career is null || club is null)
        {
            CareerTableText(s, Loc.Tr("common.no_squad_data", "NO SQUAD DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        var drill = TrainingModel.DrillAt(c.Career.TrainingDrill);
        // What the drill actually trains, spelled out — the manager should never
        // have to guess which of the seven SWOS skills a drill moves.
        string trains = drill.Recovery ? Loc.Tr("train.trains_fitness", "CONDITION")
                      : drill.Keeper ? Loc.Tr("train.trains_keeper", "GOALKEEPING")
                      : SkillListOf(drill);
        CareerTableText(s, Loc.Tr("train.trains", "TRAINS") + ": " + trains, panelX + 8, panelY + 4, head);

        int mark = panelX + 8, name = panelX + 24 + HeadIconAdvance, pos = panelX + 250;
        int age = panelX + 300, con = panelX + 348, shp = panelX + 394, pot = panelX + 450, fit = panelX + panelW - 8;
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("train.col_in", "IN"), mark, y, head);
        CareerTableText(s, Loc.Tr("col.name", "NAME"), name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"), pos, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"), age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("train.col_con", "CON"), con, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("train.col_shp", "SHP"), shp, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("train.col_room", "ROOM"), pot, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"), fit, y, head, rightAlign: true);
        y += 10;

        var players = SquadPlayers();
        var group = c.Career.TrainingGroup ?? new System.Collections.Generic.List<int>();
        int rows = TrainingRowsPerPage();
        int pages = System.Math.Max(1, (players.Count + rows - 1) / rows);
        _trainPage = System.Math.Clamp(_trainPage, 0, pages - 1);
        for (int i = _trainPage * rows; i < players.Count && i < _trainPage * rows + rows; i++)
        {
            var p = players[i];
            if (i == _trainSelectedIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            bool inGroup = group.Contains(p.Id);
            bool suits = drill.Suits.Contains(ScorerModel.LineOf(p.Position), System.StringComparison.Ordinal);
            int condition = System.Math.Clamp(100 - p.FatigueCarry, 0, 99);
            double room = System.Math.Clamp(p.Potential - PotentialModel.OverallOf(p), 0.0, 7.0);
            Color rowColor = p.InjurySeverity >= 2 ? InjuryRed : inGroup ? gold : suits ? normal : dim;

            CareerTableText(s, inGroup ? "*" : "", mark, y, gold);
            BodyHeadIcon(s, p.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(p));
            CareerCell(s, p.Name, name, y, pos - name - 4, rowColor);
            CareerCell(s, p.Position, pos, y, age - pos - 18, rowColor);
            CareerTableText(s, p.Age.ToString(), age, y, rowColor, rightAlign: true);
            CareerTableText(s, p.InjurySeverity >= 2 ? Loc.Tr("squad.fit_injured", "INJ") : condition.ToString(),
                con, y, p.InjurySeverity >= 2 ? InjuryRed : rowColor, rightAlign: true);
            CareerTableText(s, System.Math.Clamp(p.Sharpness, 0, 100).ToString(), shp, y, rowColor, rightAlign: true);
            CareerTableText(s, room.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                pot, y, room >= 1.0 ? gold : rowColor, rightAlign: true);
            CareerTableText(s, p.EffectiveSkillSum().ToString(), fit, y, rowColor, rightAlign: true);
            y += 8;
        }
    }

    private static string SkillListOf(TrainingDrill drill)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (int i in drill.Skills)
            if (i >= 0 && i < TrainingModel.SkillNames.Length)
                parts.Add(Loc.Tr("skill." + TrainingModel.SkillNames[i].ToLowerInvariant(),
                                 TrainingModel.SkillNames[i]));
        return string.Join(" + ", parts);
    }

    // ------------------------------------------------------------------
    // TRAINING REPORT
    // ------------------------------------------------------------------

    private MenuScreen BuildTrainingReportScreen()
    {
        var s = new MenuScreen { Title = Loc.Tr("train.report_title", "TRAINING REPORT"), BodyReserve = 40 };
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawTrainingReportBody(s));
        return s;
    }

    private void DrawTrainingReportBody(MenuScreen s)
    {
        var c = LoadedComp();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        var dim = new Color(0.62f, 0.68f, 0.82f);
        var report = c?.Career?.TrainingReport;
        if (report is null || report.Count == 0)
        {
            CareerTableText(s, Loc.Tr("train.no_report", "NO SESSION HAS BEEN RUN YET"), panelX + 8, panelY + 8, head);
            return;
        }

        int name = panelX + 8, grade = panelX + 170, gain = panelX + 250, fit = panelX + panelW - 8;
        int y = panelY + 4;
        CareerTableText(s, Loc.Tr("col.name", "NAME"), name, y, head);
        CareerTableText(s, Loc.Tr("train.col_grade", "SESSION"), grade, y, head);
        CareerTableText(s, Loc.Tr("train.col_gain", "PROGRESS"), gain, y, head);
        CareerTableText(s, Loc.Tr("train.col_con", "CON"), fit, y, head, rightAlign: true);
        y += 12;
        foreach (var r in report)
        {
            if (y + 9 > panelY + panelH - 4) break;
            Color gradeColor = r.Grade >= 3 ? gold : r.Grade == 0 ? InjuryYellow : normal;
            string gains = r.Gains is { Count: > 0 } ? string.Join(", ", r.Gains) : "-";
            if (r.PotentialUp) gains = (gains == "-" ? "" : gains + "  ")
                                     + Loc.Tr("train.potential_up", "CEILING RAISED");
            CareerCell(s, r.Name, name, y, grade - name - 6, r.Injury > 0 ? InjuryRed : normal);
            CareerTableText(s, Loc.Tr("train.grade_" + r.Grade, TrainingModel.GradeNames[r.Grade]),
                grade, y, gradeColor);
            CareerCell(s, gains, gain, y, fit - gain - 34, r.Gains is { Count: > 0 } || r.PotentialUp ? gold : dim);
            CareerTableText(s, (r.FitnessDelta > 0 ? "+" : "") + r.FitnessDelta, fit, y,
                r.FitnessDelta < 0 ? dim : normal, rightAlign: true);
            y += 9;
            if (r.Injury > 0)
            {
                CareerTableText(s, "  " + Loc.Tr("train.injured", "INJURED IN TRAINING"), name, y, InjuryRed);
                y += 9;
            }
        }
    }

    // ------------------------------------------------------------------
    // CHRONICLE
    // ------------------------------------------------------------------

    private MenuScreen BuildChronicleScreen()
    {
        _chronSeasonView = 0;
        _chronPage = 0;
        var s = new MenuScreen { Title = Loc.Tr("chron.title", "CLUB DIARY"), BodyReserve = 62 };
        s.Entries.Add(new MenuEntry
        {
            Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("chron.season", "SEASON"),
            Value = ChronicleSeasonLabel,
            OnStep = delta =>
            {
                int n = Chronicle.Seasons(LoadedComp()?.Career).Count + 1;
                _chronSeasonView = ((_chronSeasonView + delta) % n + n) % n;
                _chronPage = 0;
                RebuildCurrent();
            },
        });
        s.Entries.Add(new MenuEntry
        {
            Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("common.page", "PAGE"),
            Value = () => (_chronPage + 1) + "/" + System.Math.Max(1, ChroniclePageCount()),
            OnStep = delta =>
            {
                int pages = System.Math.Max(1, ChroniclePageCount());
                _chronPage = ((_chronPage + delta) % pages + pages) % pages;
                RebuildCurrent();
            },
        });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawChronicleBody(s));
        return s;
    }

    private int ChronicleSeasonFilter()
    {
        var seasons = Chronicle.Seasons(LoadedComp()?.Career);
        if (_chronSeasonView <= 0 || _chronSeasonView > seasons.Count) return 0;
        return seasons[_chronSeasonView - 1];
    }

    private string ChronicleSeasonLabel()
    {
        int f = ChronicleSeasonFilter();
        return f == 0 ? Loc.Tr("chron.all", "ALL") : Loc.Tr("scorer.season", "SEASON") + " " + f;
    }

    /// <summary>Rows the diary panel can actually hold, not a guess: the first
    /// version hardcoded 20 and left the bottom half of the box empty.</summary>
    private int ChronicleRowsPerPage()
        => System.Math.Max(1, (TableVh - TablePanelY - 21 - 10) / 8);

    private int ChroniclePageCount()
    {
        int n = Chronicle.Read(LoadedComp()?.Career, ChronicleSeasonFilter()).Count;
        int rows = ChronicleRowsPerPage();
        return System.Math.Max(1, (n + rows - 1) / rows);
    }

    private void DrawChronicleBody(MenuScreen s)
    {
        var c = LoadedComp();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        var dim = new Color(0.62f, 0.68f, 0.82f);

        var rows = Chronicle.Read(c?.Career, ChronicleSeasonFilter());
        if (rows.Count == 0)
        {
            CareerTableText(s, Loc.Tr("chron.empty", "NOTHING HAS HAPPENED YET"), panelX + 8, panelY + 8, head);
            return;
        }
        int y = panelY + 5;
        int perPage = ChronicleRowsPerPage();
        int from = _chronPage * perPage;
        for (int i = from; i < rows.Count && i < from + perPage; i++)
        {
            if (y + 8 > panelY + panelH - 3) break;
            var e = rows[i];
            string when = e.Round >= 0
                ? "S" + e.Season + " R" + (e.Round + 1)
                : "S" + e.Season;
            Color col = e.Weight >= 2 ? gold : e.Weight == 1 ? normal : dim;
            CareerTableText(s, when, panelX + 8, y, dim);
            CareerCell(s, AsciiText(Chronicle.Render(e)), panelX + 62, y, panelW - 74, col);
            y += 8;
        }
    }

    // ------------------------------------------------------------------
    // YOUTH INTAKE
    // ------------------------------------------------------------------

    /// <summary>True while this season's intake has not been looked at.</summary>
    private bool YouthIntakeWaiting()
    {
        var career = LoadedComp()?.Career;
        return career is not null && !career.YouthIntakeSeen
            && career.YouthIntakeIds is { Count: > 0 }
            && career.YouthIntakeSeason == career.Season;
    }

    private string YouthEntryLabel()
    {
        string label = Loc.Tr("youth.entry", "YOUTH INTAKE");
        return YouthIntakeWaiting() && (Godot.Time.GetTicksMsec() / 400) % 2 == 0 ? "! " + label : label;
    }

    private System.Collections.Generic.List<CareerPlayer> YouthIntakePlayers()
    {
        var list = new System.Collections.Generic.List<CareerPlayer>();
        var career = LoadedComp()?.Career;
        var club = CurrentCareerClub();
        if (career?.YouthIntakeIds is null || club?.Squad is null) return list;
        foreach (int id in career.YouthIntakeIds)
            foreach (var p in club.Squad)
                if (p is not null && p.Id == id) { list.Add(p); break; }
        return list;
    }

    private MenuScreen BuildYouthIntakeScreen()
    {
        var c = LoadedComp();
        _youthSelectedIndex = 0;
        _youthNotice = null;
        if (c?.Career is not null && c.Career.YouthIntakeSeason == c.Career.Season)
        {
            c.Career.YouthIntakeSeen = true;
            CompetitionStore.Save(c);
        }

        var s = new MenuScreen { Title = Loc.Tr("youth.title", "YOUTH INTAKE"), BodyReserve = 72 };
        var youths = YouthIntakePlayers();
        if (youths.Count == 0)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => Loc.Tr("youth.none", "THE ACADEMY PRODUCED NOBODY THIS SUMMER") });
        }
        else
        {
            var playerField = new MenuEntry
            {
                Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.player", "PLAYER"),
                Value = () =>
                {
                    var list = YouthIntakePlayers();
                    if (list.Count == 0) return Loc.Tr("common.none", "NONE");
                    _youthSelectedIndex = System.Math.Clamp(_youthSelectedIndex, 0, list.Count - 1);
                    return FitText(AsciiText(list[_youthSelectedIndex].Name), false, 132);
                },
                OnActivate = EnterTableSelectCurrent,
            };
            s.Entries.Add(playerField);
            s.TableSelect = new MenuTableSelect
            {
                Field = playerField,
                Count = () => YouthIntakePlayers().Count,
                GetIndex = () => _youthSelectedIndex,
                SetIndex = idx => _youthSelectedIndex = idx,
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("youth.promote", "ADD TO TRAINING GROUP"), OnActivate = PromoteYouthToTraining });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("youth.release", "RELEASE"), OnActivate = ReleaseYouth });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _youthNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawYouthBody(s));
        return s;
    }

    private void PromoteYouthToTraining()
    {
        var c = LoadedComp();
        var list = YouthIntakePlayers();
        if (c?.Career is null || list.Count == 0) return;
        _youthSelectedIndex = System.Math.Clamp(_youthSelectedIndex, 0, list.Count - 1);
        var p = list[_youthSelectedIndex];
        if (c.Career.TrainingGroup?.Contains(p.Id) == true)
        {
            _youthNotice = Loc.Tr("youth.already", "ALREADY IN THE GROUP");
        }
        else if (TrainingModel.ToggleGroup(c, p.Id, out _))
        {
            CompetitionStore.Save(c);
            _youthNotice = Loc.Tr("youth.added", "ADDED TO TRAINING") + " " + AsciiText(p.Name);
        }
        else _youthNotice = Loc.Tr("train.group_full", "GROUP FULL - MAX") + " " + TrainingModel.MaxGroup;
        RebuildCurrent();
    }

    private void ReleaseYouth()
    {
        var c = LoadedComp();
        var list = YouthIntakePlayers();
        if (c?.Career?.World is null || list.Count == 0) return;
        _youthSelectedIndex = System.Math.Clamp(_youthSelectedIndex, 0, list.Count - 1);
        var p = list[_youthSelectedIndex];
        string name = AsciiText(p.Name);
        if (TransferOffers.FreeTransfer(c, c.Career.World, p.Id))
        {
            c.Career.YouthIntakeIds?.Remove(p.Id);
            CompetitionStore.Save(c);
            _youthNotice = Loc.Tr("youth.released", "RELEASED") + " " + name;
            _youthSelectedIndex = 0;
        }
        else _youthNotice = Loc.Tr("youth.cannot_release", "SQUAD TOO SMALL TO RELEASE");
        RebuildCurrent();
    }

    private void DrawYouthBody(MenuScreen s)
    {
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        var dim = new Color(0.62f, 0.68f, 0.82f);

        var list = YouthIntakePlayers();
        CareerTableText(s, Loc.Tr("youth.header",
            "THE ACADEMY REPORTS - SCOUTS GIVE A RANGE, NOT A NUMBER"), panelX + 8, panelY + 4, head);
        if (list.Count == 0)
        {
            CareerTableText(s, Loc.Tr("youth.none", "THE ACADEMY PRODUCED NOBODY THIS SUMMER"),
                panelX + 8, panelY + 18, normal);
            return;
        }

        int name = panelX + 24 + HeadIconAdvance, pos = panelX + 250, age = panelX + 300;
        int skill = panelX + 360, est = panelX + panelW - 8;
        int y = panelY + 16;
        CareerTableText(s, Loc.Tr("col.name", "NAME"), name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"), pos, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"), age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"), skill, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("youth.col_est", "SCOUT VERDICT"), est, y, head, rightAlign: true);
        y += 10;
        for (int i = 0; i < list.Count; i++)
        {
            if (y + 9 > panelY + panelH - 4) break;
            var p = list[i];
            if (i == _youthSelectedIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            // A youth is judged by his RANGE, never by the hidden true ceiling —
            // the whole point of an academy is that nobody knows yet.
            double low = System.Math.Max(0.0, p.Potential - 1.1);
            double high = System.Math.Min(7.0, p.Potential + 0.9);
            string range = low.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                         + "-" + high.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            BodyHeadIcon(s, p.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(p));
            CareerCell(s, p.Name, name, y, pos - name - 4, normal);
            CareerCell(s, p.Position, pos, y, age - pos - 18, normal);
            CareerTableText(s, p.Age.ToString(), age, y, normal, rightAlign: true);
            CareerTableText(s, p.EffectiveSkillSum().ToString(), skill, y, normal, rightAlign: true);
            CareerTableText(s, range, est, y, high >= 5.5 ? gold : dim, rightAlign: true);
            y += 8;
        }
    }

    // ------------------------------------------------------------------
    // CLUB LEGENDS
    // ------------------------------------------------------------------

    private MenuScreen BuildLegendsScreen()
    {
        _legendPage = 0;
        var s = new MenuScreen { Title = Loc.Tr("legend.title", "CLUB RECORDS"), BodyReserve = 48 };
        s.Entries.Add(new MenuEntry
        {
            Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("common.page", "PAGE"),
            Value = () => (_legendPage + 1) + "/" + System.Math.Max(1, LegendPageCount()),
            OnStep = delta =>
            {
                int pages = System.Math.Max(1, LegendPageCount());
                _legendPage = ((_legendPage + delta) % pages + pages) % pages;
                RebuildCurrent();
            },
        });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawLegendsBody(s));
        return s;
    }

    private int LegendRowsPerPage()
        => System.Math.Max(1, (TableVh - TablePanelY - 21 - 26) / 8);

    private int LegendPageCount()
    {
        int n = CareerRecords.Legends(LoadedComp()?.Career).Count;
        int rows = LegendRowsPerPage();
        return System.Math.Max(1, (n + rows - 1) / rows);
    }

    private void DrawLegendsBody(MenuScreen s)
    {
        var c = LoadedComp();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        var dim = new Color(0.62f, 0.68f, 0.82f);

        var rows = CareerRecords.Legends(c?.Career);
        // Anyone still at the club, so the list can say who is still writing his
        // record and who has already gone.
        var here = new System.Collections.Generic.HashSet<int>();
        var club = CurrentCareerClub();
        if (club?.Squad is not null)
            foreach (var p in club.Squad) if (p is not null) here.Add(p.Id);

        CareerTableText(s, FitText((c?.Career?.ClubName ?? "") + " "
            + Loc.Tr("legend.header", "ALL-TIME APPEARANCES"), false, panelW - 20), panelX + 8, panelY + 4, head);
        if (rows.Count == 0)
        {
            CareerTableText(s, Loc.Tr("legend.empty", "NO SEASON COMPLETED AT THIS CLUB YET"),
                panelX + 8, panelY + 18, normal);
            return;
        }

        int name = panelX + 30, pos = panelX + 250, apps = panelX + 340, goals = panelX + 400, last = panelX + panelW - 8;
        int y = panelY + 16;
        CareerTableText(s, Loc.Tr("col.name", "NAME"), name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"), pos, y, head);
        CareerTableText(s, Loc.Tr("legend.col_apps", "APP"), apps, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("legend.col_goals", "GLS"), goals, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("legend.col_last", "LAST"), last, y, head, rightAlign: true);
        y += 10;
        int perPage = LegendRowsPerPage();
        int from = _legendPage * perPage;
        for (int i = from; i < rows.Count && i < from + perPage; i++)
        {
            if (y + 9 > panelY + panelH - 4) break;
            var r = rows[i];
            bool still = here.Contains(r.PlayerId);
            CareerTableText(s, (i + 1) + ".", panelX + 8, y, dim);
            CareerCell(s, r.Name, name, y, pos - name - 4, still ? gold : normal);
            CareerCell(s, r.Position, pos, y, apps - pos - 24, dim);
            CareerTableText(s, r.Appearances.ToString(), apps, y, normal, rightAlign: true);
            CareerTableText(s, r.Goals.ToString(), goals, y, normal, rightAlign: true);
            CareerTableText(s, still ? Loc.Tr("legend.still", "HERE") : ("S" + r.LastSeason),
                last, y, still ? gold : dim, rightAlign: true);
            y += 8;
        }
    }
}
