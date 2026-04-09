using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EnergyStarZ.Interop
{
    internal class HookManager : IDisposable
    {
        private const int WINEVENT_INCONTEXT = 4;
        private const int WINEVENT_OUTOFCONTEXT = 0;
        private const int WINEVENT_SKIPOWNPROCESS = 2;
        private const int WINEVENT_SKIPOWNTHREAD = 1;

        private const int EVENT_SYSTEM_FOREGROUND = 3;

        private static IntPtr windowEventHook;
        // Explicitly declare it to prevent GC
        // See: https://stackoverflow.com/questions/6193711/call-has-been-made-on-garbage-collected-delegate-in-c
        private static WinEventProc hookProcDelegate = WindowEventCallback;

        private bool disposedValue;

        public static void SubscribeToWindowEvents()
        {
            if (windowEventHook == IntPtr.Zero)
            {
                windowEventHook = SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND, // eventMin
                    EVENT_SYSTEM_FOREGROUND, // eventMax
                    IntPtr.Zero,             // hmodWinEventProc
                    hookProcDelegate,        // lpfnWinEventProc
                    0,                       // idProcess
                    0,                       // idThread
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

                if (windowEventHook == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        public static void UnsubscribeWindowEvents()
        {
            if (windowEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(windowEventHook);
                windowEventHook = IntPtr.Zero;
            }
        }

        public static void WindowEventCallback(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                // 检查当前模式，如果处于暂停模式则不处理事件
                if (EnergyManager.CurrentMode != PowerMode.Paused)
                {
                    EnergyManager.HandleForegroundEvent(hwnd);
                }
            }
            catch (Exception ex)
            {
                // 记录异常但不抛出，防止 Windows 移除钩子
                Console.WriteLine($"[{DateTime.UtcNow:O}] [ERROR] WindowEventCallback: {ex.Message}");
            }
        }

        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(int eventMin, int eventMax,
            IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, int idProcess, int idThread, int dwflags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int UnhookWinEvent(IntPtr hWinEventHook);
        
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // 释放托管资源
                }
                
                // 释放非托管资源
                UnsubscribeWindowEvents();
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}