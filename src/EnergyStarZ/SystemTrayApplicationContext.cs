using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using EnergyStarZ.Config;
using EnergyStarZ.Resources;
using System.Windows.Forms;
using static EnergyStarZ.Interop.Win32Api;
using EnergyStarZ.Interop;

// 为处理 Windows 消息添加必要的导入
using Message = System.Windows.Forms.Message;

namespace EnergyStarZ
{
    public class SystemTrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private readonly AppSettings _settings;
        private readonly string _configFilePath;
        private PowerMode _currentMode = PowerMode.Auto;
        private bool _hooksEnabled = true;

        // 快捷键相关的常量
        private const int HOTKEY_ID_TOGGLE_MODE = 1001;
        private const int HOTKEY_ID_PAUSE = 1002;
        private const int HOTKEY_ID_RESUME = 1003;

        public SystemTrayApplicationContext(AppSettings settings)
        {
            _settings = settings;
            _configFilePath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");

            // 初始化系统托盘图标
            trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "EnergyStarZ - Energy Efficiency Manager"
            };

            // 创建右键菜单
            trayIcon.ContextMenuStrip = CreateContextMenu();

            // 初始化热键处理
            InitializeHiddenFormForHotkeys();

            // 显示初始通知
            ShowNotification("EnergyStarZ", LocalizationManager.GetString("ApplicationStarted"), ToolTipIcon.Info);
        }

        private void UnregisterHotkeys()
        {
            _hiddenFormForHotkeys?.Dispose();
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            var autoModeItem = new ToolStripMenuItem(LocalizationManager.GetString("AutoMode"));
            autoModeItem.Checked = _currentMode == PowerMode.Auto;
            autoModeItem.Click += (sender, e) => SetPowerMode(PowerMode.Auto);

            var manualModeItem = new ToolStripMenuItem(LocalizationManager.GetString("ManualMode"));
            manualModeItem.Checked = _currentMode == PowerMode.Manual;
            manualModeItem.Click += (sender, e) => SetPowerMode(PowerMode.Manual);

            var pausedModeItem = new ToolStripMenuItem(LocalizationManager.GetString("PausedMode"));
            pausedModeItem.Checked = _currentMode == PowerMode.Paused;
            pausedModeItem.Click += (sender, e) => SetPowerMode(PowerMode.Paused);

            var separator1 = new ToolStripSeparator();

            // 语言切换子菜单
            var languageMenu = new ToolStripMenuItem(LocalizationManager.GetString("SwitchLanguage"));
            var supportedCultures = LocalizationManager.GetSupportedCultures();
            
            foreach (var culture in supportedCultures)
            {
                var langItem = new ToolStripMenuItem(GetLanguageDisplayName(culture))
                {
                    Checked = culture.Name == LocalizationManager.GetCurrentCulture().Name
                };
                langItem.Click += (sender, e) => ChangeLanguage(culture);
                languageMenu.DropDownItems.Add(langItem);
            }

            var separator2 = new ToolStripSeparator();

            var editConfigItem = new ToolStripMenuItem(LocalizationManager.GetString("EditConfiguration"));
            editConfigItem.Click += OnEditConfigClick;

            var reloadConfigItem = new ToolStripMenuItem(LocalizationManager.GetString("ReloadConfiguration"));
            reloadConfigItem.Click += OnReloadConfigClick;

            var separator3 = new ToolStripSeparator();

            var exitItem = new ToolStripMenuItem(LocalizationManager.GetString("Exit"));
            exitItem.Click += OnExitClick;

            // 添加自动电源模式开关
            var autoPowerModeItem = new ToolStripMenuItem("Auto Power Mode");
            autoPowerModeItem.Checked = _settings.EnableAutoPowerMode;
            autoPowerModeItem.Click += OnAutoPowerModeClick;

            menu.Items.Add(autoModeItem);
            menu.Items.Add(manualModeItem);
            menu.Items.Add(pausedModeItem);
            menu.Items.Add(separator1);
            menu.Items.Add(autoPowerModeItem);
            menu.Items.Add(separator2);
            menu.Items.Add(languageMenu);
            menu.Items.Add(separator3);
            menu.Items.Add(editConfigItem);
            menu.Items.Add(reloadConfigItem);
            menu.Items.Add(separator3);
            menu.Items.Add(exitItem);

            return menu;
        }

        private void OnAutoPowerModeClick(object sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            if (menuItem != null)
            {
                bool enableAutoPowerMode = !menuItem.Checked;
                menuItem.Checked = enableAutoPowerMode;

                // 更新配置
                _settings.EnableAutoPowerMode = enableAutoPowerMode;

                // 更新 EnergyManager 的设置
                EnergyManager.SetAutoPowerModeEnabled(enableAutoPowerMode);

                if (enableAutoPowerMode)
                {
                    EnergyManager.InitializePowerStatusMonitoring();
                    ShowNotification("Auto Power Mode", "Auto power mode enabled\nWill switch modes based on power source", ToolTipIcon.Info);
                }
                else
                {
                    ShowNotification("Auto Power Mode", "Auto power mode disabled", ToolTipIcon.Info);
                }
            }
        }

        private void SetPowerMode(PowerMode mode)
        {
            _currentMode = mode;
            EnergyManager.CurrentMode = mode;

            switch (mode)
            {
                case PowerMode.Auto:
                    Interop.HookManager.SubscribeToWindowEvents();
                    ShowNotification(LocalizationManager.GetString("PowerModeChanged"), LocalizationManager.GetString("AutoModeActivated"), ToolTipIcon.Info);
                    break;
                case PowerMode.Manual:
                    Interop.HookManager.UnsubscribeWindowEvents();
                    ShowNotification(LocalizationManager.GetString("PowerModeChanged"), LocalizationManager.GetString("ManualModeActivated"), ToolTipIcon.Info);
                    break;
                case PowerMode.Paused:
                    // 恢复所有已应用的效率限制
                    EnergyManager.RestoreAllProcessesToNormal();
                    Interop.HookManager.UnsubscribeWindowEvents();
                    ShowNotification(LocalizationManager.GetString("PowerModeChanged"), LocalizationManager.GetString("PausedModeActivated"), ToolTipIcon.Warning);
                    break;
            }

            // 刷新菜单
            trayIcon.ContextMenuStrip = CreateContextMenu();
        }

        private void TogglePowerMode()
        {
            var nextMode = _currentMode switch
            {
                PowerMode.Auto => PowerMode.Manual,
                PowerMode.Manual => PowerMode.Paused,
                PowerMode.Paused => PowerMode.Auto,
                _ => PowerMode.Auto
            };

            SetPowerMode(nextMode);
        }

        private void ShowNotification(string title, string message, ToolTipIcon icon)
        {
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = message;
            trayIcon.BalloonTipIcon = icon;
            trayIcon.ShowBalloonTip(3000); // 3秒后消失
        }

        private void OnEditConfigClick(object sender, EventArgs e)
        {
            try
            {
                // 使用默认文本编辑器打开配置文件
                Process.Start(new ProcessStartInfo
                {
                    FileName = _configFilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open config file: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnReloadConfigClick(object sender, EventArgs e)
        {
            try
            {
                EnergyManager.LoadConfiguration();
                ShowNotification(LocalizationManager.GetString("PowerModeChanged"), LocalizationManager.GetString("ConfigurationReloaded"), ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to reload config: {ex.Message}", LocalizationManager.GetString("PowerModeChanged"), 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ChangeLanguage(CultureInfo culture)
        {
            LocalizationManager.SetCulture(culture);
            
            // 刷新菜单以应用新语言
            trayIcon.ContextMenuStrip = CreateContextMenu();
            
            // 显示通知
            ShowNotification(
                LocalizationManager.GetString("PowerModeChanged"), 
                $"Language changed to {GetLanguageDisplayName(culture)}", 
                ToolTipIcon.Info
            );
        }

        private string GetLanguageDisplayName(CultureInfo culture)
        {
            // 根据当前语言环境显示语言名称
            var currentCulture = LocalizationManager.GetCurrentCulture();
            if (culture.Name.StartsWith("en"))
            {
                return currentCulture.Name.StartsWith("zh") ? "英语" : "English";
            }
            else if (culture.Name.StartsWith("zh"))
            {
                return currentCulture.Name.StartsWith("zh") ? "中文" : "Chinese";
            }
            return culture.NativeName;
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            // 关闭应用程序
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnregisterHotkeys();
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        // 创建一个隐藏窗体来处理热键消息
        private HiddenFormForHotkeys _hiddenFormForHotkeys;

        private void InitializeHiddenFormForHotkeys()
        {
            _hiddenFormForHotkeys = new HiddenFormForHotkeys();
            _hiddenFormForHotkeys.HotkeyPressed += OnHotkeyPressed;
        }

        private void OnHotkeyPressed(object sender, HotkeyEventArgs e)
        {
            switch (e.HotkeyId)
            {
                case HOTKEY_ID_TOGGLE_MODE:
                    TogglePowerMode();
                    break;
                case HOTKEY_ID_PAUSE:
                    SetPowerMode(PowerMode.Paused);
                    break;
                case HOTKEY_ID_RESUME:
                    SetPowerMode(PowerMode.Auto);
                    break;
            }
        }
    }

}