namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Game-sprite per-tick updaters ported from external/swos-port/src/sprites/gameSprites.cpp.
//
// SCOPE: Currently UpdateCornerFlags (gameSprites.cpp:252-279), UpdateControlledPlayerNumbers
// (gameSprites.cpp:281-312), and the two pure-helper face-offset functions
// (GetGoalkeeperSpriteOffset / GetPlayerSpriteOffsetFromFace, gameSprites.cpp:231-250).
//
// EXCLUDED: drawSprites (gameSprites.cpp:188-229), sortDisplaySprites (339-344),
// shouldZoomSprite (346-350), verifySprites (138-184), initDisplaySprites (102-111)
// — all pure render. initGameSprites (69-99) and initializePlayerSpriteFrameIndices
// (113-135) are sprite-bind-setup that the C# renderer owns (it tracks frameOffset
// directly when picking the texture).
//
// Storage for corner flags + controlled-player-num sprites lives in
// Memory.Addr.cornerFlags (24 bytes, 4 sprites × 6 bytes) and
// Memory.Addr.curPlayerNumSprites (16 bytes, 2 sprites × 8 bytes). The renderer
// reads these.
public static class GameSprites
{
    // ---- Sprite ID constants — sprites.h ----------------------------------
    // sprites.h:62-63 — corner flag sprite range.
    public const int kCornerFlagSpriteStart = 1184;
    public const int kCornerFlagSpriteEnd   = 1187;

    // sprites.h:64-65 — small digits (used for shirt-number overlay).
    public const int kSmallDigit1  = 1188;
    public const int kSmallDigit16 = 1203;

    // ---- Corner flag layout ------------------------------------------------
    // gameSprites.cpp:254-257 — coordinate constants.
    public const int kLeftCornerFlagX   = 81;
    public const int kRightCornerFlagX  = 590;
    public const int kTopCornerFlagY    = 129;
    public const int kBottomCornerFlagY = 769;

    // gameSprites.cpp:264-266 — kCornerFlagFrameOffsets. 32-element 0..3 bit-1
    // sliding window. Indexed by `(frameCount >> 1) & 0x1f`.
    private static readonly byte[] kCornerFlagFrameOffsets =
    {
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
    };

    // Per-flag stride within Memory.Addr.cornerFlags. Field order:
    //   +0 word: imageIndex
    //   +2 word: x
    //   +4 word: y
    public const int CornerFlagStride = 6;
    public const int CornerFlagOffImageIndex = 0;
    public const int CornerFlagOffX          = 2;
    public const int CornerFlagOffY          = 4;

    // gameSprites.cpp:252-279 — updateCornerFlags. Per-tick refresh of all
    // four corner-flag sprite positions + image indices. Image index advances
    // by 1 every 8 frames (table is bit-1 sliding window) cycling through
    // the 4-frame waving animation.
    public static void UpdateCornerFlags()
    {
        // gameSprites.cpp:259-262 — corner coordinate table.
        // Order: top-left, top-right, bottom-left, bottom-right (matches
        // kCornerFlagSprites enumeration order in gameSprites.cpp:17-20).
        int[] xs = { kLeftCornerFlagX, kRightCornerFlagX, kLeftCornerFlagX, kRightCornerFlagX };
        int[] ys = { kTopCornerFlagY, kTopCornerFlagY, kBottomCornerFlagY, kBottomCornerFlagY };

        // gameSprites.cpp:274 — frameCount drives the wave. `(frameCount >> 1) & 0x1f`.
        // gameLoop.cpp:269 swos.frameCount++ each tick — we mirror that via
        // Memory.Addr.frameCounter (dword) which GameLoop.UpdateTimers bumps.
        int frameCount = Memory.ReadSignedDword(Memory.Addr.frameCounter);
        int frame = (frameCount >> 1) & 0x1f;
        int imageIndex = kCornerFlagSpriteStart + kCornerFlagFrameOffsets[frame];

        for (int i = 0; i < 4; i++)
        {
            int baseAddr = Memory.Addr.cornerFlags + i * CornerFlagStride;
            Memory.WriteWord(baseAddr + CornerFlagOffX, xs[i]);
            Memory.WriteWord(baseAddr + CornerFlagOffY, ys[i]);
            Memory.WriteWord(baseAddr + CornerFlagOffImageIndex, imageIndex);
        }
    }

    // ---- Controlled-player number sprite layout ----------------------------
    // gameSprites.cpp:283 — kPlayerNumberOfset (sic) = 20. Z-offset above the
    // controlled player so the digit floats above their head.
    public const int kPlayerNumberOffset = 20;

    // Per-sprite stride within Memory.Addr.curPlayerNumSprites.
    //   +0 word: imageIndex (-1 = hidden)
    //   +2 word: x (whole pixels)
    //   +4 word: y
    //   +6 word: z
    public const int CurPlayerNumStride = 8;
    public const int CurPlayerNumOffImageIndex = 0;
    public const int CurPlayerNumOffX          = 2;
    public const int CurPlayerNumOffY          = 4;
    public const int CurPlayerNumOffZ          = 6;

    // gameSprites.cpp:281-312 — updateControlledPlayerNumbers.
    // Per-tick: for each team, decide whether to show the controlled player's
    // shirt-number sprite above their head, and where. Blinking logic:
    //   - top team    (i==0): mark/digit shown alternately when (tick & 0x10)==0
    //   - bottom team (i==1): mark/digit shown alternately when (tick & 0x10)!=0
    // i.e. the two teams blink in opposite phases (16-tick period).
    //
    // Gate (gameSprites.cpp:291-293):
    //   (team.isPlCoach OR team.playerNumber OR g_trainingGame)
    //     AND team.controlledPlayer != null
    //     AND (controlledPlayer.playerOrdinal-1 != teamInfo.markedPlayer
    //          OR !shouldShowMark())
    //
    // When the gate fails or shouldShowMark() applies, we clearImage() (write
    // imageIndex = -1).
    // Debounce for the out-of-range shirt diagnostic below (task #202): only
    // re-log when the offending ordinal changes, so a genuine regression prints
    // once per distinct fault instead of 25×/second.
    private static int s_lastBadShirtOrdinal = -1;

    public static void UpdateControlledPlayerNumbers()
    {
        ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
        short trainingGame = Memory.ReadSignedWord(Memory.Addr.g_trainingGame);

        for (int i = 0; i < 2; i++)
        {
            bool top = (i == 0);
            int teamBase = TeamData.Base(top);
            int numSpriteAddr = Memory.Addr.curPlayerNumSprites + i * CurPlayerNumStride;

            // gameSprites.cpp:286 — auto team = i == 0 ? &swos.topTeamData : &swos.bottomTeamData.
            // gameSprites.cpp:287 — teamInfo = team->inGameTeamPtr.asPtr().
            int controlledPlayerPtr = TeamData.ControlledPlayer(top);
            short playerNumber = Memory.ReadSignedWord(teamBase + TeamData.OffPlayerNumber);
            short isPlCoach    = Memory.ReadSignedWord(teamBase + TeamData.OffIsPlCoach);

            // gameSprites.cpp:290 — shouldShowMark() lambda. For top team
            // (i==0): `0 > 0 == (tick & 0x10 != 0)` → `false == ...` →
            // mark phase when (tick & 0x10) == 0. For bottom team: vice versa.
            bool tickHas16 = (tick & 0x10) != 0;
            bool shouldShowMark = (i > 0) == tickHas16;

            // gameSprites.cpp:291-293 — gate.
            bool gateOk =
                (isPlCoach != 0 || playerNumber != 0 || trainingGame != 0) &&
                controlledPlayerPtr != 0;
            if (!gateOk)
            {
                Memory.WriteWord(numSpriteAddr + CurPlayerNumOffImageIndex, -1);
                continue;
            }

            // controlledPlayer.playerOrdinal (sprite +OffPlayerOrdinal).
            short playerOrdinal = Memory.ReadSignedWord(controlledPlayerPtr + PlayerSprite.OffPlayerOrdinal);

            // gameSprites.cpp:293 — controlledPlayer.playerOrdinal - 1 != teamInfo.markedPlayer
            //                       OR !shouldShowMark()
            // teamInfo (inGameTeam) is the TeamGame struct. markedPlayer is at offset +20.
            // Source: external/swos-port/src/swos/swos.h:296-311 (TeamGame).
            // PORT-LAYOUT: our InGameTeamPtr points at TeamGame.players[0]
            // DIRECTLY (WireTeamFields writes playersBaseAddr), not at the
            // TeamGame struct head like the original. markedPlayer (struct +20,
            // players at +42) therefore sits at base - 22 — the same convention
            // Bench.cs MaintainMarkedPlayer uses. Reading base+20 instead lands
            // inside players[0]'s name bytes (user-visible bug: half the
            // controlled-player head numbers showed "1").
            int inGameTeamPtr = Memory.ReadSignedDword(teamBase + TeamData.OffInGameTeamPtr);
            short markedPlayer = inGameTeamPtr == 0
                ? (short)-1
                : Memory.ReadSignedWord(inGameTeamPtr - 22);
            bool ordinalDiffersFromMarked = (playerOrdinal - 1) != markedPlayer;

            bool drawPlayerNumber = ordinalDiffersFromMarked || !shouldShowMark;

            if (!drawPlayerNumber)
            {
                Memory.WriteWord(numSpriteAddr + CurPlayerNumOffImageIndex, -1);
                continue;
            }

            // gameSprites.cpp:296-298 — shirtNumber from teamInfo.players[ord-1].
            // Original: `auto shirtNumber = teamInfo->players[...].shirtNumber;`
            // teamInfo->players[i] is at +28 + i*61 (TeamGame.players, PlayerInfo[16]).
            // PlayerInfo.shirtNumber is at PlayerInfo offset +3 (swos.h:162-208).
            // Source: external/swos-port/src/swos/swos.h:162-208 (PlayerInfo),
            //         external/swos-port/src/swos/swos.h:296-311 (TeamGame.players).
            int shirtNumber = 1;
            if (inGameTeamPtr != 0 && playerOrdinal >= 1 && playerOrdinal <= 16)
            {
                // PORT-LAYOUT (see above): inGameTeamPtr IS players[0] — no +28
                // TeamGame header offset in our memory image.
                int playerInfoAddr = inGameTeamPtr + (playerOrdinal - 1) * kPlayerInfoSize;
                shirtNumber = Memory.ReadByte(playerInfoAddr + kPlayerInfoOffShirtNumber);

                // gameSprites.cpp:298 uses `assert(imageIndex in kSmallDigit1..16)`
                // — it NEVER silently substitutes a value. Our old code clamped a
                // bad read to 1, which is exactly what made task #202's
                // "controlled outfielder shows shirt 1" invisible: any resolution
                // fault (stale/mismatched inGameTeamPtr vs the controlled sprite's
                // physical pool) rendered a plausible-looking "1" instead of
                // failing loudly. The 2nd-half end-swap
                // (Kickoff.ReseatTeamsForNewHalf) swaps inGameTeamPtr TOGETHER with
                // the spritesTable, and every controlledPlayer assignment draws
                // from team.spritesTable, so ordinal→roster stays paired in both
                // halves — verified by Main.RunHeadNumberTest (`--headnum-test`):
                // an absolute oracle (sprite physical-pool roster) reports zero
                // mismatches across the scripted swap AND a full 25k-tick match
                // through the real timer-driven half-time, while a fault-injection
                // self-check confirms the oracle fires on a corrupted
                // inGameTeamPtr. So this range guard must NEVER trip in normal
                // play; if it ever does, that's a real regression — surface it
                // (debounced) rather than paint a fake "1".
                if (shirtNumber < 1 || shirtNumber > 16)
                {
                    if (s_lastBadShirtOrdinal != playerOrdinal)
                    {
                        s_lastBadShirtOrdinal = playerOrdinal;
                        Godot.GD.PrintErr(
                            $"[GameSprites] controlled-player shirt out of range: team={i} " +
                            $"ordinal={playerOrdinal} rawShirt={shirtNumber} " +
                            $"inGameTeamPtr=0x{inGameTeamPtr:X} — resolution fault (task #202 class). " +
                            "Rendering nothing this frame instead of a fake '1'.");
                    }
                    // Release-safe: hide the digit rather than draw a wrong number.
                    Memory.WriteWord(numSpriteAddr + CurPlayerNumOffImageIndex, -1);
                    continue;
                }
                s_lastBadShirtOrdinal = -1;
            }

            // gameSprites.cpp:297 — setImage(kSmallDigit1 + shirtNumber - 1).
            int imageIndex = kSmallDigit1 + shirtNumber - 1;
            Memory.WriteWord(numSpriteAddr + CurPlayerNumOffImageIndex, imageIndex);

            // gameSprites.cpp:303-306 — copy controlled player x/y/z (+ Z offset).
            // Production path: full FixedPoint copy. Here we read whole-pixel x/y
            // and z, since the renderer mostly cares about pixel coords.
            short playerX = Memory.ReadSignedWord(controlledPlayerPtr + PlayerSprite.OffX + 2);
            short playerY = Memory.ReadSignedWord(controlledPlayerPtr + PlayerSprite.OffY + 2);
            short playerZ = Memory.ReadSignedWord(controlledPlayerPtr + PlayerSprite.OffZ + 2);
            Memory.WriteWord(numSpriteAddr + CurPlayerNumOffX, playerX);
            Memory.WriteWord(numSpriteAddr + CurPlayerNumOffY, playerY);
            Memory.WriteWord(numSpriteAddr + CurPlayerNumOffZ, playerZ + kPlayerNumberOffset);
        }
    }

    // ---- Face-to-sprite-offset helpers -------------------------------------
    // gameSprites.cpp:231-241 — getGoalkeeperSpriteOffset(bool topTeam, int face).
    // Both top and bottom teams have a 58-sprite goalkeeper range (start_to_end
    // delta = 1004-947+1 = 58); 2 goalkeepers per team × kNumGoalkeeperSprites
    // gives the offset. face ∈ {0,1,2,3}; goalie selection from face is via
    // getGoalkeeperIndexFromFace (the C++ helper). For OpenSWOS we keep this
    // available so the renderer can compute frameOffset symmetrically.
    //
    // kNumGoalkeeperSprites is (kTeam1ReserveGoalkeeperSpriteStart -
    //   kTeam1MainGoalkeeperSpriteStart) = 1005 - 947 = 58.
    public const int kNumGoalkeeperSprites = 58;

    // gameSprites.cpp:243-250 — getPlayerSpriteOffsetFromFace(int face).
    //   return face * (kTeam1GingerPlayerSpriteStart - kTeam1WhitePlayerSpriteStart);
    //   kNumPlayerSprites = 442 - 341 = 101.
    public const int kNumPlayerSprites = 101;

    public static int GetPlayerSpriteOffsetFromFace(int face)
    {
        // gameSprites.cpp:247 — assert(face >= 0 && face <= 3).
        if (face < 0) face = 0;
        if (face > 3) face = 3;
        return face * kNumPlayerSprites;
    }

    // gameSprites.cpp:231-241 — main entry for keepers. Mirror of getGoalkeeperIndexFromFace
    // not yet ported; for now mechanical-port the per-team mapping that the
    // C++ uses: pick `goalie ∈ {0,1}` from face directly (face / 2). The full
    // version is in colorizeSprites.cpp:getGoalkeeperIndexFromFace.
    public static int GetGoalkeeperSpriteOffset(bool topTeam, int face)
    {
        if (face < 0) face = 0;
        if (face > 3) face = 3;
        // colorizeSprites.cpp: goalie 0 = main, goalie 1 = reserve. Default
        // selection by face / 2 — exact RE TBD when colorizeSprites lands.
        int goalie = face / 2;
        return goalie * kNumGoalkeeperSprites;
    }

    // ---- Renderer accessors -----------------------------------------------
    public static (int x, int y, int imageIndex) GetCornerFlag(int index)
    {
        if (index < 0 || index > 3) return (0, 0, 0);
        int baseAddr = Memory.Addr.cornerFlags + index * CornerFlagStride;
        return (Memory.ReadSignedWord(baseAddr + CornerFlagOffX),
                Memory.ReadSignedWord(baseAddr + CornerFlagOffY),
                Memory.ReadSignedWord(baseAddr + CornerFlagOffImageIndex));
    }

    public static (int x, int y, int z, int imageIndex) GetCurPlayerNumSprite(int teamIndex)
    {
        if (teamIndex < 0 || teamIndex > 1) return (0, 0, 0, -1);
        int baseAddr = Memory.Addr.curPlayerNumSprites + teamIndex * CurPlayerNumStride;
        return (Memory.ReadSignedWord(baseAddr + CurPlayerNumOffX),
                Memory.ReadSignedWord(baseAddr + CurPlayerNumOffY),
                Memory.ReadSignedWord(baseAddr + CurPlayerNumOffZ),
                Memory.ReadSignedWord(baseAddr + CurPlayerNumOffImageIndex));
    }

    // ---- TeamGame / PlayerInfo struct offsets -----------------------------
    // Source: external/swos-port/src/swos/swos.h:296-315 (TeamGame).
    //   markedPlayer (word) is at +20 (after 10 prShirt + 10 secShirt words).
    //   players[16] PlayerInfo array starts at +28 (after markedPlayer+teamName).
    private const int kTeamGameOffMarkedPlayer = 20;
    private const int kTeamGameOffPlayers      = 28;
    private const int kPlayerInfoSize          = 61;
    private const int kPlayerInfoOffShirtNumber = 3;
}
