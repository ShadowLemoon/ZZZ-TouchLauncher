using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZZZTouchLauncher
{
    // 生命周期：
    //   默认入口（orchestrator）
    //   ├─ 读取/记录配置
    //   ├─ 已运行+Touch → 启动隐藏 controller 接管 → 入口退出
    //   ├─ 已运行+PC → 不接管 → 入口退出
    //   └─ 未运行/首次记录 → Touch → 启动/定位游戏 → 启动隐藏 controller → 入口退出
    //
    //   --controller <pid>（内部模式）
    //   ├─ 持有 ZZZTouchCore / WH_GETMESSAGE Hook
    //   ├─ 注入 Runtime
    //   ├─ 等客户区就绪 + 5 秒 → 写回 PC
    //   └─ 等游戏退出 → 再确保 PC → Release → 退出
    //
    //   --restore-pc
    //   └─ Sunshine Undo / 手工恢复：将磁盘配置恢复为 PC(2) → 退出
    internal static class Program
    {
        private const string GameExecutableName = "ZenlessZoneZero.exe";
        private const string GameProcessName = "ZenlessZoneZero";
        private const string InternalControllerArgument = "--controller";
        private const uint CreateBreakawayFromJob = 0x01000000;
        private const uint CreateNoWindow = 0x08000000;
        private const int PlatformTouch = 1;
        private const int PlatformPc = 2;

        private static readonly byte[] Magic = new byte[]
        {
            85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181,
            46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116
        };

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

        private static string ConfigPath
        {
            get
            {
                string directory = AppDomain.CurrentDomain.BaseDirectory;
                return Path.Combine(directory, "config.json");
            }
        }

        private static string GeneralDataPath(string gamePath)
        {
            return Path.Combine(
                gamePath,
                "ZenlessZoneZero_Data",
                "Persistent",
                "LocalStorage",
                "GENERAL_DATA.bin");
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

        private static Process FindRunningGame()
        {
            Process[] processes = Process.GetProcessesByName(GameProcessName);
            if (processes.Length > 1)
            {
                Console.WriteLine(
                    $"警告：检测到 {processes.Length} 个游戏进程，仅处理第一个（多开场景不完整支持）。");
            }
            return processes.Length > 0 ? processes[0] : null;
        }

        // 等待游戏主窗口客户区就绪（宽高均 > 0）。
        // 游戏初次读取 GENERAL_DATA.bin 发生在客户区真正显示之后，
        // 写回 PC 必须等到这一刻之后，否则游戏会读到 PC 配置、触屏不生效。
        private static bool WaitForClientArea(uint pid, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                IntPtr hwnd = IntPtr.Zero;
                EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    GetWindowThreadProcessId(h, out uint windowPid);
                    if (windowPid == pid && IsWindowVisible(h))
                    {
                        hwnd = h;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (hwnd != IntPtr.Zero &&
                    GetClientRect(hwnd, out RECT rect) &&
                    rect.Width > 0 && rect.Height > 0)
                {
                    return true;
                }
                Thread.Sleep(500);
            }
            return false;
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

        private static int GetCurrentPlatform(string raw)
        {
            var match = Regex.Match(raw, "LocalUILayoutPlatform\"\\s*:\\s*(\\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int mode))
            {
                return mode == 1 ? PlatformTouch : PlatformPc;
            }
            return PlatformPc;
        }

        private static string SetPlatform(string raw, int platform)
        {
            return Regex.Replace(
                raw,
                "(LocalUILayoutPlatform\")(\\s*:\\s*)(-?\\d+)",
                m => m.Groups[1].Value + m.Groups[2].Value + platform);
        }

        private static int ReadPlatform(string dataPath)
        {
            string raw = Sleepy.ReadString(dataPath, Magic);
            return GetCurrentPlatform(raw);
        }

        private static void WritePlatform(string dataPath, int platform)
        {
            string raw = Sleepy.ReadString(dataPath, Magic);
            raw = SetPlatform(raw, platform);
            Sleepy.WriteString(dataPath, raw, Magic);
        }

        private static bool TryWritePc(string dataPath, string failurePrefix)
        {
            try
            {
                WritePlatform(dataPath, PlatformPc);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(failurePrefix + ex.Message);
                return false;
            }
        }

        // 显式恢复入口：供 Sunshine Undo 或手工调用，把磁盘配置恢复为 PC 模式。
        private static int RestorePcConfiguration()
        {
            LauncherConfig config = ReadConfig();
            string gamePath = config.GamePath;
            if (string.IsNullOrEmpty(gamePath))
            {
                Console.WriteLine("恢复 PC 配置失败：config.json 未记录游戏路径。");
                return 1;
            }

            string dataPath = GeneralDataPath(gamePath);
            if (!File.Exists(dataPath))
            {
                Console.WriteLine("恢复 PC 配置失败：配置文件不存在：" + dataPath);
                return 1;
            }

            if (!TryWritePc(dataPath, "恢复 PC 配置失败："))
            {
                return 1;
            }

            Console.WriteLine("已恢复 PC 配置（LocalUILayoutPlatform=2）：" + dataPath);
            return 0;
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

        // Controller 内部模式：持有 Runtime/Hook，完成注入、配置切换和游戏退出后的收尾。
        private static int RunController(uint initialPid)
        {
            LauncherConfig config = ReadConfig();
            if (string.IsNullOrEmpty(config.GamePath))
            {
                Console.WriteLine("Controller 启动失败：config.json 未记录游戏路径。");
                return 1;
            }

            string dataPath = GeneralDataPath(config.GamePath);
            if (!File.Exists(dataPath))
            {
                Console.WriteLine("Controller 启动失败：配置文件不存在：" + dataPath);
                return 1;
            }

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
                // 保持触屏配置：游戏可能已读入触屏且仍在运行，重跑启动器会重新走接管分支。
                ZZZTouchRelease();
                return 1;
            }

            // 客户区未就绪时不能写回 PC；保持触屏配置，等待游戏退出后再尝试恢复。
            if (!WaitForClientArea(targetPid, 120000))
            {
                int waitTimeout = ZZZTouchWaitGameExit(uint.MaxValue);
                if (waitTimeout == 0)
                {
                    TryWritePc(dataPath, "确保 PC 配置失败：");
                }
                ZZZTouchRelease();
                return 0;
            }

            // 客户区就绪后再等 5 秒，确保游戏已完成初次配置读取，再写回 PC。
            // 此时游戏运行态已经是触屏，磁盘配置可以恢复为 PC。
            Thread.Sleep(5000);
            TryWritePc(dataPath, "写回 PC 配置失败：");

            // Controller 继续持有 Hook，直到游戏退出；退出后再次确保 PC 配置，再释放 Runtime。
            int wait = ZZZTouchWaitGameExit(uint.MaxValue);
            if (wait == 0)
            {
                TryWritePc(dataPath, "确保 PC 配置失败：");
            }

            ZZZTouchRelease();
            return 0;
        }

        // 默认 orchestrator：只负责配置/进程编排，启动 Controller 后立即退出。
        private static int RunLauncher()
        {
            LauncherConfig config = ReadConfig();
            // 游戏路径来自 config.json；缺失或失效时回退到等待用户启动一次游戏的自愈流程。
            string gamePath = config.GamePath;
            bool recordedGamePath = false;

            string dataPath = null;
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

                dataPath = GeneralDataPath(gamePath);
                exePath = Path.Combine(gamePath, GameExecutableName);
                if (!File.Exists(dataPath) || !File.Exists(exePath))
                {
                    Console.WriteLine(
                        $"游戏路径无效：{gamePath}（配置缺失或可执行文件不存在）");
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
            // 接管分支：不修改已有配置；仅在游戏已是触屏模式时启动 Controller。
            if (running != null && !recordedGamePath)
            {
                try
                {
                    int platform;
                    try
                    {
                        platform = ReadPlatform(dataPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("读取配置失败：" + ex.Message);
                        return 1;
                    }

                    if (platform != PlatformTouch)
                    {
                        Console.WriteLine(
                            $"游戏已启动且配置为 PC 模式（LocalUILayoutPlatform={platform}）。");
                        Console.WriteLine("不注入、不修改配置，启动器退出。");
                        return 0;
                    }

                    Console.WriteLine("游戏已启动且为触屏模式，启动后台 Controller 接管...");
                    return StartController(
                        (uint)running.Id,
                        config.ControllerBreakaway) ? 0 : 1;
                }
                finally
                {
                    running.Dispose();
                }
            }

            // 启动分支或首次路径自愈分支：确保触屏 → 启动/接管游戏 → 启动 Controller。
            // 注入、客户区等待、写回 PC 和游戏退出后的收尾由 Controller 完成。
            Console.WriteLine("确保触屏模式...");
            try
            {
                WritePlatform(dataPath, PlatformTouch);
            }
            catch (Exception ex)
            {
                Console.WriteLine("写入触屏配置失败：" + ex.Message);
                if (running != null)
                {
                    running.Dispose();
                }
                return 1;
            }

            Process started = running;
            if (started == null)
            {
                Console.WriteLine("启动游戏...");
                try
                {
                    var gameStartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = gamePath,
                        UseShellExecute = false,
                    };
                    started = Process.Start(gameStartInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("游戏启动失败：" + ex.Message);
                    // 启动失败：游戏未进入触屏读取路径，恢复 PC 保持干净状态。
                    TryWritePc(dataPath, "恢复 PC 配置失败：");
                    return 1;
                }

                if (started == null)
                {
                    Console.WriteLine("游戏启动失败");
                    // Process.Start 未返回进程对象，同样恢复 PC 配置。
                    TryWritePc(dataPath, "恢复 PC 配置失败：");
                    return 1;
                }

                Console.WriteLine($"游戏进程已启动（PID={started.Id}）。");
            }
            else
            {
                Console.WriteLine($"已获取游戏路径并更新触屏配置（PID={started.Id}）。");
            }

            try
            {
                if (!StartController(
                    (uint)started.Id,
                    config.ControllerBreakaway))
                {
                    // Controller 创建失败时没有接管者，恢复 PC 配置。
                    TryWritePc(dataPath, "恢复 PC 配置失败：");
                    return 1;
                }
            }
            finally
            {
                started.Dispose();
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

            if (args.Length == 1 && args[0] == "--restore-pc")
            {
                return RestorePcConfiguration();
            }

            if (args.Length != 0)
            {
                Console.WriteLine("用法：ZZZTouchLauncher.exe [--restore-pc]");
                return 2;
            }

            return RunLauncher();
        }
    }
}
