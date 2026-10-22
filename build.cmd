@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4.8 compiler not found: %CSC%
  exit /b 1
)

"%CSC%" -nologo -target:exe -platform:anycpu -out:AIBridgeAdapter.exe ^
  -r:System.dll -r:System.Core.dll -r:System.Web.Extensions.dll -r:System.Net.Http.dll ^
  src\AIBridgeAdapter.cs

if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo Built AIBridgeAdapter.exe
