using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HardwareAnchor
{
    public partial class DeviceSelectorForm : Form
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly MonitoringService _monitoringService;
        private List<BluetoothDeviceModel> _devices = new();
        
        private ListBox? _deviceList;
        private TrackBar? _thresholdBar;
        private Label? _thresholdLabel;
        private Button? _saveButton;

        public DeviceSelectorForm(BluetoothManager bluetoothManager, MonitoringService monitoringService)
        {
            _bluetoothManager = bluetoothManager;
            _monitoringService = monitoringService;

            InitializeForm();

            _monitoringService.IsPaused = true;

            _bluetoothManager.DeviceDiscovered += OnDeviceDiscovered;
            _bluetoothManager.DiscoveryCompleted += OnDiscoveryCompleted;
            _bluetoothManager.PairingConfirmationRequested += OnPairingConfirmation;

            StartScanning();
        }

        private void OnPairingConfirmation(string pin, Action<bool> respond)
        {
            if (this.InvokeRequired) { this.Invoke(() => OnPairingConfirmation(pin, respond)); return; }

            string msg = string.IsNullOrEmpty(pin) 
                ? "Do you want to pair this device?" 
                : $"Does the code \"{pin}\" match the one on your device?";

            var result = MessageBox.Show(msg, "Confirm Bluetooth Pairing", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            respond(result == DialogResult.Yes);
        }

        private void InitializeForm()
        {
            this.Text = "Hardware Anchor - Settings";
            this.Size = new Size(420, 600);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(28, 28, 28);
            this.ForeColor = Color.White;

            var titleLabel = new Label { Text = "Bluetooth Device Discovery", Font = new Font("Segoe UI", 12, FontStyle.Bold), Location = new Point(20, 20), AutoSize = true };
            _deviceList = new ListBox
            {
                Location = new Point(20, 55),
                Size = new Size(365, 230),
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 10),
                ItemHeight = 25
            };

            var scanButton = new Button { Text = "🔄 Refresh Discovery", Location = new Point(20, 290), Size = new Size(160, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), Font = new Font("Segoe UI", 9) };
            scanButton.Click += (s, e) => StartScanning();

            var sensitivityLabel = new Label { Text = "Lock Sensitivity (Threshold)", Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 335), AutoSize = true };
            _thresholdBar = new TrackBar { Location = new Point(20, 365), Size = new Size(280, 45), Minimum = -100, Maximum = -30, Value = _monitoringService.Threshold, TickFrequency = 5 };
            _thresholdBar.ValueChanged += (s, e) => UpdateThresholdLabel();
            _thresholdLabel = new Label { Location = new Point(310, 370), Size = new Size(80, 25), Text = _monitoringService.Threshold + " dBm", Font = new Font("Segoe UI", 10) };

            var graceLabel = new Label { Text = "Grace Period (Seconds)", Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(20, 420), AutoSize = true };
            var graceNumeric = new NumericUpDown { Location = new Point(20, 450), Size = new Size(80, 30), Value = _monitoringService.GracePeriodSeconds, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, Minimum = 1, Maximum = 60, Font = new Font("Segoe UI", 10) };

            _saveButton = new Button { Text = "Select & Start Anchor", Location = new Point(20, 500), Size = new Size(365, 50), FlatStyle = FlatStyle.Flat, BackColor = Color.DodgerBlue, ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
            _saveButton.Click += async (s, e) => {
                if (_deviceList.SelectedIndex >= 0)
                {
                    _saveButton.Enabled = false;
                    var selectedDevice = _devices[_deviceList.SelectedIndex];
                    
                    if (!selectedDevice.IsPaired)
                    {
                        _bluetoothManager.StopDiscovery();
                        bool paired = await _bluetoothManager.PairDeviceAsync(selectedDevice.Id);
                        if (!paired)
                        {
                            MessageBox.Show("Pairing failed. Please ensure the device is in pairing mode and not already connected to another app.");
                            _saveButton.Enabled = true;
                            return;
                        }
                    }

                    _saveButton.Text = "Starting Anchor...";
                    _monitoringService.Threshold = (short)_thresholdBar.Value;
                    _monitoringService.GracePeriodSeconds = (int)graceNumeric.Value;
                    _monitoringService.MonitoredDeviceName = selectedDevice.Name;
                    await _monitoringService.StartMonitoringAsync(selectedDevice.Id);
                    
                    MessageBox.Show($"Monitoring started for '{selectedDevice.Name}'.", "Anchor Active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    this.Close();
                }
                else MessageBox.Show("Please select a device.");
            };

            this.Controls.AddRange(new Control[] { titleLabel, _deviceList, scanButton, sensitivityLabel, _thresholdBar, _thresholdLabel, graceLabel, graceNumeric, _saveButton });
        }

        private void StartScanning()
        {
            _deviceList!.Items.Clear();
            _devices.Clear();
            _deviceList.Items.Add("Discovering nearby devices...");
            _bluetoothManager.StartDiscovery();
        }

        private void OnDeviceDiscovered(BluetoothDeviceModel device)
        {
            if (this.InvokeRequired) { this.Invoke(() => OnDeviceDiscovered(device)); return; }
            if (_devices.Any(d => d.Id == device.Id)) return;
            if (_devices.Count == 0) _deviceList!.Items.Clear();
            _devices.Add(device);
            _deviceList!.Items.Add(device.Name + (device.IsPaired ? "" : " (Unpaired)"));
        }

        private void OnDiscoveryCompleted()
        {
            if (this.InvokeRequired) { this.Invoke(OnDiscoveryCompleted); return; }
            if (_devices.Count == 0) { _deviceList!.Items.Clear(); _deviceList.Items.Add("No Bluetooth devices found."); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _bluetoothManager.DeviceDiscovered -= OnDeviceDiscovered;
            _bluetoothManager.DiscoveryCompleted -= OnDiscoveryCompleted;
            _bluetoothManager.PairingConfirmationRequested -= OnPairingConfirmation;
            _bluetoothManager.StopDiscovery();
            base.OnFormClosing(e);
        }

        private void UpdateThresholdLabel() { _thresholdLabel!.Text = _thresholdBar!.Value + " dBm"; }
    }
}
