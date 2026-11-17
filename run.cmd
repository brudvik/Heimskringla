@echo off
rem Builds and starts Heimskringla. Optional: run.cmd 1234  (seed)
dotnet build -nologo -v q || exit /b 1
if "%1"=="" (
  "%~dp0tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe" --path "%~dp0"
) else (
  "%~dp0tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe" --path "%~dp0" -- --seed=%1
)
