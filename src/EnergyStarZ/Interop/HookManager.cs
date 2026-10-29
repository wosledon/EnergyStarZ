using EnergyStarZ.Utilities;
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

        // out-of-context 的 WinEvent 钩子只能由运行消息循环的线程安装/卸载，
        // 后台线程安装的钩子收不到前台切换事件，因此统一封送到 UI 线程执行
        private static Action<Action>? _uiInvoker;

        private static IntPtr windowEventHook;
        // Explicitly declare it to prevent GC
        // See: https://stackoverflow.com/questions/6193711/call-has-been-made-on-garbage-collected-delegate-in-c
        private static WinEventProc hookProcDelegate = WindowEventCallback;

        private bool disposedValue;

        public static void SetUiInvoker(Action<Action> uiInvoker)
        {
            _uiInvoker = uiInvoker;
        }

        public static void SubscribeToWindowEvents()
        {
            if (_uiInvoker is { } invoker)
            {
                invoker(static () =>
                {
                    try
                    {
                        SubscribeToWindowEventsCore();
                    }
                    catch (Exception ex)
                    {
                        // 封送到 UI 线程后异常无法传回调用方，记录日志即可
                        AppLogger.Error($"SubscribeToWindowEvents: {ex.Message}");
                    }
                });
                return;
            }

            SubscribeToWindowEventsCore();
        }

        public static void UnsubscribeWindowEvents()
        {
            if (_uiInvoker is { } invoker)
            {
                invoker(UnsubscribeWindowEventsCore);
                return;
            }

            UnsubscribeWindowEventsCore();
        }

        private static void SubscribeToWindowEventsCore()
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

        private static void UnsubscribeWindowEventsCore()
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
                // 检查当前模式，如果处于暂停模式或已完全关闭则不处理事件
                if (EnergyManager.CurrentMode != PowerMode.Paused && EnergyManager.IsEnabled)
                {
                    EnergyManager.HandleForegroundEvent(hwnd);
                }
            }
            catch (Exception ex)
            {
                // 记录异常但不抛出，防止 Windows 移除钩子
                AppLogger.Error($"WindowEventCallback: {ex.Message}");
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