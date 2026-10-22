@echo off
chcp 65001 >nul
cd /d "%~dp0"
AIBridgeAdapter.exe --engine hymt2
pause
