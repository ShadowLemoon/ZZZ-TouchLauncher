using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ZZZTouchLauncher
{
    // 生命周期状态机：
    //   默认启动入口
    //   └─ 启动同一 EXE 的 --session 子进程 → 等 Runtime 注入成功 → 入口退出
    //
    //   --session（内部模式，和游戏同生命周期）
    //   ├─ 首次记录路径 → 确保触屏(1) → 注入 → 通知父入口 → 写回PC(2) → 常驻
    //   ├─ 游戏已运行？ → 读配置
    //   │   ├─ 触屏(1) → 注入接管 → 通知父入口 → 游戏退出 → 确保PC(2) → 退出
    //   │   └─ 非触屏(2) → 无需接管 → 退出
    //   └─ 未运行 → 确保触屏(1) → 启动游戏 → 注入 → 通知父入口 → 写回PC(2) → 常驻
    //       └─ 游戏退出 → 确保PC(2) → 退出
    //
    //   --restore-pc
    //   └─ Sunshine Undo / 手工恢复：将磁盘配置恢复为PC(2) → 退出
    internal static class Program
    {
        private const string GameExecutableName = "ZenlessZoneZero.exe";
        private const string InternalSessionArgument = "--session";
        private const string SessionReadyEventPrefix = "Local\\ZZZTouchLauncherSessionReady-";
        private const int PlatformTouch = 1;
        private const int PlatformPc = 2;

        private static readonly byte[] Magic = new byte[]
        {
            85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181,
            46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116
        };

        // ZZZTouchCore.dll 导出（C++，__cdecl）。
        // Core/HHOOK 的所有权始终留在 --session 进程，不交给短生命周期启动入口。
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

        private static string ReadGamePathFromConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    return null;
                }
                string content = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var match = Regex.Match(content, "\"gamePath\"\\s*:\\s*\"([^\"]*)\"");
                if (!match.Success)
                {
                    return null;
                }
                return match.Groups[1].Value.Replace("\\\\", "\\");
            }
            catch
            {
                return null;
            }
        }

        private static void WriteGamePathToConfig(string gamePath)
        {
            string content = "{\n  \"gamePath\": \"" +
                gamePath.Replace("\\", "\\\\") + "\"\n}\n";
            File.WriteAllText(ConfigPath, content, Encoding.UTF8);
        }

        private static Process FindRunningGame()
        {
            Process[] processes = Process.GetProcessesByName("ZenlessZoneZero");
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

        private static string WaitForGameAndRecordPath()
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

            WriteGamePathToConfig(gamePath);
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
            string gamePath = ReadGamePathFromConfig();
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

        private static int StartSessionController()
        {
            string token = Guid.NewGuid().ToString("N");
            string eventName = SessionReadyEventPrefix + token;

            using (var readyEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                eventName))
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
                    return 1;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    Arguments = InternalSessionArgument + " " + token,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                };

                Process session;
                try
                {
                    session = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("启动会话控制器失败：" + ex.Message);
                    return 1;
                }

                if (session == null)
                {
                    Console.WriteLine("启动会话控制器失败。");
                    return 1;
                }

                using (session)
                {
                    Console.WriteLine($"会话控制器已启动（PID={session.Id}），等待 Runtime 就绪...");
                    while (true)
                    {
                        if (readyEvent.WaitOne(100))
                        {
                            Console.WriteLine("Runtime 已就绪，会话由控制器继续持有；启动入口退出。");
                            return 0;
                        }

                        if (session.HasExited)
                        {
                            int exitCode = session.ExitCode;
                            if (exitCode == 0)
                            {
                                Console.WriteLine("会话控制器无需建立运行时会话；启动入口退出。");
                            }
                            else
                            {
                                Console.WriteLine($"会话控制器在 Runtime 就绪前退出（代码 {exitCode}）。");
                            }
                            return exitCode;
                        }
                    }
                }
            }
        }

        private static int InjectAndWait(
            Process game,
            bool quiet,
            string branch,
            string dataPath,
            Action notifyRuntimeReady)
        {
            Console.WriteLine($"[{branch}] 注入纯合成器（{(quiet ? "无日志" : "日志开启")}）...");
            int result = ZZZTouchInjectToProcess(
                (uint)game.Id,
                quiet,
                30000);
            if (result != 0)
            {
                Console.WriteLine($"[{branch}] 注入失败：{DescribeInjectResult(result)}");
                ZZZTouchRelease();
                return 1;
            }

            Console.WriteLine($"[{branch}] 注入成功，Runtime 已就绪。");
            notifyRuntimeReady();

            Console.WriteLine($"[{branch}] 监视游戏进程...");
            int wait = ZZZTouchWaitGameExit(uint.MaxValue);
            if (wait != 0)
            {
                Console.WriteLine($"[{branch}] 等待游戏退出失败（{wait}）");
            }
            else
            {
                Console.WriteLine($"[{branch}] 游戏已退出，确保 PC 模式...");
                TryWritePc(dataPath, "确保 PC 配置失败：");
            }

            ZZZTouchRelease();
            return 0;
        }

        private static int RunSession(Action notifyRuntimeReady)
        {
            Console.WriteLine("=== ZZZTouchLauncher Session ===");

            string gamePath = ReadGamePathFromConfig();
            bool recordedGamePath = false;

            string dataPath = null;
            string exePath = null;
            while (true)
            {
                if (string.IsNullOrEmpty(gamePath))
                {
                    Console.WriteLine("未检测到游戏路径（config.json 无记录）。");
                    Console.WriteLine($"请手动启动 {GameExecutableName}，启动器将自动记录其路径...");
                    gamePath = WaitForGameAndRecordPath();
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
                    gamePath = WaitForGameAndRecordPath();
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
                    Console.WriteLine("不注入、不修改配置，会话控制器退出。");
                    return 0;
                }

                Console.WriteLine("游戏已启动且为触屏模式，注入接管...");
                return InjectAndWait(
                    running,
                    true,
                    "接管",
                    dataPath,
                    notifyRuntimeReady);
            }

            Console.WriteLine("确保触屏模式...");
            try
            {
                WritePlatform(dataPath, PlatformTouch);
            }
            catch (Exception ex)
            {
                Console.WriteLine("写入触屏配置失败：" + ex.Message);
                return 1;
            }

            Process started = running;
            if (started == null)
            {
                Console.WriteLine("启动游戏...");
                try
                {
                    started = Process.Start(exePath);
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
                Console.WriteLine($"游戏进程已启动（PID={started.Id}），等待主窗口并注入...");
            }
            else
            {
                Console.WriteLine($"已获取游戏路径并更新触屏配置（PID={started.Id}），等待主窗口并注入...");
            }

            uint targetPid = (uint)started.Id;
            int injectResult = ZZZTouchInjectToProcess(targetPid, true, 60000);
            if (injectResult == 1)
            {
                Console.WriteLine("主窗口未找到，重新定位游戏进程重试...");
                Thread.Sleep(5000);
                Process latest = FindRunningGame();
                if (latest != null && (uint)latest.Id != targetPid)
                {
                    targetPid = (uint)latest.Id;
                    Console.WriteLine($"重新定位到 PID={targetPid}");
                }
                injectResult = ZZZTouchInjectToProcess(targetPid, true, 30000);
            }

            if (injectResult != 0)
            {
                Console.WriteLine($"注入失败：{DescribeInjectResult(injectResult)}");
                ZZZTouchRelease();
                return 1;
            }

            Console.WriteLine("注入成功，Runtime 已就绪。");
            notifyRuntimeReady();

            Console.WriteLine("等待游戏窗口客户区就绪...");
            if (!WaitForClientArea(targetPid, 120000))
            {
                Console.WriteLine("等待窗口客户区超时，保持触屏配置，不写回 PC。");
                Console.WriteLine("监视游戏进程...");
                int waitTimeout = ZZZTouchWaitGameExit(uint.MaxValue);
                if (waitTimeout != 0)
                {
                    Console.WriteLine($"等待游戏退出失败（{waitTimeout}）");
                }
                else
                {
                    Console.WriteLine("游戏已退出，确保 PC 模式...");
                    TryWritePc(dataPath, "确保 PC 配置失败：");
                }
                ZZZTouchRelease();
                Console.WriteLine("会话控制器退出。");
                return 0;
            }

            Console.WriteLine("窗口客户区就绪，延迟 5 秒后写回 PC 模式（游戏内存已是触屏）...");
            Thread.Sleep(5000);
            TryWritePc(dataPath, "写回 PC 配置失败：");

            Console.WriteLine("监视游戏进程...");
            int wait = ZZZTouchWaitGameExit(uint.MaxValue);
            if (wait != 0)
            {
                Console.WriteLine($"等待游戏退出失败（{wait}）");
            }
            else
            {
                Console.WriteLine("游戏已退出，确保 PC 模式...");
                TryWritePc(dataPath, "确保 PC 配置失败：");
            }

            ZZZTouchRelease();
            Console.WriteLine("会话控制器退出。");
            return 0;
        }

        private static int RunInternalSession(string token)
        {
            Guid parsed;
            if (!Guid.TryParseExact(token, "N", out parsed))
            {
                Console.WriteLine("内部会话参数无效。");
                return 2;
            }

            string eventName = SessionReadyEventPrefix + token;
            EventWaitHandle readyEvent;
            try
            {
                readyEvent = EventWaitHandle.OpenExisting(eventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Console.WriteLine("内部会话握手不存在，拒绝独立启动 --session。");
                return 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("打开内部会话握手失败：" + ex.Message);
                return 2;
            }

            using (readyEvent)
            {
                bool notified = false;
                Action notifyRuntimeReady = delegate
                {
                    if (notified)
                    {
                        return;
                    }
                    readyEvent.Set();
                    notified = true;
                };
                return RunSession(notifyRuntimeReady);
            }
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 2 && args[0] == InternalSessionArgument)
            {
                return RunInternalSession(args[1]);
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

            return StartSessionController();
        }
    }
}
