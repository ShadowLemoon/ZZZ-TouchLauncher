# Sunshine 配置与退出恢复

ZZZTouchLauncher 在启动游戏前会暂时把 `GENERAL_DATA.bin` 的 `LocalUILayoutPlatform` 切换为触屏模式（`1`），并在游戏完成首次配置读取后写回 PC 模式（`2`）。

当启动器作为 Sunshine 应用的主命令运行时，停止串流/关闭应用会由 Sunshine 终止该应用的进程组。此时不要依赖启动器自身的 `finally`、`ProcessExit` 或“等待游戏退出后恢复”路径：进程被直接终止时，这些清理逻辑没有机会可靠执行。

仓库已经提供 `--restore-pc`：

```powershell
.\ZZZTouchLauncher.exe --restore-pc
```

Sunshine 的正确接入方式是把这个命令配置为 **Command Preparations / Undo**。Sunshine 在停止应用时会先结束应用进程组，再执行 Undo，因此恢复动作不依赖已被终止的 launcher 进程。

## 推荐配置

Sunshine Web UI 中为该应用设置：

- **Command**：`ZZZTouchLauncher.exe` 的完整路径
- **Working Directory**：启动器所在目录
- **Run as Admin / Elevated**：启用
- **Wait for all processes / Wait All**：保持启用
- **Command Preparations**：新增一项
  - **Do**：`cmd /c exit 0`
  - **Undo**：`"D:\\Path\\To\\ZZZTouchLauncher.exe" --restore-pc`
  - **Elevated**：启用

`Do` 使用无副作用的成功命令，避免首次运行时 `config.json` 尚未生成导致 Sunshine 阻止应用启动。真正的恢复只放在 `Undo`。

> `ZZZTouchLauncher.exe` 自带 `requireAdministrator` manifest，因此 Sunshine 的主命令和 Undo 都应以 elevated 方式启动。

## apps.json 示例

把路径替换为实际安装目录：

```json
{
  "name": "ZZZ Touch",
  "cmd": "\"D:\\\\Tools\\\\ZZZTouchLauncher\\\\ZZZTouchLauncher.exe\"",
  "working-dir": "D:\\Tools\\ZZZTouchLauncher",
  "elevated": true,
  "wait-all": true,
  "exit-timeout": 5,
  "prep-cmd": [
    {
      "do": "cmd /c exit 0",
      "undo": "\"D:\\\\Tools\\\\ZZZTouchLauncher\\\\ZZZTouchLauncher.exe\" --restore-pc",
      "elevated": true
    }
  ]
}
```

不要把恢复命令放到 **Detached Commands**。Detached 只负责“启动后不再跟踪”的命令，不表示会在会话结束时执行；退出恢复应使用 `prep-cmd.undo`。

## 为什么不在 launcher 内部加退出钩子

Sunshine 会管理主命令及其子进程的生命周期。用户从 Moonlight 停止应用、切换 Sunshine 应用，或者 Sunshine 主动结束当前应用时，launcher 可能和游戏一起被终止。

因此下面这些方案都只能覆盖“正常退出”，不能覆盖 Sunshine 强制结束：

- `try/finally`
- `AppDomain.ProcessExit`
- `Console.CancelKeyPress`
- 等待 `ZenlessZoneZero.exe` 退出后再恢复

把恢复职责放在 Sunshine 的 Undo 中，恢复动作由进程组之外的 Sunshine 自己触发，才与应用进程的存活状态解耦。

## 验证

### 1. 正常退出

1. 从 Moonlight 启动该 Sunshine 应用。
2. 进入游戏，确认触屏布局与注入正常。
3. 正常退出游戏。
4. 确认 `LocalUILayoutPlatform=2`。

### 2. 强制结束 Sunshine 会话（本修复的核心场景）

1. 从 Moonlight 启动该 Sunshine 应用。
2. 在游戏运行期间直接执行 **Stop / Quit App**，不要先正常退出游戏。
3. Sunshine 会结束 launcher/游戏进程组，然后执行：

   ```powershell
   ZZZTouchLauncher.exe --restore-pc
   ```

4. 检查 Sunshine 日志，应能看到 Undo 命令被执行且返回码为 `0`。
5. 使用配置探针或其他现有验证方式确认 `LocalUILayoutPlatform=2`。
6. 再手动启动一次游戏，确认仍为正常 PC 布局。

### 3. 首次运行

1. 删除启动器目录下的 `config.json`。
2. 通过 Sunshine 启动应用。
3. `Do` 的 `cmd /c exit 0` 应成功，不会阻止启动。
4. 按启动器提示手动启动一次游戏，让它记录路径。
5. 停止 Sunshine 应用。
6. Undo 应读取新生成的 `config.json` 并恢复 PC 模式。

## 故障排查

如果 Sunshine 日志显示 Undo 返回非零：

- 确认 Undo 中使用的是启动器的**完整路径**；
- 确认该 Command Preparation 勾选了 **Elevated**；
- 确认 `config.json` 与 `ZZZTouchLauncher.exe` 位于同一目录；
- 手动执行 `ZZZTouchLauncher.exe --restore-pc`，根据启动器输出定位路径或配置文件错误。

这套配置仍保留启动器当前“窗口客户区就绪后尽早写回 PC”的正常路径；Sunshine Undo 是额外的兜底，用来处理主进程被 Sunshine 强制终止的生命周期边界。