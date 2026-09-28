@echo off
rem Double-click: pick a save and see which of your enabled mods it uses. Read-only.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0SaveInspector.ps1" %*
pause
