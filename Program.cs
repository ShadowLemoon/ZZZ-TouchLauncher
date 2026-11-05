using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace ZZZTouchLauncher
{
    // 生命周期：
    //   默认入口（orchestrator）
    //   ├─ 读取/记录 config.json（游戏路径）
    //   ├─ 游戏已运行 → 不接管（触屏布局必须在游戏启动前注入）→ 入口退出
    //   └─ 游戏未运行 → 启动游戏（直接或经 Steam）→ 启动隐藏 controller → 入口退出
    //
    //   --wait（等进程模式）
    //   ├─ 不启动游戏，等待用户自行启动游戏进程（比如经 HoYoPlay）
    //   └─ 进程一出现就启动隐藏 controller 注入 → 入口退出
    //
    //   --controller <pid>（内部模式）
    //   ├─ 持有 ZZZTouchCore / WH_GETMESSAGE Hook
    //   ├─ 注入 ZZZTouchRuntime（游戏进程内固定触屏布局、合成多指触点）
    //   └─ 等游戏退出 → Release → 退出
    //
    // 启动器不再读写任何游戏配置（GENERAL_DATA.bin）。
    internal static class Program
    {
        private const string GameExecutableName = "ZenlessZoneZero.exe";
        private const string GameProcessName = "ZenlessZoneZero";
        private const string SteamAppIdFileName = "steam_appid.txt";
        private const string SteamAppId = "4162040";
        private const string InternalControllerArgument = "--controller";
        // 等进程模式：启动器不启动游戏，等游戏进程出现后立即注入。
        private const string WaitArgument = "--wait";
        // 等进程模式的轮询间隔：触屏布局必须在界面建好之前固定，越早发现进程越好。
        private const int WaitPollIntervalMs = 50;
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateNoWindow = 0x08000000;

        [DataContract]
        private sealed class LauncherConfig
        {
            [DataMember(Name = "gamePath", Order = 1)]
            public string GamePath { get; set; }

            [DataMember(Name = "controllerBreakaway", Order = 2)]
            public bool ControllerBreakaway { get; set; }
        }

        // ZZZTouchCore.dll 导出（C++，__cdecl）。
        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZZZTouchInjectToProcess(
            uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZZZTouchWaitGameExit(uint timeoutMs);

        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ZZZTouchRelease();

        // user32：用于等待游戏主窗口客户区就绪（>0）后再写回 PC。
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // kernel32：创建隐藏 Controller，并按配置选择继承或脱离当前 Windows Job。
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate bool ConsoleCtrlHandler(uint controlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(
            ConsoleCtrlHandler handlerRoutine,
            bool add);

        private const uint CtrlCEvent = 0;
        private const uint CtrlBreakEvent = 1;
        private const uint CloseEvent = 2;
        private const uint LogoffEvent = 5;
        private const uint ShutdownEvent = 6;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        // 等待 Steam 拉起游戏期间监听控制台关闭 / Ctrl+C / 注销 / 关机，用于退出等待循环。
        internal sealed class ConsoleCloseScope : IDisposable
        {
            private readonly ConsoleCtrlHandler handler;
            private int closeRequested;
            private bool registered;

            internal bool CloseRequested
            {
                get { return Interlocked.CompareExchange(ref closeRequested, 0, 0) != 0; }
            }

            internal ConsoleCloseScope()
            {
                handler = HandleConsoleControl;
                if (!SetConsoleCtrlHandler(handler, true))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                registered = true;
            }

            internal bool HandleConsoleControl(uint controlType)
            {
                if (controlType != CtrlCEvent &&
                    controlType != CtrlBreakEvent &&
                    controlType != CloseEvent &&
                    controlType != LogoffEvent &&
                    controlType != ShutdownEvent)
                {
                    return false;
                }

                Interlocked.Exchange(ref closeRequested, 1);
                // Continue to the next handler so Windows can close the process normally.
                return false;
            }

            public void Dispose()
            {
                if (registered)
                {
                    SetConsoleCtrlHandler(handler, false);
                    registered = false;
                }
            }
        }

        private static string ConfigPath
        {
            get
            {
                string directory = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(directory, "config.json");
            }
        }

        private static LauncherConfig ReadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                return new LauncherConfig();
            }

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(LauncherConfig));
                using (var stream = new FileStream(
                    ConfigPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    LauncherConfig config = serializer.ReadObject(stream) as LauncherConfig;
                    return config ?? new LauncherConfig();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("读取 config.json 失败，将按未配置处理：" + ex.Message);
                return new LauncherConfig();
            }
        }

        private static void WriteConfig(LauncherConfig config)
        {
            var serializer = new DataContractJsonSerializer(typeof(LauncherConfig));
            using (var stream = new FileStream(
                ConfigPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            {
                serializer.WriteObject(stream, config);
            }
        }

        internal static string BuildCommandLineArguments(string[] arguments)
        {
            var commandLine = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (commandLine.Length > 0)
                {
                    commandLine.Append(' ');
                }
                AppendCommandLineArgument(commandLine, argument);
            }
            return commandLine.ToString();
        }

        private static void AppendCommandLineArgument(StringBuilder commandLine, string argument)
        {
            bool needsQuotes = argument.Length == 0;
            for (int i = 0; i < argument.Length && !needsQuotes; i++)
            {
                needsQuotes = char.IsWhiteSpace(argument[i]) || argument[i] == '"';
            }

            if (!needsQuotes)
            {
                commandLine.Append(argument);
                return;
            }

            commandLine.Append('"');
            int backslashCount = 0;
            foreach (char value in argument)
            {
                if (value == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (value == '"')
                {
                    commandLine.Append('\\', backslashCount * 2 + 1);
                }
                else
                {
                    commandLine.Append('\\', backslashCount);
                }

                backslashCount = 0;
                commandLine.Append(value);
            }

            commandLine.Append('\\', backslashCount * 2);
            commandLine.Append('"');
        }

        internal static bool HasSteamAppId(string gamePath)
        {
            return File.Exists(Path.Combine(gamePath, SteamAppIdFileName));
        }

        internal static string BuildSteamLaunchUri(string[] gameArguments)
        {
            string arguments = BuildCommandLineArguments(gameArguments);
            string encodedArguments = Uri.EscapeDataString(arguments);
            return "steam://run/" + SteamAppId + "//" + encodedArguments;
        }

        private static Process StartSteamGame(
            string[] gameArguments,
            ConsoleCloseScope closeScope)
        {
            string launchUri = BuildSteamLaunchUri(gameArguments);
            Console.WriteLine("检测到 steam_appid.txt，使用 Steam 启动游戏（AppID=" + SteamAppId + "）。");
            Console.WriteLine("Steam 启动 URI：" + launchUri);

            try
            {
                using (Process steamRequest = Process.Start(new ProcessStartInfo
                {
                    FileName = launchUri,
                    UseShellExecute = true,
                }))
                {
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Steam 启动失败：" + ex.Message);
                return null;
            }

            Console.WriteLine("Steam 启动请求已提交，等待游戏进程出现；关闭此控制台将放弃等待。");
            while (!closeScope.CloseRequested)
            {
                Process game = FindRunningGame();
                if (game != null)
                {
                    return game;
                }
                Thread.Sleep(1000);
            }

            return null;
        }

        private static Process FindRunningGame(bool warnOnMultiple = true)
        {
            Process[] processes = Process.GetProcessesByName(GameProcessName);
            if (processes.Length > 1)
            {
                if (warnOnMultiple)
                {
                    Console.WriteLine(
                        $"警告：检测到 {processes.Length} 个游戏进程，仅处理第一个（多开场景不完整支持）。");
                }
            }
            return processes.Length > 0 ? processes[0] : null;
        }

        // 轮询等待用户启动游戏进程，记录其可执行文件所在目录到 config.json。
        // 返回游戏路径；无法读取路径时返回 null。
        private static string WaitForGameAndRecordPath(LauncherConfig config)
        {
            Process observed = null;
            while (observed == null)
            {
                Thread.Sleep(1000);
                observed = FindRunningGame();
            }

            string gamePath;
            try
            {
                gamePath = Path.GetDirectoryName(observed.MainModule.FileName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法获取游戏进程路径：" + ex.Message);
                return null;
            }
            finally
            {
                observed.Dispose();
            }

            config.GamePath = gamePath;
            WriteConfig(config);
            Console.WriteLine($"已记录游戏路径：{gamePath}");
            return gamePath;
        }

        private static string DescribeInjectResult(int result)
        {
            switch (result)
            {
                case 0: return "成功";
                case 1: return "未找到游戏主窗口";
                case 2: return "加载 ZZZTouchRuntime.dll 失败";
                case 3: return "获取合成器导出入口失败";
                case 4: return "打开游戏进程句柄失败（请以管理员权限运行）";
                case 5: return "已有另一个注入控制器占用（互斥锁）";
                case 6: return "协议事件创建失败（进程可能已有注入会话，请重启游戏）";
                case 7: return "安装 WH_GETMESSAGE Hook 失败";
                case 8: return "合成器安装失败（详见游戏侧行为）";
                case 9: return "游戏在安装确认前退出";
                case 10: return "等待 Runtime 安装结果失败";
                default: return "未知错误(" + result + ")";
            }
        }

        // 默认入口只创建隐藏 Controller；注入、Hook 和游戏生命周期由 Controller 负责。
        // 按配置请求继承或脱离当前 Windows Job。
        private static bool StartController(uint gamePid, bool breakaway)
        {
            string launcherPath;
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    launcherPath = current.MainModule.FileName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法定位启动器自身路径：" + ex.Message);
                return false;
            }

            var startupInfo = new STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFO));

            var commandLine = new StringBuilder(
                "\"" + launcherPath + "\" " + InternalControllerArgument + " " + gamePid);

            uint creationFlags = CreateNoWindow;
            if (breakaway)
            {
                creationFlags |= CreateBreakawayFromJob;
            }

            PROCESS_INFORMATION processInformation;
            bool created = CreateProcess(
                launcherPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                IntPtr.Zero,
                AppDomain.CurrentDomain.BaseDirectory,
                ref startupInfo,
                out processInformation);

            if (!created)
            {
                int error = Marshal.GetLastWin32Error();
                string mode = breakaway ? "breakaway" : "inherit";
                Console.WriteLine(
                    $"启动后台 Controller 失败（{mode}，Win32={error}）：{new Win32Exception(error).Message}");
                if (breakaway)
                {
                    Console.WriteLine(
                        "controllerBreakaway=true 要求当前 Windows Job 允许 CREATE_BREAKAWAY_FROM_JOB。");
                }
                return false;
            }

            try
            {
                Console.WriteLine(
                    $"后台 Controller 已启动（PID={processInformation.dwProcessId}，breakaway={breakaway.ToString().ToLowerInvariant()}）。");
            }
            finally
            {
                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                }
                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                }
            }

            return true;
        }

        private static bool TryGetGameProcess(uint pid, out Process game)
        {
            game = null;
            try
            {
                Process candidate = Process.GetProcessById((int)pid);
                if (!string.Equals(
                    candidate.ProcessName,
                    GameProcessName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Dispose();
                    return false;
                }

                game = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Controller 内部模式：持有 Runtime/Hook，完成注入和游戏退出后的收尾。
        private static int RunController(uint initialPid)
        {
            Process game;
            if (!TryGetGameProcess(initialPid, out game))
            {
                Console.WriteLine($"Controller 启动失败：PID={initialPid} 不是可用的游戏进程。");
                return 1;
            }
            game.Dispose();

            uint targetPid = initialPid;
            int injectResult = ZZZTouchInjectToProcess(targetPid, true, 60000);
            if (injectResult == 1)
            {
                // 仅首次注入未找到游戏主窗口时重试；
                // 其他失败（安装失败/会话残留等）重试只会得到误导性的错误码。
                Thread.Sleep(5000);
                Process latest = FindRunningGame();
                if (latest != null)
                {
                    try
                    {
                        uint latestPid = (uint)latest.Id;
                        if (latestPid != targetPid)
                        {
                            targetPid = latestPid;
                        }
                    }
                    finally
                    {
                        latest.Dispose();
                    }
                }

                injectResult = ZZZTouchInjectToProcess(targetPid, true, 30000);
            }

            if (injectResult != 0)
            {
                Console.WriteLine($"Controller 注入失败：{DescribeInjectResult(injectResult)}");
                ZZZTouchRelease();
                return 1;
            }

            // Controller 继续持有 Hook，直到游戏退出；Runtime 在游戏进程内固定触屏布局，
            // 没有磁盘配置需要收尾。
            ZZZTouchWaitGameExit(uint.MaxValue);
            ZZZTouchRelease();
            return 0;
        }

        // 等进程模式：不启动游戏，等用户自行启动游戏进程，出现后立即启动 Controller。
        // 触屏布局必须在游戏界面建好之前固定，所以轮询间隔很短；已在运行的游戏不接管。
        private static int RunWaitMode()
        {
            LauncherConfig config = ReadConfig();

            Process existing = FindRunningGame();
            if (existing != null)
            {
                int runningPid = existing.Id;
                existing.Dispose();
                Console.WriteLine(
                    "游戏已在运行（PID=" + runningPid + "），不支持中途注入：触屏布局必须在游戏启动前注入。");
                Console.WriteLine("请先退出游戏，再重新运行启动器并等待游戏启动。");
                return 1;
            }

            Console.WriteLine($"等进程模式：等待 {GameProcessName} 启动（关闭此控制台或按 Ctrl+C 取消）...");
            ConsoleCloseScope closeScope;
            try
            {
                closeScope = new ConsoleCloseScope();
            }
            catch (Exception ex)
            {
                Console.WriteLine("无法注册控制台关闭处理器：" + ex.Message);
                return 1;
            }

            using (closeScope)
            {
                while (!closeScope.CloseRequested)
                {
                    Process game = FindRunningGame(false);
                    if (game != null)
                    {
                        try
                        {
                            Console.WriteLine($"检测到游戏进程（PID={game.Id}），立即注入。");
                            return StartController(
                                (uint)game.Id,
                                config.ControllerBreakaway) ? 0 : 1;
                        }
                        finally
                        {
                            game.Dispose();
                        }
                    }
                    Thread.Sleep(WaitPollIntervalMs);
                }
            }

            Console.WriteLine("已取消等待。");
            return 1;
        }

        // 默认 orchestrator：只负责进程编排，启动 Controller 后立即退出。
        private static int RunLauncher(string[] gameArguments)
        {
            LauncherConfig config = ReadConfig();
            // 游戏路径来自 config.json；缺失或失效时回退到等待用户启动一次游戏的自愈流程。
            string gamePath = config.GamePath;
            bool recordedGamePath = false;

            string exePath = null;
            while (true)
            {
                if (string.IsNullOrEmpty(gamePath))
                {
                    Console.WriteLine("未检测到游戏路径（config.json 无记录）。");
                    Console.WriteLine($"请手动启动 {GameExecutableName}，启动器将自动记录其路径...");
                    gamePath = WaitForGameAndRecordPath(config);
                    if (gamePath == null)
                    {
                        return 1;
                    }
                    recordedGamePath = true;
                }

                exePath = Path.Combine(gamePath, GameExecutableName);
                if (!File.Exists(exePath))
                {
                    Console.WriteLine(
                        $"游戏路径无效：{gamePath}（可执行文件不存在）");
                    Console.WriteLine($"请手动启动 {GameExecutableName}，启动器将重新记录路径...");
                    gamePath = WaitForGameAndRecordPath(config);
                    if (gamePath == null)
                    {
                        return 1;
                    }
                    recordedGamePath = true;
                    continue;
                }
                break;
            }

            Process running = FindRunningGame();
            if (running != null)
            {
                // 不支持中途注入：界面在游戏启动时就按启动那一刻的布局建好了，
                // 中途才固定「当前布局平台」不会让已建好的界面切换，所以不接管。
                int runningPid = running.Id;
                running.Dispose();
                if (recordedGamePath)
                {
                    Console.WriteLine(
                        "游戏路径已记录。游戏正在运行（PID=" + runningPid + "），本次不注入；");
                    Console.WriteLine("请退出游戏后重新运行启动器。");
                    return 0;
                }

                Console.WriteLine(
                    "游戏已在运行（PID=" + runningPid + "），不支持中途注入：触屏布局必须在游戏启动前注入。");
                Console.WriteLine("请先退出游戏，再用启动器启动。");
                return 1;
            }

            // 启动游戏 → 启动 Controller。
            Process started = null;
            ConsoleCloseScope steamCloseScope = null;
            try
            {
                {
                    if (HasSteamAppId(gamePath))
                    {
                        try
                        {
                            steamCloseScope = new ConsoleCloseScope();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("无法注册控制台关闭处理器：" + ex.Message);
                            return 1;
                        }

                        started = StartSteamGame(gameArguments, steamCloseScope);
                    }
                    else
                    {
                        Console.WriteLine("启动游戏...");
                        try
                        {
                            var gameStartInfo = new ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = BuildCommandLineArguments(gameArguments),
                                WorkingDirectory = gamePath,
                                UseShellExecute = false,
                            };
                            started = Process.Start(gameStartInfo);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("游戏启动失败：" + ex.Message);
                        }
                    }

                    if (started == null)
                    {
                        Console.WriteLine("游戏启动失败");
                        return 1;
                    }

                    Console.WriteLine($"游戏进程已启动（PID={started.Id}）。");
                }

                if (!StartController(
                    (uint)started.Id,
                    config.ControllerBreakaway))
                {
                    return 1;
                }
            }
            finally
            {
                if (started != null)
                {
                    started.Dispose();
                }
                if (steamCloseScope != null)
                {
                    steamCloseScope.Dispose();
                }
            }

            Console.WriteLine("后台 Controller 已接管后续注入与生命周期；启动器退出。");
            return 0;
        }

        private static int Main(string[] args)
        {
            if (args.Length == 2 &&
                args[0] == InternalControllerArgument &&
                uint.TryParse(args[1], out uint controllerPid) &&
                controllerPid > 0)
            {
                return RunController(controllerPid);
            }

            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
            }

            Console.WriteLine("=== ZZZTouchLauncher ===");

            foreach (string argument in args)
            {
                if (argument == InternalControllerArgument ||
                    (argument == WaitArgument && args.Length != 1))
                {
                    Console.WriteLine("用法：");
                    Console.WriteLine("  ZZZTouchLauncher.exe [游戏参数...]    启动游戏并注入");
                    Console.WriteLine("  ZZZTouchLauncher.exe --wait           等待游戏进程启动后注入（不能与游戏参数组合）");
                    return 2;
                }
            }

            if (args.Length == 1 && args[0] == WaitArgument)
            {
                return RunWaitMode();
            }

            return RunLauncher(args);
        }
    }
}
