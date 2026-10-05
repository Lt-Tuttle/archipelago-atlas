@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
set "GODOT=%SCRIPT_DIR%Godot_Engine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
cd /d "%SCRIPT_DIR%AP_Atlas_Source"

rem Build the C# scripts first. "dotnet build" is preferred: it shows any errors here, and Godot 4.3's own
rem "--build-solutions --quit" crashed on exit whenever it recompiled. Godot's build is only the fallback.
where dotnet >nul 2>nul
if %errorlevel%==0 (
    echo Building AP Atlas...
    dotnet build AP_Atlas.sln -nologo -v q
    if errorlevel 1 (
        echo.
        echo Build failed - see the errors above. The app was not started.
        pause
        exit /b 1
    )
) else (
    echo .NET SDK not found on PATH, building through Godot instead...
    "%GODOT%" --path . --build-solutions --headless --quit
)

start "" "%GODOT%" --path . "res://Scenes/Main_Window.tscn"
