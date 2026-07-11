@echo off
REM ============================================================================
REM  Launch OpenSWOS game (Godot 4.6.2 + C# .NET 9)
REM
REM  Usage: tools\launch-game.bat
REM
REM  To toggle SWOS port physics:
REM    Menu → arrow down to slot 8 (physics) → arrow right to "SWOS port"
REM    Then start a Friendly Match and kick the ball to A/B with old sim.
REM ============================================================================

set GODOT=I:\GITHUB\W_OPEN_SWOS\.tools\godot\Godot_v4.6.2-stable_mono_win64.exe
set PROJECT=I:\GITHUB\W_OPEN_SWOS\game

if not exist "%GODOT%" (
    echo ERROR: Godot not found at %GODOT%
    pause
    exit /b 1
)

echo Launching OpenSWOS...
echo Godot: %GODOT%
echo Project: %PROJECT%
echo.

"%GODOT%" --path "%PROJECT%"
