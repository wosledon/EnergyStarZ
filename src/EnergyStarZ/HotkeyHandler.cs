using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

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
        public event EventHandler<HotkeyEventArgs> HotkeyPressed;

        // 快捷键相关的常量
        private const int HOTKEY_ID_TOGGLE_MODE = 1001;
        private const int HOTKEY_ID_PAUSE = 1002;
        private const int HOTKEY_ID_RESUME = 1003;

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
            // 注册 Ctrl+Alt+A 切换模式
            Win32Api.RegisterHotKey(Handle, HOTKEY_ID_TOGGLE_MODE, HotKeyModifiers.Control | HotKeyModifiers.Alt, (uint)Keys.A);
            
            // 注册 Ctrl+Alt+P 暂停
            Win32Api.RegisterHotKey(Handle, HOTKEY_ID_PAUSE, HotKeyModifiers.Control | HotKeyModifiers.Alt, (uint)Keys.P);
            
            // 注册 Ctrl+Alt+R 恢复
            Win32Api.RegisterHotKey(Handle, HOTKEY_ID_RESUME, HotKeyModifiers.Control | HotKeyModifiers.Alt, (uint)Keys.R);
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
                Win32Api.UnregisterHotKey(Handle, HOTKEY_ID_TOGGLE_MODE);
                Win32Api.UnregisterHotKey(Handle, HOTKEY_ID_PAUSE);
                Win32Api.UnregisterHotKey(Handle, HOTKEY_ID_RESUME);
            }
            base.Dispose(disposing);
        }
    }

    // 定义热键修饰符枚举
    [Flags]
    public enum HotKeyModifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008
    }

    // 在 Win32Api 中添加热键相关的 P/Invoke 声明
    public static class Win32Api
    {
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, HotKeyModifiers fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}