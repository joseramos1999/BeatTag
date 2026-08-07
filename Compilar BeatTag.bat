@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Compilando BeatTag.exe (requiere el SDK de .NET 9)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Compilar-BeatTag.ps1" %*
