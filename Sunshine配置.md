# Sunshine 配置与会话生命周期

ZZZTouchLauncher 现在把“启动入口”和“运行时控制器”拆成两个进程角色：

```text
Sunshine
  └─ ZZZTouchLauncher.exe          短生命周期启动入口
       ├─ ZZZTouchLauncher.exe --session <内部令牌>
       │    ├─ 持有 ZZZTouchCore / WH_GETMESSAGE Hook
       │    ├─ 启动并监视 ZenlessZoneZero.exe
       │    └─ 和游戏保持同生命周期
       └─ Runtime 注入成功后退出
```

`--session` 是启动器内部模式，只接受父入口创建的命名事件令牌，不需要也不应手工配置到 Sunshine。

## 为什么需要 Wait All

默认入口在 `ZZZTouchInjectToProcess()` 返回成功、Runtime 已完成安装之后就退出；真正长期持有 Core/HHOOK 的是 `--session` 子进程。

这个子进程通过普通 `Process.Start` 创建，不使用 detached/breakaway，因此会和游戏一起留在 Sunshine 管理的应用进程组中。

Sunshine 必须启用 **Wait for all processes / Wait All**。这样主入口退出后，只要 session controller 或游戏仍在进程组中，Sunshine 就继续把应用视为运行中。

不要把 session controller 配成 Detached Command。它应该属于当前 Sunshine 应用的生命周期：正常退出游戏时 controller 自己退出；Moonlight 执行 Stop / Quit App 时则让 Sunshine 一起结束 controller 和游戏。

## 配置文件恢复

启动游戏前，session controller 会暂时把 `GENERAL_DATA.bin` 的 `LocalUILayoutPlatform` 写为触屏模式（`1`）。游戏完成首次配置读取后，controller 会尽早把磁盘配置写回 PC 模式（`2`），但游戏内存中的触屏状态保持不变。

正常退出游戏时，controller 还会再确保一次 `LocalUILayoutPlatform=2`。

唯一仍需要 Sunshine 兜底的窗口是：

```text
GENERAL_DATA -> Touch
        ↓
游戏尚未完成初次读取 / 尚未写回 PC
        ↓
Moonlight 强制 Stop
```

此时 controller 和游戏会被 Sunshine 一起终止，无法依赖 controller 的退出清理。因此继续使用已有命令：

```powershell
.\ZZZTouchLauncher.exe --restore-pc
```

作为 **Command Preparations / Undo**。

## 推荐配置

Sunshine Web UI 中为该应用设置：

- **Command**：`ZZZTouchLauncher.exe` 的完整路径
- **Working Directory**：启动器所在目录
- **Run as Admin / Elevated**：启用
- **Wait for all processes / Wait All**：**必须启用**
- **Command Preparations**：新增一项
  - **Do**：`cmd /c exit 0`
  - **Undo**：`"D:\\Path\\To\\ZZZTouchLauncher.exe" --restore-pc`
  - **Elevated**：启用

`Do` 使用无副作用且始终成功的命令。首次运行时 `config.json` 可能尚不存在，如果把 `--restore-pc` 放到 Do，非零返回码会阻止 Sunshine 正常启动应用。

`ZZZTouchLauncher.exe` 自带 `requireAdministrator` manifest，因此主命令、session 子进程和 Undo 都在管理员权限边界下运行。

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

## 正常生命周期

```text
Sunshine 启动 ZZZTouchLauncher.exe
        ↓
入口创建内部 Ready 事件
        ↓
入口启动 ZZZTouchLauncher.exe --session <token>
        ↓
session 写 Touch / 启动游戏 / 调用 ZZZTouchInjectToProcess
        ↓
Runtime installSucceeded
        ↓
ZZZTouchInjectToProcess 返回 0
        ↓
session 置位 Ready
        ↓
父入口 return 0
        ↓
Sunshine wait-all 继续跟踪 session + game
        ↓
游戏客户区就绪 + 5 秒
        ↓
session 写回 PC
        ↓
游戏正常退出
        ↓
session 再确保 PC → ZZZTouchRelease → 退出
        ↓
Sunshine 应用进程组清空
```

Runtime 的生命周期和 Core/HHOOK 所有权没有改变：Runtime 仍然被 PIN 在游戏进程中，Core/HHOOK 仍由 session controller 持有到游戏退出。

## Moonlight 强制 Stop / Quit App

```text
Sunshine application job
  ├─ session controller
  └─ ZenlessZoneZero.exe
        ↓
Stop / Quit App
        ↓
Sunshine 结束整个应用进程组
        ↓
游戏地址空间销毁，Runtime 随游戏结束
        ↓
Command Preparation Undo
        ↓
ZZZTouchLauncher.exe --restore-pc
```

这里不要求 controller 的 `finally`、`ProcessExit` 或 `ZZZTouchRelease()` 能在强杀时执行。Runtime 已随游戏进程一起销毁；Undo 只负责保证磁盘配置最终回到 PC。

## 为什么不使用 Detached Commands

session controller 必须和游戏属于同一个 Sunshine 应用生命周期。如果把它 detached：

- Sunshine `wait-all` 无法使用 controller 作为应用仍在运行的成员；
- Stop / Quit App 后可能留下孤立 controller；
- controller 仍持有 Core/HHOOK，却失去与 Sunshine 会话的一致生命周期。

因此本项目不需要额外 watchdog，也不需要让 controller 逃离 Sunshine 的 Job。

## 验证

### 1. 启动入口是否正确退出

1. 从 Moonlight 启动应用。
2. 观察启动器输出，应先看到 session controller PID。
3. Runtime 注入成功后应看到：

   ```text
   Runtime 已就绪，会话由控制器继续持有；启动入口退出。
   ```

4. 此时应仍有一个 `ZZZTouchLauncher.exe --session ...` 和游戏进程存活。
5. Sunshine 串流不能因为父入口退出而结束。

如果第 5 步失败，优先确认 **Wait All** 是否启用。

### 2. 正常退出游戏

1. 进入游戏并确认触屏正常。
2. 等待客户区就绪后的自动写回。
3. 正常退出游戏。
4. session controller 应检测到游戏退出并自行结束。
5. 确认 `LocalUILayoutPlatform=2`。

### 3. 强制结束 Sunshine 会话

1. 启动游戏并确认 Runtime 已就绪。
2. 在游戏运行期间直接执行 **Stop / Quit App**。
3. Sunshine 应结束 session controller 和游戏。
4. 随后执行：

   ```powershell
   ZZZTouchLauncher.exe --restore-pc
   ```

5. Sunshine 日志中的 Undo 返回码应为 `0`。
6. 确认 `LocalUILayoutPlatform=2`。

### 4. 启动早期强制结束

这个场景专门验证 Undo：

1. 启动 Sunshine 应用。
2. 在 session 已把配置写成 Touch、但尚未完成客户区就绪 + 5 秒之前立即 Stop。
3. 确认 Undo 执行 `--restore-pc`。
4. 确认磁盘配置最终为 PC。

### 5. 首次运行

1. 删除启动器目录下的 `config.json`。
2. 通过 Sunshine 启动应用。
3. `Do` 的 `cmd /c exit 0` 应成功。
4. 按提示手动启动一次游戏，让 session controller 记录路径。
5. Runtime 注入成功后父入口退出，session 继续持有会话。
6. 强制 Stop 时 Undo 应能读取刚生成的 `config.json` 并恢复 PC。

## 故障排查

如果父入口一直显示“等待 Runtime 就绪”：

- 查看 session controller 后续输出；
- 检查游戏窗口是否已经创建；
- 检查 Core 注入返回码；
- 检查是否已有另一个 controller 占用同一游戏；
- 不要手工运行 `--session`，该参数只接受内部握手令牌。

如果父入口已经退出，但 Sunshine 随即结束应用：

- 确认 **Wait for all processes / Wait All** 已启用；
- 确认没有把 launcher 或 session 配到 Detached Commands；
- 检查 Sunshine 是否仍能看到游戏和 session controller 属于当前应用进程组。

如果 Undo 返回非零：

- 确认 Undo 中使用的是启动器完整路径；
- 确认 Command Preparation 勾选了 Elevated；
- 确认 `config.json` 与 `ZZZTouchLauncher.exe` 位于同一目录；
- 手动执行 `ZZZTouchLauncher.exe --restore-pc`，根据输出定位路径或配置文件错误。
