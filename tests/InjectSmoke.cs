// 注入全链路测试：对 FakeGame（假 UnityWndClass 进程）调用 ZZZTouchInjectToProcess。
// 预期：找窗口成功 → Hook 安装成功 → bridge 初始化失败（无 GameAssembly.dll）
// → installFailed 事件 → 返回 8（InstallFailed）。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class InjectSmoke
{
    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchInjectToProcess(
        uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ZZZTouchRelease();

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: InjectSmoke <pid>");
            return 2;
        }
        uint pid = uint.Parse(args[0]);
        Console.WriteLine("Injecting into fake game pid=" + pid);
        int result = ZZZTouchInjectToProcess(pid, true, 5000);
        Console.WriteLine("InjectResult=" + result);
        ZZZTouchRelease();
        // 期望 8（InstallFailed：bridge 在无 GameAssembly.dll 的进程中初始化失败）。
        return result == 8 ? 0 : 1;
    }
}
