// 对照组：非 quiet 模式注入假游戏进程。
// 预期：返回 8（InstallFailed），且会创建 ZZZTouchRuntime-<pid>.log。
using System;
using System.Runtime.InteropServices;

internal static class InjectSmokeNoisy
{
    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchInjectToProcess(
        uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ZZZTouchRelease();

    private static int Main(string[] args)
    {
        uint pid = uint.Parse(args[0]);
        int result = ZZZTouchInjectToProcess(pid, false, 5000);
        Console.WriteLine("InjectResult(noisy)=" + result);
        ZZZTouchRelease();
        return result == 8 ? 0 : 1;
    }
}
