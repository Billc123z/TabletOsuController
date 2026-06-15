@echo off
title Tablet Osu Server

echo [Tablet Osu Server Starter]
echo Looking for Godot executable in the project folder...

set "GODOT_EXE="

:: Check the specific folder first
if exist "%~dp0Godot_v4.2.2-stable_mono_win64\Godot_v4.2.2-stable_mono_win64.exe" (
    set "GODOT_EXE=%~dp0Godot_v4.2.2-stable_mono_win64\Godot_v4.2.2-stable_mono_win64.exe"
) else (
    :: Try to find any Godot executable
    for /r "%~dp0" %%G in (Godot*mono_win64.exe) do (
        set "GODOT_EXE=%%G"
        goto :found
    )
)

:found
if "%GODOT_EXE%"=="" (
    echo.
    echo ERROR: Could not find Godot executable!
    echo Please make sure Godot is placed inside this project folder.
    echo.
    pause
    exit /b
)

echo.
echo Found Godot: "%GODOT_EXE%"
echo Starting Tablet Osu Server natively...
echo.

"%GODOT_EXE%" --path "%~dp0." "scenes\v2_server.tscn"
