#include <windows.h>
#include <cstdio>

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    return DefWindowProcW(hwnd, msg, wp, lp);
}

int main()
{
    const HINSTANCE hInstance = GetModuleHandleW(nullptr);
    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"UnityWndClass";
    if (!RegisterClassW(&wc))
    {
        wprintf(L"RegisterClassW failed: %lu\n", GetLastError());
        return 1;
    }
    HWND hwnd = CreateWindowExW(
        0, L"UnityWndClass", L"FakeGame", WS_OVERLAPPEDWINDOW,
        100, 100, 800, 600, nullptr, nullptr, hInstance, nullptr);
    if (!hwnd)
    {
        wprintf(L"CreateWindowExW failed: %lu\n", GetLastError());
        return 1;
    }
    wprintf(L"FakeGame READY pid=%lu\n", GetCurrentProcessId());
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}
