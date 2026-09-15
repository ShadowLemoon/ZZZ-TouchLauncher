# ZZZTouchLauncher

绝区零触屏运行时的启动器。

启动器会在启动游戏前将本地 UI 配置切换为触屏模式，启动后台 Controller 注入配套 Runtime；游戏窗口就绪后会将磁盘配置写回 PC 模式，并在游戏退出后再次确认。

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

- 游戏未启动：切换为触屏模式，启动游戏并交由后台 Controller 接管；
- 游戏已启动且为触屏模式：后台 Controller 接管注入与生命周期；
- 游戏已启动且为 PC 模式：不注入、不修改配置，直接退出。

## 恢复 PC 配置

需要手动恢复本地 UI 配置时，运行：

```powershell
.\ZZZTouchLauncher.exe --restore-pc
```

该命令会把 `GENERAL_DATA.bin` 中的 `LocalUILayoutPlatform` 写为 PC 值 `2`，随后退出；不会启动游戏或注入 Runtime。

退出码：

- `0`：恢复成功；
- `1`：未记录游戏路径、找不到配置文件或写入失败；
- `2`：命令行参数无效。

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
