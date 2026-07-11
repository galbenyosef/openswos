@echo off
setlocal enabledelayedexpansion

REM ============================================================================
REM  OpenSWOS Fidelity Trace Capture
REM
REM  Launches external\swos-port-modified\bin\x64\swos-port-x64-Release.exe
REM  with proper working directory. After you exit the game, ball_trace.csv is
REM  moved into tests\OpenSwos.Tests\golden\<scenario>.csv automatically.
REM
REM  Usage:   tools\capture-trace.bat
REM           tools\capture-trace.bat kickoff       (skip prompt, name preset)
REM ============================================================================

set BINDIR=I:\GITHUB\W_OPEN_SWOS\external\swos-port-modified\bin\x64
set GOLDENDIR=I:\GITHUB\W_OPEN_SWOS\tests\OpenSwos.Tests\golden
set EXE=swos-port-x64-Release.exe

echo.
echo ============================================================
echo  OpenSWOS Fidelity Trace - capture ball_trace.csv
echo ============================================================
echo.

REM Scenario name: from arg, or prompt.
if "%~1"=="" (
    echo Available scenarios: kickoff, free_kick, corner, goal_kick
    set /p scenario="Scenario name [kickoff]: "
    if "!scenario!"=="" set scenario=kickoff
) else (
    set scenario=%~1
)

echo.
echo Will save trace as: %GOLDENDIR%\%scenario%.csv
echo.

REM Verify exe exists.
if not exist "%BINDIR%\%EXE%" (
    echo ERROR: %EXE% not found at %BINDIR%
    echo You need to build external\swos-port-modified first.
    pause
    exit /b 1
)

REM Delete stale trace, if any.
if exist "%BINDIR%\ball_trace.csv" (
    echo Removing previous ball_trace.csv...
    del "%BINDIR%\ball_trace.csv"
)

echo Launching game. Play the '%scenario%' scenario, then exit normally.
echo Press any key to start...
pause >nul

cd /d "%BINDIR%"
"%EXE%"

REM Check we got a trace.
if not exist "%BINDIR%\ball_trace.csv" (
    echo.
    echo No ball_trace.csv was produced. Did a match actually start?
    echo Did updateBall get called? Check the logs.
    pause
    exit /b 1
)

REM Make golden dir if needed.
if not exist "%GOLDENDIR%" mkdir "%GOLDENDIR%"

REM Move + rename.
move /Y "%BINDIR%\ball_trace.csv" "%GOLDENDIR%\%scenario%.csv"

echo.
echo ============================================================
echo  Saved: %GOLDENDIR%\%scenario%.csv
echo ============================================================
echo.
echo Now run the tests:
echo   cd I:\GITHUB\W_OPEN_SWOS\tests\OpenSwos.Tests
echo   dotnet test
echo.
pause
endlocal
