# ZZZTouchLauncher 离线验证测试

这些测试用于在**不启动真实游戏**的情况下验证 `ZZZTouchCore.dll` 的注入管线与
`ZZZTouchLauncher.exe` 的状态机分支。全部测试由假进程（fake_game）驱动，
不触碰真实游戏目录。

## 前置条件

- 下载 `ZZZ-TouchRuntime` 的配套 Release 包
- 将其中的 `ZZZTouchCore.dll`、`ZZZTouchRuntime.dll` 放到本目录

`--restore-pc` 测试不依赖 Hook DLL 或真实游戏，可单独运行：

```powershell
.\test_restore_pc.bat
# 预期：RestorePcSmoke=ok、PASS: --restore-pc smoke test passed
```

测试只会在被忽略的 `tests/build-restore/` 中创建假游戏目录和 Sleepy 配置，验证：

- `LocalUILayoutPlatform` 从触屏值 `1` 恢复为 PC 值 `2`；
- 其他配置字段保持不变；
- 重复恢复幂等；
- 未知参数返回退出码 `2` 且不改配置。

## 1. P/Invoke 冒烟测试（PInvokeSmoke.cs）

验证导出符号解析、调用约定、基础返回码。

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /platform:x64 /out:PInvokeSmoke.exe PInvokeSmoke.cs
.\PInvokeSmoke.exe
# 预期：Inject(nonexistent)=1（窗口未找到）、WaitGameExit(no session)=2、Release=ok，退出码 0
```

## 2. 注入全链路测试（fake_game.cpp + InjectSmoke.cs）

构造假 `UnityWndClass` 窗口进程，验证找窗口 → 事件创建 → Hook 安装 →
协议握手 → installFailed 路径（假进程无 GameAssembly.dll，必然安装失败）。

```powershell
# 构建假游戏进程（需要 CMake 或 VS 工具链）
cmake -S . -B build-fake
cmake --build build-fake --config Release

# 启动假游戏并注入（quiet=1）
.\build-fake\Release\fake_game.exe &   # 后台运行
.\InjectSmoke.exe <fake_game_pid>
# 预期：InjectResult=8（InstallFailed），退出码 0
```

quiet 验证：注入后**不应**生成 `ZZZTouchRuntime-<pid>.log`。

## 3. 非 quiet 对照（InjectSmokeNoisy.cs）

同样的注入但 `quiet=0`，**应**生成 `ZZZTouchRuntime-<pid>.log`（有内容）。
用于证明 quiet 开关精确控制日志输出。

## 4. 启动器三分支模拟

把 `fake_game.exe` 复制为 `ZenlessZoneZero.exe`，准备一个含
`GENERAL_DATA.bin` 副本的假游戏目录结构（`ZenlessZoneZero_Data/Persistent/LocalStorage/`），
config.json 指向该目录，运行 `ZZZTouchLauncher.exe`：

- 启动分支：游戏未运行 → 确保触屏 → 启动假游戏 → 注入失败(8) → 退出
- 接管分支：假游戏已运行 + 配置触屏(1) → 注入接管 → 失败退出
- PC 退出分支：假游戏已运行 + 配置 PC(2) → 打印原因退出、不动配置

注意：config.json 的 gamePath **必须指向假目录**，禁止写真实游戏路径。

## 已知行为

- 注入失败(8)后重试必然返回 6（协议事件残留，bridge FailInitialization
  不关闭事件句柄，一次会话后需重启游戏再注入）——启动器仅在窗口未找到(1)
  时重试，安装失败直接退出。
- 上述测试为 2026-08-09 离线验证所用脚本的沉淀，验证结论已记录在
  `.pai/plan/tools/20260809_自动触屏启动器.md`。

## 离线测试的边界

离线测试不会创建 Sunshine App Job，也不会覆盖以下实机行为：

- `controllerBreakaway=false/true` 对 Windows Job 归属的影响；
- Sunshine `Wait All`、Stop 和 `TerminateJobObject` 的生命周期；
- Controller 脱离 Job 后是否能在宿主结束游戏后继续收尾。

这些内容必须在 Windows + Sunshine + 真实游戏环境中单独验证。
