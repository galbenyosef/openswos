namespace OpenSwos.Assets;

// Nationality table for the PLAYER-record nationality byte (player struct +0x00),
// which uses SWOS's OWN 153-entry player-nation numbering (0=ALB .. 152=CUS).
// This is DISTINCT from the TEAM.* file-suffix numbering encoded in
// NationNames.cs (there 18=REPUBLIC IRELAND, 20=ITALY); here 18=ITA, 46=IRL,
// 70=BRA, 12=FRA, 40=ESP, 27=POR. Consumers of PlayerRecord.Nationality must
// index THIS table, not NationNames.
//
// Sources (read both; constants copied, layout paraphrased — see CLAUDE.md):
//   - index -> 3-letter code list (0..152):
//       external/swos-port/tools/swospp/doc/teams.txt:109-161
//   - authoritative 3-letter spellings (shortCountryNames, 153 x 3 chars):
//       external/swos-port/swos/swos.asm:186797-186806
//     Where the two disagree, the asm spelling wins: 45=SUI (not SWI),
//     77=VNZ (not VEN), 139=CHI (not CHN).
// Full names are derived sensibly from the codes; continents are assigned by
// geography (needed by RegenModel youth-pool grouping). A handful of very
// obscure slots (55 BHM, 66 ELS, 67 SVC, 114 SRL) are best-effort — they carry
// no real players or known ages, so their exact naming never affects matching.
public static class PlayerNationNames
{
    // Continent codes match NationNames.cs: 0=EUROPE, 1=AFRICA,
    // 2=SOUTH AMERICA, 3=NORTH AMERICA, 4=ASIA, 5=OCEANIA.
    private const byte EUR = 0, AFR = 1, SAM = 2, NAM = 3, ASI = 4, OCE = 5;

    private static readonly string[] kContinents =
    {
        "EUROPE", "AFRICA", "SOUTH AMERICA", "NORTH AMERICA", "ASIA", "OCEANIA",
    };

    // Custom-team slot: excluded from "usable" nationality pools.
    public const int CustomIndex = 152;

    // Index = player-record nationality byte (0..152).
    private static readonly (string Code, string FullName, byte Continent)[] kNations =
    {
        ("ALB", "ALBANIA", EUR),              //   0
        ("AUT", "AUSTRIA", EUR),              //   1
        ("BEL", "BELGIUM", EUR),              //   2
        ("BUL", "BULGARIA", EUR),             //   3
        ("CRO", "CROATIA", EUR),              //   4
        ("CYP", "CYPRUS", EUR),               //   5
        ("TCH", "CZECH REPUBLIC", EUR),       //   6
        ("DEN", "DENMARK", EUR),              //   7
        ("ENG", "ENGLAND", EUR),              //   8
        ("EST", "ESTONIA", EUR),              //   9
        ("FAR", "FAROE ISLANDS", EUR),        //  10
        ("FIN", "FINLAND", EUR),              //  11
        ("FRA", "FRANCE", EUR),               //  12
        ("GER", "GERMANY", EUR),              //  13
        ("GRE", "GREECE", EUR),               //  14
        ("HUN", "HUNGARY", EUR),              //  15
        ("ISL", "ICELAND", EUR),              //  16
        ("ISR", "ISRAEL", EUR),               //  17
        ("ITA", "ITALY", EUR),                //  18
        ("LAT", "LATVIA", EUR),               //  19
        ("LIT", "LITHUANIA", EUR),            //  20
        ("LUX", "LUXEMBOURG", EUR),           //  21
        ("MLT", "MALTA", EUR),                //  22
        ("HOL", "HOLLAND", EUR),              //  23
        ("NIR", "NORTHERN IRELAND", EUR),     //  24
        ("NOR", "NORWAY", EUR),               //  25
        ("POL", "POLAND", EUR),               //  26
        ("POR", "PORTUGAL", EUR),             //  27
        ("ROM", "ROMANIA", EUR),              //  28
        ("RUS", "RUSSIA", EUR),               //  29
        ("SMR", "SAN MARINO", EUR),           //  30
        ("SCO", "SCOTLAND", EUR),             //  31
        ("SLO", "SLOVENIA", EUR),             //  32
        ("SWE", "SWEDEN", EUR),               //  33
        ("TUR", "TURKEY", EUR),               //  34
        ("UKR", "UKRAINE", EUR),              //  35
        ("WAL", "WALES", EUR),                //  36
        ("YUG", "YUGOSLAVIA", EUR),           //  37
        ("BLS", "BELARUS", EUR),              //  38
        ("SVK", "SLOVAKIA", EUR),             //  39
        ("ESP", "SPAIN", EUR),                //  40
        ("ARM", "ARMENIA", EUR),              //  41
        ("BOS", "BOSNIA", EUR),               //  42
        ("AZB", "AZERBAIJAN", EUR),           //  43
        ("GEO", "GEORGIA", EUR),              //  44
        ("SUI", "SWITZERLAND", EUR),          //  45
        ("IRL", "REPUBLIC IRELAND", EUR),     //  46
        ("MAC", "MACEDONIA", EUR),            //  47
        ("TRK", "TURKMENISTAN", ASI),         //  48
        ("LIE", "LIECHTENSTEIN", EUR),        //  49
        ("MOL", "MOLDOVA", EUR),              //  50
        ("CRC", "COSTA RICA", NAM),           //  51
        ("SAL", "EL SALVADOR", NAM),          //  52
        ("GUA", "GUATEMALA", NAM),            //  53
        ("HON", "HONDURAS", NAM),             //  54
        ("BHM", "BAHRAIN", ASI),              //  55
        ("MEX", "MEXICO", NAM),               //  56
        ("PAN", "PANAMA", NAM),               //  57
        ("USA", "U.S.A.", NAM),               //  58
        ("BAH", "BAHAMAS", NAM),              //  59
        ("NIC", "NICARAGUA", NAM),            //  60
        ("BER", "BERMUDA", NAM),              //  61
        ("JAM", "JAMAICA", NAM),              //  62
        ("TRI", "TRINIDAD & TOBAGO", NAM),    //  63
        ("CAN", "CANADA", NAM),               //  64
        ("BAR", "BARBADOS", NAM),             //  65
        ("ELS", "BELIZE", NAM),               //  66
        ("SVC", "ST VINCENT", NAM),           //  67
        ("ARG", "ARGENTINA", SAM),            //  68
        ("BOL", "BOLIVIA", SAM),              //  69
        ("BRA", "BRAZIL", SAM),               //  70
        ("CHL", "CHILE", SAM),                //  71
        ("COL", "COLOMBIA", SAM),             //  72
        ("ECU", "ECUADOR", SAM),              //  73
        ("PAR", "PARAGUAY", SAM),             //  74
        ("SUR", "SURINAM", SAM),              //  75
        ("URU", "URUGUAY", SAM),              //  76
        ("VNZ", "VENEZUELA", SAM),            //  77
        ("GUY", "GUYANA", SAM),               //  78
        ("PER", "PERU", SAM),                 //  79
        ("ALG", "ALGERIA", AFR),              //  80
        ("SAF", "SOUTH AFRICA", AFR),         //  81
        ("BOT", "BOTSWANA", AFR),             //  82
        ("BFS", "BURKINA FASO", AFR),         //  83
        ("BUR", "BURUNDI", AFR),              //  84
        ("LES", "LESOTHO", AFR),              //  85
        ("ZAI", "ZAIRE", AFR),                //  86
        ("ZAM", "ZAMBIA", AFR),               //  87
        ("GHA", "GHANA", AFR),                //  88
        ("SEN", "SENEGAL", AFR),              //  89
        ("CIV", "IVORY COAST", AFR),          //  90
        ("TUN", "TUNISIA", AFR),              //  91
        ("MLI", "MALI", AFR),                 //  92
        ("MDG", "MADAGASCAR", AFR),           //  93
        ("CMR", "CAMEROON", AFR),             //  94
        ("CHD", "CHAD", AFR),                 //  95
        ("UGA", "UGANDA", AFR),               //  96
        ("LIB", "LIBYA", AFR),                //  97
        ("MOZ", "MOZAMBIQUE", AFR),           //  98
        ("KEN", "KENYA", AFR),                //  99
        ("SUD", "SUDAN", AFR),                // 100
        ("SWA", "SWAZILAND", AFR),            // 101
        ("ANG", "ANGOLA", AFR),               // 102
        ("TOG", "TOGO", AFR),                 // 103
        ("ZIM", "ZIMBABWE", AFR),             // 104
        ("EGY", "EGYPT", AFR),                // 105
        ("TAN", "TANZANIA", AFR),             // 106
        ("NIG", "NIGERIA", AFR),              // 107
        ("ETH", "ETHIOPIA", AFR),             // 108
        ("GAB", "GABON", AFR),                // 109
        ("SIE", "SIERRA LEONE", AFR),         // 110
        ("BEN", "BENIN", AFR),                // 111
        ("CON", "CONGO", AFR),                // 112
        ("GUI", "GUINEA", AFR),               // 113
        ("SRL", "SEYCHELLES", AFR),           // 114
        ("MAR", "MOROCCO", AFR),              // 115
        ("GAM", "GAMBIA", AFR),               // 116
        ("MLW", "MALAWI", AFR),               // 117
        ("JAP", "JAPAN", ASI),                // 118
        ("TAI", "TAIWAN", ASI),               // 119
        ("IND", "INDIA", ASI),                // 120
        ("BAN", "BANGLADESH", ASI),           // 121
        ("BRU", "BRUNEI", ASI),               // 122
        ("IRA", "IRAQ", ASI),                 // 123
        ("JOR", "JORDAN", ASI),               // 124
        ("SRI", "SRI LANKA", ASI),            // 125
        ("SYR", "SYRIA", ASI),                // 126
        ("KOR", "SOUTH KOREA", ASI),          // 127
        ("IRN", "IRAN", ASI),                 // 128
        ("VIE", "VIETNAM", ASI),              // 129
        ("MLY", "MALAYSIA", ASI),             // 130
        ("SAU", "SAUDI ARABIA", ASI),         // 131
        ("YEM", "YEMEN", ASI),                // 132
        ("KUW", "KUWAIT", ASI),               // 133
        ("LAO", "LAOS", ASI),                 // 134
        ("NKR", "NORTH KOREA", ASI),          // 135
        ("OMA", "OMAN", ASI),                 // 136
        ("PAK", "PAKISTAN", ASI),             // 137
        ("PHI", "PHILIPPINES", ASI),          // 138
        ("CHI", "CHINA", ASI),                // 139
        ("SGP", "SINGAPORE", ASI),            // 140
        ("MAU", "MAURITIUS", AFR),            // 141
        ("MYA", "MYANMAR", ASI),              // 142
        ("PAP", "PAPUA NEW GUINEA", OCE),     // 143
        ("TAD", "TAJIKISTAN", ASI),           // 144
        ("UZB", "UZBEKISTAN", ASI),           // 145
        ("QAT", "QATAR", ASI),                // 146
        ("UAE", "UNITED ARAB EMIRATES", ASI), // 147
        ("AUS", "AUSTRALIA", OCE),            // 148
        ("NZL", "NEW ZEALAND", OCE),          // 149
        ("FIJ", "FIJI", OCE),                 // 150
        ("SOL", "SOLOMON ISLANDS", OCE),      // 151
        ("CUS", "CUSTOM", EUR),               // 152 — custom-team slot
    };

    public static int Count => kNations.Length;

    // 3-letter code for a player-nation index ("NAT n" for out-of-range).
    public static string Code(int nation)
        => nation >= 0 && nation < kNations.Length ? kNations[nation].Code : $"NAT {nation}";

    // Full country name ("NATION n" for out-of-range).
    public static string FullName(int nation)
        => nation >= 0 && nation < kNations.Length ? kNations[nation].FullName : $"NATION {nation}";

    // Continent code (0=EUROPE..5=OCEANIA) for a player-nation index.
    public static int Continent(int nation)
        => nation >= 0 && nation < kNations.Length ? kNations[nation].Continent : 0;

    public static string ContinentName(int c)
        => c >= 0 && c < kContinents.Length ? kContinents[c] : $"CONTINENT {c}";

    // ---------------------------------------------------------------------------
    //  FLAG specs (#223) — a tiny per-nation flag drawn between the shirt number
    //  and the head bust in the career tables. Each nation maps to a simple
    //  geometric pattern plus 2-3 colours (0xRRGGBB), baked ONCE into a ~10x6 px
    //  texture by the menu client. Major nations carry their real colours;
    //  minor ones fall back to a plausible per-continent tricolour so every
    //  nation still shows something recognisable.
    // ---------------------------------------------------------------------------
    public enum FlagPattern : byte
    {
        H3,      // three horizontal stripes A/B/C
        V3,      // three vertical stripes A/B/C
        H2,      // two horizontal stripes A/B
        V2,      // two vertical stripes A/B
        Cross,   // field A, off-centre Nordic cross in B
        Plain,   // solid field A (B ignored)
        Diamond, // field A, central diamond B, small centre disc C (Brazil)
        Disc,    // field A, central disc B (Japan / Korea)
        Canton,  // H2 field A/B with a canton block C in the top-left (US / Chile)
    }

    public readonly record struct FlagSpec(FlagPattern Pattern, uint A, uint B, uint C);

    // Named colours (0xRRGGBB) tuned to read at ~10 px on the dark table panel.
    private const uint WHT = 0xF0F0F0, BLK = 0x181818, RED = 0xD01414, GRN = 0x009628,
                       YEL = 0xF5D000, BLU = 0x1032C0, LBL = 0x74C0EE, ORA = 0xF07800,
                       GLD = 0xE8C020, NAV = 0x0A2A78, SKY = 0x2C9CE0, MRN = 0x8C1818,
                       GRY = 0x808080;

    // Per-continent fallback flags (index by continent code 0..5).
    private static readonly FlagSpec[] kContinentFlags =
    {
        new(FlagPattern.H3, RED, WHT, BLU),   // EUROPE   generic tricolour
        new(FlagPattern.H3, GRN, YEL, RED),   // AFRICA   pan-African
        new(FlagPattern.H3, YEL, BLU, RED),   // S.AMER   generic
        new(FlagPattern.H3, BLU, WHT, RED),   // N.AMER   generic
        new(FlagPattern.H2, RED, WHT, BLU),   // ASIA     generic
        new(FlagPattern.Plain, NAV, NAV, RED),// OCEANIA  blue ensign
    };

    // Explicit real-ish flags, keyed by player-nation index. Anything not listed
    // uses the continent fallback above.
    private static readonly System.Collections.Generic.Dictionary<int, FlagSpec> kFlagOverrides = new()
    {
        // ---- Europe ----
        {  1, new(FlagPattern.H3, RED, WHT, RED) },   // AUT
        {  2, new(FlagPattern.V3, BLK, YEL, RED) },   // BEL
        {  3, new(FlagPattern.H3, WHT, GRN, RED) },   // BUL
        {  4, new(FlagPattern.H3, RED, WHT, BLU) },   // CRO
        {  5, new(FlagPattern.Plain, WHT, WHT, ORA) },// CYP
        {  6, new(FlagPattern.Canton, WHT, RED, BLU) },// TCH
        {  7, new(FlagPattern.Cross, RED, WHT, WHT) },// DEN
        {  8, new(FlagPattern.Cross, WHT, RED, RED) },// ENG
        {  9, new(FlagPattern.H3, BLU, BLK, WHT) },   // EST
        { 11, new(FlagPattern.Cross, WHT, BLU, BLU) },// FIN
        { 12, new(FlagPattern.V3, BLU, WHT, RED) },   // FRA
        { 13, new(FlagPattern.H3, BLK, RED, GLD) },   // GER
        { 14, new(FlagPattern.H2, BLU, WHT, WHT) },   // GRE
        { 15, new(FlagPattern.H3, RED, WHT, GRN) },   // HUN
        { 16, new(FlagPattern.Cross, BLU, RED, WHT) },// ISL
        { 17, new(FlagPattern.H3, WHT, BLU, WHT) },   // ISR
        { 18, new(FlagPattern.V3, GRN, WHT, RED) },   // ITA
        { 19, new(FlagPattern.H3, MRN, WHT, MRN) },   // LAT
        { 20, new(FlagPattern.H3, YEL, GRN, RED) },   // LIT
        { 21, new(FlagPattern.H3, RED, WHT, LBL) },   // LUX
        { 22, new(FlagPattern.V2, WHT, RED, RED) },   // MLT
        { 23, new(FlagPattern.H3, RED, WHT, BLU) },   // HOL
        { 24, new(FlagPattern.Cross, WHT, RED, RED) },// NIR
        { 25, new(FlagPattern.Cross, RED, WHT, BLU) },// NOR
        { 26, new(FlagPattern.H2, WHT, RED, RED) },   // POL
        { 27, new(FlagPattern.V2, GRN, RED, RED) },   // POR
        { 28, new(FlagPattern.V3, BLU, YEL, RED) },   // ROM
        { 29, new(FlagPattern.H3, WHT, BLU, RED) },   // RUS
        { 30, new(FlagPattern.H2, WHT, LBL, LBL) },   // SMR
        { 31, new(FlagPattern.Cross, BLU, WHT, WHT) },// SCO
        { 32, new(FlagPattern.H3, WHT, BLU, RED) },   // SLO
        { 33, new(FlagPattern.Cross, BLU, YEL, YEL) },// SWE
        { 34, new(FlagPattern.Plain, RED, RED, WHT) },// TUR
        { 35, new(FlagPattern.H2, BLU, YEL, YEL) },   // UKR
        { 36, new(FlagPattern.H2, WHT, GRN, GRN) },   // WAL
        { 37, new(FlagPattern.H3, BLU, WHT, RED) },   // YUG
        { 38, new(FlagPattern.H2, RED, GRN, GRN) },   // BLS
        { 39, new(FlagPattern.H3, WHT, BLU, RED) },   // SVK
        { 40, new(FlagPattern.H3, RED, YEL, RED) },   // ESP
        { 41, new(FlagPattern.H3, RED, BLU, ORA) },   // ARM
        { 42, new(FlagPattern.Plain, NAV, NAV, YEL) },// BOS
        { 43, new(FlagPattern.H3, SKY, RED, GRN) },   // AZB
        { 44, new(FlagPattern.Cross, WHT, RED, RED) },// GEO
        { 45, new(FlagPattern.Cross, RED, WHT, WHT) },// SUI
        { 46, new(FlagPattern.V3, GRN, WHT, ORA) },   // IRL
        { 47, new(FlagPattern.Plain, RED, RED, YEL) },// MAC
        { 49, new(FlagPattern.H2, BLU, RED, RED) },   // LIE
        { 50, new(FlagPattern.V3, BLU, YEL, RED) },   // MOL
        // ---- North / Central America ----
        { 51, new(FlagPattern.H3, BLU, WHT, RED) },   // CRC
        { 52, new(FlagPattern.H3, BLU, WHT, BLU) },   // SAL
        { 53, new(FlagPattern.V3, SKY, WHT, SKY) },   // GUA
        { 54, new(FlagPattern.H3, SKY, WHT, SKY) },   // HON
        { 56, new(FlagPattern.V3, GRN, WHT, RED) },   // MEX
        { 57, new(FlagPattern.Canton, WHT, RED, BLU) },// PAN
        { 58, new(FlagPattern.Canton, RED, WHT, NAV) },// USA
        { 59, new(FlagPattern.H3, SKY, YEL, SKY) },   // BAH
        { 60, new(FlagPattern.H3, BLU, WHT, BLU) },   // NIC
        { 61, new(FlagPattern.Plain, RED, RED, WHT) },// BER
        { 62, new(FlagPattern.Cross, GRN, YEL, BLK) },// JAM
        { 63, new(FlagPattern.Plain, RED, RED, WHT) },// TRI
        { 64, new(FlagPattern.V3, RED, WHT, RED) },   // CAN
        { 65, new(FlagPattern.V3, BLU, YEL, BLU) },   // BAR
        { 66, new(FlagPattern.Plain, BLU, BLU, RED) },// ELS/BELIZE
        { 67, new(FlagPattern.V3, BLU, YEL, GRN) },   // SVC
        // ---- South America ----
        { 68, new(FlagPattern.H3, LBL, WHT, LBL) },   // ARG
        { 69, new(FlagPattern.H3, RED, YEL, GRN) },   // BOL
        { 70, new(FlagPattern.Diamond, GRN, YEL, BLU) },// BRA
        { 71, new(FlagPattern.Canton, WHT, RED, BLU) },// CHL
        { 72, new(FlagPattern.H3, YEL, BLU, RED) },   // COL
        { 73, new(FlagPattern.H3, YEL, BLU, RED) },   // ECU
        { 74, new(FlagPattern.H3, RED, WHT, BLU) },   // PAR
        { 75, new(FlagPattern.H3, GRN, RED, GRN) },   // SUR
        { 76, new(FlagPattern.H3, WHT, SKY, WHT) },   // URU
        { 77, new(FlagPattern.H3, YEL, BLU, RED) },   // VNZ
        { 78, new(FlagPattern.Plain, GRN, GRN, YEL) },// GUY
        { 79, new(FlagPattern.V3, RED, WHT, RED) },   // PER
        // ---- Africa ----
        { 80, new(FlagPattern.V2, GRN, WHT, RED) },   // ALG
        { 81, new(FlagPattern.H3, GRN, YEL, BLU) },   // SAF
        { 88, new(FlagPattern.H3, RED, YEL, GRN) },   // GHA
        { 89, new(FlagPattern.V3, GRN, YEL, RED) },   // SEN
        { 90, new(FlagPattern.V3, ORA, WHT, GRN) },   // CIV
        { 91, new(FlagPattern.Plain, RED, RED, WHT) },// TUN
        { 94, new(FlagPattern.V3, GRN, RED, YEL) },   // CMR
        {105, new(FlagPattern.H3, RED, WHT, BLK) },   // EGY
        {107, new(FlagPattern.V3, GRN, WHT, GRN) },   // NIG
        {115, new(FlagPattern.Plain, RED, RED, GRN) },// MAR
        // ---- Asia ----
        {118, new(FlagPattern.Disc, WHT, RED, RED) }, // JAP
        {120, new(FlagPattern.H3, ORA, WHT, GRN) },   // IND
        {123, new(FlagPattern.H3, RED, WHT, BLK) },   // IRA
        {127, new(FlagPattern.Disc, WHT, RED, BLU) }, // KOR
        {128, new(FlagPattern.H3, GRN, WHT, RED) },   // IRN
        {131, new(FlagPattern.Plain, GRN, GRN, WHT) },// SAU
        {139, new(FlagPattern.Plain, RED, RED, YEL) },// CHI
        // ---- Oceania ----
        {148, new(FlagPattern.Plain, NAV, NAV, RED) },// AUS
        {149, new(FlagPattern.Plain, NAV, NAV, RED) },// NZL
        // ---- custom ----
        {152, new(FlagPattern.Plain, GRY, GRY, GRY) },// CUS
    };

    // Flag spec for a player-nation index (continent fallback for the unlisted).
    public static FlagSpec Flag(int nation)
    {
        if (kFlagOverrides.TryGetValue(nation, out FlagSpec spec)) return spec;
        int cont = Continent(nation);
        return kContinentFlags[cont >= 0 && cont < kContinentFlags.Length ? cont : 0];
    }
}
