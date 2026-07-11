namespace OpenSwos.Sim.Port;

using OpenSwos.SwosVm;

// Player-name display state machine ported from
// external/swos-port/src/game/playerNameDisplay.cpp.
//
// SCOPE: UpdateCurrentPlayerName (playerNameDisplay.cpp:20-55) drives:
//   - nobodysBallTimer countdown (50-tick grace period before the name banner
//     hides when nobody owns the ball).
//   - lastPlayerBeforeGoalkeeper clear on hide (line 103).
//   - the blinking pattern when scorer / booked player is being highlighted
//     (every-8-tick toggle via currentGameTick & 8).
//
// EXCLUDED: drawPlayerName (lines 57-70), getPlayerNumberAndSurname (lines
// 79-90), showCurrentPlayerName (92-98) — pure text rendering / pointer
// dereferencing that depends on PlayerInfo.shortName + ExtractSurname (an asm
// helper). Godot's HUD reads our state and draws using the player records.
//
// PRIVATE STATE: m_topTeam (bool) + m_playerOrdinal (int, -1 = hidden).
// Backed in Memory at pnd_topTeam / pnd_playerOrdinal — added inline below.
//
// HIDDEN VS SHOWN: lines 100-104 — hideCurrentPlayerName() sets m_playerOrdinal
// = -1 AND clears lastPlayerBeforeGoalkeeper. shown state has playerOrdinal >= 0.
public static class PlayerNameDisplay
{
    // playerNameDisplay.cpp:124 — kFramesBeforeNobodysBall = 50.
    public const int kFramesBeforeNobodysBall = 50;

    // GameState constants (swos.h:568-595).
    public const short kGameStateInProgress = 100;

    // Private-state addresses (allocated inline in Memory — see initialiser below).
    // We use 4 free bytes inside the existing pnd block carved from
    // res_team1NameLength (which is also a 'cheap' private; no overlap).
    // Actually we need new addresses — pick from Memory's free range 0x2454..
    // Already used 0x2454-0x2458 for sl_*, 0x2460..0x2477 for cornerFlags,
    // 0x2480..0x248F for curPlayerNumSprites. Free at 0x2490..
    //
    // Wired via Memory.Addr.pnd_* (added below).

    // playerNameDisplay.cpp:20-55 — updateCurrentPlayerName.
    public static void UpdateCurrentPlayerName()
    {
        // playerNameDisplay.cpp:22 — static int s_nobodysBallLastFrame.
        // Backed at Memory.Addr.pnd_nobodysBallLastFrame.

        // playerNameDisplay.cpp:24 — swos.currentPlayerNameSprite.show().
        // Pure-render visibility flag — renderer reads pnd_playerOrdinal to
        // decide whether to actually draw. We just set a 'visible' flag here
        // for parity.
        Memory.WriteWord(Memory.Addr.pnd_visible, 1);

        // playerNameDisplay.cpp:26 — if (swos.currentScorer) → showNameBlinking(scorer).
        int currentScorer = Memory.ReadSignedDword(Memory.Addr.currentScorer);
        if (currentScorer != 0)
        {
            int lastTeamScored = Memory.ReadSignedDword(Memory.Addr.lastTeamScored);
            ShowNameBlinking(currentScorer, lastTeamScored);
            return;
        }

        // playerNameDisplay.cpp:28 — else if (cardHandingInProgress()) →
        //   showNameBlinking(bookedPlayer, lastTeamBooked).
        // lastTeamBooked is now wired — it's written by PlayerTackle at
        // updatePlayers.cpp:14074 + 14317 (TryBookingThePlayer + TrySendingOffThePlayer
        // tail) which our port mirrors at PlayerTackle.cs:670 + 839. Reads
        // back as a TeamData* (top or bottom) once a card has been issued
        // this match; 0 before then.
        // Source: external/swos-port/src/game/playerNameDisplay.cpp:29
        if (Referee.CardHandingInProgress())
        {
            int bookedPlayer = Memory.ReadSignedDword(Memory.Addr.bookedPlayer);
            int lastTeamBooked = Memory.ReadSignedDword(Memory.Addr.lastTeamBooked);
            ShowNameBlinking(bookedPlayer, lastTeamBooked);
            return;
        }

        // playerNameDisplay.cpp:30 — else if (lastPlayerBeforeGoalkeeper) →
        //   prolongLastPlayersName(lastPlayerBeforeGoalkeeper, lastTeamScored).
        int lpbgk = Memory.ReadSignedDword(Memory.Addr.lastPlayerBeforeGoalkeeper);
        if (lpbgk != 0)
        {
            int lastTeamScored = Memory.ReadSignedDword(Memory.Addr.lastTeamScored);
            ProlongLastPlayersName(lpbgk, lastTeamScored);
            return;
        }

        // playerNameDisplay.cpp:32-54 — main else branch.
        int lastPlayer = Memory.ReadSignedDword(Memory.Addr.lastPlayerPlayed);
        int lastTeam   = Memory.ReadSignedDword(Memory.Addr.lastTeamPlayed);

        short gameStatePl = Memory.ReadSignedWord(Memory.Addr.gameStatePl);
        if (gameStatePl == kGameStateInProgress)
        {
            // playerNameDisplay.cpp:36-43.
            if (lastPlayer != 0)
            {
                // playerNameDisplay.cpp:38-39 — if (lastTeam->playerHasBall) resetNobodysBallTimer.
                if (lastTeam != 0 &&
                    Memory.ReadSignedWord(lastTeam + TeamData.OffPlayerHasBall) != 0)
                {
                    ResetNobodysBallTimer();
                }
                ProlongLastPlayersName(lastPlayer, lastTeam);
            }
            else
            {
                HideCurrentPlayerName();
            }
        }
        else
        {
            // playerNameDisplay.cpp:44-53 — game NOT in progress.
            int teamControlled = 0;
            if (lastTeam != 0)
                teamControlled = Memory.ReadSignedDword(lastTeam + TeamData.OffControlledPlayer);

            if (lastPlayer == 0 || teamControlled == 0)
            {
                // playerNameDisplay.cpp:45-47 — re-arm s_nobodysBallLastFrame = 1 then hide.
                Memory.WriteWord(Memory.Addr.pnd_nobodysBallLastFrame, 1);
                HideCurrentPlayerName();
            }
            else
            {
                short lastFrame = Memory.ReadSignedWord(Memory.Addr.pnd_nobodysBallLastFrame);
                if (lastFrame != 0)
                {
                    // playerNameDisplay.cpp:48-50 — decrement, hide.
                    Memory.WriteWord(Memory.Addr.pnd_nobodysBallLastFrame, (short)(lastFrame - 1));
                    HideCurrentPlayerName();
                }
                else
                {
                    // playerNameDisplay.cpp:51-52 — reset + prolong.
                    ResetNobodysBallTimer();
                    ProlongLastPlayersName(lastPlayer, lastTeam);
                }
            }
        }
    }

    // playerNameDisplay.cpp:92-98 — showCurrentPlayerName.
    //   m_topTeam = (lastTeam->inGameTeamPtr.asAligned() == &swos.topTeamInGame);
    //   m_playerOrdinal = lastPlayer->playerOrdinal - 1;
    public static void ShowCurrentPlayerName(int lastPlayerSpriteAddr, int lastTeamAddr)
    {
        if (lastPlayerSpriteAddr == 0 || lastTeamAddr == 0)
        {
            HideCurrentPlayerName();
            return;
        }

        // playerNameDisplay.cpp:96 — m_topTeam = lastTeam->inGameTeamPtr ==
        // &swos.topTeamInGame. This is a TEAM-IDENTITY check (is lastTeam the
        // roster stored in the *stable* topTeamInGame struct = team1?), NOT a
        // screen-position check. The original's topTeamInGame/bottomTeamInGame
        // are fixed team1/team2 structs; only TeamGeneralInfo.inGameTeamPtr
        // swaps ends at half-time (game.cpp:449-541 xchg).
        //
        // BUG (task #201): we previously compared against TeamData.TopBase's
        // inGameTeamPtr *field* — which DOES swap at HT. That turned this into a
        // screen-position test, so in the 2nd half topTeam went true for the
        // team now at the top slot while the banner render (Main.cs) still keys
        // off the stable topTeamInGame/bottomTeamInGame globals → the wrong
        // team's roster (e.g. the opponent keeper showed our keeper's name).
        // Fix: compare against the stable global, exactly like the original.
        int topInGame  = Memory.ReadSignedDword(Memory.Addr.topTeamInGame);
        int teamInGame = Memory.ReadSignedDword(lastTeamAddr + TeamData.OffInGameTeamPtr);
        bool topTeam = (teamInGame == topInGame);

        short playerOrdinal = Memory.ReadSignedWord(lastPlayerSpriteAddr + PlayerSprite.OffPlayerOrdinal);

        Memory.WriteWord(Memory.Addr.pnd_topTeam, topTeam ? 1 : 0);
        Memory.WriteWord(Memory.Addr.pnd_playerOrdinal, (short)(playerOrdinal - 1));
    }

    // playerNameDisplay.cpp:100-104 — hideCurrentPlayerName.
    public static void HideCurrentPlayerName()
    {
        Memory.WriteWord(Memory.Addr.pnd_playerOrdinal, -1);
        Memory.WriteDword(Memory.Addr.lastPlayerBeforeGoalkeeper, 0);
    }

    // playerNameDisplay.cpp:106-110 — showNameBlinking.
    //   bool showName = (swos.currentGameTick & 8) != 0;
    //   showName ? showCurrentPlayerName(lastPlayer, lastTeam) : hideCurrentPlayerName();
    public static void ShowNameBlinking(int lastPlayerSpriteAddr, int lastTeamAddr)
    {
        ushort tick = Memory.ReadWord(Memory.Addr.currentGameTick);
        bool showName = (tick & 8) != 0;
        if (showName)
            ShowCurrentPlayerName(lastPlayerSpriteAddr, lastTeamAddr);
        else
            HideCurrentPlayerName();
    }

    // playerNameDisplay.cpp:112-120 — prolongLastPlayersName.
    //   if (!swos.nobodysBallTimer) hideCurrentPlayerName();
    //   else { swos.nobodysBallTimer--; showCurrentPlayerName(lastPlayer, lastTeam); }
    public static void ProlongLastPlayersName(int lastPlayerSpriteAddr, int lastTeamAddr)
    {
        short timer = Memory.ReadSignedWord(Memory.Addr.nobodysBallTimer);
        if (timer == 0)
        {
            HideCurrentPlayerName();
        }
        else
        {
            Memory.WriteWord(Memory.Addr.nobodysBallTimer, (short)(timer - 1));
            ShowCurrentPlayerName(lastPlayerSpriteAddr, lastTeamAddr);
        }
    }

    // playerNameDisplay.cpp:122-128 — resetNobodysBallTimer.
    //   swos.nobodysBallTimer = kFramesBeforeNobodysBall (50).
    public static void ResetNobodysBallTimer()
    {
        Memory.WriteWord(Memory.Addr.nobodysBallTimer, kFramesBeforeNobodysBall);
    }

    // playerNameDisplay.cpp:74-77 — getDisplayedPlayerNumberAndTeam (SWOS_TEST only).
    // Exposed for renderer use.
    public static (bool topTeam, int playerOrdinal) GetDisplayedPlayerNumberAndTeam()
    {
        bool topTeam = Memory.ReadSignedWord(Memory.Addr.pnd_topTeam) != 0;
        int playerOrdinal = Memory.ReadSignedWord(Memory.Addr.pnd_playerOrdinal);
        return (topTeam, playerOrdinal);
    }
}
