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
        }

        private void RegisterHotkeys()
        {
            bool success;

            // 注册 Ctrl+Alt+A 切换模式
            success = Interop.Win32Api.RegisterHotKey(Handle, HotkeyIdToggleMode, Interop.Win32Api.HotKeyModifiers.Control | Interop.Win32Api.HotKeyModifiers.Alt, (uint)Keys.A);
            if (!success)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] [WARN] Failed to register hotkey Ctrl+Alt+A (error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }

            // 注册 Ctrl+Alt+P 暂停
            success = Interop.Win32Api.RegisterHotKey(Handle, HotkeyIdPause, Interop.Win32Api.HotKeyModifiers.Control | Interop.Win32Api.HotKeyModifiers.Alt, (uint)Keys.P);
            if (!success)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] [WARN] Failed to register hotkey Ctrl+Alt+P (error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            }

            // 注册 Ctrl+Alt+R 恢复
            success = Interop.Win32Api.RegisterHotKey(Handle, HotkeyIdResume, Interop.Win32Api.HotKeyModifiers.Control | Interop.Win32Api.HotKeyModifiers.Alt, (uint)Keys.R);
            if (!success)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] [WARN] Failed to register hotkey Ctrl+Alt+R (error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
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

            base.WndProc(ref m);
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