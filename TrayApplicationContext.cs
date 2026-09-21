using System;
using System.Drawing;
using System.Windows.Forms;

namespace BluetoothSafetyLock
{
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly BluetoothManager _bluetoothManager;
        private readonly MonitoringService _monitoringService;
        private readonly System.Timers.Timer _statusTimer;
        /// <summary>App icon instance owned by this class; released in Exit().</summary>
        private Icon? _appIcon;

        public TrayApplicationContext()
        {
            _bluetoothManager = new BluetoothManager();
            _monitoringService = new MonitoringService(_bluetoothManager);

            // FAS 1.1: restore persisted settings into the service.
            var s = SettingsStore.Current;
            _monitoringService.Threshold = s.Threshold;
            _monitoringService.GracePeriodSeconds = s.GracePeriodSeconds;
            _monitoringService.IsLockWorkstationEnabled = s.IsLockWorkstationEnabled;
            _monitoringService.IsAutoUnlockEnabled = s.IsAutoUnlockEnabled;
            _monitoringService.IsClearClipboardEnabled = s.IsClearClipboardEnabled;
            _monitoringService.IsPlayWarningEnabled = s.IsPlayWarningEnabled;

            // Embedded multi-size app icon; size chosen for the current DPI
            // (16 px at 100 % scaling, 32 px at 200 %) so the tray icon stays sharp.
            Icon appIcon = SystemIcons.Shield;
            try
            {
                _appIcon = AppIcons.GetSmallIcon();
                appIcon = _appIcon;
            }
            catch (Exception ex)
            {
                // Rare: missing/broken embedded resource — fall back to a system icon.
                Logger.Warn($"Could not load embedded tray icon; using system shield icon. {ex.Message}");
            }

            _notifyIcon = new NotifyIcon
            {
                Icon = appIcon,
                ContextMenuStrip = CreateContextMenu(),
                Visible = true,
                Text = "BluetoothSafetyLock - Redo"
            };
            _notifyIcon.MouseDoubleClick += (s, e) => {
                if (e.Button == MouseButtons.Left) ShowSettings(null, EventArgs.Empty);
            };

            _monitoringService.StatusChanged += OnStatusChanged;
            _monitoringService.Locked += OnLocked;

            _statusTimer = new System.Timers.Timer(2000);
            _statusTimer.Elapsed += (s, e) => UpdateTrayText();
            _statusTimer.Start();

            // FAS 1.1: resume monitoring of the last selected device at startup, if any.
            if (!string.IsNullOrEmpty(s.SelectedDeviceId))
            {
                _monitoringService.MonitoredDeviceName = s.SelectedDeviceName;
                _ = ResumeMonitoringAsync(s.SelectedDeviceId);
            }
        }

        private async Task ResumeMonitoringAsync(string deviceId)
        {
            try
            {
                await _monitoringService.StartMonitoringAsync(deviceId);
                Logger.Info("Monitoring resumed from persisted settings.");
            }
            catch (Exception ex)
            {
                Logger.Error("Could not resume monitoring of the last selected device.", ex);
            }
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            menu.Items.Add("Settings", null, ShowSettings);
            menu.Items.Add("Snooze (5m)", null, (s, e) => Snooze(5));
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => Exit());

            return menu;
        }

        private void OnStatusChanged(string status)
        {
            // No notifications - everything runs silently in background.
            // Just update tray text. Events arrive from background threads.
            try
            {
                if (_notifyIcon.ContextMenuStrip != null && _notifyIcon.ContextMenuStrip.InvokeRequired)
                    _notifyIcon.ContextMenuStrip.BeginInvoke((Action)UpdateTrayText);
                else
                    UpdateTrayText();
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch (InvalidOperationException) { /* handle gone during shutdown */ }
        }

        private void OnLocked()
        {
            try
            {
                _notifyIcon.Text = "BluetoothSafetyLock - LOCKED";
                _notifyIcon.Icon = SystemIcons.Error;
            }
            catch (ObjectDisposedException) { /* shutting down */ }
        }

        private void UpdateTrayText()
        {
            try
            {
                if (!_monitoringService.IsPaused)
                {
                    string status = _bluetoothManager.IsDeviceConnected
                        ? $"{_monitoringService.CurrentRssi} dBm"
                        : "Disconnected";

                    string battery = _bluetoothManager.MonitoredBatteryLevel.HasValue
                        ? $" | {_bluetoothManager.MonitoredBatteryLevel.Value}%"
                        : "";

                    string fullText = $"BluetoothSafetyLock: {status}{battery}";
                    _notifyIcon.Text = fullText.Length > 63 ? fullText.Substring(0, 63) : fullText;
                }
                else
                {
                    _notifyIcon.Text = "BluetoothSafetyLock - Paused";
                }
            }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch (Exception ex)
            {
                Logger.Warn($"UpdateTrayText failed. {ex.Message}");
            }
        }

        private MainDashboard? _settingsForm;

        private void ShowSettings(object? sender, EventArgs e)
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new MainDashboard(_bluetoothManager, _monitoringService);
                _settingsForm.FormClosed += (s, args) => _settingsForm = null;
                _settingsForm.Show();
            }
            else
            {
                if (!_settingsForm.Visible)
                {
                    _settingsForm.Show();
                }
                _settingsForm.WindowState = FormWindowState.Normal;
                _settingsForm.BringToFront();
                _settingsForm.Focus();
            }
        }

        private void Snooze(int minutes)
        {
            _monitoringService.IsPaused = true;
            System.Windows.Forms.Timer snoozeTimer = new System.Windows.Forms.Timer();
            snoozeTimer.Interval = minutes * 60 * 1000;
            snoozeTimer.Tick += (s, e) => {
                _monitoringService.IsPaused = false;
                snoozeTimer.Stop();
                snoozeTimer.Dispose();
            };
            snoozeTimer.Start();
            Logger.Info($"Snoozed for {minutes} minutes.");
        }

        private void Exit()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Disposing NotifyIcon failed. {ex.Message}");
            }
            _appIcon?.Dispose();
            _appIcon = null;
            _statusTimer.Stop();
            _statusTimer.Dispose();
            _monitoringService.Dispose();
            Application.Exit();
        }
    }
}
