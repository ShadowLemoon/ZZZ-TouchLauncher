using System;
using System.Runtime.InteropServices;

internal static class PInvokeSmoke
{
    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchInjectToProcess(
        uint pid, [MarshalAs(UnmanagedType.Bool)] bool quiet, uint windowWaitMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ZZZTouchWaitGameExit(uint timeoutMs);

    [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ZZZTouchRelease();

    private static int Main()
    {
        // 不存在的 pid：应返回 1（WindowNotFound），且快速返回（500ms 窗口等待）。
        int result = ZZZTouchInjectToProcess(0xFFFFFFF0, true, 500);
        Console.WriteLine("Inject(nonexistent)=" + result);

        // 无会话时 WaitGameExit 应返回 2。
        int wait = ZZZTouchWaitGameExit(10);
        Console.WriteLine("WaitGameExit(no session)=" + wait);

        ZZZTouchRelease();
        Console.WriteLine("Release=ok");
        return result == 1 && wait == 2 ? 0 : 1;
    }
}
