using System;
using System.Drawing;
using System.Windows.Forms;

namespace HardwareAnchor
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
            
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield, // Use a placeholder or load a premium icon
                ContextMenuStrip = CreateContextMenu(),
                Visible = true,
                Text = "Hardware Anchor - Ready"
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
            
            menu.Items.Add("Settings", null, ShowSettings);
            menu.Items.Add("Snooze (5m)", null, (s, e) => Snooze(5));
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => Exit());
            
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
            _notifyIcon.Text = "Hardware Anchor - LOCKED";
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

                string fullText = $"Anchor: {status}{battery}";
                _notifyIcon.Text = fullText.Length > 63 ? fullText.Substring(0, 63) : fullText;
            }
            else
            {
                _notifyIcon.Text = "Hardware Anchor - Pausad";
            }
        }

        private void ShowSettings(object? sender, EventArgs e)
        {
            var form = new MainDashboard(_bluetoothManager, _monitoringService);
            form.Show();
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
