using Godot;
using OpenSwos.Assets;
using OpenSwos.Competition;
using OpenSwos.Competition.Career;

namespace OpenSwos.Menu;

// Career-only views live alongside the main menu client so they can reuse its
// screen stack, body-painter and SWOS charset helpers without coupling career
// simulation code to the UI.
public sealed partial class MenuClient
{
    private int _squadPage;
    private int _squadSelectedIndex;
    private int _squadActionPlayerId = -1;
    private int _marketPage;
    private int _marketSelectedIndex;
    private int _marketActionPlayerId = -1;
    private int _marketSort = TransferModel.SortValue;
    private int _marketPriceFilter;   // 0 = ANY
    private string? _transferNotice;
    private int _staffSelectedIndex;
    private int _staffActionCoachId = -1;
    private int _staffCandidateIndex;
    private int _focusPage;
    private int _focusSelectedIndex;
    private string? _staffNotice;
    private int _scoutingMarketPage;
    private int _scoutingMarketSelectedIndex;
    private int _scoutingMarketSort = TransferModel.SortValue;
    private int _scoutingPage;
    private string? _scoutingNotice;
    // Incoming-offers screen state.
    private int _offerSelectedIndex;
    private string? _offerNotice;
    // Pre-match lineup editor state (SWOS inline-table swap: the 16 slot rows
    // are the default focus; FIRE marks a source row, FIRE again swaps).
    private int _lineupSelectedSlot;      // live table row (bound to MenuTableSelect)
    private int _lineupSwapAnchor = -1;   // -1 = no source row marked yet
    private string? _lineupNotice;
    // Buy negotiation state (bid flow that replaced the instant purchase).
    private long _bidAmount;
    private long _bidCounterAsking;   // 0 = no active counter; else the AI's counter price
    private int _negotiationTargetId = -1;   // player already charged 1 TimeToNegotiate

    // Transfer/scout market caches. TransferModel.Market() scans the whole
    // ~27.6k-player world and sorts it — far too heavy to re-run several times
    // per keypress (page/selection/label queries all called it). Cache the
    // sorted list per screen and rebuild only when the sort mode changes or a
    // mutation (buy/sell/scout) or a fresh screen entry invalidates it. Page
    // and selection stepping must NOT invalidate.
    private System.Collections.Generic.List<CareerPlayer>? _marketCache;
    private int _marketCacheSort = -1;
    private int _marketCacheFilter = -1;
    private System.Collections.Generic.List<CareerPlayer>? _scoutMarketCache;
    private int _scoutMarketCacheSort = -1;

    private void InvalidateMarketCaches()
    {
        _marketCache = null;
        _scoutMarketCache = null;
    }

    // Bitmap charset supports integer scales only.

    // ---- table design space --------------------------------------------------
    // Career tables now share the menu's own ×2 CanvasLayer and design space
    // (576×408) — they used to sit on a separate ×2 layer while the menu ran at
    // ×3, which is why these once applied a ×(3/2) conversion. With one shared
    // space that conversion is identity, so the tables use _vw/_vh/BodyTop as-is.
    private int TableVw => _vw;                     // 576
    private int TableVh => _vh;                     // 408
    private int TablePanelY => Current.BodyTop;

    private MenuScreen BuildSquadScreen()
    {
        var c = LoadedComp();
        CareerClub? club = null;
        if (c?.Career?.World?.Clubs is not null)
            c.Career.World.Clubs.TryGetValue(c.Career.ClubGlobalId, out club);

        string clubName = (c?.Career?.ClubName ?? "").Trim();
        if (clubName.Length == 0) clubName = Loc.Tr("squad.default_club", "CLUB");
        var s = new MenuScreen
        {
            Title = FitText(clubName + " " + Loc.Tr("squad.title_suffix", "SQUAD"), true, 294),
            BodyReserve = 100,
        };

        // The page controls use the normal selectable-entry flow, so UP/DOWN
        // moves between them and FIRE changes a roster page.
        if (club is not null && club.Squad is { Count: > 0 })
        {
            _squadPage = 0;
            _squadSelectedIndex = 0;
            _transferNotice = null;
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = SquadPageLabel });
            var playerField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.player", "PLAYER"), Value = SquadSelectedLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(playerField);
            s.TableSelect = new MenuTableSelect
            {
                Field = playerField,
                Count = () => SquadPlayers().Count,
                GetIndex = () => _squadSelectedIndex,
                SetIndex = idx => { _squadSelectedIndex = idx; _squadPage = idx / CareerSquadRowsPerPage(); },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("squad.player_action", "PLAYER ACTION"), OnActivate = OpenSquadAction });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.previous_page", "PREVIOUS PAGE"), OnActivate = () => StepSquadPage(-1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.next_page", "NEXT PAGE"), OnActivate = () => StepSquadPage(+1) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawCareerSquadBody(s));
        return s;
    }

    private void StepSquadPage(int delta)
    {
        int pages = CareerSquadPageCount();
        _squadPage = System.Math.Clamp(_squadPage + delta, 0, pages - 1);
        int rows = CareerSquadRowsPerPage();
        int count = SquadPlayers().Count;
        _squadSelectedIndex = System.Math.Min(_squadPage * rows, System.Math.Max(0, count - 1));
        RebuildCurrent();
    }

    private string SquadPageLabel()
    {
        int pages = CareerSquadPageCount();
        _squadPage = System.Math.Clamp(_squadPage, 0, pages - 1);
        return $"{Loc.Tr("common.page", "PAGE")} {_squadPage + 1}/{pages}";
    }

    private int CareerSquadPageCount()
    {
        CareerClub? club = CurrentCareerClub();
        int count = club?.Squad?.Count ?? 0;
        int rows = CareerSquadRowsPerPage();
        return System.Math.Max(1, (count + rows - 1) / rows);
    }

    private int CareerSquadRowsPerPage()
    {
        int panelY = TablePanelY;
        int panelH = TableVh - panelY - 21;
        return System.Math.Max(1, (panelH - 29) / 8);
    }

    private CareerClub? CurrentCareerClub()
    {
        var c = LoadedComp();
        if (c?.Career?.World?.Clubs is null) return null;
        return c.Career.World.Clubs.TryGetValue(c.Career.ClubGlobalId, out CareerClub? club) ? club : null;
    }

    private System.Collections.Generic.List<CareerPlayer> SquadPlayers()
    {
        var players = new System.Collections.Generic.List<CareerPlayer>();
        CareerClub? club = CurrentCareerClub();
        if (club?.Squad is null) return players;
        foreach (CareerPlayer? player in club.Squad)
            if (player is not null) players.Add(player);
        players.Sort((a, b) => a.ShirtNumber != b.ShirtNumber
            ? a.ShirtNumber.CompareTo(b.ShirtNumber)
            : a.Id.CompareTo(b.Id));
        return players;
    }

    private CareerPlayer? CurrentSquadPlayer()
    {
        var players = SquadPlayers();
        if (players.Count == 0) return null;
        _squadSelectedIndex = System.Math.Clamp(_squadSelectedIndex, 0, players.Count - 1);
        _squadPage = _squadSelectedIndex / CareerSquadRowsPerPage();
        return players[_squadSelectedIndex];
    }

    private string SquadSelectedLabel()
    {
        CareerPlayer? player = CurrentSquadPlayer();
        return player is null ? Loc.Tr("common.none", "NONE") : FitText((player.Name ?? "").Trim(), false, 132);
    }

    private void OpenSquadAction()
    {
        CareerPlayer? player = CurrentSquadPlayer();
        if (player is null) { _transferNotice = Loc.Tr("squad.no_player_selected", "NO PLAYER SELECTED"); RebuildCurrent(); return; }
        _squadActionPlayerId = player.Id;
        _transferNotice = null;
        Push(BuildSquadAction());
    }

    private MenuScreen BuildSquadAction()
    {
        CareerClub? club = CurrentCareerClub();
        CareerPlayer? player = club?.Squad?.Find(p => p is not null && p.Id == _squadActionPlayerId);
        var s = new MenuScreen { Title = Loc.Tr("squad.player_action", "PLAYER ACTION") };
        if (player is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE") });
        }
        else
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => FitText(player.Name ?? "", false, 294) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => Loc.Tr("paction.value_prefix", "VALUE") + " " + FormatMoney(Finance.PlayerValue(player)) + "   " + NegotiateStatus() });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _transferNotice ?? "" });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => TransferOffers.IsListed(LoadedComp()!, player.Id) ? Loc.Tr("paction.take_off_list", "TAKE OFF LIST") : Loc.Tr("paction.put_on_list", "PUT ON TRANSFER LIST"),
                OnActivate = ToggleTransferList });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("paction.give_free_transfer", "GIVE FREE TRANSFER"), OnActivate = () => Push(BuildFreeTransferConfirm()) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    // Manager's negotiation budget, shown across the transfer screens.
    private string NegotiateStatus()
    {
        var c = LoadedComp();
        if (c?.Career is null) return "";
        return Loc.Tr("paction.time_to_negotiate", "TIME TO NEGOTIATE") + " " + System.Math.Max(0, c.Career.TimeToNegotiate);
    }

    private void ToggleTransferList()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        CareerPlayer? player = club?.Squad?.Find(p => p is not null && p.Id == _squadActionPlayerId);
        if (c?.Career is null || player is null)
        {
            _transferNotice = Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        if (TransferOffers.IsListed(c, player.Id))
        {
            TransferOffers.UnlistPlayer(c, player.Id);
            _transferNotice = Loc.Tr("paction.off_list_prefix", "OFF LIST") + " " + AsciiText(player.Name);
        }
        else if (TransferOffers.ListPlayer(c, player.Id))
        {
            _transferNotice = Loc.Tr("paction.listed_prefix", "LISTED") + " " + AsciiText(player.Name);
        }
        else
        {
            _transferNotice = Loc.Tr("paction.list_full_prefix", "LIST FULL (MAX") + " " + TransferOffers.MaxTransferListed + ")";
            RebuildCurrent();
            return;
        }
        CompetitionStore.Save(c);
        RebuildCurrent();
    }

    private MenuScreen BuildFreeTransferConfirm()
    {
        CareerClub? club = CurrentCareerClub();
        CareerPlayer? player = club?.Squad?.Find(p => p is not null && p.Id == _squadActionPlayerId);
        var s = new MenuScreen { Title = Loc.Tr("free.title", "FREE TRANSFER") };
        if (player is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE") });
        }
        else
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => FitText(string.Format(Loc.Tr("career.release_confirm", "RELEASE {0} FOR FREE?"), player.Name ?? ""), false, 294) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _transferNotice ?? "" });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("free.release", "RELEASE"), OnActivate = FreeTransferSelectedPlayer });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void FreeTransferSelectedPlayer()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        CareerPlayer? player = club?.Squad?.Find(p => p is not null && p.Id == _squadActionPlayerId);
        if (c?.Career?.World is null || club is null || player is null)
        {
            _transferNotice = Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        if (TransferOffers.FreeTransfer(c, c.Career.World, player.Id))
        {
            CompetitionStore.Save(c);
            InvalidateMarketCaches();
            _transferNotice = Loc.Tr("free.released_prefix", "RELEASED") + " " + AsciiText(player.Name);
            Pop();   // back to squad action
            Pop();   // back to squad
            return;
        }
        _transferNotice = club.Squad.Count <= 12 ? Loc.Tr("free.squad_too_small", "SQUAD TOO SMALL") : Loc.Tr("free.release_failed", "RELEASE FAILED");
        RebuildCurrent();
    }

    private void DrawCareerSquadBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 40) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);

        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (club is null || club.Squad is null)
        {
            BodyText(s, Loc.Tr("common.no_squad_data", "NO SQUAD DATA"), false, panelX + 8, panelY + 8, head);
            return;
        }

        // 560 px inner panel (×2 layer) — columns spread out generously so full
        // names fit without touching POS.
        int no = panelX + 20;
        int flag = panelX + 24;
        int name = flag + FlagAdvance + HeadIconAdvance;
        int pos = panelX + 190;
        // POS advances ~22 px, so the old skl=212 anchor sat flush against it
        // ("POSSKL"). Nudge the SKL/AGE/SKILL cluster right just enough to open a
        // clean gap on every side without disturbing the columns past SKILL.
        int skl = panelX + 218;
        int age = panelX + 268;
        int eff = panelX + 306;
        int pot = panelX + 348;
        int sta = panelX + 376;
        // Career depth plan feature #8: APPearances and GoaLS for THIS club.
        // Squeezed in by re-spacing the POT..FIT cluster rather than by dropping
        // a column — every one of them answers a question at selection time.
        int apps = panelX + 410;
        int gls = panelX + 440;
        int formCol = panelX + 464;
        int fit = panelX + 492;
        int value = panelX + panelW - 6;

        CareerTableText(s, Loc.Tr("common.budget", "BUDGET") + " " +FormatMoney(club.Budget), panelX + 8, panelY + 4, head);
        if (!string.IsNullOrEmpty(_transferNotice))
            CareerTableText(s, FitText(_transferNotice, false, panelW - 124), panelX + 116, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.number", "NO"),no, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"),pos, y, head);
        CareerTableText(s, Loc.Tr("col.skl", "SKL"),skl, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"),age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"),eff, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.pot", "POT"),pot, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.sta", "STA"),sta, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("legend.col_apps", "APP"),apps, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("legend.col_goals", "GLS"),gls, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.form", "F"),formCol, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.fit", "FIT"),fit, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.val", "VAL"),value, y, head, rightAlign: true);
        y += 10;

        var players = SquadPlayers();

        int rows = System.Math.Max(1, (panelH - 29) / 8);
        int pages = System.Math.Max(1, (players.Count + rows - 1) / rows);
        _squadPage = System.Math.Clamp(_squadPage, 0, pages - 1);
        int first = _squadPage * rows;
        for (int i = first; i < players.Count && i < first + rows; i++)
        {
            CareerPlayer player = players[i];
            if (i == _squadSelectedIndex)
                BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            string potential = player.Scouted
                ? player.EstLow.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + player.EstHigh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : "?";
            string stamina = System.Math.Clamp(player.Stamina, 0, 7)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            int form = System.Math.Clamp(player.Form, -3, 3);
            int freshness = System.Math.Clamp(100 - player.FatigueCarry, 0, 99);
            string formText = form > 0 ? "+" + form : form < 0 ? form.ToString() : "-";
            int inj = player.InjurySeverity;
            Color nameColor = inj >= 2 ? InjuryRed : normal;
            Color fitColor = inj >= 2 ? InjuryRed : inj == 1 ? InjuryYellow : normal;
            string fitText = inj >= 2 ? Loc.Tr("squad.fit_injured", "INJ") : freshness.ToString();

            CareerTableText(s, player.ShirtNumber.ToString(), no, y, normal, rightAlign: true);
            BodyPlayerFlag(s, player.Nationality, flag, y);
            BodyHeadIcon(s, player.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(player));
            CareerCell(s, player.Name, name, y, pos - name - 4, nameColor);
            CareerCell(s, player.Position, pos, y, skl - pos - 4, normal);
            CareerTableText(s, TopSkillLetters(player), skl, y, normal);
            CareerTableText(s, player.Age.ToString(), age, y, normal, rightAlign: true);
            CareerTableText(s, player.EffectiveSkillSum().ToString(), eff, y, normal, rightAlign: true);
            CareerTableText(s, potential, pot, y, normal, rightAlign: true);
            CareerTableText(s, stamina, sta, y, normal, rightAlign: true);
            OpenSwos.Competition.Career.CareerRecords.EnsureClubStats(player);
            CareerTableText(s, player.ClubAppearances.ToString(), apps, y, normal, rightAlign: true);
            CareerTableText(s, player.ClubGoals.ToString(), gls, y,
                player.ClubGoals > 0 ? new Color(1f, 0.85f, 0.25f) : normal, rightAlign: true);
            CareerTableText(s, formText, formCol, y, normal, rightAlign: true);
            CareerTableText(s, fitText, fit, y, fitColor, rightAlign: true);
            CareerTableText(s, FormatMoney(Finance.PlayerValue(player)), value, y, normal, rightAlign: true);
            y += 8;
        }
    }

    // ======================================================================
    //  PRE-MATCH LINEUP EDITOR (SWOS-style inline-table swap)
    // ======================================================================
    // Edits CurrentCareerClub().PreferredLineup the way the original SWOS team-
    // sheet does: the 16 slot rows ARE the screen (auto-EnterTableSelect on
    // push). FIRE marks a source row (persistent gold mark), FIRE on a second
    // row swaps the two players and saves, FIRE on the marked row unmarks it.
    // Slot 0 (goal) stays keeper-only. AUTO clears the custom order (back to the
    // club's original lineup); BACK / ESC leave the screen. Each change re-orders
    // via CareerMatchTeam.BuildOrder and saves, so the stadium view + the match
    // use it immediately.
    private MenuScreen BuildLineupEditor()
    {
        CareerClub? club = CurrentCareerClub();
        _lineupSelectedSlot = 0;
        _lineupSwapAnchor = -1;
        _lineupNotice = null;
        var s = new MenuScreen { Title = Loc.Tr("lineup.title", "TEAM LINEUP"), BodyReserve = 92 };
        if (club?.Squad is not { Count: > 0 })
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_squad_data", "NO SQUAD DATA") });
        }
        else
        {
            // AUTO / BACK live above the table; the table is the default focus,
            // so they are reached by scrolling UP out of the slot rows.
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("lineup.auto", "AUTO"), OnActivate = ResetLineupToAuto });
            s.TableSelect = new MenuTableSelect
            {
                Field = null,
                Count = LineupSlotCount,
                GetIndex = () => _lineupSelectedSlot,
                SetIndex = idx => { _lineupSelectedSlot = idx; },
                OnConfirm = LineupFireRow,
                StayOnConfirm = true,        // pick source, then pick target, without leaving the table
                OnCancel = () => Pop(),      // ESC leaves the whole screen (UP off row 0 -> entries)
                Hint = Loc.Tr("lineup.hint", "UP/DOWN ROW   FIRE PICK/SWAP   ESC BACK"),
            };
            s.AutoTableSelect = true;        // enter directly in table mode over the slots
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawLineupEditorBody(s));
        return s;
    }

    // The 16 in-game slots resolved from the live squad (0 keeper, 1..10 XI,
    // 11..15 bench). Order reflects the current PreferredLineup when valid,
    // otherwise the club's ORIGINAL TeamRecord lineup (projected from the base
    // roster) — see CareerMatchTeam.BuildOrder.
    private System.Collections.Generic.List<CareerPlayer> LineupSlots()
        => CareerMatchTeam.BuildOrder(CurrentCareerClub(), CurrentCareerBaseTeam());

    // Read-only master TeamRecord for the player's own club, used as the source
    // of the default (original) lineup order. Resolved via the competition's
    // PlayerTeam -> MasterIndex mapping into Main's _allTeams.
    private TeamRecord? CurrentCareerBaseTeam()
    {
        var c = LoadedComp();
        if (c?.Career is null) return null;
        int pt = c.PlayerTeam;
        if (pt < 0 || pt >= c.Teams.Count) return null;
        try { return _host.Team(c.Teams[pt].MasterIndex); } catch { return null; }
    }

    private int LineupSlotCount() => System.Math.Min(16, LineupSlots().Count);

    // SWOS labels the keeper slot GK, the XI 2..11, the bench SUB.
    private static string LineupSlotTag(int slot)
        => slot == 0 ? Loc.Tr("lineup.slot_gk", "GK") : slot < 11 ? (slot + 1).ToString() : Loc.Tr("lineup.slot_sub", "SUB");

    // FIRE on a slot row (StayOnConfirm table flow). First FIRE marks the source
    // row; FIRE on a different row swaps the two players and saves; FIRE on the
    // marked row unmarks it. Slot 0 (goal) is refused a non-keeper.
    private void LineupFireRow()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        int count = LineupSlotCount();
        if (c is null || club is null || count == 0) { _lineupNotice = Loc.Tr("common.no_squad_data", "NO SQUAD DATA"); return; }
        int cur = System.Math.Clamp(_lineupSelectedSlot, 0, count - 1);

        if (_lineupSwapAnchor < 0)
        {
            _lineupSwapAnchor = cur;
            _lineupNotice = Loc.Tr("lineup.picked_prefix", "PICKED") + " " + LineupSlotTag(cur) + " " + Loc.Tr("lineup.picked_swap_hint", "- FIRE A ROW TO SWAP");
            return;
        }
        if (_lineupSwapAnchor == cur)
        {
            _lineupSwapAnchor = -1;
            _lineupNotice = Loc.Tr("lineup.unmarked_prefix", "UNMARKED") + " " + LineupSlotTag(cur);
            return;
        }

        int a = _lineupSwapAnchor, b = cur;
        var slots = LineupSlots();
        if (a >= slots.Count || b >= slots.Count) { _lineupSwapAnchor = -1; _lineupNotice = Loc.Tr("lineup.slot_not_available", "SLOT NOT AVAILABLE"); return; }
        // Guard: the goal slot (0) can only hold a keeper. Keep the mark so the
        // user can pick a different, valid target.
        if ((a == 0 && !CareerMatchTeam.IsKeeper(slots[b]))
            || (b == 0 && !CareerMatchTeam.IsKeeper(slots[a])))
        {
            _lineupNotice = Loc.Tr("lineup.goal_needs_keeper", "GOAL SLOT NEEDS A KEEPER");
            return;
        }

        // Materialize the current 16-slot order as ids, swap, and persist.
        var ids = new System.Collections.Generic.List<int>(slots.Count);
        foreach (CareerPlayer p in slots) ids.Add(p.Id);
        (ids[a], ids[b]) = (ids[b], ids[a]);
        club.PreferredLineup = ids;
        CompetitionStore.Save(c);
        _lineupNotice = Loc.Tr("lineup.swapped_prefix", "SWAPPED") + " " + LineupSlotTag(a) + " " + Loc.Tr("lineup.swapped_and", "AND") + " " + LineupSlotTag(b);
        _lineupSwapAnchor = -1;
    }

    private void ResetLineupToAuto()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        if (c is null || club is null) { _lineupNotice = Loc.Tr("common.no_squad_data", "NO SQUAD DATA"); RebuildCurrent(); return; }
        _lineupSwapAnchor = -1;
        club.PreferredLineup?.Clear();
        club.PreferredLineup ??= new System.Collections.Generic.List<int>();
        CompetitionStore.Save(c);
        _lineupNotice = Loc.Tr("lineup.auto_restored", "AUTO LINEUP RESTORED");
        RebuildCurrent();
    }

    private void DrawLineupEditorBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var dim = new Color(0.62f, 0.66f, 0.78f);
        if (club is null) { CareerTableText(s, Loc.Tr("common.no_squad_data", "NO SQUAD DATA"), panelX + 8, panelY + 8, head); return; }

        bool auto = club.PreferredLineup is not { Count: > 0 };
        // SLOT | NO | FLAG | NAME | POS | SKL | EFF | FIT | VAL across the 560 px panel.
        int slotCol = panelX + 24, no = panelX + 56, flag = panelX + 66,
            name = flag + FlagAdvance + HeadIconAdvance, pos = panelX + 360,
            skl = panelX + 410, eff = panelX + 470, fit = panelX + panelW - 60,
            val = panelX + panelW - 6;
        CareerTableText(s, auto ? Loc.Tr("lineup.auto_label", "AUTO LINEUP") : Loc.Tr("lineup.custom_label", "CUSTOM LINEUP"), panelX + 8, panelY + 2, new Color(1f, 0.84f, 0.2f));
        if (!string.IsNullOrEmpty(_lineupNotice))
            CareerTableText(s, FitText(_lineupNotice, false, panelW - 132), panelX + 124, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("lineup.slot", "SLOT"), slotCol, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"),pos, y, head);
        CareerTableText(s, Loc.Tr("col.skl", "SKL"),skl, y, head);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"),eff, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.fit", "FIT"),fit, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.val", "VAL"),val, y, head, rightAlign: true);
        y += 10;

        var slots = LineupSlots();
        int count = System.Math.Min(16, slots.Count);
        for (int i = 0; i < count && y + 8 <= panelY + panelH - 2; i++)
        {
            if (i == 11) y += 3;   // separate the bench block
            CareerPlayer p = slots[i];
            // The marked swap source stays lit in the Accent style (a distinct
            // colour from the Info navigation row) so both read clearly while the
            // cursor moves to pick a target. z=21 keeps the box UNDER the row
            // text (z=22) — the last BodyBox arg is the Z-index, not an alpha.
            if (i == _lineupSwapAnchor)
                BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Accent, 21);
            if (i == _lineupSelectedSlot)
                BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            Color rc = i < 11 ? normal : dim;
            int freshness = System.Math.Clamp(100 - p.FatigueCarry, 0, 99);
            int inj = p.InjurySeverity;
            Color nameColor = inj >= 2 ? InjuryRed : rc;
            Color fitColor = inj >= 2 ? InjuryRed : inj == 1 ? InjuryYellow : rc;
            string fitText = inj >= 2 ? Loc.Tr("squad.fit_injured", "INJ") : freshness.ToString();
            CareerTableText(s, LineupSlotTag(i), slotCol, y, rc, rightAlign: true);
            CareerTableText(s, p.ShirtNumber.ToString(), no, y, rc, rightAlign: true);
            BodyPlayerFlag(s, p.Nationality, flag, y);
            BodyHeadIcon(s, p.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(p));
            CareerCell(s, p.Name, name, y, pos - name - 4, nameColor);
            CareerCell(s, p.Position, pos, y, skl - pos - 4, rc);
            CareerTableText(s, TopSkillLetters(p), skl, y, rc);
            CareerTableText(s, p.EffectiveSkillSum().ToString(), eff, y, rc, rightAlign: true);
            CareerTableText(s, fitText, fit, y, fitColor, rightAlign: true);
            CareerTableText(s, FormatMoney(Finance.PlayerValue(p)), val, y, rc, rightAlign: true);
            y += 8;
        }
    }

    // ================================================================
    // FINANCES — career depth plan feature #1
    // ================================================================
    // Prints the season income and expenditure statement in the ORIGINAL
    // game's line items. The rows AND their labels come from the engine
    // (Competition/Career/SeasonFinances.StatementRows), which is the same
    // source the browser client reads through /api/finances — so both
    // front-ends always show the identical statement.

    private int _financesPage;   // 0 = this season's sheet, 1 = season by season

    private OpenSwos.Competition.Career.SeasonAccount? CurrentAccount()
        => _comp?.Career?.LastAccount;

    private MenuScreen BuildFinancesScreen()
    {
        _financesPage = 0;
        var s = new MenuScreen { Title = Loc.Tr("fin.title", "FINANCES"), BodyReserve = 82 };
        var career = _comp?.Career;
        if (career is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => _financesPage == 0
                    ? Loc.Tr("fin.show_history", "SEASON BY SEASON")
                    : Loc.Tr("fin.show_sheet", "THIS SEASON"),
                OnActivate = () => { _financesPage = _financesPage == 0 ? 1 : 0; RebuildCurrent(); } });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawFinancesBody(s));
        return s;
    }

    private void DrawFinancesBody(MenuScreen s)
    {
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        var income = new Color(0.47f, 0.85f, 0.19f);
        var spend = new Color(0.91f, 0.47f, 0.47f);
        var total = new Color(1f, 0.82f, 0.24f);

        var career = _comp?.Career;
        if (career is null)
        {
            CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        CareerClub? club = CurrentCareerClub();
        CareerTableText(s, Loc.Tr("common.budget", "BUDGET") + " " + FormatMoney(club?.Budget ?? 0),
            panelX + 8, panelY + 4, head);

        var account = CurrentAccount();
        if (account is null)
        {
            CareerTableText(s, Loc.Tr("fin.no_season", "NO COMPLETED SEASON YET"), panelX + 8, panelY + 18, normal);
            CareerTableText(s, Loc.Tr("fin.drawn_up", "THE ACCOUNTS ARE DRAWN UP AT THE END OF THE SEASON"),
                panelX + 8, panelY + 28, normal);
            return;
        }

        int labelX = panelX + 8;
        int amountX = panelX + panelW - 6;
        int y = panelY + 15;

        if (_financesPage == 1)
        {
            // ---- season by season ----------------------------------------
            int seasonX = labelX, leagueX = panelX + 70, cupX = panelX + 150;
            int incomeX = panelX + 330, wagesX = panelX + 420, balanceX = amountX;
            CareerTableText(s, Loc.Tr("fin.col_season", "SEASON"), seasonX, y, head);
            CareerTableText(s, Loc.Tr("fin.col_league", "LEAGUE"), leagueX, y, head);
            CareerTableText(s, Loc.Tr("fin.col_cup", "CUP"), cupX, y, head);
            CareerTableText(s, Loc.Tr("fin.col_income", "INCOME"), incomeX, y, head, rightAlign: true);
            CareerTableText(s, Loc.Tr("fin.col_wages", "WAGES"), wagesX, y, head, rightAlign: true);
            CareerTableText(s, Loc.Tr("fin.col_balance", "BALANCE"), balanceX, y, head, rightAlign: true);
            y += 10;
            var history = career.AccountHistory;
            for (int i = history.Count - 1; i >= 0 && y < panelY + panelH - 8; i--)
            {
                var h = history[i];
                CareerTableText(s, h.Season.ToString(), seasonX, y, normal);
                CareerTableText(s, h.LeagueTeams > 0 ? h.LeaguePosition + "/" + h.LeagueTeams : "-",
                    leagueX, y, normal);
                CareerCell(s, string.IsNullOrEmpty(h.CupResult) ? "-" : h.CupResult,
                    cupX, y, incomeX - cupX - 60, normal);
                CareerTableText(s, FormatMoney(h.TotalIncome), incomeX, y, income, rightAlign: true);
                CareerTableText(s, FormatMoney(h.WageBill), wagesX, y, spend, rightAlign: true);
                CareerTableText(s, FormatMoney(h.ClosingBalance), balanceX, y, total, rightAlign: true);
                y += 8;
            }
            return;
        }

        // ---- this season's statement -------------------------------------
        string cup = string.IsNullOrEmpty(account.CupResult) ? "-" : account.CupResult;
        string caption = Loc.Tr("fin.season", "SEASON") + " " + account.Season + "  "
            + Loc.Tr("fin.league", "LEAGUE") + " " + account.LeaguePosition + "/" + account.LeagueTeams
            + "  " + Loc.Tr("fin.cup", "CUP") + " " + cup;
        CareerTableText(s, FitText(caption, false, panelW - 120), panelX + 120, panelY + 4, head);

        CareerTableText(s, account.HomeGames + " " + Loc.Tr("fin.home_games", "HOME GAMES")
            + "  " + Loc.Tr("fin.crowd", "CROWD") + " " + account.Attendance.ToString("N0"),
            labelX, y, head);
        y += 12;

        foreach (var (key, label, amount, kind) in
                 OpenSwos.Competition.Career.SeasonFinances.StatementRows(account))
        {
            if (y >= panelY + panelH - 8) break;
            Color rowColour = kind == "out" ? spend : kind == "total" ? total : income;
            // Totals get a rule above them, like the original ledger.
            if (kind == "total" && y > panelY + 30)
            {
                // Breathe before the rule: at 9 px row pitch a rule at y-3 clips
                // the descenders of the row above (seen on 13a_career_finances).
                y += 4;
                BodyBox(s, panelX + 4, y - 4, panelW - 8, 1, MenuTheme.Style.Header, 21);
            }
            CareerCell(s, Loc.Tr(key, label), labelX, y, amountX - labelX - 90, rowColour);
            string money = (kind == "out" ? "-" : "") + FormatMoney(amount);
            CareerTableText(s, money, amountX, y, rowColour, rightAlign: true);
            y += 9;
        }
    }

    // ================================================================
    // THE CHAIRMAN — career depth plan feature #2
    // ================================================================
    // The memo inbox. Text and severity come from the ENGINE
    // (Competition/Career/ChairmanModel.cs) so this screen and the browser's
    // CHAIRMAN tab print the same words; the menu only translates them.

    private int _chairmanIndex;

    private System.Collections.Generic.List<OpenSwos.Competition.Career.ChairmanMemo> ChairmanMemos()
    {
        var memos = _comp?.Career?.Memos;
        var list = new System.Collections.Generic.List<OpenSwos.Competition.Career.ChairmanMemo>();
        if (memos is null) return list;
        for (int i = memos.Count - 1; i >= 0; i--)      // newest first
            if (memos[i] is not null) list.Add(memos[i]);
        return list;
    }

    private string ChairmanEntryLabel()
    {
        var career = LoadedComp()?.Career;
        int unread = career?.UnreadMemos ?? 0;
        string prefix = unread > 0 ? Loc.Tr("dash.offers_unseen_mark", "!") + " " : "";
        return prefix + Loc.Tr("dash.chairman", "CHAIRMAN");
    }

    private MenuScreen BuildChairmanScreen()
    {
        // Open on the memo that MATTERS. The newest entry after a rollover is
        // the chairman's new-season pleasantry, which would otherwise bury the
        // verdict the player actually needs to read.
        _chairmanIndex = 0;
        var opening = ChairmanMemos();
        for (int i = 0; i < opening.Count; i++)
            if (opening[i].Kind != "renewed") { _chairmanIndex = i; break; }
        // Opening the inbox is what marks the memos read.
        if (_comp?.Career is not null)
        {
            _comp.Career.UnreadMemos = 0;
            CompetitionStore.Save(_comp);
        }
        var s = new MenuScreen { Title = Loc.Tr("chair.title", "CHAIRMAN"), BodyReserve = 82 };
        var memos = ChairmanMemos();
        if (memos.Count > 1)
        {
            var memoField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("chair.memo", "MEMO"), Value = ChairmanMemoLabel,
                OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(memoField);
            s.TableSelect = new MenuTableSelect
            {
                Field = memoField,
                Count = () => ChairmanMemos().Count,
                GetIndex = () => _chairmanIndex,
                SetIndex = idx => { _chairmanIndex = idx; },
            };
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawChairmanBody(s));
        return s;
    }

    private string ChairmanMemoLabel()
    {
        var memos = ChairmanMemos();
        if (memos.Count == 0) return Loc.Tr("chair.none", "NO MEMOS");
        _chairmanIndex = System.Math.Clamp(_chairmanIndex, 0, memos.Count - 1);
        var m = memos[_chairmanIndex];
        return Loc.Tr("fin.season", "SEASON") + " " + m.Season
             + "  " + (_chairmanIndex + 1) + "/" + memos.Count;
    }

    /// <summary>1 -> 1ST, 2 -> 2ND ... for the board's expectation line.</summary>
    private static string Ordinal(int n)
    {
        if (n <= 0) return "-";
        int t = n % 100, u = n % 10;
        string suffix = (t is >= 11 and <= 13) ? "TH"
                      : u == 1 ? "ST" : u == 2 ? "ND" : u == 3 ? "RD" : "TH";
        return n + suffix;
    }

    private void DrawChairmanBody(MenuScreen s)
    {
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var body = new Color(0.92f, 0.94f, 1f);
        var alarm = new Color(0.95f, 0.55f, 0.35f);
        var good = new Color(0.55f, 0.88f, 0.35f);

        var career = _comp?.Career;
        if (career is null)
        {
            CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        // Standing order from the board, if any.
        string status = OpenSwos.Competition.Career.ChairmanModel.StatusLine(career);
        if (status.Length > 0)
            CareerTableText(s, FitText(status, false, panelW - 16), panelX + 8, panelY + 4,
                career.Sacked ? alarm : head);

        // What the board wants THIS season, and what it judged the last one
        // against. Without these the grade reads as a black box, and a
        // mis-ranked expectation stays invisible until someone plays a career.
        int yTop = panelY + 18;
        if (career.SeasonExpectedPosition > 0 && career.SeasonLeagueTeams > 0)
        {
            string want = Loc.Tr("chair.expects", "THE BOARD EXPECTS") + " "
                + Ordinal(career.SeasonExpectedPosition) + " "
                + Loc.Tr("chair.of", "OF") + " " + career.SeasonLeagueTeams;
            CareerTableText(s, FitText(want, false, panelW - 16), panelX + 8, yTop, head);
            yTop += 12;
        }
        if (career.LastExpectedPosition > 0 && career.LastLeagueTeams > 0)
        {
            string judged = Loc.Tr("chair.expected", "BOARD EXPECTED") + " "
                + Ordinal(career.LastExpectedPosition) + " "
                + Loc.Tr("chair.of", "OF") + " " + career.LastLeagueTeams + "   "
                + Loc.Tr("chair.finished", "FINISHED") + " "
                + Ordinal(career.LastLeaguePosition);
            CareerTableText(s, FitText(judged, false, panelW - 16), panelX + 8, yTop,
                career.LastLeaguePosition <= career.LastExpectedPosition ? good : body);
            yTop += 12;
        }

        var memos = ChairmanMemos();
        if (memos.Count == 0)
        {
            CareerTableText(s, Loc.Tr("chair.none", "NO MEMOS"), panelX + 8, yTop + 4, body);
            return;
        }
        _chairmanIndex = System.Math.Clamp(_chairmanIndex, 0, memos.Count - 1);
        var memo = memos[_chairmanIndex];

        int y = yTop;
        CareerTableText(s, Loc.Tr("chair.memo_header", OpenSwos.Competition.Career.ChairmanModel.MemoHeader),
            panelX + 8, y, head);
        y += 14;
        Color line = memo.Severity >= 2 ? alarm : memo.Severity == 0 ? good : body;
        // Memo lines are keyed per line (chair.verdict0.1, .2 ...) so a
        // translation can re-break a sentence differently from the English.
        for (int i = 0; i < memo.Lines.Count; i++)
        {
            if (y >= panelY + panelH - 24) break;
            // The stored line already has its subject in it; a TRANSLATED line
            // still carries the original %a placeholder, so substitute again.
            // Memo.Subject says WHAT %a meant — the manager for the chairman's
            // own letters, the club for a job-market one (feature #3).
            string mgr = (career.ManagerName ?? "").Trim();
            string subject = memo.Subject.Length > 0 ? memo.Subject
                           : (mgr.Length > 0 ? mgr : "BOSS");
            string text = Loc.Tr(memo.Key + "." + (i + 1), memo.Lines[i])
                .Replace("%a", AsciiText(subject));
            CareerTableText(s, FitText(text, false, panelW - 16), panelX + 8, y, line);
            y += 11;
        }
        y += 8;
        CareerTableText(s, Loc.Tr("chair.memo_sign", OpenSwos.Competition.Career.ChairmanModel.MemoSignature),
            panelX + 8, y, head);

        // Older memos below, one line each, so the season reads as a story.
        y += 16;
        if (memos.Count > 1 && y < panelY + panelH - 10)
        {
            CareerTableText(s, Loc.Tr("chair.earlier", "EARLIER MEMOS"), panelX + 8, y, head);
            y += 10;
            for (int i = 0; i < memos.Count && y < panelY + panelH - 8; i++)
            {
                if (i == _chairmanIndex) continue;
                var m = memos[i];
                Color c2 = m.Severity >= 2 ? alarm : m.Severity == 0 ? good : body;
                string first = m.Lines.Count > 0 ? m.Lines[0] : "";
                CareerCell(s, "S" + m.Season + "  " + first, panelX + 8, y, panelW - 20, c2);
                y += 8;
            }
        }
    }

    // ================================================================
    // JOB OFFERS — career depth plan feature #3
    // ================================================================
    // Other clubs come for the MANAGER. Every rule lives in the engine
    // (Competition/Career/JobMarket.cs) so this screen and the browser's JOBS
    // tab offer the same clubs with the same money; the menu only translates
    // the letter and prints the figure in its own format.

    private int _jobIndex;
    private string? _jobNotice;

    private System.Collections.Generic.List<OpenSwos.Competition.Career.JobOffer> JobOfferList()
        => OpenSwos.Competition.Career.JobMarket.LiveOffers(LoadedComp()?.Career);

    /// <summary>True while there is anything to show — an open letter or a signed deal.</summary>
    private bool JobOffersWaiting()
    {
        var career = LoadedComp()?.Career;
        if (career is null) return false;
        return JobOfferList().Count > 0
            || OpenSwos.Competition.Career.JobMarket.HasAcceptedOffer(career);
    }

    private string JobOffersEntryLabel()
    {
        var career = LoadedComp()?.Career;
        int unseen = OpenSwos.Competition.Career.JobMarket.UnseenOffers(career);
        string prefix = unseen > 0 ? Loc.Tr("dash.offers_unseen_mark", "!") + " " : "";
        if (OpenSwos.Competition.Career.JobMarket.HasAcceptedOffer(career))
            return Loc.Tr("job.new_job", "NEW JOB");
        return prefix + Loc.Tr("job.title", "JOB OFFERS")
             + " (" + JobOfferList().Count + ")";
    }

    private OpenSwos.Competition.Career.JobOffer? CurrentJobOffer()
    {
        var list = JobOfferList();
        if (list.Count == 0) return null;
        _jobIndex = System.Math.Clamp(_jobIndex, 0, list.Count - 1);
        return list[_jobIndex];
    }

    private MenuScreen BuildJobOffersScreen()
    {
        var c = LoadedComp();
        _jobIndex = 0;
        _jobNotice = null;
        // Opening the pile is what stops the entry flashing.
        if (c?.Career is not null
            && OpenSwos.Competition.Career.JobMarket.UnseenOffers(c.Career) > 0)
        {
            OpenSwos.Competition.Career.JobMarket.MarkSeen(c.Career);
            CompetitionStore.Save(c);
        }

        var s = new MenuScreen { Title = Loc.Tr("job.title", "JOB OFFERS"), BodyReserve = 82 };
        var list = JobOfferList();
        bool signed = OpenSwos.Competition.Career.JobMarket.HasAcceptedOffer(c?.Career);
        if (list.Count == 0)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => Loc.Tr("job.none", "NO JOB OFFERS") });
        }
        else
        {
            if (list.Count > 1)
            {
                var field = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                    Label = () => Loc.Tr("job.offer", "OFFER"), Value = JobSelectedLabel,
                    OnActivate = EnterTableSelectCurrent };
                s.Entries.Add(field);
                s.TableSelect = new MenuTableSelect
                {
                    Field = field,
                    Count = () => JobOfferList().Count,
                    GetIndex = () => _jobIndex,
                    SetIndex = idx => { _jobIndex = idx; },
                };
            }
            if (!signed)
            {
                s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                    Label = () => Loc.Tr("job.accept", "ACCEPT"), OnActivate = AcceptSelectedJob });
                s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                    Label = () => Loc.Tr("job.decline", "DECLINE"), OnActivate = DeclineSelectedJob });
            }
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _jobNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawJobOffersBody(s));
        return s;
    }

    private string JobSelectedLabel()
    {
        var offer = CurrentJobOffer();
        if (offer is null) return Loc.Tr("common.none", "NONE");
        var list = JobOfferList();
        return FitText(AsciiText(offer.ClubName) + "  " + (_jobIndex + 1) + "/" + list.Count, false, 150);
    }

    private void AcceptSelectedJob()
    {
        var c = LoadedComp();
        var offer = CurrentJobOffer();
        if (c?.Career is null || offer is null)
        {
            _jobNotice = Loc.Tr("job.not_available", "OFFER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        if (OpenSwos.Competition.Career.JobMarket.Accept(c.Career, offer.Id))
        {
            CompetitionStore.Save(c);
            _jobNotice = Loc.Tr("job.signed_prefix", "AGREED WITH") + " " + AsciiText(offer.ClubName);
        }
        else _jobNotice = Loc.Tr("job.not_available", "OFFER NOT AVAILABLE");
        _jobIndex = 0;
        RebuildCurrent();
    }

    private void DeclineSelectedJob()
    {
        var c = LoadedComp();
        var offer = CurrentJobOffer();
        if (c?.Career is null || offer is null)
        {
            _jobNotice = Loc.Tr("job.not_available", "OFFER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        if (OpenSwos.Competition.Career.JobMarket.Decline(c.Career, offer.Id))
        {
            CompetitionStore.Save(c);
            _jobNotice = Loc.Tr("job.declined", "OFFER DECLINED");
        }
        else _jobNotice = Loc.Tr("job.not_available", "OFFER NOT AVAILABLE");
        _jobIndex = 0;
        RebuildCurrent();
    }

    private void DrawJobOffersBody(MenuScreen s)
    {
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var body = new Color(0.92f, 0.94f, 1f);
        var good = new Color(0.55f, 0.88f, 0.35f);

        var career = LoadedComp()?.Career;
        if (career is null)
        {
            CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        // What the game thinks of the manager — the reason these clubs called.
        string rep = Loc.Tr("job.reputation", "REPUTATION") + ": "
            + Loc.Tr("job.rep_" + career.Reputation switch
              {
                  >= 80 => "world", >= 65 => "high", >= 48 => "good",
                  >= 32 => "known", >= 18 => "unproven", _ => "unwanted",
              },
              OpenSwos.Competition.Career.JobMarket.ReputationLabel(career.Reputation))
            + "  (" + career.Reputation + ")";
        CareerTableText(s, FitText(rep, false, panelW - 16), panelX + 8, panelY + 4, head);

        var offer = CurrentJobOffer();
        if (offer is null)
        {
            CareerTableText(s, Loc.Tr("job.none", "NO JOB OFFERS"), panelX + 8, panelY + 20, body);
            return;
        }

        int y = panelY + 18;
        CareerTableText(s, FitText(
            Loc.Tr("job.letter_header", OpenSwos.Competition.Career.JobMarket.LetterHeader)
                .Replace("%a", AsciiText(offer.ClubName)), false, panelW - 16),
            panelX + 8, y, head);
        y += 13;

        // The club's own line: which country, which division, how strong.
        string div = Loc.Tr("job.division", "DIVISION") + " " + (offer.Division + 1);
        string where = AsciiText(offer.NationName.Length > 0 ? offer.NationName : "") ;
        string sub = (where.Length > 0 ? where + "   " : "") + div
                   + "   " + Loc.Tr("job.squad", "SQUAD") + " " + offer.Strength + "/7";
        CareerTableText(s, FitText(sub, false, panelW - 16), panelX + 8, y, body);
        y += 13;

        // The letter, one translatable line at a time (job.letter.1, .2 ...).
        var lines = OpenSwos.Competition.Career.JobMarket.OfferLetterLines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (y >= panelY + panelH - 22) break;
            string text = Loc.Tr("job.letter." + (i + 1), lines[i])
                .Replace("%a", AsciiText(offer.ClubName))
                .Replace("%b", FormatMoney(offer.TransferFunds));
            CareerTableText(s, FitText(text, false, panelW - 16), panelX + 8, y, body);
            y += 10;
        }

        y += 6;
        if (y < panelY + panelH - 10)
        {
            string foot = offer.Accepted
                ? Loc.Tr("job.accepted_note", "YOU HAVE ACCEPTED - THE MOVE HAPPENS NEXT SEASON")
                : Loc.Tr("job.waiting_prefix", "THEY WILL WAIT") + " " + offer.MatchesLeft + " "
                  + Loc.Tr("job.waiting_suffix", "MORE MATCHES");
            CareerTableText(s, FitText(foot, false, panelW - 16), panelX + 8, y,
                offer.Accepted ? good : head);
        }
    }

    // ================================================================
    // TOP GOAL SCORERS — career depth plan feature #5
    // ================================================================
    // The original has this in two places and we keep both:
    //   the STATS menu's scorer list (asm:295076 "TOP GOAL SCORERS",
    //   asm:295043 "LEADING COMPETITION GOAL SCORERS", with the two aggregate
    //   rows asm:295686 "OWN GOALS" and asm:295696 "EX. PLAYER GOALS"), and
    //   the MANAGEMENT RECORD's per-season "SEASON'S TOP SCORER" line
    //   (asm:283007, plural asm:283027) — drawn by DrawManagementRecordBody.
    // Every number comes from Competition/Career/ScorerModel.cs; this screen
    // only lays it out, so the browser client and the menu cannot disagree.

    private int _scorerView;    // 0 = the competition, 1 = my club, 2 = season by season
    private int _scorerPage;

    private const int ScorerViews = 3;

    private MenuScreen BuildScorersScreen()
    {
        _scorerView = 0;
        _scorerPage = 0;
        var s = new MenuScreen { Title = Loc.Tr("scorer.title", "TOP GOAL SCORERS"), BodyReserve = 82 };

        s.Entries.Add(new MenuEntry
        {
            Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("scorer.view", "LIST"),
            Value = ScorerViewLabel,
            // RebuildCurrent, not just a Refresh: the panel is drawn by
            // s.Body, which only runs on a rebuild (LayoutAndBuild). Stepping
            // the option without it left the LIST reading SEASON BY SEASON over
            // last view's rows — caught by looking at the screenshot.
            OnStep = delta =>
            {
                _scorerView = ((_scorerView + delta) % ScorerViews + ScorerViews) % ScorerViews;
                _scorerPage = 0;
                RebuildCurrent();
            },
        });
        s.Entries.Add(new MenuEntry
        {
            Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
            Label = () => Loc.Tr("scorer.page", "PAGE"),
            Value = () => (_scorerPage + 1) + "/" + System.Math.Max(1, ScorerPageCount()),
            OnStep = delta =>
            {
                int pages = System.Math.Max(1, ScorerPageCount());
                _scorerPage = ((_scorerPage + delta) % pages + pages) % pages;
                RebuildCurrent();
            },
        });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawScorersBody(s));
        return s;
    }

    private string ScorerViewLabel()
    {
        var c = LoadedComp();
        return _scorerView switch
        {
            0 => Loc.Tr("scorer.view_comp", "COMPETITION"),
            1 => c is not null && c.PlayerTeam >= 0 && c.PlayerTeam < c.Teams.Count
                    ? FitText(AsciiText(c.Teams[c.PlayerTeam].Name), false, 118)
                    : Loc.Tr("scorer.view_club", "MY CLUB"),
            _ => Loc.Tr("scorer.view_history", "SEASON BY SEASON"),
        };
    }

    /// <summary>Rows the panel can hold — the paging step for every view.</summary>
    private const int ScorerRowsPerPage = 9;

    private int ScorerRowCount()
    {
        var c = LoadedComp();
        if (c is null) return 0;
        return _scorerView switch
        {
            0 => OpenSwos.Competition.Career.ScorerModel.Leaderboard(c, 30).Count,
            1 => OpenSwos.Competition.Career.ScorerModel.FoldForClub(c, c.PlayerTeam).Count,
            _ => c.Career?.SeasonTopScorers?.Count ?? 0,
        };
    }

    private int ScorerPageCount()
        => (System.Math.Max(1, ScorerRowCount()) + ScorerRowsPerPage - 1) / ScorerRowsPerPage;

    /// <summary>The label for a row that stands for a group rather than a person.</summary>
    private static string ScorerRowName(OpenSwos.Competition.Career.ScorerRow r)
        => r.PlayerId == OpenSwos.Competition.Career.ScorerModel.OwnGoalPlayerId
               ? Loc.Tr("scorer.own_goals", "OWN GOALS")
         : r.PlayerId == OpenSwos.Competition.Career.ScorerModel.ExPlayerPlayerId
               ? Loc.Tr("scorer.ex_player", "EX. PLAYER GOALS")
         : r.Name;

    private void DrawScorersBody(MenuScreen s)
    {
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var body = new Color(0.92f, 0.94f, 1f);
        var gold = new Color(1f, 0.85f, 0.25f);
        var dim  = new Color(0.62f, 0.68f, 0.82f);

        var c = LoadedComp();
        if (c is null)
        {
            CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        int goalsX = panelX + panelW - 30;
        int y = panelY + 4;

        if (_scorerView == 2)
        {
            CareerTableText(s, Loc.Tr("scorer.season_top", "SEASON'S TOP SCORER"), panelX + 8, y, head);
            y += 13;
            var hist = c.Career?.SeasonTopScorers;
            if (hist is null || hist.Count == 0)
            {
                CareerTableText(s, Loc.Tr("scorer.no_seasons", "NO SEASON COMPLETED YET"), panelX + 8, y, body);
                return;
            }
            int from = _scorerPage * ScorerRowsPerPage;
            for (int i = from; i < hist.Count && i < from + ScorerRowsPerPage; i++)
            {
                if (y + 9 > panelY + panelH - 4) break;
                var h = hist[i];
                // Plural when the season ended level (asm:283027).
                string who = string.Join(" / ", h.Names);
                CareerTableText(s, Loc.Tr("scorer.season", "SEASON") + " " + h.Season, panelX + 8, y, dim);
                // +70, not +62: "SEASON 12" is nine characters and ran into the
                // name once the career passed season 9.
                CareerTableText(s, FitText(AsciiText(who), false, panelW - 104), panelX + 70, y, body);
                CareerTableText(s, h.Goals.ToString(), goalsX, y, gold);
                y += 10;
            }
            return;
        }

        if (_scorerView == 0)
        {
            CareerTableText(s, FitText(Loc.Tr("scorer.leading",
                "LEADING COMPETITION GOAL SCORERS"), false, panelW - 76), panelX + 8, y, head);
            CareerTableText(s, Loc.Tr("scorer.goals", "GOALS"), goalsX - 26, y, dim);
            y += 13;
            var rows = OpenSwos.Competition.Career.ScorerModel.Leaderboard(c, 30);
            if (rows.Count == 0)
            {
                CareerTableText(s, Loc.Tr("scorer.no_goals", "NO GOALS YET THIS SEASON"), panelX + 8, y, body);
                return;
            }
            int from = _scorerPage * ScorerRowsPerPage;
            for (int i = from; i < rows.Count && i < from + ScorerRowsPerPage; i++)
            {
                if (y + 9 > panelY + panelH - 4) break;
                var r = rows[i];
                bool mine = r.Team == c.PlayerTeam;
                CareerTableText(s, (i + 1) + ".", panelX + 8, y, dim);
                CareerTableText(s, FitText(AsciiText(ScorerRowName(r)), false, 104),
                    panelX + 26, y, mine ? gold : body);
                CareerTableText(s, FitText(AsciiText(TeamShort(c, r.Team, 18)), false, 124),
                    panelX + 134, y, dim);
                CareerTableText(s, r.Goals.ToString(), goalsX, y, mine ? gold : body);
                y += 10;
            }
            return;
        }

        // My club: this season's goals and, where it differs, the running total.
        CareerTableText(s, FitText(AsciiText(
            c.PlayerTeam >= 0 && c.PlayerTeam < c.Teams.Count ? c.Teams[c.PlayerTeam].Name : ""),
            false, panelW - 90), panelX + 8, y, head);
        CareerTableText(s, Loc.Tr("scorer.goals", "GOALS"), panelX + 152, y, dim);
        CareerTableText(s, Loc.Tr("scorer.career_total", "CAREER TOTAL"), panelX + 196, y, dim);
        y += 13;
        var mineRows = OpenSwos.Competition.Career.ScorerModel.FoldForClub(c, c.PlayerTeam);
        if (mineRows.Count == 0)
        {
            CareerTableText(s, Loc.Tr("scorer.no_goals", "NO GOALS YET THIS SEASON"), panelX + 8, y, body);
            return;
        }
        var totals = ScorerCareerTotals(c);
        int start = _scorerPage * ScorerRowsPerPage;
        for (int i = start; i < mineRows.Count && i < start + ScorerRowsPerPage; i++)
        {
            if (y + 9 > panelY + panelH - 4) break;
            var r = mineRows[i];
            bool aggregate = r.PlayerId < 0;
            CareerTableText(s, FitText(AsciiText(ScorerRowName(r)), false, 138),
                panelX + 8, y, aggregate ? dim : body);
            CareerTableText(s, r.Goals.ToString(), panelX + 152, y, aggregate ? dim : gold);
            // The running total always, not only when it differs: a blank
            // column under a heading reads as a bug, and a player in his first
            // season legitimately has the same number twice.
            if (!aggregate && totals.TryGetValue(r.PlayerId, out int total) && total > 0)
                CareerTableText(s, total.ToString(), panelX + 200, y, dim);
            y += 10;
        }
    }

    /// <summary>CareerPlayer.CareerGoals for the managed club, keyed by player id.</summary>
    private static System.Collections.Generic.Dictionary<int, int> ScorerCareerTotals(CompetitionState c)
    {
        var map = new System.Collections.Generic.Dictionary<int, int>();
        var world = c.Career?.World;
        if (world?.Clubs is null || c.PlayerTeam < 0 || c.PlayerTeam >= c.Teams.Count) return map;
        if (!world.Clubs.TryGetValue(c.Teams[c.PlayerTeam].GlobalId, out var club) || club?.Squad is null)
            return map;
        foreach (var p in club.Squad)
            if (p is not null && p.CareerGoals > 0) map[p.Id] = p.CareerGoals;
        return map;
    }

    // ================================================================
    // THE NATIONAL-TEAM JOB — career depth plan feature #4
    // ================================================================
    // Held ALONGSIDE the club. Every rule, every letter and the tournament
    // itself live in the engine (Competition/Career/NationalJob.cs) so this
    // screen and the browser's NATIONAL tab name the same squad and play the
    // same competition; the menu only translates and draws.

    private int _natIndex;
    private string? _natNotice;
    private System.Collections.Generic.List<OpenSwos.Competition.Career.NationalCandidate>? _natPool;

    /// <summary>
    /// The eligible pool, cached for the life of one screen build. Candidates()
    /// walks every club in the world, which is far too heavy to re-run for each
    /// drawn row and each label query.
    /// </summary>
    private System.Collections.Generic.List<OpenSwos.Competition.Career.NationalCandidate> NatPool()
    {
        if (_natPool is not null) return _natPool;
        _natPool = OpenSwos.Competition.Career.NationalJob.Candidates(
            _comp?.Career, _comp?.Career?.World);
        return _natPool;
    }

    private void InvalidateNatPool() { _natPool = null; }

    private bool NationalEntryVisible()
    {
        var career = LoadedComp()?.Career;
        return OpenSwos.Competition.Career.NationalJob.HasJob(career)
            || OpenSwos.Competition.Career.NationalJob.HasOffer(career);
    }

    private string NationalEntryLabel()
    {
        var career = LoadedComp()?.Career;
        if (OpenSwos.Competition.Career.NationalJob.HasOffer(career))
            return Loc.Tr("dash.offers_unseen_mark", "!") + " "
                 + Loc.Tr("natjob.entry_offer", "INTERNATIONAL JOB OFFER");
        int missing = OpenSwos.Competition.Career.NationalJob.StillToSelect(career);
        string label = Loc.Tr("natjob.entry", "NATIONAL TEAM");
        return missing > 0 ? Loc.Tr("dash.offers_unseen_mark", "!") + " " + label : label;
    }

    private MenuScreen BuildNationalScreen()
    {
        var c = LoadedComp();
        var career = c?.Career;
        _natIndex = 0;
        _natNotice = null;
        InvalidateNatPool();

        bool offer = OpenSwos.Competition.Career.NationalJob.HasOffer(career);
        var s = new MenuScreen
        {
            Title = offer
                ? Loc.Tr("natjob.title_offer", "INTERNATIONAL JOB OFFER")
                : FitText(((career?.NationalCountry ?? "").Trim().Length > 0
                    ? career!.NationalCountry + " "
                    : "") + Loc.Tr("natjob.title", "NATIONAL TEAM"), true, 294),
            BodyReserve = 82,
        };

        if (offer)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = () => Loc.Tr("job.accept", "ACCEPT"), OnActivate = AcceptNationalJob });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("job.decline", "DECLINE"), OnActivate = DeclineNationalJob });
        }
        else if (OpenSwos.Competition.Career.NationalJob.HasJob(career))
        {
            var field = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("natjob.player", "PLAYER"), Value = NatSelectedLabel,
                OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(field);
            s.TableSelect = new MenuTableSelect
            {
                Field = field,
                Count = () => NatPool().Count,
                GetIndex = () => _natIndex,
                SetIndex = idx => { _natIndex = idx; },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = NatToggleLabel, OnActivate = ToggleNationalPick });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("natjob.auto", "AUTO PICK"), OnActivate = AutoPickNationalSquad });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("natjob.resign", "RESIGN"), OnActivate = ResignNationalJob });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _natNotice ?? "" });
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawNationalBody(s));
        return s;
    }

    private string NatSelectedLabel()
    {
        var pool = NatPool();
        if (pool.Count == 0) return Loc.Tr("common.none", "NONE");
        _natIndex = System.Math.Clamp(_natIndex, 0, pool.Count - 1);
        var p = pool[_natIndex];
        return FitText(AsciiText(p.Name) + "  " + (_natIndex + 1) + "/" + pool.Count, false, 150);
    }

    private string NatToggleLabel()
    {
        var pool = NatPool();
        if (pool.Count == 0) return Loc.Tr("natjob.add", "ADD PLAYER");
        _natIndex = System.Math.Clamp(_natIndex, 0, pool.Count - 1);
        return pool[_natIndex].Selected
            ? Loc.Tr("natjob.remove", "REMOVE PLAYER")
            : Loc.Tr("natjob.add", "ADD PLAYER");
    }

    private void AcceptNationalJob()
    {
        var c = LoadedComp();
        if (c?.Career is null) return;
        string country = c.Career.NationalOffer?.Country ?? "";
        if (OpenSwos.Competition.Career.NationalJob.AcceptOffer(c.Career))
        {
            CompetitionStore.Save(c);
            InvalidateNatPool();
            _natNotice = Loc.Tr("natjob.appointed", "COACH OF") + " " + AsciiText(country);
        }
        ReplaceTop(BuildNationalScreen());
    }

    private void DeclineNationalJob()
    {
        var c = LoadedComp();
        if (c?.Career is null) return;
        OpenSwos.Competition.Career.NationalJob.DeclineOffer(c.Career);
        CompetitionStore.Save(c);
        Pop();
    }

    private void ToggleNationalPick()
    {
        var c = LoadedComp();
        var pool = NatPool();
        if (c?.Career is null || pool.Count == 0) return;
        _natIndex = System.Math.Clamp(_natIndex, 0, pool.Count - 1);
        var p = pool[_natIndex];
        if (!OpenSwos.Competition.Career.NationalJob.ToggleSelection(c.Career, p.PlayerId))
        {
            _natNotice = Loc.Tr("natjob.full", "SQUAD FULL");
            RebuildCurrent();
            return;
        }
        CompetitionStore.Save(c);
        InvalidateNatPool();
        _natNotice = OpenSwos.Competition.Career.NationalJob.StatusLine(c.Career);
        RebuildCurrent();
    }

    private void AutoPickNationalSquad()
    {
        var c = LoadedComp();
        if (c?.Career is null) return;
        OpenSwos.Competition.Career.NationalJob.AutoPick(c.Career, c.Career.World);
        CompetitionStore.Save(c);
        InvalidateNatPool();
        _natNotice = OpenSwos.Competition.Career.NationalJob.StatusLine(c.Career);
        RebuildCurrent();
    }

    private void ResignNationalJob()
    {
        var c = LoadedComp();
        if (c?.Career is null) return;
        OpenSwos.Competition.Career.NationalJob.EndJob(c.Career, byFederation: false);
        CompetitionStore.Save(c);
        InvalidateNatPool();
        Pop();
    }

    private void DrawNationalBody(MenuScreen s)
    {
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var body = new Color(0.92f, 0.94f, 1f);
        var good = new Color(0.55f, 0.88f, 0.35f);
        var cursor = new Color(1f, 0.86f, 0.35f);

        var career = _comp?.Career;
        if (career is null)
        {
            CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        // ---- the federation's letter ----
        if (OpenSwos.Competition.Career.NationalJob.HasOffer(career))
        {
            string country = career.NationalOffer!.Country;
            int ly = panelY + 6;
            CareerTableText(s, FitText(
                Loc.Tr("natjob.letter_header",
                    OpenSwos.Competition.Career.NationalJob.OfferHeader).Replace("%a", AsciiText(country)),
                false, panelW - 16), panelX + 8, ly, head);
            ly += 14;
            var lines = OpenSwos.Competition.Career.NationalJob.OfferLetterLines;
            for (int i = 0; i < lines.Count && ly < panelY + panelH - 12; i++)
            {
                // Same six lines the memo inbox prints, so one set of keys
                // serves both (natjob.offer.1 .. .6).
                string text = Loc.Tr("natjob.offer." + (i + 1), lines[i])
                    .Replace("%a", AsciiText(country));
                CareerTableText(s, FitText(text, false, panelW - 16), panelX + 8, ly, body);
                ly += 11;
            }
            return;
        }

        if (!OpenSwos.Competition.Career.NationalJob.HasJob(career))
        {
            CareerTableText(s, Loc.Tr("natjob.none", "NO INTERNATIONAL JOB"),
                panelX + 8, panelY + 8, head);
            return;
        }

        // ---- the job ----
        int y = panelY + 4;
        int strength = OpenSwos.Competition.Career.NationalJob.SquadStrength(career, career.World);
        CareerTableText(s, FitText(
            OpenSwos.Competition.Career.NationalJob.TournamentName(career.NationalContinent)
            + "   " + Loc.Tr("natjob.squad_word", "SQUAD") + " "
            + (career.NationalSquad?.Count ?? 0) + "/"
            + OpenSwos.Competition.Career.NationalJob.SquadSize
            + "   " + Loc.Tr("natjob.rating", "RATING") + " " + strength + "/7",
            false, panelW - 16), panelX + 8, y, head);
        y += 11;
        string status = OpenSwos.Competition.Career.NationalJob.StatusLine(career);
        if (status.Length > 0)
        {
            CareerTableText(s, FitText(status, false, panelW - 16), panelX + 8, y,
                OpenSwos.Competition.Career.NationalJob.StillToSelect(career) > 0 ? head : good);
            y += 12;
        }

        // Column header, then a window of the pool around the selection.
        int nameX = panelX + 8, posX = panelX + 200, ovrX = panelX + 250,
            whereX = panelX + 300, clubX = panelX + 360;
        CareerTableText(s, Loc.Tr("natjob.col_name", "NAME"), nameX, y, head);
        CareerTableText(s, Loc.Tr("natjob.col_pos", "POS"), posX, y, head);
        CareerTableText(s, Loc.Tr("natjob.col_ovr", "OVR"), ovrX, y, head);
        CareerTableText(s, Loc.Tr("natjob.col_where", "WHERE"), whereX, y, head);
        CareerTableText(s, Loc.Tr("natjob.col_club", "CLUB"), clubX, y, head);
        y += 10;

        var pool = NatPool();
        if (pool.Count == 0)
        {
            CareerTableText(s, Loc.Tr("natjob.no_players", "NO ELIGIBLE PLAYERS"), nameX, y, body);
            return;
        }
        _natIndex = System.Math.Clamp(_natIndex, 0, pool.Count - 1);
        int rows = System.Math.Max(1, (panelY + panelH - 8 - y) / 8);
        int first = System.Math.Clamp(_natIndex - rows / 2, 0, System.Math.Max(0, pool.Count - rows));
        var names = TeamNamesByGlobalId();

        for (int i = first; i < pool.Count && i < first + rows; i++)
        {
            var p = pool[i];
            // The SWOS charset has no '>' — the cursor row is marked by COLOUR,
            // not by a glyph that renders as a box.
            Color col = i == _natIndex ? cursor : (p.Selected ? good : body);
            CareerCell(s, AsciiText(p.Name), nameX, y, 186, col);
            CareerTableText(s, p.Position, posX, y, col);
            CareerTableText(s, p.Overall.ToString(), ovrX, y, col);
            CareerTableText(s, p.Home ? Loc.Tr("natjob.home", "HOME") : Loc.Tr("natjob.abroad", "ABROAD"),
                whereX, y, col);
            string club = names.TryGetValue(p.ClubId, out string? cn) ? AsciiText(cn) : "";
            CareerCell(s, club, clubX, y, panelW - (clubX - panelX) - 10, col);
            y += 8;
        }
    }

    private MenuScreen BuildStaffScreen()
    {
        CareerClub? club = CurrentCareerClub();
        var s = new MenuScreen { Title = Loc.Tr("staff.title", "STAFF"), BodyReserve = 82 };
        if (club is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            _staffSelectedIndex = 0;
            _staffActionCoachId = -1;
            _staffNotice = null;
            var coachField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("staff.coach", "COACH"), Value = StaffSelectedLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(coachField);
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("staff.fire_selected", "FIRE SELECTED"), OnActivate = OpenFireCoach });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("staff.hire_coach", "HIRE COACH"), OnActivate = () => Push(BuildHireCoachScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("staff.training_focus", "TRAINING FOCUS"), OnActivate = () => Push(BuildTrainingFocusScreen()) });
            s.TableSelect = new MenuTableSelect
            {
                Field = coachField,
                Count = () => StaffCoaches().Count,
                GetIndex = () => _staffSelectedIndex,
                SetIndex = idx => { _staffSelectedIndex = idx; },
            };
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawStaffBody(s));
        return s;
    }

    private System.Collections.Generic.List<Coach> StaffCoaches()
    {
        var coaches = new System.Collections.Generic.List<Coach>();
        if (CurrentCareerClub()?.Coaches is not { } source) return coaches;
        foreach (Coach? coach in source)
            if (coach is not null) coaches.Add(coach);
        coaches.Sort((left, right) => left.Id.CompareTo(right.Id));
        return coaches;
    }

    private Coach? CurrentStaffCoach()
    {
        var coaches = StaffCoaches();
        if (coaches.Count == 0) return null;
        _staffSelectedIndex = System.Math.Clamp(_staffSelectedIndex, 0, coaches.Count - 1);
        return coaches[_staffSelectedIndex];
    }

    private string StaffSelectedLabel()
    {
        Coach? coach = CurrentStaffCoach();
        return coach is null ? Loc.Tr("common.none", "NONE") : FitText(coach.Name, false, 132);
    }

    private void OpenFireCoach()
    {
        Coach? coach = CurrentStaffCoach();
        if (coach is null) { _staffNotice = Loc.Tr("staff.no_coach_selected", "NO COACH SELECTED"); RebuildCurrent(); return; }
        _staffActionCoachId = coach.Id;
        _staffNotice = null;
        Push(BuildFireCoachScreen());
    }

    private MenuScreen BuildFireCoachScreen()
    {
        CareerClub? club = CurrentCareerClub();
        Coach? coach = club?.Coaches?.Find(item => item is not null && item.Id == _staffActionCoachId);
        var s = new MenuScreen { Title = Loc.Tr("firecoach.title", "FIRE COACH") };
        if (club is null || coach is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("firecoach.coach_not_available", "COACH NOT AVAILABLE") });
        }
        else
        {
            long severance = System.Math.Max(0L, coach.Wage) / 2L;
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => FitText(coach.Name, false, 294) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("firecoach.severance_prefix", "SEVERANCE") + " " + FormatMoney(severance) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.budget", "BUDGET") + " " +FormatMoney(club.Budget) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _staffNotice ?? "" });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("firecoach.fire", "FIRE"), OnActivate = FireSelectedCoach });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void FireSelectedCoach()
    {
        var c = LoadedComp();
        if (c?.Career?.World is null)
        {
            _staffNotice = Loc.Tr("firecoach.coach_not_available", "COACH NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        Coach? coach = CurrentCareerClub()?.Coaches?.Find(item => item is not null && item.Id == _staffActionCoachId);
        string name = coach?.Name ?? Loc.Tr("staff.coach", "COACH");
        if (StaffModel.TryFire(c.Career.World, c.Career.ClubGlobalId, _staffActionCoachId, out string refusal))
        {
            CompetitionStore.Save(c);
            _staffNotice = Loc.Tr("firecoach.fired_prefix", "FIRED") + " " + AsciiText(name);
            Pop();
            return;
        }
        _staffNotice = refusal;
        RebuildCurrent();
    }

    private MenuScreen BuildHireCoachScreen()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        var s = new MenuScreen { Title = Loc.Tr("hire.title", "HIRE COACH"), BodyReserve = 94 };
        if (c?.Career?.World is null || club is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            _staffCandidateIndex = 0;
            _staffNotice = null;
            var candidateField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("hire.candidate", "CANDIDATE"), Value = StaffCandidateLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(candidateField);
            s.TableSelect = new MenuTableSelect
            {
                Field = candidateField,
                Count = () => StaffCandidates().Count,
                GetIndex = () => _staffCandidateIndex,
                SetIndex = idx => { _staffCandidateIndex = idx; },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = () => Loc.Tr("hire.hire", "HIRE"), OnActivate = HireSelectedCoach });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _staffNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawHireCoachBody(s));
        return s;
    }

    private System.Collections.Generic.List<CoachHireCandidate> StaffCandidates()
    {
        var c = LoadedComp();
        return c?.Career?.World is null
            ? new System.Collections.Generic.List<CoachHireCandidate>()
            : StaffModel.HireCandidates(c.Career.World, c.Career.ClubGlobalId);
    }

    private string StaffCandidateLabel()
    {
        var candidates = StaffCandidates();
        if (candidates.Count == 0) return Loc.Tr("common.none", "NONE");
        _staffCandidateIndex = System.Math.Clamp(_staffCandidateIndex, 0, candidates.Count - 1);
        return FitText(candidates[_staffCandidateIndex].Name, false, 132);
    }

    private void HireSelectedCoach()
    {
        var c = LoadedComp();
        var candidates = StaffCandidates();
        CoachHireCandidate? candidate = candidates.Find(item => item.Slot == _staffCandidateIndex);
        if (c?.Career?.World is null || candidate is null)
        {
            _staffNotice = Loc.Tr("firecoach.coach_not_available", "COACH NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        if (StaffModel.TryHire(c.Career.World, c.Career.ClubGlobalId, candidate.Slot, out string refusal))
        {
            CompetitionStore.Save(c);
            _staffNotice = Loc.Tr("hire.hired_prefix", "HIRED") + " " + AsciiText(candidate.Name);
            Pop();
            return;
        }
        _staffNotice = refusal;
        RebuildCurrent();
    }

    private void DrawStaffBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (club is null) { CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head); return; }

        // NAME | SPEC | Q | WAGE spread across the 560 px inner panel.
        int name = panelX + 8, specialty = panelX + 200, quality = panelX + 470, wage = panelX + panelW - 6;
        CareerTableText(s, Loc.Tr("common.budget", "BUDGET") + " " +FormatMoney(club.Budget), panelX + 8, panelY + 4, head);
        if (!string.IsNullOrEmpty(_staffNotice))
            CareerTableText(s, FitText(_staffNotice, false, panelW - 124), panelX + 116, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.spec", "SPEC"),specialty, y, head);
        CareerTableText(s, Loc.Tr("col.quality", "Q"),quality, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.wage", "WAGE"),wage, y, head, rightAlign: true);
        y += 10;
        var coaches = StaffCoaches();
        if (coaches.Count == 0) { CareerTableText(s, Loc.Tr("staff.no_coaches", "NO COACHES"), name, y, normal); return; }
        for (int i = 0; i < coaches.Count && y < panelY + panelH - 8; i++)
        {
            Coach coach = coaches[i];
            if (i == _staffSelectedIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            CareerCell(s, coach.Name, name, y, specialty - name - 4, normal);
            CareerCell(s, CompLoc.TrSpecialty(coach.Specialty), specialty, y, quality - specialty - 12, normal);
            CareerTableText(s, System.Math.Clamp(coach.Quality, 0, 7).ToString(), quality, y, normal, rightAlign: true);
            CareerTableText(s, FormatMoney(coach.Wage), wage, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private void DrawHireCoachBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (club is null) { CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head); return; }
        // NAME | SPEC | Q | FEE spread across the 560 px inner panel.
        int name = panelX + 8, specialty = panelX + 200, quality = panelX + 470, fee = panelX + panelW - 6;
        CareerTableText(s, Loc.Tr("common.budget", "BUDGET") + " " +FormatMoney(club.Budget), panelX + 8, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.spec", "SPEC"),specialty, y, head);
        CareerTableText(s, Loc.Tr("col.quality", "Q"),quality, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.fee", "FEE"),fee, y, head, rightAlign: true);
        y += 10;
        foreach (CoachHireCandidate candidate in StaffCandidates())
        {
            if (candidate.Slot == _staffCandidateIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            CareerCell(s, candidate.Name, name, y, specialty - name - 4, normal);
            CareerCell(s, CompLoc.TrSpecialty(candidate.Specialty), specialty, y, quality - specialty - 12, normal);
            CareerTableText(s, candidate.Quality.ToString(), quality, y, normal, rightAlign: true);
            CareerTableText(s, FormatMoney(candidate.SigningFee), fee, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private MenuScreen BuildTrainingFocusScreen()
    {
        CareerClub? club = CurrentCareerClub();
        var s = new MenuScreen { Title = Loc.Tr("focus.title", "TRAINING FOCUS"), BodyReserve = 100 };
        if (club?.Squad is not { Count: > 0 })
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_squad_data", "NO SQUAD DATA") });
        }
        else
        {
            _focusPage = 0;
            _focusSelectedIndex = 0;
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = FocusPageLabel });
            var focusField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.player", "PLAYER"), Value = FocusSelectedLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(focusField);
            s.TableSelect = new MenuTableSelect
            {
                Field = focusField,
                Count = () => SquadPlayers().Count,
                GetIndex = () => _focusSelectedIndex,
                SetIndex = idx => { _focusSelectedIndex = idx; _focusPage = idx / FocusRowsPerPage(); },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("focus.toggle", "TOGGLE FOCUS"), OnActivate = ToggleTrainingFocus });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.previous_page", "PREVIOUS PAGE"), OnActivate = () => StepFocusPage(-1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.next_page", "NEXT PAGE"), OnActivate = () => StepFocusPage(+1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _staffNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawTrainingFocusBody(s));
        return s;
    }

    private int FocusRowsPerPage()
    {
        int panelY = TablePanelY;
        int panelH = TableVh - panelY - 21;
        return System.Math.Max(1, (panelH - 29) / 8);
    }

    private int FocusPageCount() => System.Math.Max(1, (SquadPlayers().Count + FocusRowsPerPage() - 1) / FocusRowsPerPage());

    private string FocusPageLabel()
    {
        int pages = FocusPageCount();
        _focusPage = System.Math.Clamp(_focusPage, 0, pages - 1);
        return $"{Loc.Tr("common.page", "PAGE")} {_focusPage + 1}/{pages}";
    }

    private CareerPlayer? CurrentFocusPlayer()
    {
        var players = SquadPlayers();
        if (players.Count == 0) return null;
        _focusSelectedIndex = System.Math.Clamp(_focusSelectedIndex, 0, players.Count - 1);
        _focusPage = _focusSelectedIndex / FocusRowsPerPage();
        return players[_focusSelectedIndex];
    }

    private string FocusSelectedLabel()
    {
        CareerPlayer? player = CurrentFocusPlayer();
        return player is null ? Loc.Tr("common.none", "NONE") : FitText(player.Name, false, 132);
    }

    private void StepFocusPage(int delta)
    {
        _focusPage = System.Math.Clamp(_focusPage + delta, 0, FocusPageCount() - 1);
        _focusSelectedIndex = System.Math.Min(_focusPage * FocusRowsPerPage(), System.Math.Max(0, SquadPlayers().Count - 1));
        RebuildCurrent();
    }

    private void ToggleTrainingFocus()
    {
        var c = LoadedComp();
        CareerPlayer? player = CurrentFocusPlayer();
        if (c?.Career?.World is null || player is null)
        {
            _staffNotice = Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        bool wasFocused = CurrentCareerClub()?.TrainingFocusIds?.Contains(player.Id) == true;
        if (StaffModel.TryToggleTrainingFocus(c.Career.World, c.Career.ClubGlobalId, player.Id, out string refusal))
        {
            CompetitionStore.Save(c);
            _staffNotice = (wasFocused ? Loc.Tr("focus.removed_prefix", "REMOVED") : Loc.Tr("focus.focused_prefix", "FOCUSED")) + " " + AsciiText(player.Name);
        }
        else _staffNotice = refusal;
        RebuildCurrent();
    }

    private void DrawTrainingFocusBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (club is null) { CareerTableText(s, Loc.Tr("common.no_squad_data", "NO SQUAD DATA"), panelX + 8, panelY + 8, head); return; }
        // F | NAME | POS | AGE | EFF spread across the 560 px inner panel.
        int mark = panelX + 8, name = panelX + 24 + HeadIconAdvance, pos = panelX + 280, age = panelX + 360, eff = panelX + 410;
        CareerTableText(s, Loc.Tr("focus.max_prefix", "MAX") + " " + StaffModel.MaximumTrainingFocus + " " + Loc.Tr("focus.max_suffix", "FOCUS PLAYERS"), panelX + 8, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.form", "F"),mark, y, head);
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"),pos, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"),age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"),eff, y, head, rightAlign: true);
        y += 10;
        var players = SquadPlayers();
        int rows = FocusRowsPerPage();
        _focusPage = System.Math.Clamp(_focusPage, 0, System.Math.Max(1, (players.Count + rows - 1) / rows) - 1);
        for (int i = _focusPage * rows; i < players.Count && i < _focusPage * rows + rows; i++)
        {
            CareerPlayer player = players[i];
            if (i == _focusSelectedIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            CareerTableText(s, club.TrainingFocusIds?.Contains(player.Id) == true ? Loc.Tr("focus.mark", "*") : "", mark, y, normal);
            BodyHeadIcon(s, player.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(player));
            CareerCell(s, player.Name, name, y, pos - name - 4, normal);
            CareerCell(s, player.Position, pos, y, age - pos - 18, normal);
            CareerTableText(s, player.Age.ToString(), age, y, normal, rightAlign: true);
            CareerTableText(s, player.EffectiveSkillSum().ToString(), eff, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private MenuScreen BuildScoutingScreen()
    {
        CareerClub? club = CurrentCareerClub();
        var s = new MenuScreen { Title = Loc.Tr("scout.title", "SCOUTING"), BodyReserve = 78 };
        if (club is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            _scoutingPage = 0;
            _scoutingNotice = null;
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = ScoutingPageLabel });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("scout.scout_player", "SCOUT PLAYER"), OnActivate = () => Push(BuildScoutMarketScreen()) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = ImproveScoutingLabel, OnActivate = ImproveScouting });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.previous_page", "PREVIOUS PAGE"), OnActivate = () => StepScoutingPage(-1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.next_page", "NEXT PAGE"), OnActivate = () => StepScoutingPage(+1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _scoutingNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawScoutingBody(s));
        return s;
    }

    private int ScoutQuality() => System.Math.Clamp(CurrentCareerClub()?.Scouting?.ScoutQuality ?? 0, 0, 7);

    private string ImproveScoutingLabel()
    {
        int quality = ScoutQuality();
        return quality >= 7
            ? Loc.Tr("scout.maxed", "SCOUTING MAXED")
            : Loc.Tr("scout.improve_prefix", "IMPROVE SCOUTING") + " " + FormatMoney(Scouting.ScoutUpgradeCost(quality));
    }

    private System.Collections.Generic.List<CareerPlayer> WatchedPlayers()
    {
        var watched = new System.Collections.Generic.List<CareerPlayer>();
        var ids = CurrentCareerClub()?.Scouting?.WatchedPlayerIds;
        if (ids is null) return watched;
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (int id in ids)
            if (seen.Add(id) && FindCareerPlayer(id) is CareerPlayer player)
                watched.Add(player);
        return watched;
    }

    private CareerPlayer? FindCareerPlayer(int playerId)
    {
        var world = LoadedComp()?.Career?.World;
        if (world?.Clubs is null) return null;
        if (world.FreeAgents is not null)
            foreach (CareerPlayer? player in world.FreeAgents)
                if (player?.Id == playerId) return player;
        var clubIds = new System.Collections.Generic.List<ushort>(world.Clubs.Keys);
        clubIds.Sort();
        foreach (ushort clubId in clubIds)
        {
            CareerClub? club = world.Clubs[clubId];
            if (club?.Squad is null) continue;
            foreach (CareerPlayer? player in club.Squad)
                if (player?.Id == playerId) return player;
        }
        return null;
    }

    private int ScoutingRowsPerPage()
    {
        int panelY = TablePanelY;
        int panelH = TableVh - panelY - 21;
        return System.Math.Max(1, (panelH - 15) / 8);
    }

    private int ScoutingPageCount() => System.Math.Max(1, (WatchedPlayers().Count + ScoutingRowsPerPage() - 1) / ScoutingRowsPerPage());

    private string ScoutingPageLabel()
    {
        int pages = ScoutingPageCount();
        _scoutingPage = System.Math.Clamp(_scoutingPage, 0, pages - 1);
        return $"{Loc.Tr("common.page", "PAGE")} {_scoutingPage + 1}/{pages}";
    }

    private void StepScoutingPage(int delta)
    {
        _scoutingPage = System.Math.Clamp(_scoutingPage + delta, 0, ScoutingPageCount() - 1);
        RebuildCurrent();
    }

    private void ImproveScouting()
    {
        var c = LoadedComp();
        if (c?.Career?.World is null)
        {
            _scoutingNotice = Loc.Tr("scout.unavailable", "SCOUTING UNAVAILABLE");
            RebuildCurrent();
            return;
        }
        if (Scouting.TryImproveScoutQuality(c.Career.World, c.Career.ClubGlobalId, out string refusal))
        {
            CompetitionStore.Save(c);
            _scoutingNotice = Loc.Tr("scout.quality_prefix", "SCOUT QUALITY") + " " + ScoutQuality();
        }
        else _scoutingNotice = refusal;
        RebuildCurrent();
    }

    private void DrawScoutingBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (club is null) { CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head); return; }

        // NAME | CLUB | AGE | EFF | POT spread across the 560 px inner panel.
        int name = panelX + 8 + HeadIconAdvance, clubName = panelX + 210, age = panelX + 420, eff = panelX + 460, estimate = panelX + panelW - 6;
        CareerTableText(s, Loc.Tr("scout.quality_prefix", "SCOUT QUALITY") + " " + ScoutQuality() + "/7  " + Loc.Tr("common.budget", "BUDGET") + " " + FormatMoney(club.Budget), panelX + 8, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.club", "CLUB"),clubName, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"),age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"),eff, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.pot", "POT"),estimate, y, head, rightAlign: true);
        y += 10;
        var players = WatchedPlayers();
        int rows = ScoutingRowsPerPage();
        int pages = System.Math.Max(1, (players.Count + rows - 1) / rows);
        _scoutingPage = System.Math.Clamp(_scoutingPage, 0, pages - 1);
        if (players.Count == 0) { CareerTableText(s, Loc.Tr("scout.no_watched", "NO WATCHED PLAYERS"), name, y, normal); return; }
        for (int i = _scoutingPage * rows; i < players.Count && i < _scoutingPage * rows + rows; i++)
        {
            CareerPlayer player = players[i];
            string potential = player.Scouted
                ? player.EstLow.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + player.EstHigh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : "?";
            BodyHeadIcon(s, player.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(player));
            CareerCell(s, player.Name, name, y, clubName - name - 4, normal);
            CareerCell(s, MarketClubName(player), clubName, y, age - clubName - 18, normal);
            CareerTableText(s, player.Age.ToString(), age, y, normal, rightAlign: true);
            CareerTableText(s, player.EffectiveSkillSum().ToString(), eff, y, normal, rightAlign: true);
            CareerTableText(s, potential, estimate, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private MenuScreen BuildScoutMarketScreen()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        var s = new MenuScreen { Title = Loc.Tr("scout.scout_player", "SCOUT PLAYER"), BodyReserve = 70 };
        if (c?.Career?.World is null || club is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            _scoutingMarketPage = 0;
            _scoutingMarketSelectedIndex = 0;
            _scoutingMarketSort = TransferModel.SortValue;
            _scoutingNotice = null;
            _scoutMarketCache = null;   // fresh screen entry rebuilds the list
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = ScoutingMarketPageLabel });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.sort", "SORT"), Value = ScoutingMarketSortLabel, OnActivate = OpenScoutSortPicker });
            var scoutPlayerField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.player", "PLAYER"), Value = ScoutingMarketSelectedLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(scoutPlayerField);
            s.TableSelect = new MenuTableSelect
            {
                Field = scoutPlayerField,
                Count = () => ScoutingMarketPlayers().Count,
                GetIndex = () => _scoutingMarketSelectedIndex,
                SetIndex = idx => { _scoutingMarketSelectedIndex = idx; _scoutingMarketPage = idx / ScoutingMarketRowsPerPage(); },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = ScoutingSelectedLabel, OnActivate = ScoutSelectedPlayer });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.previous_page", "PREVIOUS PAGE"), OnActivate = () => StepScoutingMarketPage(-1) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.next_page", "NEXT PAGE"), OnActivate = () => StepScoutingMarketPage(+1) });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawScoutMarketBody(s));
        return s;
    }

    private System.Collections.Generic.List<CareerPlayer> ScoutingMarketPlayers()
    {
        var c = LoadedComp();
        if (c?.Career?.World is null) return new System.Collections.Generic.List<CareerPlayer>();
        if (_scoutMarketCache is null || _scoutMarketCacheSort != _scoutingMarketSort)
        {
            _scoutMarketCache = TransferModel.Market(c.Career.World, c.Career.ClubGlobalId, _scoutingMarketSort);
            _scoutMarketCacheSort = _scoutingMarketSort;
        }
        return _scoutMarketCache;
    }

    private int ScoutingMarketRowsPerPage()
    {
        int panelY = TablePanelY;
        int panelH = TableVh - panelY - 21;
        // Rows start at panelY+25 (budget line + column headers), not +15 — the
        // old formula overcounted by one and the last row sat on the border.
        return System.Math.Max(1, (panelH - 25) / 8);
    }

    private int ScoutingMarketPageCount() => System.Math.Max(1, (ScoutingMarketPlayers().Count + ScoutingMarketRowsPerPage() - 1) / ScoutingMarketRowsPerPage());

    private string ScoutingMarketPageLabel()
    {
        int pages = ScoutingMarketPageCount();
        _scoutingMarketPage = System.Math.Clamp(_scoutingMarketPage, 0, pages - 1);
        return $"{Loc.Tr("common.page", "PAGE")} {_scoutingMarketPage + 1}/{pages}";
    }

    private string ScoutingMarketSortLabel() => _scoutingMarketSort switch
    {
        TransferModel.SortEffectiveOverall => Loc.Tr("common.skill", "SKILL"),
        TransferModel.SortAge => Loc.Tr("common.age", "AGE"),
        _ => Loc.Tr("common.value", "VALUE"),
    };

    private CareerPlayer? CurrentScoutingMarketPlayer()
    {
        var players = ScoutingMarketPlayers();
        if (players.Count == 0) return null;
        _scoutingMarketSelectedIndex = System.Math.Clamp(_scoutingMarketSelectedIndex, 0, players.Count - 1);
        _scoutingMarketPage = _scoutingMarketSelectedIndex / ScoutingMarketRowsPerPage();
        return players[_scoutingMarketSelectedIndex];
    }

    private string ScoutingMarketSelectedLabel()
    {
        CareerPlayer? player = CurrentScoutingMarketPlayer();
        return player is null ? Loc.Tr("common.none", "NONE") : FitText(player.Name, false, 132);
    }

    private string ScoutingSelectedLabel()
    {
        CareerPlayer? player = CurrentScoutingMarketPlayer();
        return player is null ? Loc.Tr("scout.scout_selected", "SCOUT SELECTED") : Loc.Tr("scout.scout_prefix", "SCOUT") + " " + FormatMoney(Scouting.PlayerScoutingCost(ScoutQuality()));
    }

    private void OpenScoutSortPicker()
    {
        var rows = new System.Collections.Generic.List<string>();
        foreach (var m in kSortModes) rows.Add(SortModeName(m.Mode));
        int cur = System.Array.FindIndex(kSortModes, m => m.Mode == _scoutingMarketSort);
        PushListPicker(Loc.Tr("common.sort_by", "SORT BY"), rows, cur < 0 ? 0 : cur, idx =>
        {
            _scoutingMarketSort = kSortModes[idx].Mode;
            _scoutingMarketPage = 0;
            _scoutingMarketSelectedIndex = 0;
            _scoutMarketCache = null;   // re-sort under the new sort mode
            RebuildCurrent();
        });
    }

    private void StepScoutingMarketPage(int delta)
    {
        _scoutingMarketPage = System.Math.Clamp(_scoutingMarketPage + delta, 0, ScoutingMarketPageCount() - 1);
        _scoutingMarketSelectedIndex = System.Math.Min(_scoutingMarketPage * ScoutingMarketRowsPerPage(), System.Math.Max(0, ScoutingMarketPlayers().Count - 1));
        RebuildCurrent();
    }

    private void ScoutSelectedPlayer()
    {
        var c = LoadedComp();
        CareerPlayer? player = CurrentScoutingMarketPlayer();
        if (c?.Career?.World is null || player is null)
        {
            _scoutingNotice = Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        string name = player.Name;
        if (Scouting.TryScoutPlayer(c.Career.World, c.Career.ClubGlobalId, player.Id, out string refusal))
        {
            CompetitionStore.Save(c);
            InvalidateMarketCaches();   // scouting flags changed on the pooled player
            _scoutingNotice = Loc.Tr("scout.scouted_prefix", "SCOUTED") + " " + AsciiText(name);
            Pop();
            return;
        }
        _scoutingNotice = refusal;
        RebuildCurrent();
    }

    private void DrawScoutMarketBody(MenuScreen s)
    {
        CareerClub? club = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (club is null) { CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head); return; }
        // NAME | POS | AGE | SKILL | CLUB | PRICE across the 560 px inner panel
        // (PRICE is what a scouting decision hinges on — user request).
        int name = panelX + 8 + HeadIconAdvance, pos = panelX + 190, age = panelX + 250, eff = panelX + 290, clubName = panelX + 320;
        int price = panelX + panelW - 6;
        CareerTableText(s, Loc.Tr("common.budget", "BUDGET") + " " + FormatMoney(club.Budget) + "  " + Loc.Tr("scout.cost_prefix", "COST") + " " + FormatMoney(Scouting.PlayerScoutingCost(ScoutQuality())), panelX + 8, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"),pos, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"),age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"),eff, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.club", "CLUB"),clubName, y, head);
        CareerTableText(s, Loc.Tr("col.price", "PRICE"),price, y, head, rightAlign: true);
        y += 10;
        var players = ScoutingMarketPlayers();
        int rows = ScoutingMarketRowsPerPage();
        int pages = System.Math.Max(1, (players.Count + rows - 1) / rows);
        _scoutingMarketPage = System.Math.Clamp(_scoutingMarketPage, 0, pages - 1);
        if (players.Count == 0) { CareerTableText(s, Loc.Tr("scout.no_players_available", "NO PLAYERS AVAILABLE"), name, y, normal); return; }
        for (int i = _scoutingMarketPage * rows; i < players.Count && i < _scoutingMarketPage * rows + rows; i++)
        {
            CareerPlayer player = players[i];
            if (i == _scoutingMarketSelectedIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            BodyHeadIcon(s, player.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(player));
            CareerCell(s, player.Name, name, y, pos - name - 4, normal);
            CareerCell(s, player.Position, pos, y, age - pos - 18, normal);
            CareerTableText(s, player.Age.ToString(), age, y, normal, rightAlign: true);
            CareerTableText(s, player.EffectiveSkillSum().ToString(), eff, y, normal, rightAlign: true);
            CareerCell(s, MarketClubName(player), clubName, y, price - 58 - clubName, normal);
            CareerTableText(s, FormatMoney(TransferModel.AskingPrice(player)), price, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private MenuScreen BuildTransferMarket()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        var s = new MenuScreen
        {
            Title = Loc.Tr("market.title", "TRANSFER MARKET"),
            BodyReserve = 70,
        };

        if (c?.Career?.World is null || club is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.no_career_data", "NO CAREER DATA") });
        }
        else
        {
            _marketPage = 0;
            _marketSelectedIndex = 0;
            _marketActionPlayerId = -1;
            _marketSort = TransferModel.SortValue;
            _marketPriceFilter = 0;
            _transferNotice = null;
            _marketCache = null;   // fresh screen entry rebuilds the list
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = MarketPageLabel });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.sort", "SORT"), Value = MarketSortLabel, OnActivate = OpenMarketSortPicker });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("market.max_price", "MAX PRICE"), Value = () => _marketPriceFilter == 0 ? Loc.Tr("career.price_any", "ANY") : kPriceFilters[_marketPriceFilter].Label,
                OnStep = StepMarketPriceFilter });
            var marketPlayerField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("common.player", "PLAYER"), Value = MarketSelectedLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(marketPlayerField);
            s.TableSelect = new MenuTableSelect
            {
                Field = marketPlayerField,
                Count = () => MarketPlayers().Count,
                GetIndex = () => _marketSelectedIndex,
                SetIndex = idx => { _marketSelectedIndex = idx; _marketPage = idx / MarketRowsPerPage(); },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("market.buy_selected", "BUY SELECTED"), OnActivate = OpenBuyConfirm, WidthOverride = 176 });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.previous_page", "PREVIOUS PAGE"), OnActivate = () => StepMarketPage(-1), WidthOverride = 176 });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Tool, Big = false,
                Label = () => Loc.Tr("common.next_page", "NEXT PAGE"), OnActivate = () => StepMarketPage(+1), WidthOverride = 176 });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop(), WidthOverride = 176 });
        s.Body = client => client.InTableSpace(() => client.DrawTransferMarketBody(s));
        return s;
    }

    private System.Collections.Generic.List<CareerPlayer> MarketPlayers()
    {
        var c = LoadedComp();
        if (c?.Career?.World is null) return new System.Collections.Generic.List<CareerPlayer>();
        if (_marketCache is null || _marketCacheSort != _marketSort || _marketCacheFilter != _marketPriceFilter)
        {
            var f = kPriceFilters[_marketPriceFilter];
            _marketCache = TransferModel.Market(c.Career.World, c.Career.ClubGlobalId, _marketSort, f.Min, f.Max);
            _marketCacheSort = _marketSort;
            _marketCacheFilter = _marketPriceFilter;
        }
        return _marketCache;
    }

    private int MarketRowsPerPage()
    {
        int panelY = TablePanelY;
        int panelH = TableVh - panelY - 21;
        return System.Math.Max(1, (panelH - 29) / 8);
    }

    private int MarketPageCount()
    {
        int count = MarketPlayers().Count;
        int rows = MarketRowsPerPage();
        return System.Math.Max(1, (count + rows - 1) / rows);
    }

    private CareerPlayer? CurrentMarketPlayer()
    {
        var players = MarketPlayers();
        if (players.Count == 0) return null;
        _marketSelectedIndex = System.Math.Clamp(_marketSelectedIndex, 0, players.Count - 1);
        _marketPage = _marketSelectedIndex / MarketRowsPerPage();
        return players[_marketSelectedIndex];
    }

    private string MarketPageLabel()
    {
        int pages = MarketPageCount();
        _marketPage = System.Math.Clamp(_marketPage, 0, pages - 1);
        return $"{Loc.Tr("common.page", "PAGE")} {_marketPage + 1}/{pages}";
    }

    private string MarketSortLabel() => _marketSort switch
    {
        TransferModel.SortEffectiveOverall => Loc.Tr("common.skill", "SKILL"),
        TransferModel.SortAge => Loc.Tr("common.age", "AGE"),
        _ => Loc.Tr("common.value", "VALUE"),
    };

    private string MarketSelectedLabel()
    {
        CareerPlayer? player = CurrentMarketPlayer();
        return player is null ? Loc.Tr("common.none", "NONE") : FitText(player.Name ?? "", false, 132);
    }

    // The three market/scout sort modes and their labels, in display order. The
    // sort constants are not assumed to be 0/1/2, so the picker maps by value.
    private static readonly (int Mode, string Name)[] kSortModes =
    {
        (TransferModel.SortValue, "VALUE"),
        (TransferModel.SortEffectiveOverall, "SKILL"),
        (TransferModel.SortAge, "AGE"),
    };

    // Display-time name for a sort MODE (the stored mode int is never changed).
    private static string SortModeName(int mode) => mode switch
    {
        TransferModel.SortEffectiveOverall => Loc.Tr("common.skill", "SKILL"),
        TransferModel.SortAge => Loc.Tr("common.age", "AGE"),
        _ => Loc.Tr("common.value", "VALUE"),
    };

    // Transfer-market max-price bands. Each band is a half-open [Min, Max) range
    // matched against a player's asking price; ANY passes everything.
    private static readonly (string Label, long Min, long Max)[] kPriceFilters =
    {
        ("ANY", 0L, long.MaxValue),
        ("<100K", 0L, 100_000L),
        ("<250K", 0L, 250_000L),
        ("<500K", 0L, 500_000L),
        ("<1M", 0L, 1_000_000L),
        ("<2M", 0L, 2_000_000L),
        ("<5M", 0L, 5_000_000L),
        ("<10M", 0L, 10_000_000L),
        (">=10M", 10_000_000L, long.MaxValue),
    };

    private void StepMarketPriceFilter(int delta)
    {
        int n = kPriceFilters.Length;
        _marketPriceFilter = ((_marketPriceFilter + delta) % n + n) % n;
        _marketPage = 0;
        _marketSelectedIndex = 0;
        _marketCache = null;   // filter changed: rebuild the cached list
        RebuildCurrent();
    }

    private void OpenMarketSortPicker()
    {
        var rows = new System.Collections.Generic.List<string>();
        foreach (var m in kSortModes) rows.Add(SortModeName(m.Mode));
        int cur = System.Array.FindIndex(kSortModes, m => m.Mode == _marketSort);
        PushListPicker(Loc.Tr("common.sort_by", "SORT BY"), rows, cur < 0 ? 0 : cur, idx =>
        {
            _marketSort = kSortModes[idx].Mode;
            _marketPage = 0;
            _marketSelectedIndex = 0;
            _marketCache = null;   // re-sort under the new sort mode
            RebuildCurrent();
        });
    }

    private void StepMarketPage(int delta)
    {
        int pages = MarketPageCount();
        _marketPage = System.Math.Clamp(_marketPage + delta, 0, pages - 1);
        int count = MarketPlayers().Count;
        _marketSelectedIndex = System.Math.Min(_marketPage * MarketRowsPerPage(), System.Math.Max(0, count - 1));
        RebuildCurrent();
    }

    private void OpenBuyConfirm()
    {
        CareerPlayer? player = CurrentMarketPlayer();
        if (player is null) { _transferNotice = Loc.Tr("market.no_player_selected", "NO PLAYER SELECTED"); RebuildCurrent(); return; }
        _marketActionPlayerId = player.Id;
        _transferNotice = null;
        // A fresh target clears any prior counter; seed the bid at the asking price.
        if (_negotiationTargetId != player.Id) _bidCounterAsking = 0;
        CareerClub? club = CurrentCareerClub();
        long asking = EffectiveAsking(player);
        _bidAmount = System.Math.Clamp(asking, 0, club?.Budget ?? asking);
        Push(BuildBuyConfirm());
    }

    // The price the AI will accept: the club's standard asking price, or its
    // running counter-offer once one is on the table for this target.
    private long EffectiveAsking(CareerPlayer player)
        => _bidCounterAsking > 0 && _negotiationTargetId == player.Id
            ? _bidCounterAsking
            : TransferModel.AskingPrice(player);

    private MenuScreen BuildBuyConfirm()
    {
        CareerClub? club = CurrentCareerClub();
        CareerPlayer? player = FindMarketPlayer(_marketActionPlayerId);
        var s = new MenuScreen { Title = Loc.Tr("buy.title", "BUY PLAYER") };
        if (club is null || player is null)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE") });
        }
        else
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => FitText(player.Name + "  " + Loc.Tr("buy.asking_prefix", "ASKING") + " " + FormatMoney(EffectiveAsking(player)), false, 294) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false,
                Label = () => Loc.Tr("common.budget", "BUDGET") + " " +FormatMoney(club.Budget) + "   " + NegotiateStatus() });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("buy.your_bid", "YOUR BID"), Value = () => FormatMoney(_bidAmount), OnStep = d => StepBid(d, player, club) });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _transferNotice ?? "" });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = () => Loc.Tr("buy.make_bid", "MAKE BID"), OnActivate = MakeBid });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        return s;
    }

    private void StepBid(int delta, CareerPlayer player, CareerClub club)
    {
        long asking = EffectiveAsking(player);
        long step = System.Math.Max(1L, asking / 20L);
        _bidAmount = System.Math.Clamp(_bidAmount + delta * step, 0L, System.Math.Max(0L, club.Budget));
        RebuildCurrent();
    }

    private CareerPlayer? FindMarketPlayer(int playerId)
    {
        foreach (CareerPlayer player in MarketPlayers())
            if (player.Id == playerId) return player;
        return null;
    }

    // BID flow that replaced the instant purchase. AI accepts a bid >= asking;
    // a bid in [value, asking) draws one midpoint counter-offer; anything below
    // value is rejected. Each NEW target spends one TimeToNegotiate (re-bidding
    // the same target is free); the club refuses once the buy quota is spent or
    // the negotiation budget is exhausted.
    private void MakeBid()
    {
        var c = LoadedComp();
        CareerClub? club = CurrentCareerClub();
        CareerPlayer? player = FindMarketPlayer(_marketActionPlayerId);
        if (c?.Career?.World is null || club is null || player is null)
        {
            _transferNotice = Loc.Tr("common.player_not_available", "PLAYER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        if (c.Career.BuysThisSeason >= TransferOffers.BuyQuotaPerSeason)
        {
            _transferNotice = Loc.Tr("buy.club_unwilling", "CLUB UNWILLING TO PURCHASE PLAYERS");
            RebuildCurrent();
            return;
        }

        bool newTarget = _negotiationTargetId != player.Id;
        if (newTarget && c.Career.TimeToNegotiate <= 0)
        {
            _transferNotice = Loc.Tr("buy.no_time", "NO MORE TIME TO NEGOTIATE");
            RebuildCurrent();
            return;
        }
        if (newTarget)
        {
            c.Career.TimeToNegotiate--;
            _negotiationTargetId = player.Id;
            _bidCounterAsking = 0;
        }

        long value = Finance.PlayerValue(player);
        long asking = EffectiveAsking(player);
        long bid = _bidAmount;
        if (bid <= 0) { CompetitionStore.Save(c); _transferNotice = Loc.Tr("buy.enter_a_bid", "ENTER A BID"); RebuildCurrent(); return; }
        if (club.Budget < bid) { CompetitionStore.Save(c); _transferNotice = Loc.Tr("buy.not_enough_money", "NOT ENOUGH MONEY"); RebuildCurrent(); return; }

        if (bid >= asking)
        {
            if (TransferModel.Buy(c.Career.World, c.Career.ClubGlobalId, player.Id, bid))
            {
                c.Career.BuysThisSeason++;
                Chronicle.Signed(c.Career, ScorerModel.CleanName(player.Name), bid, c.CurrentRound);
                _negotiationTargetId = -1;
                _bidCounterAsking = 0;
                CompetitionStore.Save(c);
                InvalidateMarketCaches();
                _transferNotice = Loc.Tr("buy.bought_prefix", "BOUGHT") + " " + AsciiText(player.Name);
                Pop();
                return;
            }
            CompetitionStore.Save(c);
            _transferNotice = club.Squad.Count >= 22 ? Loc.Tr("buy.squad_full", "SQUAD FULL") : Loc.Tr("buy.transfer_failed", "TRANSFER FAILED");
            RebuildCurrent();
            return;
        }
        if (bid >= value)
        {
            _bidCounterAsking = (bid + asking) / 2L;
            _bidAmount = System.Math.Clamp(_bidCounterAsking, 0L, club.Budget);
            CompetitionStore.Save(c);
            _transferNotice = Loc.Tr("buy.counter_prefix", "THEY COUNTER AT") + " " + FormatMoney(_bidCounterAsking);
            RebuildCurrent();
            return;
        }
        CompetitionStore.Save(c);
        _transferNotice = Loc.Tr("buy.rejected", "BID TOO LOW - REJECTED");
        RebuildCurrent();
    }

    // ---- incoming offers -----------------------------------------------------
    private string OffersEntryLabel()
    {
        var c = LoadedComp();
        int count = c?.Career?.PendingOffers?.Count ?? 0;
        string prefix = c is not null && TransferOffers.HasUnseenOffers(c) ? Loc.Tr("dash.offers_unseen_mark", "!") + " " : "";
        return prefix + Loc.Tr("dash.offers_prefix", "OFFERS") + " (" + count + ")";
    }

    private System.Collections.Generic.List<TransferOffer> OfferList()
    {
        var offers = LoadedComp()?.Career?.PendingOffers;
        return offers is null
            ? new System.Collections.Generic.List<TransferOffer>()
            : new System.Collections.Generic.List<TransferOffer>(offers);
    }

    private TransferOffer? CurrentOffer()
    {
        var offers = OfferList();
        if (offers.Count == 0) return null;
        _offerSelectedIndex = System.Math.Clamp(_offerSelectedIndex, 0, offers.Count - 1);
        return offers[_offerSelectedIndex];
    }

    private CareerPlayer? OfferPlayer(TransferOffer offer)
        => CurrentCareerClub()?.Squad?.Find(p => p is not null && p.Id == offer.PlayerId);

    private string OfferClubName(TransferOffer offer)
    {
        if (TeamNamesByGlobalId().TryGetValue(offer.BidderClubId, out string? name))
            return AsciiText(name);
        return Loc.Tr("market.club_fallback_prefix", "CLUB") + " " +offer.BidderClubId;
    }

    private MenuScreen BuildOffersScreen()
    {
        var c = LoadedComp();
        var s = new MenuScreen { Title = Loc.Tr("offers.title", "TRANSFER OFFERS"), BodyReserve = 70 };
        // Entering the screen marks every pending offer as seen (clears the "!").
        if (c?.Career?.PendingOffers is not null)
        {
            bool changed = false;
            foreach (var o in c.Career.PendingOffers) if (o is not null && !o.Seen) { o.Seen = true; changed = true; }
            if (changed) CompetitionStore.Save(c);
        }
        _offerSelectedIndex = 0;
        _offerNotice = null;
        if (c?.Career is null || OfferList().Count == 0)
        {
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => Loc.Tr("offers.no_offers", "NO OFFERS") });
        }
        else
        {
            var offerField = new MenuEntry { Kind = EntryKind.Option, Style = MenuTheme.Style.Value,
                Label = () => Loc.Tr("offers.offer", "OFFER"), Value = OfferSelectedLabel, OnActivate = EnterTableSelectCurrent };
            s.Entries.Add(offerField);
            s.TableSelect = new MenuTableSelect
            {
                Field = offerField,
                Count = () => OfferList().Count,
                GetIndex = () => _offerSelectedIndex,
                SetIndex = idx => { _offerSelectedIndex = idx; },
            };
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.PlayPrimary, Big = false,
                Label = () => Loc.Tr("offers.accept", "ACCEPT"), OnActivate = AcceptSelectedOffer });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Accent, Big = false,
                Label = () => Loc.Tr("offers.demand_more", "DEMAND MORE"), OnActivate = DemandMoreSelectedOffer });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Danger, Big = false,
                Label = () => Loc.Tr("offers.reject", "REJECT"), OnActivate = RejectSelectedOffer });
            s.Entries.Add(new MenuEntry { Kind = EntryKind.Label, Big = false, Label = () => _offerNotice ?? "" });
        }
        s.Entries.Add(new MenuEntry { Kind = EntryKind.Button, Style = MenuTheme.Style.Plain, Big = false,
            Label = () => Loc.Tr("common.back", "BACK"), OnActivate = () => Pop() });
        s.Body = client => client.InTableSpace(() => client.DrawOffersBody(s));
        return s;
    }

    private string OfferSelectedLabel()
    {
        TransferOffer? offer = CurrentOffer();
        if (offer is null) return Loc.Tr("common.none", "NONE");
        CareerPlayer? player = OfferPlayer(offer);
        string who = player is null ? ("#" + offer.PlayerId) : (player.Name ?? "");
        return FitText(who + " " + FormatMoney(offer.Amount), false, 150);
    }

    private void AcceptSelectedOffer()
    {
        var c = LoadedComp();
        TransferOffer? offer = CurrentOffer();
        if (c?.Career?.World is null || offer is null)
        {
            _offerNotice = Loc.Tr("offers.not_available", "OFFER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        CareerPlayer? player = OfferPlayer(offer);
        string name = player?.Name ?? ("#" + offer.PlayerId);
        if (TransferOffers.Accept(c, c.Career.World, offer))
        {
            CompetitionStore.Save(c);
            InvalidateMarketCaches();
            _offerNotice = Loc.Tr("offers.sold_prefix", "SOLD") + " " + AsciiText(name) + " " + Loc.Tr("offers.sold_for", "FOR") + " " + FormatMoney(offer.Amount);
        }
        else _offerNotice = CurrentCareerClub()?.Squad?.Count <= 12 ? Loc.Tr("offers.squad_too_small", "SQUAD TOO SMALL") : Loc.Tr("offers.accept_failed", "ACCEPT FAILED");
        _offerSelectedIndex = 0;
        RebuildCurrent();
    }

    private void DemandMoreSelectedOffer()
    {
        var c = LoadedComp();
        TransferOffer? offer = CurrentOffer();
        if (c?.Career is null || offer is null)
        {
            _offerNotice = Loc.Tr("offers.not_available", "OFFER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        var outcome = TransferOffers.RejectDemandMore(c, offer);
        CompetitionStore.Save(c);
        _offerNotice = outcome switch
        {
            DemandOutcome.Improved => Loc.Tr("offers.improved_prefix", "IMPROVED TO") + " " + FormatMoney(offer.Amount),
            DemandOutcome.Withdrawn => Loc.Tr("offers.withdrawn", "OFFER WITHDRAWN"),
            DemandOutcome.Refused => Loc.Tr("offers.refused", "THEY REFUSE TO PAY MORE"),
            _ => Loc.Tr("offers.not_available", "OFFER NOT AVAILABLE"),
        };
        _offerSelectedIndex = 0;
        RebuildCurrent();
    }

    private void RejectSelectedOffer()
    {
        var c = LoadedComp();
        TransferOffer? offer = CurrentOffer();
        if (c?.Career?.PendingOffers is null || offer is null)
        {
            _offerNotice = Loc.Tr("offers.not_available", "OFFER NOT AVAILABLE");
            RebuildCurrent();
            return;
        }
        c.Career.PendingOffers.Remove(offer);
        CompetitionStore.Save(c);
        _offerNotice = Loc.Tr("offers.rejected", "OFFER REJECTED");
        _offerSelectedIndex = 0;
        RebuildCurrent();
    }

    private void DrawOffersBody(MenuScreen s)
    {
        var c = LoadedComp();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 32) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);
        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (c?.Career is null) { CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head); return; }

        // CLUB | PLAYER | AMOUNT | EXP spread across the 560 px inner panel.
        int club = panelX + 8, name = panelX + 220 + HeadIconAdvance, exp = panelX + panelW - 6, amount = exp - 40;
        CareerTableText(s, Loc.Tr("offers.header_negotiate_prefix", "TIME TO NEGOTIATE") + " " + System.Math.Max(0, c.Career.TimeToNegotiate)
            + "   " + Loc.Tr("offers.header_sells", "SELLS") + " " + c.Career.SellsThisSeason, panelX + 8, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.club", "CLUB"),club, y, head);
        CareerTableText(s, Loc.Tr("common.player", "PLAYER"), name, y, head);
        CareerTableText(s, Loc.Tr("col.amount", "AMOUNT"),amount, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.exp", "EXP"),exp, y, head, rightAlign: true);
        y += 10;
        var offers = OfferList();
        if (offers.Count == 0) { CareerTableText(s, Loc.Tr("offers.no_offers", "NO OFFERS"), club, y, normal); return; }
        for (int i = 0; i < offers.Count && y < panelY + panelH - 8; i++)
        {
            TransferOffer offer = offers[i];
            if (i == _offerSelectedIndex) BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            CareerPlayer? player = OfferPlayer(offer);
            CareerCell(s, OfferClubName(offer), club, y, name - club - 4, normal);
            if (player is not null) BodyHeadIcon(s, player.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(player));
            CareerCell(s, player?.Name ?? ("#" + offer.PlayerId), name, y, amount - name - 6, normal);
            CareerTableText(s, FormatMoney(offer.Amount), amount, y, normal, rightAlign: true);
            CareerTableText(s, offer.ExpiryRounds.ToString(), exp, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private void DrawTransferMarketBody(MenuScreen s)
    {
        CareerClub? buyer = CurrentCareerClub();
        int panelX = 8, panelY = TablePanelY, panelW = TableVw - 16, panelH = TableVh - panelY - 21;
        if (panelH < 40) return;
        BodyBox(s, panelX, panelY, panelW, panelH, MenuTheme.Style.Value, 6);

        var head = new Color(0.7f, 0.85f, 1f);
        var normal = new Color(0.92f, 0.94f, 1f);
        if (buyer is null)
        {
            CareerTableText(s, Loc.Tr("common.no_career_data", "NO CAREER DATA"), panelX + 8, panelY + 8, head);
            return;
        }

        // NAME | NAT | POS | AGE | SKILL | CLUB | PRICE spread across the 560 px panel.
        int name = panelX + 8 + HeadIconAdvance;
        int nat = panelX + 150;
        int pos = panelX + 182;
        int age = panelX + 232;
        int skill = panelX + 268;
        int club = panelX + 300;
        int price = panelX + panelW - 6;
        CareerTableText(s, Loc.Tr("common.budget", "BUDGET") + " " +FormatMoney(buyer.Budget), panelX + 8, panelY + 4, head);
        if (!string.IsNullOrEmpty(_transferNotice))
            CareerTableText(s, FitText(_transferNotice, false, panelW - 124), panelX + 116, panelY + 4, head);
        int y = panelY + 15;
        CareerTableText(s, Loc.Tr("col.name", "NAME"),name, y, head);
        CareerTableText(s, Loc.Tr("col.nat", "NAT"),nat, y, head);
        CareerTableText(s, Loc.Tr("col.pos", "POS"),pos, y, head);
        CareerTableText(s, Loc.Tr("col.age", "AGE"),age, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.skill", "SKILL"),skill, y, head, rightAlign: true);
        CareerTableText(s, Loc.Tr("col.club", "CLUB"),club, y, head);
        CareerTableText(s, Loc.Tr("col.price", "PRICE"),price, y, head, rightAlign: true);
        y += 10;

        var players = MarketPlayers();
        int rows = System.Math.Max(1, (panelH - 29) / 8);
        int pages = System.Math.Max(1, (players.Count + rows - 1) / rows);
        _marketPage = System.Math.Clamp(_marketPage, 0, pages - 1);
        int first = _marketPage * rows;
        if (players.Count == 0)
        {
            CareerTableText(s, Loc.Tr("scout.no_players_available", "NO PLAYERS AVAILABLE"), panelX + 8, y, normal);
            return;
        }

        for (int i = first; i < players.Count && i < first + rows; i++)
        {
            CareerPlayer player = players[i];
            if (i == _marketSelectedIndex)
                BodyBox(s, panelX + 4, y - 1, panelW - 8, 7, MenuTheme.Style.Info, 21);
            BodyHeadIcon(s, player.Face, name - HeadIconAdvance, y - 1, PlayerHeadKit(player));
            CareerCell(s, player.Name, name, y, nat - name - 4, normal);
            BodyPlayerFlag(s, player.Nationality, nat, y);
            CareerCell(s, player.Position, pos, y, age - pos - 18, normal);
            CareerTableText(s, player.Age.ToString(), age, y, normal, rightAlign: true);
            CareerTableText(s, player.EffectiveSkillSum().ToString(), skill, y, normal, rightAlign: true);
            CareerCell(s, MarketClubName(player), club, y, price - club - 34, normal);
            CareerTableText(s, FormatMoney(TransferModel.AskingPrice(player)), price, y, normal, rightAlign: true);
            y += 8;
        }
    }

    private string MarketClubName(CareerPlayer player)
    {
        if (player.ClubId == 0) return Loc.Tr("market.free_agent", "FREE AGENT");
        if (TeamNamesByGlobalId().TryGetValue(player.ClubId, out string? name))
            return AsciiText(name);
        return Loc.Tr("market.club_fallback_prefix", "CLUB") + " " +player.ClubId;
    }

    // Transfermarkt-style money: two significant digits with a K/M suffix, e.g.
    // 37K, 270K, 1.3M, 11M, 110M. A decimal appears only when the leading unit
    // is a single digit (1.3M) — never when the integer part has two-plus
    // digits (11M, 270K). Values under 1000 print in full.
    private static string FormatMoney(long amount)
    {
        string sign = amount < 0 ? "-" : "";
        double absolute = System.Math.Abs((double)amount);
        if (absolute < 1_000) return sign + ((long)absolute).ToString(System.Globalization.CultureInfo.InvariantCulture);

        double rounded = RoundToSignificant(absolute, 2);
        if (rounded >= 1_000_000)
        {
            double m = rounded / 1_000_000.0;
            return sign + (m < 10.0
                ? m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : m.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "M";
        }
        double k = rounded / 1_000.0;
        return sign + (k < 10.0
            ? k.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : k.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "K";
    }

    // Round a positive magnitude to N significant figures (away-from-zero).
    private static double RoundToSignificant(double value, int digits)
    {
        if (value <= 0.0) return 0.0;
        double magnitude = System.Math.Floor(System.Math.Log10(value));
        double factor = System.Math.Pow(10.0, magnitude - (digits - 1));
        return System.Math.Round(value / factor, System.MidpointRounding.AwayFromZero) * factor;
    }

    private Sprite2D CareerTableText(MenuScreen s, string text, int x, int y, Color color, bool rightAlign = false)
        => BodyText(s, AsciiText(text), false, x, y, color, rightAlign);

    // Left-aligned free-text cell HARD-clipped to maxW px (ellipsis) so a long
    // name/club/specialty can never bleed into the next column — the fix for the
    // "MIROSLAW DRESZERG" spilling into POS overlap.
    private void CareerCell(MenuScreen s, string? text, int x, int y, int maxW, Color color)
        => CareerTableText(s, FitText(text ?? "", false, System.Math.Max(6, maxW)), x, y, color);

    // --menu-shot only: force a couple of injuries into the player's career squad
    // so the red Loc.Tr("squad.fit_injured", "INJ") row (severity >= 2) and the yellow "carrying a knock" FIT
    // (severity 1) are visible in the 11b_career_squad screenshot. Never called in
    // normal play (guarded by the harness).
    public void DebugInjureSquadPlayers()
    {
        var club = CurrentCareerClub();
        if (club?.Squad is null) return;
        if (club.Squad.Count > 3) club.Squad[3].InjurySeverity = 4;   // unavailable -> red INJ
        if (club.Squad.Count > 6) club.Squad[6].InjurySeverity = 1;   // knock -> yellow FIT
    }

    private static string FitCareerTableText(string? text, int maxCharacters)
    {
        string value = AsciiText(text);
        if (value.Length <= maxCharacters) return value;
        const string suffix = "...";
        return maxCharacters <= suffix.Length ? value.Substring(0, maxCharacters) : value.Substring(0, maxCharacters - suffix.Length).TrimEnd() + suffix;
    }

    // The font is proportional, so character-count clipping can still spill
    // outside a panel. Keep a visible ASCII ellipsis only when it fits too.
    private string FitText(string text, bool big, int maxWidth)
        => FitTextToWidth(text, big, maxWidth);

    private string FitTextToWidth(string text, bool big, int maxWidth)
    {
        string value = AsciiText(text);
        if (_font is null || _font.Measure(value, big) <= maxWidth) return value;
        const string suffix = "...";
        if (_font.Measure(suffix, big) > maxWidth) return "";
        for (int length = value.Length - 1; length >= 0; length--)
        {
            string candidate = value.Substring(0, length).TrimEnd() + suffix;
            if (_font.Measure(candidate, big) <= maxWidth) return candidate;
        }
        return suffix;
    }

    // The source roster can contain arbitrary text, while the authentic
    // charset only has printable ASCII.  Keep every career UI string visible
    // and deterministic rather than silently dropping unsupported characters.
    private static string AsciiText(string? text)
    {
        string value = (text ?? "").Trim().ToUpperInvariant();
        if (value.Length == 0) return "";
        var ascii = new System.Text.StringBuilder(value.Length);
        foreach (char c in value) ascii.Append(c is >= ' ' and <= '~' ? c : '?');
        return ascii.ToString();
    }

    // ======================================================================
    //  Player HEAD ICONS for the career tables (#220)
    // ======================================================================
    // The original SWOS drew a tiny per-player head (blond / dark-haired /
    // black) to the left of the name in the squad/transfer lists. We recreate
    // that by cropping the head-and-shoulders bust out of the standing
    // (South-facing) outfield sprite of the CJCTEAM1 Amiga atlas — cell
    // (col 3, row 0), the exact art the match renderer uses — recoloured for the
    // player's FACE type (0 WHITE, 1 GINGER, 2 BLACK) via KitPalette.ApplyFace,
    // over a neutral grey kit so every bust's shoulders read the same regardless
    // of team.
    //
    // The bust is a 7x7 crop at atlas pixels (49,0): within the 16x16 cell,
    // cols 1-7 / rows 0-6 hold the hair cap (rows 0-2), the face + eyes
    // (rows 2-3) and the grey shoulder line (rows 4-6). Palette index 0 is
    // transparent (AmigaSpriteAtlas.GetRegion) so the corners round off. Each
    // face is baked ONCE into an ImageTexture and cached.
    private const int HeadIconW = 7;
    private const int HeadIconH = 7;
    private const int HeadIconGap = 2;
    private const int HeadIconAdvance = HeadIconW + HeadIconGap;   // name column shift = 9 px

    // Head busts are cached by (face, kitKey): the same face over different club
    // kits is a distinct texture (#223 — heads now wear the club's HOME kit
    // colours instead of neutral grey). Free agents keep the neutral grey key.
    private readonly System.Collections.Generic.Dictionary<long, ImageTexture> _headIcons = new();

    // Neutral grey kit {shirtType, stripes, basic, shorts, socks} — SWOS colour
    // name 0 is grey (KitPalette.Colours[0]); used for free agents (no club).
    private static readonly byte[] kNeutralHeadKit = { 0, 0, 0, 0, 0 };

    // HomeKit bytes per club GlobalId, probed once from the host team list (the
    // master roster never changes at runtime) — lets the market/scout tables
    // colour every player's bust in their OWN club's kit.
    private System.Collections.Generic.Dictionary<ushort, byte[]>? _homeKitByGlobalId;

    private System.Collections.Generic.Dictionary<ushort, byte[]> HomeKitByGlobalId()
    {
        if (_homeKitByGlobalId is not null) return _homeKitByGlobalId;
        var kits = new System.Collections.Generic.Dictionary<ushort, byte[]>();
        for (int i = 0; i < _host.TeamCount; i++)
        {
            TeamRecord team;
            try { team = _host.Team(i); }
            catch { continue; }
            if (team.HomeKit is { Length: > 0 }) kits.TryAdd(team.GlobalId, team.HomeKit);
        }
        return _homeKitByGlobalId = kits;
    }

    // The bust kit for a career player: their club's HomeKit, or neutral grey
    // for free agents / unknown clubs.
    private byte[] PlayerHeadKit(CareerPlayer p)
    {
        if (p.ClubId != 0 && HomeKitByGlobalId().TryGetValue(p.ClubId, out byte[]? kit) && kit.Length > 0)
            return kit;
        return kNeutralHeadKit;
    }

    // Pack a face id + kit bytes into a stable cache key.
    private static long HeadKey(int face, byte[] kit)
    {
        long k = (uint)face;
        for (int i = 0; i < kit.Length && i < 5; i++) k = (k << 8) | kit[i];
        return k;
    }

    // Draw a player's head bust at (x, y) in the current body space and register
    // it for automatic cleanup alongside the rest of the screen's body nodes.
    private void BodyHeadIcon(MenuScreen s, int face, int x, int y, byte[]? kit = null)
    {
        var tex = HeadIconTexture(face, kit ?? kNeutralHeadKit);
        if (tex is null) return;
        var spr = MakeSprite(tex, x, y, 22, BodyParent);
        s.BodyNodes.Add(spr);
    }

    private ImageTexture? HeadIconTexture(int face, byte[] kit)
    {
        if (face < 0 || face >= KitPalette.FaceCount) face = 0;
        long key = HeadKey(face, kit);
        if (_headIcons.TryGetValue(key, out ImageTexture? cached)) return cached;
        Image? img = BuildHeadImage(face, kit);
        if (img is null) return null;
        var tex = ImageTexture.CreateFromImage(img);
        _headIcons[key] = tex;
        return tex;
    }

    // The bust itself now lives in Assets/HeadIcon.cs so the web career client
    // renders the identical 7x7 image; this stays as the local call site.
    private Image? BuildHeadImage(int face, byte[] kit)
        => OpenSwos.Assets.HeadIcon.Build(face, kit);

    // ======================================================================
    //  Player NATION FLAGS for the career tables (#223)
    // ======================================================================
    // A tiny flag drawn between the shirt number and the head bust, with the
    // 3-letter nation code rendered at true x1 (fine-print layer) just to its
    // right — an 8 px row is too short to stack a 6 px flag AND a 6 px code, so
    // the code goes to the RIGHT of the flag. Each flag pattern (see
    // PlayerNationNames.FlagPattern) is baked ONCE into a 12x8 texture (a 1 px
    // black border around a 10x6 field) and cached by nation index.
    private const int FlagW = 12;          // outer flag width (incl. 1px border)
    private const int FlagH = 8;           // outer flag height (incl. 1px border)
    // Column advance from the flag's left edge to the head bust: flag + the x1
    // code sitting to its right + a small gap.
    private const int FlagAdvance = 22;

    private readonly System.Collections.Generic.Dictionary<int, ImageTexture> _flagIcons = new();

    // Injury row colours. An unavailable injury (severity >= 2) paints the name +
    // FIT cell red, echoing the original's red squad row (swos.asm:53842-53870)
    // and bench-cross aesthetic, with FIT text Loc.Tr("squad.fit_injured", "INJ"). A "carrying a knock"
    // (severity 1) tints only the FIT number yellow — the player is still
    // selectable. Fatigue (freshness) and injury coexist: this only overrides the
    // FIT cell's colour/text, never the FatigueCarry value.
    private static readonly Color InjuryRed = new(0.96f, 0.32f, 0.26f);
    private static readonly Color InjuryYellow = new(1f, 0.82f, 0.25f);

    private ImageTexture? FlagTexture(int nation)
    {
        if (_flagIcons.TryGetValue(nation, out ImageTexture? cached)) return cached;

        // Prefer a real flag PNG shipped under res://data/flags; fall back to the
        // procedural pattern generator when the file is absent or fails to load.
        string path = Godot.ProjectSettings.GlobalizePath(
            $"res://data/flags/{nation:D3}_{PlayerNationNames.Code(nation)}.png");
        ImageTexture tex;
        if (System.IO.File.Exists(path))
        {
            var img = Godot.Image.LoadFromFile(path);
            if (img is not null && img.GetWidth() > 0 && img.GetHeight() > 0)
            {
                tex = ImageTexture.CreateFromImage(BuildRealFlagImage(img));
                _flagIcons[nation] = tex;
                return tex;
            }
            GD.PushWarning($"[flags] falling back to procedural for nation {nation} ({PlayerNationNames.Code(nation)})");
        }
        tex = ImageTexture.CreateFromImage(BuildFlagImage(PlayerNationNames.Flag(nation)));
        _flagIcons[nation] = tex;
        return tex;
    }

    // Build a row-sized flag texture from a real PNG: nearest-neighbour scale to
    // a 6 px inner height (aspect-preserved, clamped 6..10 wide) then compose
    // onto an outer image with a 1 px black border. Outer width = iw+2 <= 12 =
    // FlagW, so the x1 nation code drawn at flagX+FlagW+1 never overlaps.
    private static Image BuildRealFlagImage(Godot.Image src)
    {
        if (src.GetFormat() != Image.Format.Rgba8) src.Convert(Image.Format.Rgba8);
        // UNIFORM box (user request): every flag stretches to the SAME 10x6 field
        // regardless of its native ratio (BEL is officially 13:15 and looked
        // oddly narrow next to BRA/ENG). Only genuinely square flags (SUI) keep
        // a 6x6 field, centred with a thicker black border in the same box.
        // 1 px smaller on every side than the previous 10x6 (user: adjacent
        // flags looked glued) — an 8x4 field leaves 2 px of transparent air.
        int ih = 4;
        double ratio = (double)src.GetWidth() / src.GetHeight();
        bool square = ratio <= 1.05;
        int iw = square ? 4 : 8;
        var scaled = (Image)src.Duplicate();
        scaled.Resize(iw, ih, Image.Interpolation.Lanczos);

        int outerW = FlagW, outerH = FlagH;
        int blitX = square ? (FlagW - iw) / 2 : 2;
        var bytes = new byte[outerW * outerH * 4];
        // No frame (user: flags visually glued together with the black border) —
        // the padding stays TRANSPARENT so adjacent flags get a natural gap.
        // bytes[] is already all-zero = fully transparent; nothing to fill.
        // Blit the scaled flag field (x-offset centres square flags like SUI).
        for (int yy = 0; yy < ih; yy++)
            for (int xx = 0; xx < iw; xx++)
            {
                Color p = scaled.GetPixel(xx, yy);
                int o = ((yy + 2) * outerW + (xx + blitX)) * 4;
                bytes[o] = (byte)System.Math.Clamp((int)System.Math.Round(p.R * 255f), 0, 255);
                bytes[o + 1] = (byte)System.Math.Clamp((int)System.Math.Round(p.G * 255f), 0, 255);
                bytes[o + 2] = (byte)System.Math.Clamp((int)System.Math.Round(p.B * 255f), 0, 255);
                bytes[o + 3] = 255;
            }
        return Image.CreateFromData(outerW, outerH, false, Image.Format.Rgba8, bytes);
    }

    private static Image BuildFlagImage(PlayerNationNames.FlagSpec spec)
    {
        const int W = FlagW, H = FlagH;
        const int ix = 1, iy = 1, iw = W - 2, ih = H - 2;   // inner 10x6 field
        var bytes = new byte[W * H * 4];
        (byte r, byte g, byte b) Rgb(uint c) => ((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        var (ar, ag, ab) = Rgb(spec.A);
        var (br, bg, bb) = Rgb(spec.B);
        var (cr, cg, cb) = Rgb(spec.C);
        void Px(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            int o = (y * W + x) * 4;
            bytes[o] = r; bytes[o + 1] = g; bytes[o + 2] = b; bytes[o + 3] = 255;
        }
        for (int yy = 0; yy < ih; yy++)
            for (int xx = 0; xx < iw; xx++)
            {
                byte r = ar, g = ag, b = ab;
                float u = (xx + 0.5f) / iw, v = (yy + 0.5f) / ih;
                switch (spec.Pattern)
                {
                    case PlayerNationNames.FlagPattern.H3:
                        if (v >= 2f / 3f) { r = cr; g = cg; b = cb; }
                        else if (v >= 1f / 3f) { r = br; g = bg; b = bb; }
                        break;
                    case PlayerNationNames.FlagPattern.V3:
                        if (u >= 2f / 3f) { r = cr; g = cg; b = cb; }
                        else if (u >= 1f / 3f) { r = br; g = bg; b = bb; }
                        break;
                    case PlayerNationNames.FlagPattern.H2:
                        if (v >= 0.5f) { r = br; g = bg; b = bb; }
                        break;
                    case PlayerNationNames.FlagPattern.V2:
                        if (u >= 0.5f) { r = br; g = bg; b = bb; }
                        break;
                    case PlayerNationNames.FlagPattern.Cross:
                        // Off-centre Nordic cross in colour B over field A.
                        if (xx == 3 || yy == ih / 2) { r = br; g = bg; b = bb; }
                        break;
                    case PlayerNationNames.FlagPattern.Diamond:
                    {
                        float dx = System.Math.Abs(u - 0.5f), dy = System.Math.Abs(v - 0.5f);
                        if (dx + dy < 0.5f) { r = br; g = bg; b = bb; }
                        if (dx + dy < 0.16f) { r = cr; g = cg; b = cb; }
                        break;
                    }
                    case PlayerNationNames.FlagPattern.Disc:
                    {
                        float dx = (u - 0.5f) * iw, dy = (v - 0.5f) * ih;
                        if (dx * dx + dy * dy <= 2.6f * 2.6f) { r = br; g = bg; b = bb; }
                        break;
                    }
                    case PlayerNationNames.FlagPattern.Canton:
                        if (v >= 0.5f) { r = br; g = bg; b = bb; }
                        if (u < 0.45f && v < 0.5f) { r = cr; g = cg; b = cb; }
                        break;
                    case PlayerNationNames.FlagPattern.Plain:
                    default:
                        break;
                }
                Px(ix + xx, iy + yy, r, g, b);
            }
        // 1px black border for definition against the panel.
        for (int x = 0; x < W; x++) { Px(x, 0, 0, 0, 0); Px(x, H - 1, 0, 0, 0); }
        for (int y = 0; y < H; y++) { Px(0, y, 0, 0, 0); Px(W - 1, y, 0, 0, 0); }
        return Image.CreateFromData(W, H, false, Image.Format.Rgba8, bytes);
    }

    // Draw the flag at (flagX, rowY-1) plus the x1 nation code just to its right
    // (fine-print layer). rowY is the row's text baseline. Returns nothing; the
    // caller places the head bust at flagX + FlagAdvance.
    private void BodyPlayerFlag(MenuScreen s, int nation, int flagX, int rowY)
    {
        var tex = FlagTexture(nation);
        if (tex is not null)
        {
            s.BodyNodes.Add(MakeSprite(tex, flagX, rowY - 1, 22, BodyParent));
            // Overlay the 3-letter code centered on the flag's lower half (white
            // glyphs, 1px black outline). The flag rect spans rowY-1 .. rowY+7,
            // so rowY+2 sits the x1 text across the lower portion.
            if (!MenuTheme.SmallScreen)
            {
                int fw = tex.GetWidth();   // menu-space px (<= FlagW)
                int centerX = flagX + fw / 2;
                FinePrintTextCentered(s, PlayerNationNames.Code(nation), centerX, rowY + 2);
            }
        }
    }

    // ======================================================================
    //  Top-3 SWOS skill letters (#223)
    // ======================================================================
    // Letters follow the fixed SWOS order P V H T C S F (P=passing,
    // V=shooting/velocity, H=heading, T=tackling, C=control, S=speed,
    // F=finishing). The three highest-valued skills are shown, highest first,
    // ties broken by that fixed order — e.g. "CSV" or "PVF". The letters are a
    // DISPLAY glyph string keyed to the skill ORDER (never a stored value), so a
    // translation supplies its 7 letters in that same fixed order.
    private const string kSkillLettersEn = "PVHTCSF";

    private static string TopSkillLetters(System.ReadOnlySpan<int> skills)
    {
        // skills in fixed order: passing, shooting, heading, tackling, control,
        // speed, finishing. Pick the 3 highest (value desc, then fixed order).
        System.Span<int> order = stackalloc int[7] { 0, 1, 2, 3, 4, 5, 6 };
        for (int i = 0; i < 7; i++)
            for (int j = i + 1; j < 7; j++)
                if (skills[order[j]] > skills[order[i]])
                    (order[i], order[j]) = (order[j], order[i]);
        string letters = Loc.Tr("career.skill_letters", kSkillLettersEn);
        if (letters.Length < 7) letters = kSkillLettersEn;
        var sb = new System.Text.StringBuilder(3);
        for (int i = 0; i < 3; i++) sb.Append(letters[order[i]]);
        return sb.ToString();
    }

    private static string TopSkillLetters(CareerPlayer p)
    {
        int[] q = p.QuantizedSkills();   // passing,shooting,heading,tackling,control,speed,finishing
        return TopSkillLetters(q);
    }
}
