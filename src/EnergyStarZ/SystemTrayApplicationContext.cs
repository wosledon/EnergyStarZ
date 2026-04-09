using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using EnergyStarZ.Config;
using EnergyStarZ.Resources;
using System.Windows.Forms;
using static EnergyStarZ.Interop.Win32Api;
using EnergyStarZ.Interop;

using Message = System.Windows.Forms.Message;

namespace EnergyStarZ
{
    public class SystemTrayApplicationContext : ApplicationContext
    {
        private NotifyIcon trayIcon = null!;
        private readonly AppSettings _settings;
        private readonly string _configFilePath;

        private HiddenFormForHotkeys? _hiddenFormForHotkeys;

        public SystemTrayApplicationContext(AppSettings settings)
        {
            _settings = settings;
            _configFilePath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");

            trayIcon = new NotifyIcon()
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "EnergyStarZ - Energy Efficiency Manager"
            };

            trayIcon.ContextMenuStrip = CreateContextMenu();

            InitializeHiddenFormForHotkeys();

            ShowNotification("EnergyStarZ", LocalizationManager.GetString("ApplicationStarted"), ToolTipIcon.Info);
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();
            var currentMode = EnergyManager.CurrentMode;

            var autoModeItem = new ToolStripMenuItem(LocalizationManager.GetString("AutoMode"));
            autoModeItem.Checked = currentMode == PowerMode.Auto;
            autoModeItem.Click += (sender, e) => SetPowerMode(PowerMode.Auto);

            var manualModeItem = new ToolStripMenuItem(LocalizationManager.GetString("ManualMode"));
            manualModeItem.Checked = currentMode == PowerMode.Manual;
            manualModeItem.Click += (sender, e) => SetPowerMode(PowerMode.Manual);

            var pausedModeItem = new ToolStripMenuItem(LocalizationManager.GetString("PausedMode"));
            pausedModeItem.Checked = currentMode == PowerMode.Paused;
            pausedModeItem.Click += (sender, e) => SetPowerMode(PowerMode.Paused);

            var separator1 = new ToolStripSeparator();

            var autoPowerModeItem = new ToolStripMenuItem(LocalizationManager.GetString("AutoPowerMode"));
            autoPowerModeItem.Checked = _settings.EnableAutoPowerMode;
            autoPowerModeItem.Click += OnAutoPowerModeClick;

            var separator2 = new ToolStripSeparator();

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

            var separator3 = new ToolStripSeparator();

            var editConfigItem = new ToolStripMenuItem(LocalizationManager.GetString("EditConfiguration"));
            editConfigItem.Click += OnEditConfigClick;

            var reloadConfigItem = new ToolStripMenuItem(LocalizationManager.GetString("ReloadConfiguration"));
            reloadConfigItem.Click += OnReloadConfigClick;

            var separator4 = new ToolStripSeparator();

            var exitItem = new ToolStripMenuItem(LocalizationManager.GetString("Exit"));
            exitItem.Click += OnExitClick;

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
            menu.Items.Add(separator4);
            menu.Items.Add(exitItem);

            return menu;
        }

        private void OnAutoPowerModeClick(object? sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            if (menuItem != null)
            {
                bool enableAutoPowerMode = !menuItem.Checked;
                menuItem.Checked = enableAutoPowerMode;

                _settings.EnableAutoPowerMode = enableAutoPowerMode;
                EnergyManager.SetAutoPowerModeEnabled(enableAutoPowerMode);

                if (enableAutoPowerMode)
                {
                    ShowNotification(
                        LocalizationManager.GetString("AutoPowerMode"),
                        LocalizationManager.GetString("AutoPowerModeEnabled"),
                        ToolTipIcon.Info);
                }
                else
                {
                    ShowNotification(
                        LocalizationManager.GetString("AutoPowerMode"),
                        LocalizationManager.GetString("AutoPowerModeDisabled"),
                        ToolTipIcon.Info);
                }
            }
        }

        private void SetPowerMode(PowerMode mode)
        {
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
                    EnergyManager.RestoreAllProcessesToNormal();
                    Interop.HookManager.UnsubscribeWindowEvents();
                    ShowNotification(LocalizationManager.GetString("PowerModeChanged"), LocalizationManager.GetString("PausedModeActivated"), ToolTipIcon.Warning);
                    break;
            }

            trayIcon.ContextMenuStrip = CreateContextMenu();
        }

        private void TogglePowerMode()
        {
            var nextMode = EnergyManager.CurrentMode switch
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
            trayIcon.ShowBalloonTip(3000);
        }

        private void OnEditConfigClick(object? sender, EventArgs e)
        {
            try
            {
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

        private void OnReloadConfigClick(object? sender, EventArgs e)
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

            trayIcon.ContextMenuStrip = CreateContextMenu();

            ShowNotification(
                LocalizationManager.GetString("PowerModeChanged"),
                $"Language changed to {GetLanguageDisplayName(culture)}",
                ToolTipIcon.Info
            );
        }

        private string GetLanguageDisplayName(CultureInfo culture)
        {
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

        private void OnExitClick(object? sender, EventArgs e)
        {
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hiddenFormForHotkeys?.Dispose();
                _hiddenFormForHotkeys = null;

                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeHiddenFormForHotkeys()
        {
            _hiddenFormForHotkeys = new HiddenFormForHotkeys();
            _hiddenFormForHotkeys.HotkeyPressed += OnHotkeyPressed;
        }

        private void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
        {
            switch (e.HotkeyId)
            {
                case HiddenFormForHotkeys.HotkeyIdToggleMode:
                    TogglePowerMode();
                    break;
                case HiddenFormForHotkeys.HotkeyIdPause:
                    SetPowerMode(PowerMode.Paused);
                    break;
                case HiddenFormForHotkeys.HotkeyIdResume:
                    SetPowerMode(PowerMode.Auto);
                    break;
            }
        }
    }

}
