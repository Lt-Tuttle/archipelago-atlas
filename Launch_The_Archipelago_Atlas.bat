@echo off
set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%AP_Atlas_Source"
"%SCRIPT_DIR%Godot_Engine\Godot_v4.3-stable_mono_win64\Godot_v4.3-stable_mono_win64.exe" --path . --build-solutions --headless --quit
start "" "%SCRIPT_DIR%Godot_Engine\Godot_v4.3-stable_mono_win64\Godot_v4.3-stable_mono_win64.exe" --path . "res://Scenes/Main_Window.tscn"
