@echo off
rem ============================================================
rem Offline smoke test for --restore-pc.
rem Uses only tests\build-restore fixtures; never touches real game data.
rem Usage: test_restore_pc.bat
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
set "BUILD=build-restore"

if not exist "%CSC%" (
  echo FAIL: Roslyn csc.exe not found: %CSC%
  exit /b 1
)

if not exist "%BUILD%" mkdir "%BUILD%"

"%CSC%" /nologo /platform:x64 /target:exe /out:"%BUILD%\ZZZTouchLauncher.exe" ^
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
