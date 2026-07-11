namespace OpenSwos.SwosVm;

// Player animation tables — ported from swos-port/swos/swos.asm:218368-219641.
//
// Each `PlayerAnimationTable` is a 130-byte struct read by
// `SetPlayerAnimationTable` (swos.asm:104309) / `SetPlayerAnimationTableAndPictureIndex`
// (player.cpp:3450, PlayerActions.SetPlayerAnimationTableAndPictureIndex):
//
//     +0    word    frameDelay        (== 5 ticks for every table SWOS ships)
//     +2    dword[8]  team1 player frame-indices pointers (one per direction)
//     +34   dword[8]  team2 player frame-indices pointers
//     +66   dword[8]  team1 goalie frame-indices pointers
//     +98   dword[8]  team2 goalie frame-indices pointers
//
// `SetPlayerAnimationTable` (swos.asm:104325) selects an entry with
//   index = (teamNumber - 1 + (ordinal == 1 ? 2 : 0)) * 8 + direction
//          = 0..3 (player or goalie of team1/team2) * 8 + 0..7 (direction)
//
// The dword at `table + 2 + index*4` is then dereferenced as a pointer to a
// stream of word image indices terminated by **-999** (or other negative
// opcodes — see `dseg_13110A` for an exception). The renderer steps through
// the stream one entry per `frameDelay` ticks.
//
// **Port semantics** — we put the 17 anim tables we currently need at fixed
// addresses inside `Memory` (see `Memory.Addr.k*AnimTableAddr` constants) and
// initialise them at boot via `AnimationTablesData.Init()`. Pointer targets
// (the frame-indices streams) live in a contiguous arena starting at
// `Memory.Addr.kFrameIndicesArraysBase`; a tiny bump-allocator inside this
// file lays them out and records their addresses so the table pointers can
// reference them.
//
// **Direction order** (matches swos.asm and PlayerSprite.OffDirection): 0=N
// (up), 1=NE, 2=E (right), 3=SE, 4=S (down), 5=SW, 6=W (left), 7=NW.
//
// Source line numbers are quoted from `external/swos-port/swos/swos.asm`.
public static class AnimationTablesData
{
    // ---- Frame-indices streams (raw image indices from swos.asm) ------------
    // Each is the literal `dw ..., -999` line from swos.asm. Terminator -999
    // means "loop back to start" in the renderer; intermediate negatives are
    // animation opcodes (-3..-50 = pause, -90 = goto stop, etc.) preserved
    // verbatim. For the smoke-test we only need them to be present at the
    // table-pointer targets.
    //
    // ASM-LINE-NUMBERS  – source line in swos-port/swos/swos.asm

    // 218368-218382 — playerRunning* (team1 outfielders, 4-frame walk cycle).
    private static readonly short[] s_PlayerRunningUpTeam1        = { 343, 341, 342, 341, -999 };
    private static readonly short[] s_PlayerRunningDownTeam1      = { 346, 344, 345, 344, -999 };
    private static readonly short[] s_PlayerRunningRightTeam1     = { 349, 347, 348, 347, -999 };
    private static readonly short[] s_PlayerRunningLeftTeam1      = { 352, 350, 351, 350, -999 };
    private static readonly short[] s_PlayerRunningUpRightTeam1   = { 364, 363, 362, 363, -999 };
    private static readonly short[] s_PlayerRunningUpLeftTeam1    = { 361, 360, 359, 360, -999 };
    private static readonly short[] s_PlayerRunningDownRightTeam1 = { 358, 357, 356, 357, -999 };
    private static readonly short[] s_PlayerRunningDownLeftTeam1  = { 355, 354, 353, 354, -999 };

    // 218454-218468 — playerRunning* (team2 outfielders, palette-shifted).
    private static readonly short[] s_PlayerRunningUpTeam2        = { 646, 644, 645, 644, -999 };
    private static readonly short[] s_PlayerRunningDownTeam2      = { 649, 647, 648, 647, -999 };
    private static readonly short[] s_PlayerRunningRightTeam2     = { 652, 650, 651, 650, -999 };
    private static readonly short[] s_PlayerRunningLeftTeam2      = { 655, 653, 654, 653, -999 };
    private static readonly short[] s_PlayerRunningUpRightTeam2   = { 667, 665, 666, 665, -999 };
    private static readonly short[] s_PlayerRunningUpLeftTeam2    = { 664, 662, 663, 662, -999 };
    private static readonly short[] s_PlayerRunningDownRightTeam2 = { 661, 659, 660, 659, -999 };
    private static readonly short[] s_PlayerRunningDownLeftTeam2  = { 658, 656, 657, 656, -999 };

    // 218540-218554 — goalkeeperRunning* (team1 goalie).
    private static readonly short[] s_KeeperRunningUpTeam1        = { 949, 947, 948, 947, -999 };
    private static readonly short[] s_KeeperRunningDownTeam1      = { 952, 950, 951, 950, -999 };
    private static readonly short[] s_KeeperRunningRightTeam1     = { 955, 953, 954, 953, -999 };
    private static readonly short[] s_KeeperRunningLeftTeam1      = { 958, 956, 957, 956, -999 };
    private static readonly short[] s_KeeperRunningUpRightTeam1   = { 970, 969, 968, 969, -999 };
    private static readonly short[] s_KeeperRunningUpLeftTeam1    = { 967, 966, 965, 966, -999 };
    private static readonly short[] s_KeeperRunningDownRightTeam1 = { 964, 963, 962, 963, -999 };
    private static readonly short[] s_KeeperRunningDownLeftTeam1  = { 961, 960, 959, 960, -999 };

    // 218600-218614 — goalkeeperRunning* (team2 goalie).
    private static readonly short[] s_KeeperRunningUpTeam2        = { 1065, 1063, 1064, 1063, -999 };
    private static readonly short[] s_KeeperRunningDownTeam2      = { 1068, 1066, 1067, 1066, -999 };
    private static readonly short[] s_KeeperRunningRightTeam2     = { 1071, 1069, 1070, 1069, -999 };
    private static readonly short[] s_KeeperRunningLeftTeam2      = { 1074, 1072, 1073, 1072, -999 };
    private static readonly short[] s_KeeperRunningUpRightTeam2   = { 1086, 1085, 1084, 1085, -999 };
    private static readonly short[] s_KeeperRunningUpLeftTeam2    = { 1083, 1082, 1081, 1082, -999 };
    private static readonly short[] s_KeeperRunningDownRightTeam2 = { 1080, 1079, 1078, 1079, -999 };
    private static readonly short[] s_KeeperRunningDownLeftTeam2  = { 1077, 1076, 1075, 1076, -999 };

    // 218384-218398 — team1 player standing (1-frame idle pose per direction).
    private static readonly short[] s_StandTeam1Up           = { 341, -999 };
    private static readonly short[] s_StandTeam1UpRight      = { 363, -999 };
    private static readonly short[] s_StandTeam1Right        = { 347, -999 };
    private static readonly short[] s_StandTeam1BottomRight  = { 357, -999 };
    private static readonly short[] s_StandTeam1Bottom       = { 344, -999 };
    private static readonly short[] s_StandTeam1BottomLeft   = { 354, -999 };
    private static readonly short[] s_StandTeam1Left         = { 350, -999 };
    private static readonly short[] s_StandTeam1TopLeft      = { 360, -999 };

    // 218470-218477 — team2 player standing (dseg_1312B2 .. dseg_1312CE in
    // swos.asm). Listed in `playerNormalStandingAnimTable` (lines 218965-218972).
    private static readonly short[] s_StandTeam2Up           = { 644, -999 };
    private static readonly short[] s_StandTeam2UpRight      = { 666, -999 };
    private static readonly short[] s_StandTeam2Right        = { 650, -999 };
    private static readonly short[] s_StandTeam2BottomRight  = { 660, -999 };
    private static readonly short[] s_StandTeam2Bottom       = { 647, -999 };
    private static readonly short[] s_StandTeam2BottomLeft   = { 657, -999 };
    private static readonly short[] s_StandTeam2Left         = { 653, -999 };
    private static readonly short[] s_StandTeam2TopLeft      = { 663, -999 };

    // 218556-218563 — team1 goalie standing (dseg_1314E2..1314FE).
    // Referenced from `playerNormalStandingAnimTable` lines 218973-218980.
    private static readonly short[] s_StandKeeper1Up         = { 947, -999 };
    private static readonly short[] s_StandKeeper1UpRight    = { 969, -999 };
    private static readonly short[] s_StandKeeper1Right      = { 953, -999 };
    private static readonly short[] s_StandKeeper1BottomRight= { 963, -999 };
    private static readonly short[] s_StandKeeper1Bottom     = { 950, -999 };
    private static readonly short[] s_StandKeeper1BottomLeft = { 960, -999 };
    private static readonly short[] s_StandKeeper1Left       = { 956, -999 };
    private static readonly short[] s_StandKeeper1TopLeft    = { 966, -999 };

    // 218616-218623 — team2 goalie standing (dseg_1316E2..1316FE).
    // Referenced from `playerNormalStandingAnimTable` lines 218981-218988.
    private static readonly short[] s_StandKeeper2Up         = { 1063, -999 };
    private static readonly short[] s_StandKeeper2UpRight    = { 1085, -999 };
    private static readonly short[] s_StandKeeper2Right      = { 1069, -999 };
    private static readonly short[] s_StandKeeper2BottomRight= { 1079, -999 };
    private static readonly short[] s_StandKeeper2Bottom     = { 1066, -999 };
    private static readonly short[] s_StandKeeper2BottomLeft = { 1076, -999 };
    private static readonly short[] s_StandKeeper2Left       = { 1072, -999 };
    private static readonly short[] s_StandKeeper2TopLeft    = { 1082, -999 };

    // 218564-218567 — goalie catching ball animation streams (only the goalie
    // entries; outfielders are 0 in this table). Used by both team1 and team2
    // goalies — palette is selected by the renderer from sprite.teamNumber.
    // Pulled from `dseg_131502` / `dseg_131516` (team1) and `dseg_131702` /
    // `dseg_131716` (team2). Each is the same shape: opcode -5 (delay), frames,
    // looping back to standing.
    private static readonly short[] s_KeeperCatch1A = { -5, 999, 1000, 1001, -3, 1000, -2, 999, 947, -101 };
    private static readonly short[] s_KeeperCatch1B = { -5, 1002, 1003, 1004, -3, 1003, -2, 1002, 950, -101 };
    private static readonly short[] s_KeeperCatch2A = { -5, 1115, 1116, 1117, -3, 1116, -2, 1115, 1063, -101 };
    private static readonly short[] s_KeeperCatch2B = { -5, 1118, 1119, 1120, -3, 1119, -2, 1118, 1066, -101 };

    // 218400-218407 — plTacklingAnimTable streams (dseg_1310CE..1310EA, team1).
    private static readonly short[] s_Tackle1Up         = { 395, -999 };
    private static readonly short[] s_Tackle1UpRight    = { 402, -999 };
    private static readonly short[] s_Tackle1Right      = { 398, -999 };
    private static readonly short[] s_Tackle1BottomRight= { 400, -999 };
    private static readonly short[] s_Tackle1Bottom     = { 396, -999 };
    private static readonly short[] s_Tackle1BottomLeft = { 399, -999 };
    private static readonly short[] s_Tackle1Left       = { 397, -999 };
    private static readonly short[] s_Tackle1TopLeft    = { 401, -999 };

    // 218478-218485 — plTacklingAnimTable streams (dseg_1312D2..1312EE, team2).
    private static readonly short[] s_Tackle2Up         = { 698, -999 };
    private static readonly short[] s_Tackle2UpRight    = { 705, -999 };
    private static readonly short[] s_Tackle2Right      = { 701, -999 };
    private static readonly short[] s_Tackle2BottomRight= { 703, -999 };
    private static readonly short[] s_Tackle2Bottom     = { 699, -999 };
    private static readonly short[] s_Tackle2BottomLeft = { 702, -999 };
    private static readonly short[] s_Tackle2Left       = { 700, -999 };
    private static readonly short[] s_Tackle2TopLeft    = { 704, -999 };

    // 218408-218417 — playerTackledAnimTable streams (dseg_1310EE..13110A, team1).
    private static readonly short[] s_Tackled1Up         = { 403, -999 };
    private static readonly short[] s_Tackled1UpRight    = { 410, -999 };
    private static readonly short[] s_Tackled1Right      = { 406, -999 };
    private static readonly short[] s_Tackled1BottomRight= { 408, -999 };
    private static readonly short[] s_Tackled1Bottom     = { 404, -999 };
    private static readonly short[] s_Tackled1BottomLeft = { 407, -999, -50, 417, -999, -50, 418, -999, -50, 419, -999, -50, 420, -999, -50, 424, -999, -50, 423, -999, -50, 422, -999, -50, 421, -999 };
    private static readonly short[] s_Tackled1Left       = { 405, -999 };
    private static readonly short[] s_Tackled1TopLeft    = { 409, -999 };

    // 218486-218495 — playerTackledAnimTable streams (dseg_1312F2..13130E, team2).
    private static readonly short[] s_Tackled2Up         = { 706, -999 };
    private static readonly short[] s_Tackled2UpRight    = { 713, -999 };
    private static readonly short[] s_Tackled2Right      = { 709, -999 };
    private static readonly short[] s_Tackled2BottomRight= { 711, -999 };
    private static readonly short[] s_Tackled2Bottom     = { 707, -999 };
    private static readonly short[] s_Tackled2BottomLeft = { 710, -999, -50, 417, -999, -50, 418, -999, -50, 419, -999, -50, 420, -999, -50, 424, -999, -50, 423, -999, -50, 422, -999, -50, 421, -999 };
    private static readonly short[] s_Tackled2Left       = { 708, -999 };
    private static readonly short[] s_Tackled2TopLeft    = { 712, -999 };

    // 218450-218453 / 218536-218538 — plInjuredAnimTable writhe streams. The
    // injured player rolls/curls on the ground alternating two poses with a
    // 20-tick pause between them (-20 = pause opcode, -999 = terminator). Only
    // TWO variants per team (TopLeft covers dirs 0..3, TopRight dirs 4..7 —
    // swos.asm:219567-219582). Ordinals: team1 411/412 (TopRight) 413/414
    // (TopLeft); team2 714/715 (TopRight) 716/717 (TopLeft). These are the
    // "player on the ground, injured/rolling" poses adjacent to the tackled/
    // fallen block (task #184).
    private static readonly short[] s_Injured1TopLeft    = { 413, -20, 414, -20, -999 };
    private static readonly short[] s_Injured1TopRight   = { 411, -20, 412, -20, -999 };
    private static readonly short[] s_Injured2TopLeft    = { 716, -20, 717, -20, -999 };
    private static readonly short[] s_Injured2TopRight   = { 714, -20, 715, -20, -999 };

    // 218418-218433 — aboutToThrowIn streams (dseg_13113E..1311A0 + 13136E..1313D0).
    private static readonly short[] s_ThrowInReady1Up         = { -90, 372, -20, 371, -20, 372, -104 };
    private static readonly short[] s_ThrowInReady1UpRight    = { -90, 393, -20, 392, -20, 393, -104 };
    private static readonly short[] s_ThrowInReady1Right      = { -90, 378, -20, 377, -20, 378, -104 };
    private static readonly short[] s_ThrowInReady1BottomRight= { -90, 387, -20, 386, -20, 387, -104 };
    private static readonly short[] s_ThrowInReady1Bottom     = { -90, 375, -20, 374, -20, 375, -104 };
    private static readonly short[] s_ThrowInReady1BottomLeft = { -90, 384, -20, 383, -20, 384, -104 };
    private static readonly short[] s_ThrowInReady1Left       = { -90, 381, -20, 380, -20, 381, -104 };
    private static readonly short[] s_ThrowInReady1TopLeft    = { -90, 390, -20, 389, -20, 390, -104 };

    private static readonly short[] s_ThrowInReady2Up         = { -90, 675, -20, 674, -20, 675, -104 };
    private static readonly short[] s_ThrowInReady2UpRight    = { -90, 696, -20, 695, -20, 696, -104 };
    private static readonly short[] s_ThrowInReady2Right      = { -90, 681, -20, 680, -20, 681, -104 };
    private static readonly short[] s_ThrowInReady2BottomRight= { -90, 690, -20, 689, -20, 690, -104 };
    private static readonly short[] s_ThrowInReady2Bottom     = { -90, 678, -20, 677, -20, 678, -104 };
    private static readonly short[] s_ThrowInReady2BottomLeft = { -90, 687, -20, 686, -20, 687, -104 };
    private static readonly short[] s_ThrowInReady2Left       = { -90, 684, -20, 683, -20, 684, -104 };
    private static readonly short[] s_ThrowInReady2TopLeft    = { -90, 693, -20, 692, -20, 693, -104 };

    // 218434-218441 — throwInPass streams (dseg_1311AE..1311E6, team1).
    private static readonly short[] s_ThrowPass1Up         = { -5, 372, 373, -101 };
    private static readonly short[] s_ThrowPass1UpRight    = { -5, 393, 394, -101 };
    private static readonly short[] s_ThrowPass1Right      = { -5, 378, 379, -101 };
    private static readonly short[] s_ThrowPass1BottomRight= { -5, 387, 388, -101 };
    private static readonly short[] s_ThrowPass1Bottom     = { -5, 375, 376, -101 };
    private static readonly short[] s_ThrowPass1BottomLeft = { -5, 384, 385, -101 };
    private static readonly short[] s_ThrowPass1Left       = { -5, 381, 382, -101 };
    private static readonly short[] s_ThrowPass1TopLeft    = { -5, 390, 391, -101 };

    // 218520-218527 — throwInPass streams (dseg_1313DE..131416, team2).
    private static readonly short[] s_ThrowPass2Up         = { -5, 675, 676, -101 };
    private static readonly short[] s_ThrowPass2UpRight    = { -5, 696, 697, -101 };
    private static readonly short[] s_ThrowPass2Right      = { -5, 681, 682, -101 };
    private static readonly short[] s_ThrowPass2BottomRight= { -5, 690, 691, -101 };
    private static readonly short[] s_ThrowPass2Bottom     = { -5, 678, 679, -101 };
    private static readonly short[] s_ThrowPass2BottomLeft = { -5, 687, 688, -101 };
    private static readonly short[] s_ThrowPass2Left       = { -5, 684, 685, -101 };
    private static readonly short[] s_ThrowPass2TopLeft    = { -5, 693, 694, -101 };

    // 218442-218449 — throwInKick streams (dseg_1311EE..131242, team1).
    private static readonly short[] s_ThrowKick1Up         = { -5, 372, 371, 372, 373, -101 };
    private static readonly short[] s_ThrowKick1UpRight    = { -5, 393, 392, 393, 394, -101 };
    private static readonly short[] s_ThrowKick1Right      = { -5, 378, 377, 378, 379, -101 };
    private static readonly short[] s_ThrowKick1BottomRight= { -5, 387, 386, 387, 388, -101 };
    private static readonly short[] s_ThrowKick1Bottom     = { -5, 375, 374, 375, 376, -101 };
    private static readonly short[] s_ThrowKick1BottomLeft = { -5, 384, 383, 384, 385, -101 };
    private static readonly short[] s_ThrowKick1Left       = { -5, 381, 380, 381, 382, -101 };
    private static readonly short[] s_ThrowKick1TopLeft    = { -5, 390, 389, 390, 391, -101 };

    // 218528-218535 — throwInKick streams (dseg_13141E..131472, team2).
    private static readonly short[] s_ThrowKick2Up         = { -5, 675, 674, 675, 676, -101 };
    private static readonly short[] s_ThrowKick2UpRight    = { -5, 696, 695, 696, 697, -101 };
    private static readonly short[] s_ThrowKick2Right      = { -5, 681, 680, 681, 682, -101 };
    private static readonly short[] s_ThrowKick2BottomRight= { -5, 690, 689, 690, 691, -101 };
    private static readonly short[] s_ThrowKick2Bottom     = { -5, 678, 677, 678, 679, -101 };
    private static readonly short[] s_ThrowKick2BottomLeft = { -5, 687, 686, 687, 688, -101 };
    private static readonly short[] s_ThrowKick2Left       = { -5, 684, 683, 684, 685, -101 };
    private static readonly short[] s_ThrowKick2TopLeft    = { -5, 693, 692, 693, 694, -101 };

    // 218568-218590 (team1) / 218628-218650 (team2) — goalkeeper dive streams.
    // Naming: "LeftGoal" = leftGoalieJumping* tables (TOP team keeper),
    // "RightGoal" = rightGoalieJumping* (BOTTOM team keeper). E/W = the two
    // populated direction slots (2 and 6) of each table — SWOS keepers only
    // dive sideways. Values verbatim from swos.asm (dseg label in comment).
    // -1..-99 are variable-pause opcodes (delay N ticks), -101 = hold last.
    private static readonly short[] s_Keeper1DiveHighLeftE  = { -5, 971, -7, 972, -3, 973, 974, -2, 975, -50, 976, 947, -101 };  // dseg_1315FA (218584)
    private static readonly short[] s_Keeper1DiveHighLeftW  = { -5, 983, -7, 982, -3, 981, 980, -2, 979, -50, 978, 947, -101 };  // dseg_131614 (218586)
    private static readonly short[] s_Keeper1DiveHighRightE = { -5, 985, -7, 986, -3, 987, 988, -2, 989, -50, 990, 950, -101 };  // dseg_13162E (218588)
    private static readonly short[] s_Keeper1DiveHighRightW = { -5, 997, -7, 996, -3, 995, 994, -2, 993, -50, 992, 950, -101 };  // dseg_131648 (218590)
    private static readonly short[] s_Keeper1DiveLowLeftE   = { -5, 971, -5, 975, -5, 976, 976, -2, 976, -50, 976, 947, -101 };  // dseg_131592 (218576)
    private static readonly short[] s_Keeper1DiveLowLeftW   = { -5, 983, -5, 979, -5, 978, 978, -2, 978, -50, 978, 947, -101 };  // dseg_1315AC (218578)
    private static readonly short[] s_Keeper1DiveLowRightE  = { -5, 985, -5, 989, -5, 990, 990, -2, 990, -50, 990, 950, -101 };  // dseg_1315C6 (218580)
    private static readonly short[] s_Keeper1DiveLowRightW  = { -5, 997, -5, 993, -5, 992, 992, -2, 992, -50, 992, 950, -101 };  // dseg_1315E0 (218582)

    private static readonly short[] s_Keeper2DiveHighLeftE  = { -5, 1087, -7, 1088, -3, 1089, 1090, -2, 1091, -50, 1092, 1063, -101 };  // dseg_1317FA (218644)
    private static readonly short[] s_Keeper2DiveHighLeftW  = { -5, 1099, -7, 1098, -3, 1097, 1096, -2, 1095, -50, 1094, 1063, -101 };  // dseg_131814 (218646)
    private static readonly short[] s_Keeper2DiveHighRightE = { -5, 1101, -7, 1102, -3, 1103, 1104, -2, 1105, -50, 1106, 1066, -101 };  // dseg_13182E (218648)
    private static readonly short[] s_Keeper2DiveHighRightW = { -5, 1113, -7, 1112, -3, 1111, 1110, -2, 1109, -50, 1108, 1066, -101 };  // dseg_131848 (218650)
    private static readonly short[] s_Keeper2DiveLowLeftE   = { -5, 1087, -5, 1091, -5, 1092, 1092, -2, 1092, -50, 1092, 1063, -101 };  // dseg_131792 (218636)
    private static readonly short[] s_Keeper2DiveLowLeftW   = { -5, 1099, -5, 1095, -5, 1094, 1094, -2, 1094, -50, 1094, 1063, -101 };  // dseg_1317AC (218638)
    private static readonly short[] s_Keeper2DiveLowRightE  = { -5, 1101, -5, 1105, -5, 1106, 1106, -2, 1106, -50, 1106, 1066, -101 };  // dseg_1317C6 (218640)
    private static readonly short[] s_Keeper2DiveLowRightW  = { -5, 1113, -5, 1109, -5, 1108, 1108, -2, 1108, -50, 1108, 1066, -101 };  // dseg_1317E0 (218642)

    // ---- Referee frame-indices streams (swos.asm:218660-218676) --------------
    // The referee is a separate Sprite (kRefereeSpriteStart=1273 ..
    // kRefereeSpriteEnd=1283, sprites.h:72-73). Its imageIndex cycles through
    // these streams via SpriteUpdate.UpdateSpriteAnimation the same way player
    // sprites do. Ordinal legend recovered from the streams:
    //   1273-1275 / 1276-1278  running (two opposing walk directions)
    //   1279                    standing
    //   1280                    holding YELLOW card
    //   1281                    holding RED card
    //   1282-1283               arm-raised card-wave (shared by both colours)
    // The card COLOUR is encoded by the distinct 1280 (yellow) vs 1281 (red)
    // pose that each card stream ends on.
    private static readonly short[] s_RefComingA = { 1275, 1273, 1274, 1273, -999 };  // dseg_131892
    private static readonly short[] s_RefComingB = { 1278, 1276, 1277, 1276, -999 };  // dseg_13189C
    private static readonly short[] s_RefWaiting = { 1279, -999 };                    // dseg_1318AE
    // dseg_1318B2 — yellow card ceremony (ends on 1280 = yellow-card pose).
    private static readonly short[] s_RefYellowCard =
        { -10, 1279, -12, 1282, -12, 1283, -12, 1282, -12, 1283, -12, 1282, -12,
          1283, -12, 1282, -12, 1283, -20, 1279, -80, 1280, -50, 1279, -101 };
    // dseg_1318E4 — red card ceremony (ends on 1281 = red-card pose).
    private static readonly short[] s_RefRedCard =
        { -10, 1279, -12, 1282, -12, 1283, -12, 1282, -12, 1283, -12, 1282, -12,
          1283, -12, 1282, -12, 1283, -20, 1279, -80, 1281, -50, 1279, -101 };
    // refSecondYellowFrames — second-yellow ceremony (shows 1280 yellow THEN
    // 1281 red, since a second booking is also a dismissal).
    private static readonly short[] s_RefSecondYellow =
        { -10, 1279, -12, 1282, -12, 1283, -12, 1282, -12, 1283, -12, 1282, -12,
          1283, -12, 1282, -12, 1283, -20, 1279, -80, 1280, -20, 1279, -80, 1281,
          -50, 1279, -101 };

    // ---- Bump-allocator state ------------------------------------------------
    // Each frame-indices array is interned into Memory starting at
    // kFrameIndicesArraysBase. `s_BumpCursor` tracks the next free byte.
    private static int s_BumpCursor;

    // Intern a frame-indices stream into Memory and return its absolute
    // address. Each entry is a word (2 bytes). Asserts allocation stays
    // inside [kFrameIndicesArraysBase, kFrameIndicesArraysEnd).
    private static int InternStream(short[] stream)
    {
        int addr = s_BumpCursor;
        for (int i = 0; i < stream.Length; i++)
            Memory.WriteWord(addr + i * 2, stream[i]);
        s_BumpCursor += stream.Length * 2;
        if (s_BumpCursor > Memory.Addr.kFrameIndicesArraysEnd)
        {
            // Should never trigger — we sized the arena generously. Throw
            // loudly so the smoke test catches any regression that bumps it
            // past the boundary.
            throw new System.InvalidOperationException(
                $"AnimationTablesData frame-indices arena overflow: " +
                $"cursor=0x{s_BumpCursor:X}, limit=0x{Memory.Addr.kFrameIndicesArraysEnd:X}");
        }
        return addr;
    }

    // Write a PlayerAnimationTable struct at `tableAddr`. The 4 × 8 entries
    // are passed as (team1Player[8], team2Player[8], team1Goalie[8],
    // team2Goalie[8]) groups in direction order 0..7. Layout matches
    // swos.asm:218920+ (frameDelay word, then 32 dword pointers).
    private static void WriteTable(int tableAddr, short frameDelay,
        int[] team1Player, int[] team2Player,
        int[] team1Goalie, int[] team2Goalie)
    {
        Memory.WriteWord(tableAddr, frameDelay);
        for (int i = 0; i < 8; i++) Memory.WriteDword(tableAddr +   2 + i * 4, team1Player[i]);
        for (int i = 0; i < 8; i++) Memory.WriteDword(tableAddr +  34 + i * 4, team2Player[i]);
        for (int i = 0; i < 8; i++) Memory.WriteDword(tableAddr +  66 + i * 4, team1Goalie[i]);
        for (int i = 0; i < 8; i++) Memory.WriteDword(tableAddr +  98 + i * 4, team2Goalie[i]);
    }

    // Write a RefereeAnimationTable struct at `tableAddr` (swos.h:130-134 —
    // `word numCycles; SwosDataPointer<int16_t> indicesTable[8]`). Layout matches
    // what Referee.InitRefereeAnimationTable reads: numCycles at +0, then one
    // dword frame-stream pointer per direction 0..7 at +2 + dir*4.
    private static void WriteRefTable(int tableAddr, short numCycles, int[] dirPointers)
    {
        Memory.WriteWord(tableAddr, numCycles);
        for (int i = 0; i < 8; i++) Memory.WriteDword(tableAddr + 2 + i * 4, dirPointers[i]);
    }

    // All-zero direction slots — used when an animation table only carries
    // entries for a subset of the (player1/player2/goalie1/goalie2) groups.
    private static readonly int[] s_Zero8 = { 0, 0, 0, 0, 0, 0, 0, 0 };

    // ---- Public init ---------------------------------------------------------
    // Called from Memory.Init(). Interns every frame-indices stream, then
    // writes each animation-table struct at its allotted address.
    public static void Init()
    {
        s_BumpCursor = Memory.Addr.kFrameIndicesArraysBase;

        // --- Player running -------------------------------------------------
        int[] runT1Pl = {
            InternStream(s_PlayerRunningUpTeam1),
            InternStream(s_PlayerRunningUpRightTeam1),
            InternStream(s_PlayerRunningRightTeam1),
            InternStream(s_PlayerRunningDownRightTeam1),
            InternStream(s_PlayerRunningDownTeam1),
            InternStream(s_PlayerRunningDownLeftTeam1),
            InternStream(s_PlayerRunningLeftTeam1),
            InternStream(s_PlayerRunningUpLeftTeam1),
        };
        int[] runT2Pl = {
            InternStream(s_PlayerRunningUpTeam2),
            InternStream(s_PlayerRunningUpRightTeam2),
            InternStream(s_PlayerRunningRightTeam2),
            InternStream(s_PlayerRunningDownRightTeam2),
            InternStream(s_PlayerRunningDownTeam2),
            InternStream(s_PlayerRunningDownLeftTeam2),
            InternStream(s_PlayerRunningLeftTeam2),
            InternStream(s_PlayerRunningUpLeftTeam2),
        };
        int[] runT1Gk = {
            InternStream(s_KeeperRunningUpTeam1),
            InternStream(s_KeeperRunningUpRightTeam1),
            InternStream(s_KeeperRunningRightTeam1),
            InternStream(s_KeeperRunningDownRightTeam1),
            InternStream(s_KeeperRunningDownTeam1),
            InternStream(s_KeeperRunningDownLeftTeam1),
            InternStream(s_KeeperRunningLeftTeam1),
            InternStream(s_KeeperRunningUpLeftTeam1),
        };
        int[] runT2Gk = {
            InternStream(s_KeeperRunningUpTeam2),
            InternStream(s_KeeperRunningUpRightTeam2),
            InternStream(s_KeeperRunningRightTeam2),
            InternStream(s_KeeperRunningDownRightTeam2),
            InternStream(s_KeeperRunningDownTeam2),
            InternStream(s_KeeperRunningDownLeftTeam2),
            InternStream(s_KeeperRunningLeftTeam2),
            InternStream(s_KeeperRunningUpLeftTeam2),
        };
        WriteTable(Memory.Addr.kPlayerRunningAnimTableAddr, 5,
            runT1Pl, runT2Pl, runT1Gk, runT2Gk);

        // --- Player normal standing -----------------------------------------
        int[] standT1Pl = {
            InternStream(s_StandTeam1Up),
            InternStream(s_StandTeam1UpRight),
            InternStream(s_StandTeam1Right),
            InternStream(s_StandTeam1BottomRight),
            InternStream(s_StandTeam1Bottom),
            InternStream(s_StandTeam1BottomLeft),
            InternStream(s_StandTeam1Left),
            InternStream(s_StandTeam1TopLeft),
        };
        int[] standT2Pl = {
            InternStream(s_StandTeam2Up),
            InternStream(s_StandTeam2UpRight),
            InternStream(s_StandTeam2Right),
            InternStream(s_StandTeam2BottomRight),
            InternStream(s_StandTeam2Bottom),
            InternStream(s_StandTeam2BottomLeft),
            InternStream(s_StandTeam2Left),
            InternStream(s_StandTeam2TopLeft),
        };
        int[] standT1Gk = {
            InternStream(s_StandKeeper1Up),
            InternStream(s_StandKeeper1UpRight),
            InternStream(s_StandKeeper1Right),
            InternStream(s_StandKeeper1BottomRight),
            InternStream(s_StandKeeper1Bottom),
            InternStream(s_StandKeeper1BottomLeft),
            InternStream(s_StandKeeper1Left),
            InternStream(s_StandKeeper1TopLeft),
        };
        int[] standT2Gk = {
            InternStream(s_StandKeeper2Up),
            InternStream(s_StandKeeper2UpRight),
            InternStream(s_StandKeeper2Right),
            InternStream(s_StandKeeper2BottomRight),
            InternStream(s_StandKeeper2Bottom),
            InternStream(s_StandKeeper2BottomLeft),
            InternStream(s_StandKeeper2Left),
            InternStream(s_StandKeeper2TopLeft),
        };
        WriteTable(Memory.Addr.kPlayerStandingAnimTableAddr, 5,
            standT1Pl, standT2Pl, standT1Gk, standT2Gk);

        // --- Player tackling (only outfielders defined; goalies = 0) --------
        int[] tackleT1Pl = {
            InternStream(s_Tackle1Up),
            InternStream(s_Tackle1UpRight),
            InternStream(s_Tackle1Right),
            InternStream(s_Tackle1BottomRight),
            InternStream(s_Tackle1Bottom),
            InternStream(s_Tackle1BottomLeft),
            InternStream(s_Tackle1Left),
            InternStream(s_Tackle1TopLeft),
        };
        int[] tackleT2Pl = {
            InternStream(s_Tackle2Up),
            InternStream(s_Tackle2UpRight),
            InternStream(s_Tackle2Right),
            InternStream(s_Tackle2BottomRight),
            InternStream(s_Tackle2Bottom),
            InternStream(s_Tackle2BottomLeft),
            InternStream(s_Tackle2Left),
            InternStream(s_Tackle2TopLeft),
        };
        WriteTable(Memory.Addr.kPlTacklingAnimTableAddr, 5,
            tackleT1Pl, tackleT2Pl, s_Zero8, s_Zero8);

        // --- Player tackled (only outfielders) ------------------------------
        int[] tackledT1Pl = {
            InternStream(s_Tackled1Up),
            InternStream(s_Tackled1UpRight),
            InternStream(s_Tackled1Right),
            InternStream(s_Tackled1BottomRight),
            InternStream(s_Tackled1Bottom),
            InternStream(s_Tackled1BottomLeft),
            InternStream(s_Tackled1Left),
            InternStream(s_Tackled1TopLeft),
        };
        int[] tackledT2Pl = {
            InternStream(s_Tackled2Up),
            InternStream(s_Tackled2UpRight),
            InternStream(s_Tackled2Right),
            InternStream(s_Tackled2BottomRight),
            InternStream(s_Tackled2Bottom),
            InternStream(s_Tackled2BottomLeft),
            InternStream(s_Tackled2Left),
            InternStream(s_Tackled2TopLeft),
        };
        WriteTable(Memory.Addr.kPlayerTackledAnimTableAddr, 5,
            tackledT1Pl, tackledT2Pl, s_Zero8, s_Zero8);

        // --- Goalie catching ball (only goalies; players = 0) ---------------
        // swos.asm:219082-219097 — only directions 0..7 of the GOALIE groups
        // are populated. The first 16 entries (player team1/team2) are all 0.
        // Per swos.asm: T1 goalie uses dseg_131502 for dirs 0..2 and 7,
        // dseg_131516 for dirs 3..6. T2 goalie uses dseg_131702 / dseg_131716
        // the same way.
        int catch1A = InternStream(s_KeeperCatch1A);
        int catch1B = InternStream(s_KeeperCatch1B);
        int catch2A = InternStream(s_KeeperCatch2A);
        int catch2B = InternStream(s_KeeperCatch2B);
        int[] catchT1Gk = { catch1A, catch1A, catch1A, catch1B, catch1B, catch1B, catch1B, catch1A };
        int[] catchT2Gk = { catch2A, catch2A, catch2A, catch2B, catch2B, catch2B, catch2B, catch2A };
        WriteTable(Memory.Addr.kGoalieCatchingBallAnimTableAddr, 5,
            s_Zero8, s_Zero8, catchT1Gk, catchT2Gk);

        // --- About to throw in (only outfielders) ---------------------------
        int[] throwReadyT1Pl = {
            InternStream(s_ThrowInReady1Up),
            InternStream(s_ThrowInReady1UpRight),
            InternStream(s_ThrowInReady1Right),
            InternStream(s_ThrowInReady1BottomRight),
            InternStream(s_ThrowInReady1Bottom),
            InternStream(s_ThrowInReady1BottomLeft),
            InternStream(s_ThrowInReady1Left),
            InternStream(s_ThrowInReady1TopLeft),
        };
        int[] throwReadyT2Pl = {
            InternStream(s_ThrowInReady2Up),
            InternStream(s_ThrowInReady2UpRight),
            InternStream(s_ThrowInReady2Right),
            InternStream(s_ThrowInReady2BottomRight),
            InternStream(s_ThrowInReady2Bottom),
            InternStream(s_ThrowInReady2BottomLeft),
            InternStream(s_ThrowInReady2Left),
            InternStream(s_ThrowInReady2TopLeft),
        };
        WriteTable(Memory.Addr.kAboutToThrowInAnimTableAddr, 5,
            throwReadyT1Pl, throwReadyT2Pl, s_Zero8, s_Zero8);

        // --- Throw-in pass (only outfielders) -------------------------------
        int[] throwPassT1Pl = {
            InternStream(s_ThrowPass1Up),
            InternStream(s_ThrowPass1UpRight),
            InternStream(s_ThrowPass1Right),
            InternStream(s_ThrowPass1BottomRight),
            InternStream(s_ThrowPass1Bottom),
            InternStream(s_ThrowPass1BottomLeft),
            InternStream(s_ThrowPass1Left),
            InternStream(s_ThrowPass1TopLeft),
        };
        int[] throwPassT2Pl = {
            InternStream(s_ThrowPass2Up),
            InternStream(s_ThrowPass2UpRight),
            InternStream(s_ThrowPass2Right),
            InternStream(s_ThrowPass2BottomRight),
            InternStream(s_ThrowPass2Bottom),
            InternStream(s_ThrowPass2BottomLeft),
            InternStream(s_ThrowPass2Left),
            InternStream(s_ThrowPass2TopLeft),
        };
        WriteTable(Memory.Addr.kThrowInPassAnimTableAddr, 5,
            throwPassT1Pl, throwPassT2Pl, s_Zero8, s_Zero8);

        // --- Throw-in kick (only outfielders) -------------------------------
        int[] throwKickT1Pl = {
            InternStream(s_ThrowKick1Up),
            InternStream(s_ThrowKick1UpRight),
            InternStream(s_ThrowKick1Right),
            InternStream(s_ThrowKick1BottomRight),
            InternStream(s_ThrowKick1Bottom),
            InternStream(s_ThrowKick1BottomLeft),
            InternStream(s_ThrowKick1Left),
            InternStream(s_ThrowKick1TopLeft),
        };
        int[] throwKickT2Pl = {
            InternStream(s_ThrowKick2Up),
            InternStream(s_ThrowKick2UpRight),
            InternStream(s_ThrowKick2Right),
            InternStream(s_ThrowKick2BottomRight),
            InternStream(s_ThrowKick2Bottom),
            InternStream(s_ThrowKick2BottomLeft),
            InternStream(s_ThrowKick2Left),
            InternStream(s_ThrowKick2TopLeft),
        };
        WriteTable(Memory.Addr.kThrowInKickAnimTableAddr, 5,
            throwKickT1Pl, throwKickT2Pl, s_Zero8, s_Zero8);

        // --- Goalie jumping (left/right + low/high) -------------------------
        // swos.asm:219232 / 219298 / 219332 / 219365 — populated only for the
        // goalie groups (outfielders are 0), and within those only directions
        // 2 (E) and 6 (W): keepers dive sideways. The real dive frame streams
        // (dseg_131592..131648 team1 / dseg_131792..131848 team2, quoted
        // verbatim above) replace the old catch-stream stand-ins — bug #155:
        // the stand-ins made the sim emit catch imageIndexes during a dive so
        // the renderer never saw the CJCTEAMG dive frames (971-997/1087-1113).
        int[] jumpHighLeftT1Gk = { 0, 0, InternStream(s_Keeper1DiveHighLeftE), 0, 0, 0, InternStream(s_Keeper1DiveHighLeftW), 0 };
        int[] jumpHighLeftT2Gk = { 0, 0, InternStream(s_Keeper2DiveHighLeftE), 0, 0, 0, InternStream(s_Keeper2DiveHighLeftW), 0 };
        WriteTable(Memory.Addr.kLeftGoalieJumpingHighAnimTableAddr, 5,
            s_Zero8, s_Zero8, jumpHighLeftT1Gk, jumpHighLeftT2Gk);
        int[] jumpHighRightT1Gk = { 0, 0, InternStream(s_Keeper1DiveHighRightE), 0, 0, 0, InternStream(s_Keeper1DiveHighRightW), 0 };
        int[] jumpHighRightT2Gk = { 0, 0, InternStream(s_Keeper2DiveHighRightE), 0, 0, 0, InternStream(s_Keeper2DiveHighRightW), 0 };
        WriteTable(Memory.Addr.kRightGoalieJumpingHighAnimTableAddr, 5,
            s_Zero8, s_Zero8, jumpHighRightT1Gk, jumpHighRightT2Gk);
        int[] jumpLowLeftT1Gk = { 0, 0, InternStream(s_Keeper1DiveLowLeftE), 0, 0, 0, InternStream(s_Keeper1DiveLowLeftW), 0 };
        int[] jumpLowLeftT2Gk = { 0, 0, InternStream(s_Keeper2DiveLowLeftE), 0, 0, 0, InternStream(s_Keeper2DiveLowLeftW), 0 };
        WriteTable(Memory.Addr.kLeftGoalieJumpingLowAnimTableAddr, 5,
            s_Zero8, s_Zero8, jumpLowLeftT1Gk, jumpLowLeftT2Gk);
        int[] jumpLowRightT1Gk = { 0, 0, InternStream(s_Keeper1DiveLowRightE), 0, 0, 0, InternStream(s_Keeper1DiveLowRightW), 0 };
        int[] jumpLowRightT2Gk = { 0, 0, InternStream(s_Keeper2DiveLowRightE), 0, 0, 0, InternStream(s_Keeper2DiveLowRightW), 0 };
        WriteTable(Memory.Addr.kRightGoalieJumpingLowAnimTableAddr, 5,
            s_Zero8, s_Zero8, jumpLowRightT1Gk, jumpLowRightT2Gk);

        // --- Static / jump header tables ------------------------------------
        // swos.asm:218678 / 218746 / 218781 / 218817. These all share the
        // shape of `playerNormalStandingAnimTable` for header poses — for the
        // smoke test we wire each per-direction entry at the appropriate
        // standing frame so SetPlayerAnimationTable can dereference cleanly.
        // The renderer in our port doesn't read these yet (still uses
        // KeeperSim/PlayerFrames Phase A picks), so re-using stand frames is
        // safe and keeps the dword pointers in-range.
        WriteTable(Memory.Addr.kStaticHeaderAttemptAnimTableAddr, 5,
            standT1Pl, standT2Pl, standT1Gk, standT2Gk);
        WriteTable(Memory.Addr.kStaticHeaderHitAnimTableAddr, 5,
            standT1Pl, standT2Pl, standT1Gk, standT2Gk);
        WriteTable(Memory.Addr.kJumpHeaderAttemptAnimTableAddr, 5,
            standT1Pl, standT2Pl, standT1Gk, standT2Gk);
        WriteTable(Memory.Addr.kJumpHeaderHitAnimTableAddr, 5,
            standT1Pl, standT2Pl, standT1Gk, standT2Gk);

        // --- Injured player (only outfielders; goalies = 0) ------------------
        // swos.asm:219566 plInjuredAnimTable → team1/team2InjuredPlayer streams.
        // dirs 0..3 use the TopLeft writhe stream, dirs 4..7 the TopRight one
        // (swos.asm:219567-219582). Each stream alternates two on-ground curl/
        // roll poses with a 20-tick pause. PREVIOUSLY stubbed to the standing
        // frames, which left an injured player standing upright instead of
        // writhing (task #184). Main.cs SwosOrdinalToAtlasCell maps the injured
        // ordinals 411-414 / 714-717 (rel 70-73) to the fallen ground poses.
        int inj1TopLeft  = InternStream(s_Injured1TopLeft);
        int inj1TopRight = InternStream(s_Injured1TopRight);
        int inj2TopLeft  = InternStream(s_Injured2TopLeft);
        int inj2TopRight = InternStream(s_Injured2TopRight);
        int[] injuredT1Pl = { inj1TopLeft, inj1TopLeft, inj1TopLeft, inj1TopLeft,
                              inj1TopRight, inj1TopRight, inj1TopRight, inj1TopRight };
        int[] injuredT2Pl = { inj2TopLeft, inj2TopLeft, inj2TopLeft, inj2TopLeft,
                              inj2TopRight, inj2TopRight, inj2TopRight, inj2TopRight };
        WriteTable(Memory.Addr.kPlInjuredAnimTableAddr, 5,
            injuredT1Pl, injuredT2Pl, s_Zero8, s_Zero8);

        // --- Referee animation tables (swos.asm:219599-219650) ---------------
        // The referee FSM (Sim/Port/Referee.cs) installs these per phase via
        // InitRefereeAnimationTable; UpdateSpriteAnimation then walks the chosen
        // stream and writes RefereeSprite.imageIndex (1273-1283). Without them
        // the referee sprite's frameIndicesTable stayed null and no image was
        // ever produced, so the run-in + card ceremony was invisible (task #181).
        int refComingA = InternStream(s_RefComingA);
        int refComingB = InternStream(s_RefComingB);
        int refWaiting = InternStream(s_RefWaiting);
        int refYellow  = InternStream(s_RefYellowCard);
        int refRed     = InternStream(s_RefRedCard);
        int refSecond  = InternStream(s_RefSecondYellow);
        // refComingAnimTable (219599-219608) — dirs 0-2,7 walk one way (A),
        // dirs 3-6 the other (B).
        WriteRefTable(Memory.Addr.refComingAnimTable, 5,
            new[] { refComingA, refComingA, refComingA, refComingB,
                    refComingB, refComingB, refComingB, refComingA });
        // refWaitingAnimTable (219609-219618) — standing, all directions.
        WriteRefTable(Memory.Addr.refWaitingAnimTable, 5,
            new[] { refWaiting, refWaiting, refWaiting, refWaiting,
                    refWaiting, refWaiting, refWaiting, refWaiting });
        // refYellowCardAnimTable (219623-219631).
        WriteRefTable(Memory.Addr.refYellowCardAnimTable, 5,
            new[] { refYellow, refYellow, refYellow, refYellow,
                    refYellow, refYellow, refYellow, refYellow });
        // refRedCardAnimTable (219632-219640).
        WriteRefTable(Memory.Addr.refRedCardAnimTable, 5,
            new[] { refRed, refRed, refRed, refRed, refRed, refRed, refRed, refRed });
        // refSecondYellowAnimTable (219641-219650).
        WriteRefTable(Memory.Addr.refSecondYellowAnimTable, 5,
            new[] { refSecond, refSecond, refSecond, refSecond,
                    refSecond, refSecond, refSecond, refSecond });
    }
}
