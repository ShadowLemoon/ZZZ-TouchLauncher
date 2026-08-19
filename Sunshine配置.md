# Sunshine 配置与 Controller 生命周期

ZZZTouchLauncher 把“启动游戏”和“长期持有 Runtime Controller”拆成两个进程角色，但仍然只发布一个 `ZZZTouchLauncher.exe`。

默认入口负责路径发现、临时写入 Touch、启动/定位游戏和创建后台 Controller；内部 `--controller <pid>` 负责 Core/HHOOK、Runtime 注入、5 秒写回以及等待游戏退出。

```text
ZZZTouchLauncher.exe
  ├─ 默认入口                 用户 / Sunshine 启动
  ├─ --controller <pid>       内部后台 Controller
  └─ --restore-pc             Sunshine Undo / 手工恢复
```

`--controller` 是内部实现参数，不需要手工配置到 Sunshine。

## config.json

配置现在作为对象统一序列化，不再用正则单独读写 `gamePath`。

示例：

```json
{
  "gamePath": "D:\\Games\\ZenlessZoneZero",
  "controllerBreakaway": false
}
```

字段含义：

- `gamePath`：游戏目录。
- `controllerBreakaway`：后台 Controller 是否请求脱离当前 Windows Job。

`controllerBreakaway` 是普通布尔值：

```text
false
→ Controller 使用 CREATE_NO_WINDOW
→ 正常继承 Launcher 当前所在的 Windows Job

true
→ Controller 使用 CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB
→ 成功时 Controller 不属于 Launcher 当前所在的 Windows Job
```

旧配置无需迁移。例如原来的：

```json
{
  "gamePath": "D:\\Games\\ZenlessZoneZero"
}
```

仍然可以直接读取；缺失的 `controllerBreakaway` 自然按 `false` 处理。只有配置下一次因为记录/更新游戏路径而真正序列化时，新字段才会自然写入，不会为了“升级配置”单独重写文件。

### Breakaway 的失败语义

Windows 规定：如果调用方本身位于 Job 中，使用 `CREATE_BREAKAWAY_FROM_JOB` 时该 Job 必须允许 breakaway；否则 `CreateProcess` 失败。

因此 `controllerBreakaway=true` 是严格配置：

```text
Controller breakaway 创建成功
→ 继续启动流程

Controller breakaway 创建失败
→ 不降级成普通继承 Controller
→ 尝试把磁盘配置恢复为 PC
→ Launcher 返回非零
```

这样不会出现“配置看起来是 breakaway，实际 Controller 仍被宿主 Job 一起终止”的静默退化。

## 首次运行与路径发现

首次运行流程保持不变。

当 `config.json` 不存在、没有 `gamePath`，或记录路径已失效时：

```text
Launcher 启动
↓
等待用户手工启动 ZenlessZoneZero.exe
↓
定位游戏目录
↓
更新 LauncherConfig.GamePath
↓
统一序列化 config.json
↓
继续 Touch / Controller 流程
```

第一次生成配置时 `controllerBreakaway` 的默认值是 `false`。

这条路径主要用于一次性发现游戏目录。由于游戏是用户在 Launcher 外部手工启动的，它是否属于 Sunshine 当前应用 Job 取决于启动方式；后续由 Launcher 自己启动游戏时，进程归属才是确定的。

## 正常启动生命周期

已记录有效 `gamePath` 且游戏尚未运行时：

```text
Launcher Entry
  │
  ├─ GENERAL_DATA → Touch
  │
  ├─ 直接启动 ZenlessZoneZero.exe
  │    └─ UseShellExecute=false
  │
  ├─ 启动隐藏 Controller --controller <gamePid>
  │    ├─ controllerBreakaway=false → 继承当前 Job
  │    └─ controllerBreakaway=true  → 请求脱离当前 Job
  │
  └─ Controller 创建成功后立即退出
```

游戏始终由默认入口直接启动，不由 breakaway Controller 启动。因此在 Sunshine 正常启动路径下，游戏仍然属于 Sunshine 管理的应用进程树；breakaway 只改变 Controller 的生命周期。

Controller 接手之后：

```text
Controller
  ↓
ZZZTouchInjectToProcess(gamePid)
  ↓
Core 持有 WH_GETMESSAGE HHOOK
Runtime 注入游戏并 PIN
  ↓
等待游戏客户区就绪
  ↓
等待 5 秒
  ↓
GENERAL_DATA → PC
  ↓
ZZZTouchWaitGameExit(INFINITE)
  ↓
游戏退出
  ↓
再次确保 GENERAL_DATA → PC
  ↓
ZZZTouchRelease
  ↓
Controller exit
```

5 秒等待属于 Controller，不再阻塞默认 Launcher 入口。

Runtime/Core 本身没有因为这个方案改变生命周期：游戏内 Runtime 仍然 PIN 到游戏退出；外部 Controller 仍然是 Core/HHOOK owner。

## 已运行游戏的接管

如果 Launcher 启动时已经存在游戏进程：

- 磁盘配置是 PC：保持现有行为，不注入、不修改配置，直接退出。
- 磁盘配置是 Touch：启动隐藏 Controller 接管现有 PID。

`controllerBreakaway=true` 只保证 Controller 请求脱离 Launcher 当前 Job，并不能把一个已经存在的游戏进程重新移动进 Sunshine Job。因此 Sunshine 场景下最完整的生命周期仍然是“由 Sunshine 启动 Launcher，再由 Launcher 启动游戏”。

## Sunshine：controllerBreakaway=false

默认值 `false` 时：

```text
Sunshine App Job
  ├─ Launcher Entry   （很快退出）
  ├─ Controller
  └─ ZenlessZoneZero.exe
```

Sunshine 应启用 **Wait for all processes / Wait All**。父入口退出后，Controller 和游戏仍然留在应用 Job 中。

Moonlight Stop / Quit App 时，Sunshine 可以同时结束游戏和 Controller。此模式下不能依赖 Controller 在强杀后完成清理，因此 `--restore-pc` Undo 是必要兜底。

## Sunshine：controllerBreakaway=true

开启后，目标进程关系是：

```text
Sunshine App Job
  ├─ Launcher Entry   （很快退出）
  └─ ZenlessZoneZero.exe

Job 外
  └─ Controller
       └─ 持有 Core/HHOOK
```

Moonlight Stop / Quit App 时：

```text
Sunshine 结束 App Job
↓
游戏退出
↓
Controller 不随 App Job 一起被杀
↓
ZZZTouchWaitGameExit 返回
↓
Controller 确保 PC + ZZZTouchRelease
↓
Controller 自行退出
```

这正是 breakaway 配置的目的：让游戏继续受 Sunshine 管理，而 Controller 有机会在游戏被 Sunshine 终止后完成自己的收尾。

但是否能成功 break away 取决于宿主 Job 的 Windows 限制。如果 Controller 创建时报 `CREATE_BREAKAWAY_FROM_JOB` 相关 Win32 错误，不应改代码做静默 fallback；应将配置改回 `false`，或者先确认宿主是否允许显式 breakaway。

`Wait All` 仍建议保持启用，因为 Sunshine 需要跟踪由 Launcher 启动的游戏进程；breakaway Controller 本身不用于维持 Sunshine 的应用进程组。

## 配置文件恢复与 Undo

正常路径会在游戏完成初次配置读取后尽早把磁盘配置恢复成 PC，并在游戏退出时再确保一次 PC。

仍然存在一个必须由 Sunshine 外部兜底的极短窗口：

```text
GENERAL_DATA → Touch
↓
游戏刚启动
↓
Controller 尚未成功创建 / 尚未来得及接管
↓
Sunshine 强制 Stop
```

因此无论 `controllerBreakaway` 是 `false` 还是 `true`，都建议保留：

```powershell
ZZZTouchLauncher.exe --restore-pc
```

作为 **Command Preparations / Undo**。

在 `breakaway=true` 下，正常情况下 Controller 和 Undo 最终都只会把相同字段写回 PC；Undo 的定位仍然是宿主级最后保险，而不是 Runtime 生命周期的一部分。

## Sunshine 推荐配置

Sunshine Web UI：

- **Command**：`ZZZTouchLauncher.exe` 的完整路径
- **Working Directory**：启动器所在目录
- **Run as Admin / Elevated**：启用
- **Wait for all processes / Wait All**：启用
- **Command Preparations**：
  - **Do**：`cmd /c exit 0`
  - **Undo**：`"D:\\Path\\To\\ZZZTouchLauncher.exe" --restore-pc`
  - **Elevated**：启用

`Do` 使用无副作用的成功命令，避免首次运行时尚未生成 `config.json` 导致启动被阻止。

`ZZZTouchLauncher.exe` 自带 `requireAdministrator` manifest，主入口、Controller 和 Undo 都应处在一致的管理员权限边界。

### apps.json 示例

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

## 验证

### 1. 老配置兼容

使用只包含 `gamePath` 的旧 `config.json`：

1. `--restore-pc` 应正常工作；
2. 不要求预先补 `controllerBreakaway`；
3. 缺字段按 `false` 处理。

仓库的 `RestorePcSmoke` 故意使用这一旧格式。

### 2. 普通继承模式

配置：

```json
"controllerBreakaway": false
```

验证：

1. Launcher 启动游戏和隐藏 Controller 后立即退出；
2. Controller 与游戏均保持运行；
3. 客户区就绪 + 5 秒后磁盘为 PC；
4. 正常退出游戏后 Controller 自行退出；
5. Sunshine Stop 时 Undo 能恢复 PC。

### 3. Breakaway 模式

配置：

```json
"controllerBreakaway": true
```

验证：

1. Controller 创建必须成功，不能出现 fallback；
2. Launcher 退出后游戏继续属于 Sunshine 应用生命周期；
3. Moonlight Stop / Quit App 后游戏被结束；
4. Controller 不应同时被 Sunshine 结束；
5. Controller 随后从 `ZZZTouchWaitGameExit` 返回并自行退出；
6. 最终 `LocalUILayoutPlatform=2`。

如果第 1 步因 Job breakaway 权限失败，当前宿主不支持该配置，Launcher 应返回非零并尝试恢复 PC。

### 4. 启动早期强制 Stop

专门验证 Undo：

1. 在写入 Touch 后、Controller 接管前尽快 Stop；
2. Sunshine 执行 `--restore-pc` Undo；
3. 最终磁盘配置必须为 PC。

## 故障排查

Controller 无法启动且 `controllerBreakaway=true`：

- 查看输出中的 Win32 错误码；
- 确认宿主 Windows Job 是否允许 `CREATE_BREAKAWAY_FROM_JOB`；
- 不要依赖自动 fallback，本实现故意 fail closed；
- 临时将 `controllerBreakaway` 改成 `false` 可回到普通继承模式。

Sunshine 在 Launcher 退出后立即认为应用结束：

- 确认 **Wait for all processes / Wait All** 已启用；
- 确认游戏确实由 Launcher 在当前 Sunshine 应用内启动；
- 已经在 Launcher 之前手工启动的游戏无法被重新归入 Sunshine Job。

Undo 返回非零：

- 确认 Undo 使用启动器完整路径；
- 确认 Command Preparation 启用了 Elevated；
- 确认 `config.json` 与 `ZZZTouchLauncher.exe` 位于同一目录；
- 手工运行 `ZZZTouchLauncher.exe --restore-pc` 查看具体错误。
