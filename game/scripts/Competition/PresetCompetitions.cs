namespace OpenSwos.Competition;

// ============================================================================
// PresetCompetitions — PURE DATA table of preset competitions modelled on the
// original's PRESET COMPETITION list (PresetCompetitionMenu, swos.asm:23781).
// The originals are .tmd competition files (eurocup/eurocwc/uefacup loaded at
// swos.asm:38311-38314 into the euroCup/cupWinnersCup/uefaCup structures,
// swos.asm:187107-187126; worldCup swos.asm:187239/187127;
// europeanChampionships swos.asm:188256; copaAmerica swos.asm:188659) whose
// fields reference concrete team ids. We approximate those fields with famous
// mid-90s sides resolvable BY NAME against our 1730-team master list.
//
// NOTE: team-name resolution + fallback happens MENU-side — the menu matches
// TeamNames against the master list (national sides only when NationalTeams
// is set), takes the first Size hits and random-fills anything unresolved.
// Lists deliberately carry a few spare names beyond Size so a failed match
// still yields a themed field. All names below were verified verbatim against
// the SWOS 96/97 PC TEAM.* data.
// ============================================================================

public sealed class PresetCompetition
{
    public string Name = "";                 // "EUROPEAN CUP"
    public CompetitionKind Kind;             // Cup or Tournament
    public int Size;                         // cup: 4/8/16/32; tournament: groups*4
    public int GroupCount;                   // tournament only (2 or 4), else 0
    public bool NationalTeams;               // resolve names against national sides
    public string[] TeamNames = System.Array.Empty<string>();  // preferred entrants, uppercase
}

public static class PresetCompetitions
{
    public static readonly PresetCompetition[] All =
    {
        // ------------------------------------------------------------------
        // EUROPEAN CUP — continental champions flavour.
        // Original: euroCup structure swos.asm:187107, eurocup .tmd
        // swos.asm:38311, listed in PresetCompetitionMenu swos.asm:23781.
        // ------------------------------------------------------------------
        new PresetCompetition
        {
            Name = "EUROPEAN CUP",
            Kind = CompetitionKind.Cup,
            Size = 16,
            GroupCount = 0,
            NationalTeams = false,
            TeamNames = new[]
            {
                "AC MILAN", "JUVENTUS", "AJAX", "BAYERN MUNICH",
                "BOR. DORTMUND", "REAL MADRID", "MANCHESTER UTD", "FC PORTO",
                "PSV", "RANGERS", "CELTIC", "PANATHINAIKOS",
                "GALATASARAY", "SPARTAK MOSCOW", "DINAMO KIEV", "STEAUA BUCHAREST",
                // spares
                "RED STAR B'GRADE", "ROSENBORG", "BRONDBY", "FERENCVAROS",
            },
        },

        // ------------------------------------------------------------------
        // UEFA CUP — broad European league field.
        // Original: uefaCup structure swos.asm:187126, uefacup .tmd
        // swos.asm:38314, listed in PresetCompetitionMenu swos.asm:23781.
        // ------------------------------------------------------------------
        new PresetCompetition
        {
            Name = "UEFA CUP",
            Kind = CompetitionKind.Cup,
            Size = 32,
            GroupCount = 0,
            NationalTeams = false,
            TeamNames = new[]
            {
                "INTER MILAN", "PARMA", "NAPOLI", "SAMPDORIA",
                "ARSENAL", "NEWCASTLE UNITED", "TOTTENHAM", "ASTON VILLA",
                "LEEDS", "ATLETICO MADRID", "VALENCIA", "SEVILLA",
                "ZARAGOZA", "DEP. LA CORUNA", "ATHLETIC BILBAO", "MONACO",
                "AUXERRE", "BORDEAUX", "NANTES", "MARSEILLE",
                "KAISERSLAUTERN", "BAYER LEVERKUSEN", "SCHALKE", "SPORTING LISBON",
                "OLYMPIAKOS", "FENERBAHCE", "SLAVIA PRAGUE", "CROATIA ZAGREB",
                "BRONDBY", "MALMO", "LOKOMOTIV MOSCOW", "FERENCVAROS",
                // spares
                "ESPANYOL", "BETIS", "NOTT'M FOREST", "AUSTRIA VIENNA",
            },
        },

        // ------------------------------------------------------------------
        // CUP WINNERS CUP — domestic cup winners flavour (distinct from the
        // EUROPEAN CUP entrants where possible).
        // Original: cupWinnersCup structure (swos.asm:187107-187126 block),
        // eurocwc .tmd swos.asm:38311-38314, PresetCompetitionMenu
        // swos.asm:23781.
        // ------------------------------------------------------------------
        new PresetCompetition
        {
            Name = "CUP WINNERS CUP",
            Kind = CompetitionKind.Cup,
            Size = 16,
            GroupCount = 0,
            NationalTeams = false,
            TeamNames = new[]
            {
                "BARCELONA", "PARIS ST-GERMAIN", "LAZIO", "FIORENTINA",
                "LIVERPOOL", "BENFICA", "FEYENOORD", "WERDER BREMEN",
                "ANDERLECHT", "CLUB BRUGES", "SPARTA PRAGUE", "LEGIA WARSAW",
                "BESIKTAS", "AEK ATHENS", "IFK GOTHENBURG", "RAPID VIENNA",
                // spares
                "HAJDUK SPLIT", "MALMO", "AUSTRIA VIENNA", "LOKOMOTIV MOSCOW",
            },
        },

        // ------------------------------------------------------------------
        // WORLD CUP — 4 groups of 4, national sides (USA 94 flavour).
        // Original: worldCup structure swos.asm:187239 (group data 187127),
        // listed in PresetCompetitionMenu swos.asm:23781.
        // ------------------------------------------------------------------
        new PresetCompetition
        {
            Name = "WORLD CUP",
            Kind = CompetitionKind.Tournament,
            Size = 16,
            GroupCount = 4,
            NationalTeams = true,
            TeamNames = new[]
            {
                "BRAZIL", "GERMANY", "ITALY", "ARGENTINA",
                "HOLLAND", "FRANCE", "SPAIN", "ENGLAND",
                "ROMANIA", "BULGARIA", "SWEDEN", "COLOMBIA",
                "USA", "NIGERIA", "MEXICO", "RUSSIA",
                // spares
                "CAMEROON", "MOROCCO", "BELGIUM", "SOUTH KOREA",
            },
        },

        // ------------------------------------------------------------------
        // EUROPEAN CHAMPIONSHIP — 4 groups of 4, national sides (the Euro 96
        // field). Original: europeanChampionships swos.asm:188256, listed in
        // PresetCompetitionMenu swos.asm:23781.
        // ------------------------------------------------------------------
        new PresetCompetition
        {
            Name = "EUROPEAN CHAMPIONSHIP",
            Kind = CompetitionKind.Tournament,
            Size = 16,
            GroupCount = 4,
            NationalTeams = true,
            TeamNames = new[]
            {
                "GERMANY", "CZECH REPUBLIC", "ENGLAND", "FRANCE",
                "HOLLAND", "ITALY", "SPAIN", "PORTUGAL",
                "DENMARK", "CROATIA", "BULGARIA", "ROMANIA",
                "RUSSIA", "SCOTLAND", "SWITZERLAND", "TURKEY",
                // spares
                "SWEDEN", "NORWAY", "BELGIUM", "GREECE",
            },
        },

        // ------------------------------------------------------------------
        // COPA AMERICA — 2 groups of 4, South American national sides.
        // Original: copaAmerica swos.asm:188659, listed in
        // PresetCompetitionMenu swos.asm:23781.
        // ------------------------------------------------------------------
        new PresetCompetition
        {
            Name = "COPA AMERICA",
            Kind = CompetitionKind.Tournament,
            Size = 8,
            GroupCount = 2,
            NationalTeams = true,
            TeamNames = new[]
            {
                "BRAZIL", "ARGENTINA", "URUGUAY", "COLOMBIA",
                "CHILE", "PARAGUAY", "ECUADOR", "PERU",
                // spares (CONMEBOL rest + traditional invitees)
                "BOLIVIA", "VENEZUELA", "MEXICO", "USA",
            },
        },
    };
}
