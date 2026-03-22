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

        public TrayApplicationContext()
        {
            _bluetoothManager = new BluetoothManager();
            _monitoringService = new MonitoringService(_bluetoothManager);
            
            Icon appIcon = SystemIcons.Shield;
            try {
                string logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bluetooth-safetylock-icon.png");
                if (!System.IO.File.Exists(logoPath))
                    logoPath = @"d:\APPS By nRn World\Windows\BluetoothSafetyLock\bluetooth-safetylock-icon.png";

                if (System.IO.File.Exists(logoPath)) {
                    using (Bitmap bmp = new Bitmap(logoPath)) {
                        appIcon = Icon.FromHandle(bmp.GetHicon());
                    }
                }
            } catch { }

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
        }

        private ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();
            
            menu.Items.Add("Inställningar", null, ShowSettings);
            menu.Items.Add("Snooze (5m)", null, (s, e) => Snooze(5));
            menu.Items.Add("-");
            menu.Items.Add("Avsluta", null, (s, e) => Exit());
            
            return menu;
        }

        private void OnStatusChanged(string status)
        {
            // Inga notiser - allt körs tyst i bakgrunden.
            // Uppdatera bara tray-texten.
            UpdateTrayText();
        }

        private void OnLocked()
        {
            _notifyIcon.Text = "BluetoothSafetyLock - LÅST";
            _notifyIcon.Icon = SystemIcons.Error;
        }

        private void UpdateTrayText()
        {
            if (!_monitoringService.IsPaused)
            {
                string status = _bluetoothManager.IsDeviceConnected
                    ? $"{_monitoringService.CurrentRssi} dBm"
                    : "Frånkopplad";

                string battery = _bluetoothManager.MonitoredBatteryLevel.HasValue
                    ? $" | 🔋{_bluetoothManager.MonitoredBatteryLevel.Value}%"
                    : "";

                string fullText = $"BluetoothSafetyLock: {status}{battery}";
                _notifyIcon.Text = fullText.Length > 63 ? fullText.Substring(0, 63) : fullText;
            }
            else
            {
                _notifyIcon.Text = "BluetoothSafetyLock - Pausad";
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
        }

        private void Exit()
        {
            _notifyIcon.Visible = false;
            _monitoringService.Dispose();
            Application.Exit();
        }
    }
}
