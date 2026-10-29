using EnergyStarZ.Utilities;
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using EnergyStarZ.Interop;

namespace EnergyStarZ
{
    // 热键事件参数
    public class HotkeyEventArgs : EventArgs
    {
        public int HotkeyId { get; }

        public HotkeyEventArgs(int hotkeyId)
        {
            HotkeyId = hotkeyId;
        }
    }

    // 隐藏窗体用于接收热键消息
    public partial class HiddenFormForHotkeys : Form
    {
        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

        // 快捷键相关的常量
        public const int HotkeyIdToggleMode = 1001;
        public const int HotkeyIdPause = 1002;
        public const int HotkeyIdResume = 1003;

        public HiddenFormForHotkeys()
        {
            // 设置窗体为不可见
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
            Size = new Size(0, 0);
            
            // 注册热键
            RegisterHotkeys();

            // 注册屏幕电源通知：熄屏时进入深度节流，亮屏时恢复常规策略
            RegisterScreenPowerNotification();

            // WinEvent out-of-context 钩子必须由运行消息循环的 UI 线程安装/卸载，
            // 后台线程安装的钩子收不到前台切换事件。
            // 已在 UI 线程时同步执行，否则投递到消息队列
            HookManager.SetUiInvoker(action =>
            {
                if (!IsHandleCreated || !InvokeRequired)
                {
                    action();
                }
                else
                {
                    BeginInvoke(action);
                }
            });
        }

        private void RegisterScreenPowerNotification()
        {
            try
            {
                var guid = Interop.Win32Api.GUID_MONITOR_POWER_ON;
                Interop.Win32Api.RegisterPowerSettingNotification(
                    Handle,
                    ref guid,
                    Interop.Win32Api.DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to register screen power notification: {ex.Message}");
            }
        }

        private void RegisterHotkeys()
        {
            bool success;

            // 注册 Ctrl+Alt+A 切换模式
            success = Interop.Win32Api.RegisterHotKey(Handle, HotkeyIdToggleMode, Interop.Win32Api.HotKeyModifiers.Control | Interop.Win32Api.HotKeyModifiers.Alt, (uint)Keys.A);
            if (!success)
            {
                AppLogger.Warn($"Failed to register hotkey Ctrl+Alt+A (error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }

            // 注册 Ctrl+Alt+P 暂停
            success = Interop.Win32Api.RegisterHotKey(Handle, HotkeyIdPause, Interop.Win32Api.HotKeyModifiers.Control | Interop.Win32Api.HotKeyModifiers.Alt, (uint)Keys.P);
            if (!success)
            {
                AppLogger.Warn($"Failed to register hotkey Ctrl+Alt+P (error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }

            // 注册 Ctrl+Alt+R 恢复
            success = Interop.Win32Api.RegisterHotKey(Handle, HotkeyIdResume, Interop.Win32Api.HotKeyModifiers.Control | Interop.Win32Api.HotKeyModifiers.Alt, (uint)Keys.R);
            if (!success)
            {
                AppLogger.Warn($"Failed to register hotkey Ctrl+Alt+R (error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;

            if (m.Msg == WM_HOTKEY)
            {
                int hotkeyId = m.WParam.ToInt32();
                OnHotkeyPressed(new HotkeyEventArgs(hotkeyId));
            }
            else if (m.Msg == Interop.Win32Api.WM_POWERBROADCAST && m.WParam.ToInt32() == Interop.Win32Api.PBT_POWERSETTINGCHANGE)
            {
                HandlePowerSettingChange(m.LParam);
            }

            base.WndProc(ref m);
        }

        private void HandlePowerSettingChange(IntPtr lParam)
        {
            try
            {
                var setting = Marshal.PtrToStructure<Interop.Win32Api.POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting != Interop.Win32Api.GUID_MONITOR_POWER_ON)
                    return;

                bool screenOn = setting.Data != 0;
                if (screenOn == ProcessContext.IsScreenOn)
                    return;

                ProcessContext.IsScreenOn = screenOn;
                AppLogger.Info($"Screen turned {(screenOn ? "on" : "off")}; refreshing throttle policy.");

                // 立即按新状态重算节流策略
                EnergyManager.ThrottleAllUserBackgroundProcesses();
            }
            catch (Exception ex)
            {
                AppLogger.Error($"HandlePowerSettingChange: {ex.Message}");
            }
        }

        protected virtual void OnHotkeyPressed(HotkeyEventArgs e)
        {
            HotkeyPressed?.Invoke(this, e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 注销热键
                Interop.Win32Api.UnregisterHotKey(Handle, HotkeyIdToggleMode);
                Interop.Win32Api.UnregisterHotKey(Handle, HotkeyIdPause);
                Interop.Win32Api.UnregisterHotKey(Handle, HotkeyIdResume);
            }
            base.Dispose(disposing);
        }
    }


}