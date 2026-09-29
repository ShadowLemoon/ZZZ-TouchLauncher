@echo off
rem ============================================================
rem Offline smoke test for game argument forwarding.
rem Uses only tests\build-arguments fixtures; never touches real game data.
rem Usage: test_argument_forwarding.bat
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC="
for /f "delims=" %%i in ('where msbuild.exe 2^>nul') do if not defined CSC set "MSBUILD=%%i"
if defined MSBUILD (
  for %%i in ("%MSBUILD%") do set "CSC=%%~dpiRoslyn\csc.exe"
)
if not defined CSC set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "BUILD=build-arguments"
set "GAME=%BUILD%\game"

if not exist "%CSC%" (
  echo FAIL: Roslyn csc.exe not found: %CSC%
  exit /b 1
)

if not exist "%GAME%" mkdir "%GAME%"

"%CSC%" /nologo /platform:x64 /target:exe ^
  /reference:System.Runtime.Serialization.dll ^
  /reference:System.Xml.dll ^
  /out:"%BUILD%\ZZZTouchLauncher.exe" ^
  ..\Program.cs ..\Sleepy.cs ..\Properties\AssemblyInfo.cs
if errorlevel 1 (
  echo FAIL: could not compile ZZZTouchLauncher
  exit /b 1
)

"%CSC%" /nologo /platform:x64 /target:exe ^
  /out:"%GAME%\ZenlessZoneZero.exe" ArgumentEchoGame.cs
if errorlevel 1 (
  echo FAIL: could not compile argument echo game
  exit /b 1
)

"%CSC%" /nologo /platform:x64 /target:exe ^
  /main:ZZZTouchLauncher.ArgumentForwardSmoke ^
  /out:"%BUILD%\ArgumentForwardSmoke.exe" ArgumentForwardSmoke.cs ..\Sleepy.cs
if errorlevel 1 (
  echo FAIL: could not compile ArgumentForwardSmoke
  exit /b 1
)

"%BUILD%\ArgumentForwardSmoke.exe" "%CD%\%BUILD%\ZZZTouchLauncher.exe" "%CD%\%GAME%"
if errorlevel 1 (
  echo FAIL: game argument forwarding smoke test failed
  exit /b 1
)

echo PASS: game argument forwarding smoke test passed
exit /b 0
