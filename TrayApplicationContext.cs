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
        private readonly System.Timers.Timer _updateCheckTimer;
        private System.Windows.Forms.Timer? _autoCheckStartupTimer;
        /// <summary>Shown in the tray menu only while an update is downloaded and waiting.</summary>
        private ToolStripMenuItem? _updatePendingMenuItem;
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

            // Auto-updater (Doggy Player flow): check shortly after launch, then every 6 h.
            // The check itself respects the IsAutoUpdateEnabled toggle in Settings.
            _updateCheckTimer = new System.Timers.Timer(TimeSpan.FromHours(6).TotalMilliseconds);
            _updateCheckTimer.Elapsed += (s, e) => RunAutoUpdateCheck();
            _updateCheckTimer.Start();

            _autoCheckStartupTimer = new System.Windows.Forms.Timer { Interval = 8000 };
            _autoCheckStartupTimer.Tick += (s2, e2) =>
            {
                _autoCheckStartupTimer?.Stop();
                _autoCheckStartupTimer?.Dispose();
                _autoCheckStartupTimer = null;
                RunAutoUpdateCheck();
            };
            _autoCheckStartupTimer.Start();
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
            menu.Items.Add("Check for updates", null, (s, e) => _ = CheckForUpdatesInternalAsync(autoTriggered: false));

            // Visible only when an update is staged and ready (refreshed on every open).
            _updatePendingMenuItem = new ToolStripMenuItem("Install update & restart", null,
                (s, e) => InstallPendingUpdateNow())
            { Visible = UpdaterService.HasStagedUpdate };
            menu.Items.Add(_updatePendingMenuItem);
            menu.Opening += (s, e) =>
            {
                if (_updatePendingMenuItem != null)
                    _updatePendingMenuItem.Visible = UpdaterService.HasStagedUpdate;
            };

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
            _updateCheckTimer.Stop();
            _updateCheckTimer.Dispose();
            if (_autoCheckStartupTimer != null)
            {
                _autoCheckStartupTimer.Stop();
                _autoCheckStartupTimer.Dispose();
                _autoCheckStartupTimer = null;
            }
            _monitoringService.Dispose();
            Application.Exit();
        }

        // ─────────────────────────── Auto-updater ───────────────────────────

        /// <summary>Periodic/auto path: staged-update balloon first, then a network check when enabled.</summary>
        private void RunAutoUpdateCheck()
        {
            try
            {
                // A staged update waiting from a previous session always deserves a balloon,
                // even when further network checks are switched off.
                if (ShowPendingUpdateBalloonIfNeeded()) return;

                if (!SettingsStore.Current.IsAutoUpdateEnabled) return;

                _ = CheckForUpdatesInternalAsync(autoTriggered: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Auto update check failed. {ex.Message}");
            }
        }

        /// <summary>Shows a balloon when an update is staged and waiting; returns true when shown.</summary>
        private bool ShowPendingUpdateBalloonIfNeeded()
        {
            var pending = UpdaterService.PendingVersion;
            if (pending == null) return false;

            ShowBalloon(
                ToolTipIcon.Info,
                "Update ready to install",
                $"BluetoothSafetyLock {pending} is downloaded. Right-click the tray icon and choose \"Install update & restart\" — or it installs automatically next time you start the app.");
            return true;
        }

        /// <summary>
        /// Shared check path for the tray menu ("Check for updates") and the
        /// automatic timer. Downloads and stages the update in the background;
        /// the UI stays fully usable. Errors land in the log and (manual checks only)
        /// as balloons.
        /// </summary>
        private async Task CheckForUpdatesInternalAsync(bool autoTriggered)
        {
            try
            {
                if (UpdaterService.HasStagedUpdate)
                {
                    ShowPendingUpdateBalloonIfNeeded();
                    return;
                }

                var update = await UpdaterService.CheckForUpdateAsync();
                if (update == null)
                {
                    if (!autoTriggered)
                        ShowBalloon(ToolTipIcon.Info, "You're up to date",
                            $"BluetoothSafetyLock {UpdaterService.CurrentVersion} is the latest version.");
                    return;
                }

                ShowBalloon(ToolTipIcon.Info, "Update available",
                    $"Downloading BluetoothSafetyLock {update.Value.Version}… You can keep working; it installs on restart.");

                string staged = await UpdaterService.DownloadAndStageAsync(update.Value.AssetUrl);
                Logger.Info($"Update {update.Value.Version} staged at '{staged}'.");

                ShowBalloon(ToolTipIcon.Info, "Update ready to install",
                    $"BluetoothSafetyLock {update.Value.Version} is downloaded. Right-click the tray icon and choose \"Install update & restart\" — or it installs automatically next time you start the app.");
            }
            catch (Exception ex)
            {
                Logger.Error("Update check/download failed.", ex);
                if (!autoTriggered)
                    ShowBalloon(ToolTipIcon.Warning, "Update failed",
                        "The update could not be downloaded. Check your internet connection and try again.");
            }
        }

        /// <summary>Swaps in the staged update and starts the new version.</summary>
        private void InstallPendingUpdateNow()
        {
            if (!UpdaterService.HasStagedUpdate) return;

            Logger.Info("User requested install-and-restart from the tray menu.");
            UpdaterService.RestartToInstall();
            Exit();
        }

        private void ShowBalloon(ToolTipIcon icon, string title, string text)
        {
            try
            {
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.ShowBalloonTip(6000);
            }
            catch (Exception ex)
            {
                // Notifications must never take the app down.
                Logger.Warn($"Balloon notification failed. {ex.Message}");
            }
        }
    }
}
