namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Mechanical port of external/swos-port/src/game/pitch/pitch.cpp:222-284.
//
// Two pre-match functions decide:
//   * setPitchType()   — pitch *condition* (Frozen..Hard, 0..6). Drives the
//                        ball-physics tables (initPitchBallFactors picks the
//                        row from pitchType — see GameTime.InitPitchBallFactors).
//   * setPitchNumber() — pitch *variant* (0..3 = SWCPICH{1..4}.MAP). Pure
//                        visual selection — no gameplay coupling.
//
// Both are called via initMatch → setPitchTypeAndNumber (game.cpp:65), so they
// must run before InitPitchBallFactors at match start.
//
// Inputs:
//   swos.gamePitchTypeOrSeason — when non-zero, pick from a seasonal probability
//      table; when zero AND gamePitchType==-1, pick from a fixed probability
//      table; otherwise honour gamePitchType verbatim.
//   swos.gameSeason            — 0..11 month index into the seasonal table.
//   swos.gamePitchType         — manual override (-1 = random).
//   swos.plg_D0_param          — when non-zero, friendlies path: pure RNG-driven
//                                pitch variant. Zero = career path: deterministic
//                                hash of (topTeamName[0..1], shirtCol, shortsCol).
//   swos.g_trainingGame        — training pins variant to kTrainingPitchIndex (5).
//
// Outputs:
//   BallSim.CurrentPitchType   — 0..6 row index for the ball/bounce tables.
//   BallSim.CurrentPitchNumber — 0..3 visual variant.
//
// OpenSWOS does not yet surface gamePitchType / gameSeason / plg_D0_param via
// the pre-match menu — Main.cs currently picks the pitch variant directly. This
// port keeps the deterministic path live (training and "no override" callers
// both hit it) and stubs the rest until a season selector lands.
//
// License rule (CLAUDE.md): paraphrase algorithms, copy constants. The tables
// below are byte-for-byte copies of the C++ literals at pitch.cpp:226-240,261.
public static class Pitch
{
    // pitch.cpp:23-33 — PitchTypes enum.
    public const int kRandom      = -1;
    public const int kFrozen      = 0;
    public const int kMuddy       = 1;
    public const int kWet         = 2;
    public const int kSoft        = 3;
    public const int kNormal      = 4;
    public const int kDry         = 5;
    public const int kHard        = 6;
    public const int kMaxPitchType = 6;

    // pitch.cpp:266 (referenced symbol; numeric value comes from
    // pitchConstants.h — kTrainingPitchIndex = 5). Used only for training games.
    public const int kTrainingPitchIndex = 5;

    // pitch.cpp:226-239 — 12 × 7 byte matrix. Rows are months (0=Jan), columns
    // are pitch-type probabilities (sum = 100 per row).
    public static readonly byte[,] kPitchTypeSeasonalProbabilities = new byte[12, 7]
    {
        { 30, 20, 30, 20,  0,  0,  0 },
        { 20, 30, 20, 20, 10,  0,  0 },
        { 10, 30, 10, 30, 20,  0,  0 },
        {  0, 10, 10, 30, 40, 10,  0 },
        {  0,  0,  0, 10, 40, 40, 10 },
        {  0,  0,  0,  0, 40, 40, 20 },
        {  0,  0,  0,  0, 30, 30, 40 },
        {  0,  0,  0,  0, 50, 30, 20 },
        {  0,  0,  0, 20, 40, 30, 10 },
        {  0, 20,  0, 40, 30, 10,  0 },
        { 10, 30, 10, 40, 10,  0,  0 },
        { 20, 30, 20, 30,  0,  0,  0 },
    };

    // pitch.cpp:240 — single fallback row used when gamePitchTypeOrSeason==0
    // AND gamePitchType==-1 (i.e. friendly with random condition).
    public static readonly byte[] kPitchTypeProbabilities = { 5, 5, 10, 20, 30, 20, 10 };

    // pitch.cpp:261-263 — 16-entry table indexed by the 4 low bits of either an
    // RNG byte (friendlies) or the team-name hash (career). Maps to variants 0..4
    // with weighted distribution.
    public static readonly byte[] kPitchNumberProbabilities =
        { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 4, 4, 2, 2, 3, 3 };

    // pitch.cpp:222-257 — setPitchType.
    //
    // Three branches:
    //   1. gamePitchTypeOrSeason != 0 → seasonal table indexed by gameSeason.
    //   2. gamePitchTypeOrSeason == 0 AND gamePitchType == -1 → fixed table.
    //   3. Otherwise → m_pitchType = gamePitchType (honour the override).
    //
    // Branches 1 & 2 advance the RNG by ONE byte and walk the probability table
    // until the rolling 0..99 sample is consumed. We preserve the exact RNG
    // cadence — single Rng.NextByte() per call — for lockstep parity.
    //
    // Writes BallSim.CurrentPitchType. GameTime.InitPitchBallFactors reads this
    // immediately afterwards.
    public static void SetPitchType()
    {
        // Read the three input flags. OpenSWOS doesn't yet wire pre-match menu
        // options to these addresses, so they default to 0 / 0 / -1 — falling
        // into branch 2 (random fixed-table pick). Once the menu lands, the
        // surface can write Memory.Addr.gamePitchTypeOrSeason etc. directly.
        short typeOrSeason = Memory.ReadSignedWord(Memory.Addr.gamePitchTypeOrSeason);
        short manualType   = Memory.ReadSignedWord(Memory.Addr.gamePitchType);
        short season       = Memory.ReadSignedWord(Memory.Addr.gameSeason);

        // pitch.cpp:242 — m_pitchType = 0; ensures a known default before walking.
        int pitchType = 0;

        // pitch.cpp:244 — gamePitchTypeOrSeason != 0 || gamePitchType == -1.
        if (typeOrSeason != 0 || manualType == -1)
        {
            // pitch.cpp:245-246 — pick the probability row.
            // Seasonal lookup needs a clamped season (0..11) — the matrix has
            // exactly 12 rows; out-of-range season would read past the end.
            if (typeOrSeason != 0)
            {
                if (season < 0) season = 0;
                if (season > 11) season = 11;
            }

            // pitch.cpp:247 — probability = SWOS::rand() * 100 / 256.
            // (Rng.NextByte() returns 0..255; the *100/256 maps to 0..99.)
            int probability = Rng.NextByte() * 100 / 256;

            // pitch.cpp:248-251 — while (probability >= *probabilities) {
            //     probability -= *probabilities++; m_pitchType++; }
            // The original walks a pointer; we walk the 2-D table by column.
            for (;;)
            {
                int weight = typeOrSeason != 0
                    ? kPitchTypeSeasonalProbabilities[season, pitchType]
                    : kPitchTypeProbabilities[pitchType];
                if (probability < weight) break;
                probability -= weight;
                pitchType++;
                // Safety: rows sum to 100 so we always break before the end
                // (assert pitch.cpp:256). Defensive clamp protects against a
                // hand-edited probability table that doesn't sum to 100.
                if (pitchType >= kMaxPitchType) { pitchType = kMaxPitchType; break; }
            }
        }
        else
        {
            // pitch.cpp:252-254 — honour the manual selection. Clamp to the
            // valid range (kRandom..kMaxPitchType) before publishing.
            pitchType = manualType;
            if (pitchType < 0)               pitchType = 0;
            if (pitchType > kMaxPitchType)   pitchType = kMaxPitchType;
        }

        // pitch.cpp:256 — assert(m_pitchType >= kRandom && m_pitchType <= kMaxPitchType).
        BallSim.CurrentPitchType = pitchType;
    }

    // pitch.cpp:259-284 — setPitchNumber.
    //
    // Pitch *variant* (0..3 = SWCPICH{1..4}.MAP):
    //   * Training: pinned to kTrainingPitchIndex.
    //   * Friendly (plg_D0_param != 0): random — RNG byte's low nibble.
    //   * Career (plg_D0_param == 0): deterministic hash of the top team's
    //     name and primary kit colours, so the same pairing always plays on
    //     the same variant. (XOR of teamName[0], teamName[1], prShirtCol,
    //     prShortsCol — low 4 bits index kPitchNumberProbabilities.)
    //
    // Writes BallSim.CurrentPitchNumber. Read by the host renderer (Main.cs)
    // when loading the pitch atlas; netcode-safe because the inputs are all
    // mirrored in shared memory + the RNG is deterministic.
    public static void SetPitchNumber()
    {
        // pitch.cpp:265 — training case wins outright.
        if (Memory.ReadSignedWord(Memory.Addr.g_trainingGame) != 0)
        {
            BallSim.CurrentPitchNumber = kTrainingPitchIndex;
            return;
        }

        // pitch.cpp:269 — plg_D0_param chooses between random and deterministic.
        short plgD0 = Memory.ReadSignedWord(Memory.Addr.plg_D0_param);

        int index;
        if (plgD0 != 0)
        {
            // pitch.cpp:270-271 — friendly: index = SWOS::rand() & 0xf.
            // Single RNG byte; only the low nibble matters but we still
            // advance the stream by 1 (matches C++).
            index = Rng.NextByte() & 0xF;
        }
        else
        {
            // pitch.cpp:273-277 — career: hash of (teamName[0..1], shirtCol, shortsCol).
            // Top team's TeamGame block starts at team1InGameTeamHeader.
            // swos.h:296-311 — TeamGame layout:
            //   +0..+9   prShirtType, prShirtCol, prStripesCol, prShortsCol, prSocksCol (5 × word)
            //   +22..+38 teamName[17]
            // (See Result.cs:345-350 for the verified offset table.)
            const int kOffPrShirtCol  = 2;   // swos.h:298
            const int kOffPrShortsCol = 6;   // swos.h:300
            const int kOffTeamName    = 22;  // swos.h:308

            int headerBase = Memory.Addr.team1InGameTeamHeader;
            byte name0      = Memory.ReadByte(headerBase + kOffTeamName + 0);
            byte name1      = Memory.ReadByte(headerBase + kOffTeamName + 1);
            short shirtCol  = Memory.ReadSignedWord(headerBase + kOffPrShirtCol);
            short shortsCol = Memory.ReadSignedWord(headerBase + kOffPrShortsCol);

            // pitch.cpp:274 — index = teamName[0] | (teamName[1] << 8).
            index = name0 | (name1 << 8);
            // pitch.cpp:275-276 — XOR with the kit colours.
            index ^= shirtCol;
            index ^= shortsCol;
            // pitch.cpp:277 — &= 0xf.
            index &= 0xF;
        }

        // pitch.cpp:280 — kPitchNumberProbabilities[16] lookup.
        BallSim.CurrentPitchNumber = kPitchNumberProbabilities[index];
    }

    // pitch.cpp:58-62 — setPitchTypeAndNumber. Pure wrapper.
    public static void SetPitchTypeAndNumber()
    {
        SetPitchType();
        SetPitchNumber();
    }

    // pitch.cpp:64-67 / 69-72 — getPitchType / getPitchNumber. Expose the
    // BallSim fields for parity with the swos-port API. Used by
    // initPitchBallFactors and the host renderer respectively.
    public static int GetPitchType()   => BallSim.CurrentPitchType;
    public static int GetPitchNumber() => BallSim.CurrentPitchNumber;
}
