# ZZZTouchLauncher

绝区零触屏运行时的启动器。

启动器启动游戏并启动后台 Controller 注入配套 Runtime。Runtime 在游戏进程内固定触屏布局、合成多指触点，让界面和摇杆直接读触点；**启动器不读写任何游戏配置**（`GENERAL_DATA.bin`），崩溃或异常退出也没有配置残留。

## 下载与文件布局

从 [Releases](../../releases) 下载发布包并解压到同一目录。发布包固定包含：

- `ZZZTouchLauncher.exe`
- `ZZZTouchCore.dll`
- `ZZZTouchRuntime.dll`

三个文件必须保持在同一目录。

## 使用

直接运行 `ZZZTouchLauncher.exe`。

首次运行时，若尚未记录游戏路径，启动器会提示手动启动一次 `ZenlessZoneZero.exe`，随后将游戏目录写入启动器同目录的 `config.json`。

之后运行启动器时：

- 游戏未启动：启动游戏并交由后台 Controller 接管；
- 游戏已启动：**不接管、不注入**，提示先退出游戏再用启动器启动。触屏布局必须在游戏启动前注入，界面是在游戏启动时按当时的布局建好的，中途才固定布局不会让已建好的界面切换。

## 等进程模式（`--wait`）

```powershell
.\ZZZTouchLauncher.exe --wait
```

启动器**不启动游戏**，而是等待游戏进程出现：你自己用 HoYoPlay、Steam 或任何方式启动游戏，进程一出现（每 50ms 轮询）启动器就立即启动后台 Controller 注入。

- 触屏布局必须在游戏界面建好之前固定，所以要先运行 `--wait`、再启动游戏；如果游戏已经在运行，启动器提示先退出游戏并以退出码 1 退出；
- `--wait` 不能与游戏参数组合（启动器没有启动游戏，没有地方透传），组合时输出用法并以退出码 2 退出；
- 不依赖 `config.json` 里的游戏路径；
- 等待期间关闭控制台或按 `Ctrl+C` 取消，退出码 1。

## Steam 模式

如果 `gamePath` 目录下存在 `steam_appid.txt`，启动器会自动切换到 Steam 模式：

- 不直接启动 `ZenlessZoneZero.exe`；
- 通过 `steam://run/4162040//` 请求 Steam 启动 AppID `4162040`；
- 等待 Steam 真正拉起 `ZenlessZoneZero.exe` 后，再启动后台 Controller；
- 启动器收到的游戏参数会整体编码后传入 Steam URI。

Steam 启动请求提交后，启动器会一直等待游戏进程出现，不设置自动超时。等待期间关闭控制台窗口、按 `Ctrl+C` 或系统注销/关机时，会尝试把配置恢复为 PC 模式。Steam 可能要求用户登录、更新或确认启动；如果不希望继续等待，可关闭控制台。

Steam 客户端和启动器最好使用相同的权限级别。启动器要求管理员权限，而 Steam 若以普通权限运行，Steam 启动或游戏注入可能受到 Windows 权限隔离影响。

## 游戏启动参数

传给启动器的非保留参数会在启动新游戏时透传给 `ZenlessZoneZero.exe`：

```powershell
.\ZZZTouchLauncher.exe <游戏参数...>
```

参数值中的空格、空参数、双引号和反斜杠会按 Windows 命令行规则保留。

- `--controller` 是内部使用的保留参数，不会透传，也不能与游戏参数组合使用；`--wait` 见上文，同样不能与游戏参数组合；

## 配置

`config.json` 位于启动器同目录：

```json
{
  "gamePath": "C:\\Program Files\\HoYoPlay\\games\\ZenlessZoneZero Game",
  "controllerBreakaway": false
}
```

- `gamePath`：游戏目录，首次成功识别游戏进程后自动写入；
- `controllerBreakaway`：是否让后台 Controller 脱离当前 Windows Job，默认 `false`。

只有宿主 Job 明确允许 `CREATE_BREAKAWAY_FROM_JOB` 时才应设为 `true`，否则后台 Controller 无法启动。

## 注意事项

- 仅支持 Windows x64。
- 启动器需要访问游戏进程；若提示无法打开游戏进程句柄，请以管理员权限运行。
- 多开场景不完整支持；发现多个游戏进程时只处理第一个。
- 注入 Runtime 可能受游戏版本、系统环境或安全软件影响。本项目不保证规避任何检测，也不保证兼容所有版本。
- 若注入失败后提示已有注入会话，请完全退出游戏后再重试。

## 构建

项目目标框架为 .NET Framework 4.7.2，需要 Visual Studio 的 MSBuild：

```powershell
msbuild ZZZTouchLauncher.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU
```

## Runtime 固定版本

Runtime 的版本、发布资产名和 SHA-256 校验值固定在 [runtime-pin.json](runtime-pin.json)。CI 会校验 ZIP、归档文件清单和两个 DLL 的 SHA-256 后再打包发布。

手动升级固定版本：

```powershell
pwsh -File tools\update_runtime_pin.ps1 -Version v1.0.3
```

只预览而不修改文件：

```powershell
pwsh -File tools\update_runtime_pin.ps1 -Version v1.0.3 -WhatIf
```

## 许可证

本仓库中的启动器源码以 [MIT License](LICENSE) 发布。

发布包内的 `ZZZTouchCore.dll` 和 `ZZZTouchRuntime.dll` 为专有组件，不适用 MIT License；除非另有明确书面授权，不得基于这些 DLL 进行复制、修改、再分发或逆向工程。
