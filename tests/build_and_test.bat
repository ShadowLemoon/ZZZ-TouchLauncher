@echo off
rem ============================================================
rem ZZZTouchLauncher 离线回归测试（一键运行）
rem 不启动真实游戏，全部由假进程（fake_game）驱动。
rem 前置：已构建 ZZZ-TouchHook（build/Release 下三个 DLL）
rem 用法：build_and_test.bat
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CMAKE=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "HOOK_BUILD=..\..\ZZZ-TouchHook\build\Release"
set "PASS=0"
set "FAIL=0"

echo [1/5] 准备 DLL ...
if not exist ZZZTouchCore.dll copy /Y "%HOOK_BUILD%\ZZZTouchCore.dll" . >nul
if not exist ZZZTouchFilterHook.dll copy /Y "%HOOK_BUILD%\ZZZTouchFilterHook.dll" . >nul
if not exist ZZZTouchCore.dll (echo FAIL: ZZZTouchCore.dll 缺失 & goto :end)
if not exist ZZZTouchFilterHook.dll (echo FAIL: ZZZTouchFilterHook.dll 缺失 & goto :end)
echo      OK

echo [2/5] 编译 P/Invoke 冒烟 ...
"%CSC%" /nologo /platform:x64 /out:PInvokeSmoke.exe PInvokeSmoke.cs >nul
if errorlevel 1 (echo FAIL: 编译 PInvokeSmoke & goto :end)
PInvokeSmoke.exe >nul
if errorlevel 1 (echo FAIL: PInvokeSmoke 返回码异常 & goto :end)
echo      OK

echo [3/5] 构建假游戏进程 ...
if not exist build-fake (
  "%CMAKE%" -S . -B build-fake >nul || (echo FAIL: cmake 配置 & goto :end)
)
"%CMAKE%" --build build-fake --config Release >nul
if errorlevel 1 (echo FAIL: 构建 fake_game & goto :end)
if not exist build-fake\Release\fake_game.exe (echo FAIL: fake_game.exe 缺失 & goto :end)
echo      OK

echo [4/5] 编译注入测试 ...
"%CSC%" /nologo /platform:x64 /out:InjectSmoke.exe InjectSmoke.cs >nul
if errorlevel 1 (echo FAIL: 编译 InjectSmoke & goto :end)
"%CSC%" /nologo /platform:x64 /out:InjectSmokeNoisy.exe InjectSmokeNoisy.cs >nul
if errorlevel 1 (echo FAIL: 编译 InjectSmokeNoisy & goto :end)
echo      OK

echo [5/5] 运行注入全链路测试（quiet 无日志 vs 非 quiet 对照）...
rem 注意：同一进程一次注入失败后会残留协议事件（返回6），
rem 因此 quiet 与 noisy 必须各用独立的 fake_game 进程。

rem --- quiet 模式：预期 8，且无日志 ---
start /b build-fake\Release\fake_game.exe >nul 2>&1
timeout /t 2 /nobreak >nul
for /f "tokens=2 delims=," %%p in ('tasklist /FI "IMAGENAME eq fake_game.exe" /FO CSV /NH') do set PID=%%~p
if "%PID%"=="" (echo FAIL: fake_game 未启动 & goto :end)

InjectSmoke.exe %PID% >nul
if errorlevel 1 (echo FAIL: quiet 注入返回码 != 8 & goto :cleanup)
if exist ZZZTouchFilter-%PID%.log (echo FAIL: quiet 模式生成了日志 & goto :cleanup)
echo      OK (quiet: 返回8 + 无日志)
taskkill /F /IM fake_game.exe >nul 2>&1
timeout /t 1 /nobreak >nul

rem --- 非 quiet 对照：预期 8，且生成日志 ---
start /b build-fake\Release\fake_game.exe >nul 2>&1
timeout /t 2 /nobreak >nul
for /f "tokens=2 delims=," %%p in ('tasklist /FI "IMAGENAME eq fake_game.exe" /FO CSV /NH') do set PID=%%~p
if "%PID%"=="" (echo FAIL: fake_game 未启动(noisy) & goto :end)

InjectSmokeNoisy.exe %PID% >nul
if errorlevel 1 (echo FAIL: 非 quiet 注入返回码 != 8 & goto :cleanup)
if not exist ZZZTouchFilter-%PID%.log (echo FAIL: 非 quiet 模式未生成日志 & goto :cleanup)
echo      OK (noisy: 返回8 + 生成日志)

:cleanup
taskkill /F /IM fake_game.exe >nul 2>&1
del /Q ZZZTouchFilter-%PID%.log >nul 2>&1

echo.
echo ============================================================
echo 全部离线回归测试通过。
echo 下一步：按 ..\实机验证指引.md 执行实机验证。
echo ============================================================
exit /b 0

:end
taskkill /F /IM fake_game.exe >nul 2>&1
echo.
echo 回归测试失败，请检查上方输出。
exit /b 1
