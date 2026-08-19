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

        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZZZTouchInjectToProcess(
            uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZZZTouchWaitGameExit(uint timeoutMs);

        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ZZZTouchRelease();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

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

            Thread.Sleep(5000);
            TryWritePc(dataPath, "写回 PC 配置失败：");

            int wait = ZZZTouchWaitGameExit(uint.MaxValue);
            if (wait == 0)
            {
                TryWritePc(dataPath, "确保 PC 配置失败：");
            }

            ZZZTouchRelease();
            return 0;
        }

        private static int RunLauncher()
        {
            LauncherConfig config = ReadConfig();
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
                    TryWritePc(dataPath, "恢复 PC 配置失败：");
                    return 1;
                }

                if (started == null)
                {
                    Console.WriteLine("游戏启动失败");
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
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 2 &&
                args[0] == InternalControllerArgument &&
                uint.TryParse(args[1], out uint controllerPid) &&
                controllerPid > 0)
            {
                return RunController(controllerPid);
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
