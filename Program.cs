using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ZZZTouchLauncher
{
    // 生命周期状态机：
    //   启动器运行
    //   ├─ 游戏已运行？ → 读配置
    //   │   ├─ 触屏(1) → 注入接管 → 不碰配置 → 游戏退出 → 退出
    //   │   └─ 非触屏(2) → 打印原因退出（不动配置）
    //   └─ 未运行 → 确保触屏(1) → 启动游戏 → 注入 → 写回PC(2) → 常驻
    //       └─ 游戏退出 → 确保PC(2) → 退出
    internal static class Program
    {
        private const string GameExecutableName = "ZenlessZoneZero.exe";
        private const int PlatformTouch = 1;
        private const int PlatformPc = 2;

        private static readonly byte[] Magic = new byte[]
        {
            85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181,
            46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116
        };

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
                // 启动器写回时会把 \ 转义为 \\；手工编辑可能只写单反斜杠，
                // 统一还原为标准 JSON 转义。
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
                        return false; // 停止枚举
                    }
                    return true;
                }, IntPtr.Zero);

                if (hwnd != IntPtr.Zero &&
                    GetClientRect(hwnd, out RECT rect) &&
                    rect.Width > 0 && rect.Height > 0)
                {
                    return true;
                }
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        // 轮询等待用户启动游戏进程，记录其可执行文件所在目录到 config.json。
        // 返回游戏路径；失败返回 null。
        private static string WaitForGameAndRecordPath()
        {
            Process observed = null;
            while (observed == null)
            {
                System.Threading.Thread.Sleep(1000);
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

        private static string DescribeInjectResult(int result)
        {
            switch (result)
            {
                case 0: return "成功";
                case 1: return "未找到游戏主窗口";
                case 2: return "加载 ZZZTouchFilterHook.dll 失败";
                case 3: return "获取合成器导出入口失败";
                case 4: return "打开游戏进程句柄失败（请以管理员权限运行）";
                case 5: return "已有另一个注入控制器占用（互斥锁）";
                case 6: return "协议事件创建失败（进程可能已有注入会话，请重启游戏）";
                case 7: return "安装 WH_GETMESSAGE Hook 失败";
                case 8: return "合成器安装失败（详见游戏侧行为）";
                case 9: return "游戏在安装确认前退出";
                default: return "未知错误(" + result + ")";
            }
        }

        private static int InjectAndWait(Process game, bool quiet, string branch)
        {
            Console.WriteLine($"[{branch}] 注入纯合成器（{(quiet ? "无日志" : "日志开启")}）...");            int result = ZZZTouchInjectToProcess(
                (uint)game.Id,
                quiet,
                30000);
            if (result != 0)
            {
                Console.WriteLine($"[{branch}] 注入失败：{DescribeInjectResult(result)}");
                ZZZTouchRelease();
                return 1;
            }
            Console.WriteLine($"[{branch}] 注入成功，监视游戏进程...");
            int wait = ZZZTouchWaitGameExit(uint.MaxValue);
            if (wait != 0)
            {
                Console.WriteLine($"[{branch}] 等待游戏退出失败（{wait}）");
            }
            else
            {
                Console.WriteLine($"[{branch}] 游戏已退出");
            }
            ZZZTouchRelease();
            return 0;
        }

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("=== ZZZTouchLauncher ===");

            // 游戏路径：仅来自 config.json；缺失或失效时回退到
            // 等待用户启动一次游戏、记录路径的自愈流程。
            string gamePath = ReadGamePathFromConfig();

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
                    continue;
                }
                break;
            }

            Process running = FindRunningGame();
            if (running != null)
            {
                // 接管分支：不碰配置。
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
                Console.WriteLine("游戏已启动且为触屏模式，注入接管...");
                return InjectAndWait(running, true, "接管");
            }

            // 启动分支：确保触屏 → 启动 → 注入 → 等待窗口客户区就绪 → 写回PC → 游戏退出后确保PC。
            // 游戏初次读取 GENERAL_DATA.bin 发生在主窗口客户区真正显示之后，
            // 因此写回 PC 必须等到客户区 > 0，否则游戏读到 PC 配置、触屏不生效。
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

            Console.WriteLine("启动游戏...");
            Process started;
            try
            {
                started = Process.Start(exePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("游戏启动失败：" + ex.Message);
                // 启动失败：文件未进入触屏读取路径，恢复 PC 保持干净状态。
                try
                {
                    WritePlatform(dataPath, PlatformPc);
                }
                catch
                {
                }
                return 1;
            }
            if (started == null)
            {
                Console.WriteLine("游戏启动失败");
                try
                {
                    WritePlatform(dataPath, PlatformPc);
                }
                catch
                {
                }
                return 1;
            }

            Console.WriteLine($"游戏进程已启动（PID={started.Id}），等待主窗口并注入...");
            uint targetPid = (uint)started.Id;
            int injectResult = ZZZTouchInjectToProcess(targetPid, true, 60000);
            if (injectResult == 1)
            {
                // 仅冷启动窗口出现慢或启动器进程链变化时重试；
                // 其他失败（安装失败/会话残留等）重试只会得到误导性的错误码。
                Console.WriteLine("主窗口未找到，重新定位游戏进程重试...");
                System.Threading.Thread.Sleep(5000);
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
                // 保持触屏配置：游戏已读入触屏且仍在运行，
                // 重跑启动器会走「已运行+触屏」接管分支重试注入。
                return 1;
            }

            Console.WriteLine("注入成功。等待游戏窗口客户区就绪...");
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
                    try
                    {
                        WritePlatform(dataPath, PlatformPc);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("确保 PC 配置失败：" + ex.Message);
                    }
                }
                ZZZTouchRelease();
                Console.WriteLine("启动器退出。");
                return 0;
            }
            // 客户区就绪后再等 5 秒，确保游戏已完成初次配置读取，再写回 PC。
            Console.WriteLine("窗口客户区就绪，延迟 5 秒后写回 PC 模式（游戏内存已是触屏）...");
            System.Threading.Thread.Sleep(5000);
            try
            {
                WritePlatform(dataPath, PlatformPc);
            }
            catch (Exception ex)
            {
                Console.WriteLine("写回 PC 配置失败：" + ex.Message);
            }

            Console.WriteLine("监视游戏进程...");
            int wait = ZZZTouchWaitGameExit(uint.MaxValue);
            if (wait != 0)
            {
                Console.WriteLine($"等待游戏退出失败（{wait}）");
            }
            else
            {
                Console.WriteLine("游戏已退出，确保 PC 模式...");
                try
                {
                    WritePlatform(dataPath, PlatformPc);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("确保 PC 配置失败：" + ex.Message);
                }
            }
            ZZZTouchRelease();
            Console.WriteLine("启动器退出。");
            return 0;
        }
    }
}
