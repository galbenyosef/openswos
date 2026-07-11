@echo off
REM Launch OpenSWOS with SWOS port path enabled.
REM If you can't toggle from menu, edit user://settings.json:
REM   {"gameSpeedScale":1.00,"useSwosPort":true}
REM Tip: in-game menu has "physics" slot — toggle via left/right arrow.

cd /d "%~dp0\.."
"%~dp0\..\.tools\godot\Godot_v4.6.2-stable_mono_win64.exe" --path game
