@echo off
rem ============================================================
rem Offline smoke test for --restore-pc.
rem Uses only tests\build-restore fixtures; never touches real game data.
rem The fixture intentionally writes the legacy config containing only gamePath,
rem so this also verifies missing controllerBreakaway naturally defaults to false.
rem Usage: test_restore_pc.bat
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC="
for /f "delims=" %%i in ('where msbuild.exe 2^>nul') do if not defined CSC set "MSBUILD=%%i"
if defined MSBUILD (
  for %%i in ("%MSBUILD%") do set "CSC=%%~dpiRoslyn\csc.exe"
)
if not defined CSC set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "BUILD=build-restore"

if not exist "%CSC%" (
  echo FAIL: Roslyn csc.exe not found: %CSC%
  exit /b 1
)

if not exist "%BUILD%" mkdir "%BUILD%"

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
  /main:ZZZTouchLauncher.RestorePcSmoke ^
  /out:"%BUILD%\RestorePcSmoke.exe" RestorePcSmoke.cs ..\Sleepy.cs
if errorlevel 1 (
  echo FAIL: could not compile RestorePcSmoke
  exit /b 1
)

"%BUILD%\RestorePcSmoke.exe" "%CD%\%BUILD%\ZZZTouchLauncher.exe"
if errorlevel 1 (
  echo FAIL: --restore-pc smoke test failed
  exit /b 1
)

echo PASS: --restore-pc smoke test passed
exit /b 0
