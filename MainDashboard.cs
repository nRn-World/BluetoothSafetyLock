using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HardwareAnchor
{
    public partial class MainDashboard : Form
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly MonitoringService _monitoringService;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly List<int> _signalHistory = new();
        private string _activeView = "System Status";
        
        private List<BluetoothDeviceModel> _pairedDevices = new();
        private bool _lockWorkstation = true;
        private bool _autoUnlock = true;
        private bool _clearClipboard = true;
        private bool _playWarning = false;
        private string _appearanceTheme = "Auto"; // Light, Dark, Auto
        private int _settingsScrollY = 0;
        private bool _isDraggingSlider = false;
        private Point _mouseLocation = Point.Empty;

        private bool IsDarkTheme => _appearanceTheme == "Dark" || (_appearanceTheme == "Auto" && IsWindowsInDarkMode());

        private bool IsWindowsInDarkMode()
        {
            try {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    if (key != null) {
                        var val = key.GetValue("AppsUseLightTheme");
                        if (val is int i) return i == 0;
                    }
                }
            } catch { }
            return true; // Default till mörkt om vi inte kan läsa registret
        }

        private Color BackgroundColor => !IsDarkTheme ? Color.FromArgb(245, 247, 250) : Color.FromArgb(14, 18, 26);
        private Color SidebarColor => !IsDarkTheme ? Color.FromArgb(255, 255, 255) : Color.FromArgb(19, 24, 35);
        private Color CardColor => !IsDarkTheme ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 35, 51);
        private Color CardInnerColor => !IsDarkTheme ? Color.FromArgb(240, 242, 245) : Color.FromArgb(19, 24, 35);
        private Color PrimaryTextColor => !IsDarkTheme ? Color.FromArgb(30, 35, 45) : Color.White;
        private Color SecondaryTextColor => !IsDarkTheme ? Color.FromArgb(100, 110, 130) : Color.Gray;
        private Color SidebarActiveColor => !IsDarkTheme ? Color.FromArgb(240, 245, 255) : Color.FromArgb(30, 38, 54);

        public MainDashboard(BluetoothManager bluetoothManager, MonitoringService monitoringService)
        {
            _bluetoothManager = bluetoothManager;
            _monitoringService = monitoringService;

            this.Text = "Bluetooth SafetyLock";
            this.Size = new Size(1000, 700);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(14, 18, 26);
            this.DoubleBuffered = true;

            this.MouseWheel += (s, e) => {
                if (_activeView == "Settings") {
                    _settingsScrollY = Math.Clamp(_settingsScrollY - (e.Delta / 2), 0, 300);
                    this.Invalidate();
                }
            };

            _bluetoothManager.DeviceDiscovered += OnDeviceDiscovered;
            _bluetoothManager.StartDiscovery();

            _bluetoothManager.MonitoredBatteryChanged += () => {
                if (this.InvokeRequired)
                    this.Invoke(() => { SyncMonitoredBatteryIntoList(); this.Invalidate(); });
                else {
                    SyncMonitoredBatteryIntoList();
                    this.Invalidate();
                }
            };

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => {
                UpdateSignalHistory();
                this.Invalidate();
            };
            _refreshTimer.Start();

            this.MouseDown += (s, e) => {
                if (_activeView == "Settings") {
                    _isDraggingSlider = true;
                    HandleSliderDrag(e);
                }
            };
            this.MouseMove += (s, e) => {
                _mouseLocation = e.Location;
                if (_activeView == "Settings" && _isDraggingSlider && e.Button == MouseButtons.Left) {
                    HandleSliderDrag(e);
                }
                if (_activeView == "System Status") {
                    this.Invalidate(); // För hover-effekt i grafen
                }
            };
            this.MouseUp += (s, e) => {
                _isDraggingSlider = false;
            };
        }

        private void HandleSliderDrag(MouseEventArgs e)
        {
            int curY = e.Y + _settingsScrollY;
            var thresholdRect = new Rectangle(330, 540, 580, 40);
            if (thresholdRect.Contains(e.X, curY))
            {
                float pct = Math.Clamp((float)(e.X - thresholdRect.Left) / thresholdRect.Width, 0, 1);
                _monitoringService.Threshold = (short)(-100 + (pct * 60));
                this.Invalidate();
            }
            var graceRect = new Rectangle(330, 760, 580, 40);
            if (graceRect.Contains(e.X, curY))
            {
                float pct = Math.Clamp((float)(e.X - graceRect.Left) / graceRect.Width, 0, 1);
                _monitoringService.GracePeriodSeconds = (int)(pct * 30);
                this.Invalidate();
            }
        }

        private void OnDeviceDiscovered(BluetoothDeviceModel dev)
        {
            if (this.InvokeRequired) { this.Invoke(() => OnDeviceDiscovered(dev)); return; }
            
            var existing = _pairedDevices.FirstOrDefault(d => d.Id == dev.Id);
            if (dev.IsPaired)
            {
                if (existing == null) _pairedDevices.Add(dev);
                else {
                    existing.BatteryLevel = dev.BatteryLevel ?? existing.BatteryLevel;
                    existing.IsNearby = dev.IsNearby;
                    existing.Category = dev.Category;
                    existing.IsConnected = dev.IsConnected;
                }
                SyncMonitoredBatteryIntoList();
                this.Invalidate();
            }
        }

        private void SyncMonitoredBatteryIntoList()
        {
            if (!_bluetoothManager.MonitoredBatteryLevel.HasValue || string.IsNullOrEmpty(_bluetoothManager.MonitoredDeviceId)) return;
            var match = _pairedDevices.FirstOrDefault(d => d.Id == _bluetoothManager.MonitoredDeviceId);
            if (match != null)
                match.BatteryLevel = _bluetoothManager.MonitoredBatteryLevel;
        }

        private void UpdateSignalHistory()
        {
            if (_monitoringService.IsPaused)
            {
                _signalHistory.Add(-110);
            }
            else
            {
                int rssi = _monitoringService.CurrentRssi;
                if (rssi > -110) _signalHistory.Add(rssi);
                else _signalHistory.Add(-110);
            }

            if (_signalHistory.Count > 100) _signalHistory.RemoveAt(0);
        }

        protected override async void OnMouseClick(MouseEventArgs e)
        {
            string[] items = { "System Status", "Devices", "Security Actions", "Settings" };
            int menuY = 180;
            for (int i = 0; i < items.Length; i++) {
                if (new Rectangle(30, menuY - 5, 200, 40).Contains(e.Location)) {
                    _activeView = items[i];
                    _settingsScrollY = 0;
                    this.Invalidate();
                    return;
                }
                menuY += 50;
            }

            if (_activeView == "System Status")
            {
                var testBtnRect = new Rectangle(810, 335, 120, 35); // Korrigerad Y
                if (testBtnRect.Contains(e.Location))
                {
                    // (Test drop är nu borttaget, men behåll logiken om vi vill återinföra)
                }
            }

            if (_activeView == "Settings")
            {
                int curY = e.Y + _settingsScrollY;

                // Appearance Buttons (Y: 290)
                if (new Rectangle(330, 290, 200, 60).Contains(e.X, curY)) _appearanceTheme = "Light";
                if (new Rectangle(530, 290, 200, 60).Contains(e.X, curY)) _appearanceTheme = "Dark";
                if (new Rectangle(730, 290, 200, 60).Contains(e.X, curY)) _appearanceTheme = "Auto";

                // Threshold Slider (Y: 550)
                var thresholdRect = new Rectangle(330, 540, 580, 40);
                if (thresholdRect.Contains(e.X, curY))
                {
                    float pct = Math.Clamp((float)(e.X - thresholdRect.Left) / thresholdRect.Width, 0, 1);
                    _monitoringService.Threshold = (short)(-100 + (pct * 60));
                }

                // Grace Period Slider (Y: 770)
                var graceRect = new Rectangle(330, 760, 580, 40);
                if (graceRect.Contains(e.X, curY))
                {
                    float pct = Math.Clamp((float)(e.X - graceRect.Left) / graceRect.Width, 0, 1);
                    _monitoringService.GracePeriodSeconds = (int)(pct * 30);
                }

                this.Invalidate();
                return;
            }

            if (_activeView == "Security Actions") {
                if (new Rectangle(860, 255, 60, 30).Contains(e.Location)) _lockWorkstation = !_lockWorkstation;
                if (new Rectangle(860, 345, 60, 30).Contains(e.Location)) _autoUnlock = !_autoUnlock;
                if (new Rectangle(860, 435, 60, 30).Contains(e.Location)) _clearClipboard = !_clearClipboard;
                if (new Rectangle(860, 525, 60, 30).Contains(e.Location)) _playWarning = !_playWarning;
                this.Invalidate();
                return;
            }

            if (new Rectangle(960, 5, 30, 30).Contains(e.Location)) this.Hide();
            if (_activeView == "Devices" && new Rectangle(810, 95, 130, 40).Contains(e.Location)) {
                var selector = new DeviceSelectorForm(_bluetoothManager, _monitoringService);
                selector.ShowDialog();
                _bluetoothManager.StartDiscovery();
            }

            if (_activeView == "Devices") {
                int clickY = 200;
                for (int i = 0; i < _pairedDevices.Count; i++) {
                    if (new Rectangle(880, clickY + 20, 50, 50).Contains(e.Location)) {
                        var dev = _pairedDevices[i];
                        if (MessageBox.Show($"Unpair {dev.Name}?", "Confirm", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                            await _bluetoothManager.UnpairDeviceAsync(dev.Id);
                            _pairedDevices.RemoveAt(i);
                            this.Invalidate();
                        }
                        return;
                    }
                    clickY += 115;
                }
            }

            if (new Rectangle(40, this.Height - 80, 180, 45).Contains(e.Location)) {
                _monitoringService.IsPaused = !_monitoringService.IsPaused;
                if (_monitoringService.IsPaused) _monitoringService.StopMonitoring();
                this.Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            
            this.BackColor = BackgroundColor;

            var sidebarRect = new Rectangle(0, 0, 260, this.Height);
            using (var sbBrush = new SolidBrush(SidebarColor)) g.FillRectangle(sbBrush, sidebarRect);

            g.FillEllipse(new LinearGradientBrush(new Rectangle(40, 60, 48, 48), Color.DodgerBlue, Color.DeepSkyBlue, 45), 40, 60, 48, 48);
            using (var secondaryBrush = new SolidBrush(SecondaryTextColor)) g.DrawString("Bluetooth", new Font("Segoe UI", 9), secondaryBrush, 100, 70);
            using (var primaryBrush = new SolidBrush(PrimaryTextColor)) g.DrawString("SafetyLock", new Font("Segoe UI", 16, FontStyle.Bold), primaryBrush, 100, 85);

            string[] sidebarItems = { "System Status", "Devices", "Security Actions", "Settings" };
            int menuY = 180;
            foreach (var item in sidebarItems) {
                bool isActive = item == _activeView;
                if (isActive) FillRoundedRect(g, SidebarActiveColor, new Rectangle(30, menuY - 5, 200, 40), 8);
                using (var brush = new SolidBrush(isActive ? Color.DodgerBlue : SecondaryTextColor))
                    g.DrawString(item, new Font("Segoe UI", 11, isActive ? FontStyle.Bold : FontStyle.Regular), brush, 70, menuY);
                menuY += 50;
            }

            var stopRect = new Rectangle(40, this.Height - 80, 180, 45);
            bool isPaused = _monitoringService.IsPaused;
            FillRoundedRect(g, isPaused ? Color.FromArgb(25, 45, 30) : Color.FromArgb(45, 25, 30), stopRect, 8);
            g.DrawString(isPaused ? "⏵ Start Service" : "⏸ Stop Service", new Font("Segoe UI", 11, FontStyle.Bold), new SolidBrush(isPaused ? Color.MediumSpringGreen : Color.IndianRed), 65, this.Height - 65);

            if (_activeView == "Settings")
            {
                var settingsArea = new Rectangle(260, 0, this.Width - 260, this.Height);
                g.SetClip(settingsArea);
                g.TranslateTransform(0, -_settingsScrollY);
                DrawSettingsPage(g);
                g.ResetTransform();
                g.ResetClip();
            }
            else
            {
                switch (_activeView) {
                    case "System Status": DrawStatusPage(g); break;
                    case "Devices": DrawDevicesPage(g); break;
                    case "Security Actions": DrawSecurityPage(g); break;
                }
            }
        }

        private void DrawStatusPage(Graphics g)
        {
            using (var primaryBrush = new SolidBrush(PrimaryTextColor))
            using (var secondaryBrush = new SolidBrush(SecondaryTextColor))
            {
                g.DrawString("System Status", new Font("Segoe UI", 24, FontStyle.Bold), primaryBrush, 300, 60);
                g.DrawString("Monitor your paired Bluetooth devices and configure auto-lock thresholds.", new Font("Segoe UI", 11), secondaryBrush, 300, 110);

                // Card 1: Connection Status
                var card1 = new Rectangle(300, 160, 640, 120);
                FillRoundedRect(g, CardColor, card1, 12);
                bool isActive = !_monitoringService.IsPaused;
                
                // Icon Background
                using (var iconBrush = new SolidBrush(CardInnerColor))
                    g.FillEllipse(iconBrush, 330, 195, 50, 50);
                
                // Pulse Icon
                using (var pulsePen = new Pen(isActive ? Color.MediumSpringGreen : SecondaryTextColor, 2))
                {
                    g.DrawLines(pulsePen, new Point[] { 
                        new Point(340, 220), new Point(345, 220), new Point(350, 210), 
                        new Point(355, 230), new Point(360, 220), new Point(370, 220) 
                    });
                }

                g.DrawString(isActive ? "Monitoring Active" : "Monitoring Paused", new Font("Segoe UI", 14, FontStyle.Bold), primaryBrush, 395, 195);
                g.DrawString($"Tracking \"{_monitoringService.MonitoredDeviceName}\"", new Font("Segoe UI", 10), secondaryBrush, 395, 225);
                
                // Visa positivt värde (t.ex. 88 istället för -88)
                string rssiVal = _monitoringService.CurrentRssi > -110 ? Math.Abs(_monitoringService.CurrentRssi).ToString() : "—";
                g.DrawString(rssiVal, new Font("Segoe UI", 28, FontStyle.Bold), primaryBrush, 780, 185);
                g.DrawString("dBm", new Font("Segoe UI", 10, FontStyle.Bold), secondaryBrush, 885, 202);
                g.DrawString("CURRENT RSSI", new Font("Segoe UI", 8, FontStyle.Bold), new SolidBrush(Color.MediumSpringGreen), 830, 235);

                // Card 2: Live Signal Strength
                var card2 = new Rectangle(300, 310, 640, 320);
                FillRoundedRect(g, CardColor, card2, 12);
                
                g.DrawString("Live Signal Strength", new Font("Segoe UI", 12, FontStyle.Bold), primaryBrush, 330, 340);

                // Graph Area
                var graphRect = new Rectangle(330, 400, 580, 150);
                
                // Threshold Line (Red dotted)
            using (var thresholdPen = new Pen(Color.FromArgb(150, 255, 60, 60), 1))
            {
                thresholdPen.DashStyle = DashStyle.Dash;
                // Vänd logiken: Stark signal (-40) är högst upp (0), svag (-110) är längst ner (height)
                float normalizedThreshold = (_monitoringService.Threshold + 110) / 70f;
                int thresholdY = graphRect.Bottom - (int)(normalizedThreshold * graphRect.Height);
                g.DrawLine(thresholdPen, graphRect.Left, thresholdY, graphRect.Right, thresholdY);
            }

            // Signal Line
                if (_signalHistory.Count > 1)
                {
                    using (var linePen = new Pen(Color.DodgerBlue, 2.5f))
                    {
                        linePen.LineJoin = LineJoin.Round;
                        var points = new List<PointF>();
                        float xStep = (float)graphRect.Width / 100f;
                        
                        int hoverIndex = -1;
                        float hoverX = -1, hoverY = -1;

                        for (int i = 0; i < _signalHistory.Count; i++)
                        {
                            float x = graphRect.Left + i * xStep;
                            // Skala: 0% vid -110 dBm, 100% vid -40 dBm
                            float strengthPct = Math.Clamp((_signalHistory[i] + 110) / 70f, 0, 1);
                            float y = graphRect.Bottom - (strengthPct * graphRect.Height);
                            points.Add(new PointF(x, y));

                            if (Math.Abs(_mouseLocation.X - x) < xStep / 2 && graphRect.Contains(_mouseLocation))
                            {
                                hoverIndex = i;
                                hoverX = x;
                                hoverY = y;
                            }
                        }
                        g.DrawCurve(linePen, points.ToArray(), 0.5f);

                        if (hoverIndex != -1)
                        {
                            g.FillEllipse(Brushes.White, hoverX - 4, hoverY - 4, 8, 8);
                            g.DrawEllipse(new Pen(Color.DodgerBlue, 2), hoverX - 4, hoverY - 4, 8, 8);

                            int strength = (int)Math.Clamp(((_signalHistory[hoverIndex] + 110) / 70f) * 100, 0, 100);
                            string tooltip = $"Signal Level: {strength}% ({Math.Abs(_signalHistory[hoverIndex])} dBm)";
                            var tipSize = g.MeasureString(tooltip, new Font("Segoe UI", 9, FontStyle.Bold));
                            var tipRect = new RectangleF(hoverX - tipSize.Width / 2, hoverY - tipSize.Height - 15, tipSize.Width + 10, tipSize.Height + 5);
                            
                            FillRoundedRect(g, Color.FromArgb(220, 30, 40, 60), new Rectangle((int)tipRect.X, (int)tipRect.Y, (int)tipRect.Width, (int)tipRect.Height), 4);
                            g.DrawString(tooltip, new Font("Segoe UI", 9, FontStyle.Bold), Brushes.White, tipRect.X + 5, tipRect.Y + 2);
                        }
                    }
                }
            }
        }

        private void DrawDevicesPage(Graphics g)
        {
            g.DrawString("Devices", new Font("Segoe UI", 24, FontStyle.Bold), Brushes.White, 300, 80);
            var addBtnRect = new Rectangle(810, 95, 130, 40);
            FillRoundedRect(g, Color.DodgerBlue, addBtnRect, 10);
            g.DrawString("+ Add Device", new Font("Segoe UI", 11, FontStyle.Bold), Brushes.White, 830, 105);

            int devY = 200;
            foreach (var dev in _pairedDevices) {
                var devRect = new Rectangle(300, devY, 640, 100);
                FillRoundedRect(g, Color.FromArgb(26, 35, 51), devRect, 12);
                
                g.DrawEllipse(new Pen(Color.DodgerBlue, 2), 330, devY + 35, 32, 32);
                g.DrawString(dev.Name, new Font("Segoe UI", 13, FontStyle.Bold), Brushes.White, 380, devY + 25);
                
                int currentX = 380;
                g.DrawString(dev.Category, new Font("Segoe UI", 10), Brushes.Gray, currentX, devY + 55);
                currentX += (int)g.MeasureString(dev.Category + " ", new Font("Segoe UI", 10)).Width;

                g.FillEllipse(Brushes.DimGray, currentX + 5, devY + 63, 4, 4);
                currentX += 15;

                byte? batPct = dev.BatteryLevel;
                if (dev.Id == _bluetoothManager.MonitoredDeviceId && _bluetoothManager.MonitoredBatteryLevel.HasValue)
                    batPct = _bluetoothManager.MonitoredBatteryLevel;

                if (batPct.HasValue) {
                    DrawBatteryIcon(g, currentX, devY + 58, batPct.Value);
                    currentX += 30;
                    g.DrawString($"{batPct.Value}%", new Font("Segoe UI", 10, FontStyle.Bold), Brushes.LightGray, currentX, devY + 55);
                } else {
                    g.DrawString("—", new Font("Segoe UI", 10, FontStyle.Italic), Brushes.DimGray, currentX, devY + 55);
                }

                g.DrawString("🗑", new Font("Segoe UI", 16), Brushes.DimGray, 900, devY + 35);
                devY += 115;
            }
        }

        private void DrawBatteryIcon(Graphics g, int x, int y, int percentage)
        {
            Color batteryColor = percentage > 20 ? Color.MediumSpringGreen : Color.FromArgb(255, 60, 60);
            if (percentage <= 15) batteryColor = Color.FromArgb(255, 60, 60);

            g.DrawRectangle(new Pen(Color.FromArgb(80, 90, 110), 1), x, y, 22, 12);
            g.FillRectangle(new SolidBrush(Color.FromArgb(80, 90, 110)), x + 22, y + 3, 2, 6);
            float width = (percentage / 100f) * 20;
            g.FillRectangle(new SolidBrush(batteryColor), x + 1, y + 1, width, 10);
            if (percentage <= 15) g.DrawString("(!)", new Font("Segoe UI", 8, FontStyle.Bold), new SolidBrush(batteryColor), x - 20, y - 2);
        }

        private void DrawSecurityPage(Graphics g)
        {
            g.DrawString("Security Actions", new Font("Segoe UI", 24, FontStyle.Bold), Brushes.White, 300, 80);
            var listRect = new Rectangle(300, 220, 640, 400);
            FillRoundedRect(g, Color.FromArgb(26, 35, 51), listRect, 12);
            string[] actions = { "Lock Workstation", "Auto-Unlock (Wake on Approach)", "Clear Clipboard", "Play Warning Sound" };
            bool[] states = { _lockWorkstation, _autoUnlock, _clearClipboard, _playWarning };
            int actY = 250;
            for (int i = 0; i < actions.Length; i++) {
                g.DrawString(actions[i], new Font("Segoe UI", 11, FontStyle.Bold), Brushes.White, 380, actY);
                DrawToggle(g, 860, actY + 5, states[i]);
                actY += 90;
            }
        }

        private void DrawSettingsPage(Graphics g) 
        { 
            using (var primaryBrush = new SolidBrush(PrimaryTextColor))
            using (var secondaryBrush = new SolidBrush(SecondaryTextColor))
            {
                g.DrawString("Settings", new Font("Segoe UI", 24, FontStyle.Bold), primaryBrush, 300, 40);
                g.DrawString("Adjust the sensitivity, timing, and appearance of the app.", new Font("Segoe UI", 11), secondaryBrush, 300, 90);

                // 1. Appearance Card
                var cardAppearance = new Rectangle(300, 160, 640, 220);
                FillRoundedRect(g, CardColor, cardAppearance, 12);
                g.DrawString("Appearance", new Font("Segoe UI", 13, FontStyle.Bold), primaryBrush, 330, 195);
                g.DrawString("Choose your preferred theme or sync with Windows.", new Font("Segoe UI", 10), secondaryBrush, 330, 235);

                // Theme Buttons Area
                var themeBarRect = new Rectangle(330, 290, 580, 60);
                FillRoundedRect(g, CardInnerColor, themeBarRect, 8);

                string[] themes = { "Light", "Dark", "Auto" };
                int themeX = 330;
                foreach (var t in themes)
                {
                    bool isSel = _appearanceTheme == t;
                    var btnRect = new Rectangle(themeX, 290, t == "Auto" ? 180 : 200, 60);
                    if (t == "Auto") btnRect.X = 730;

                    if (isSel) FillRoundedRect(g, SidebarActiveColor, btnRect, 8);
                    
                    string icon = t == "Light" ? "☀" : (t == "Dark" ? "☾" : "🖥");
                    using (var themeTextBrush = new SolidBrush(isSel ? Color.DodgerBlue : secondaryBrush.Color))
                        g.DrawString($"{icon} {t}", new Font("Segoe UI", 11, isSel ? FontStyle.Bold : FontStyle.Regular), themeTextBrush, btnRect.X + (btnRect.Width/2) - 30, btnRect.Y + 18);
                    
                    if (t == "Light") themeX += 200;
                    else if (t == "Dark") themeX += 200;
                }

                // 2. Lock Threshold Card
                var cardThreshold = new Rectangle(300, 410, 640, 190);
                FillRoundedRect(g, CardColor, cardThreshold, 12);
                g.DrawString("Lock Threshold", new Font("Segoe UI", 13, FontStyle.Bold), primaryBrush, 330, 445);
                g.DrawString("If the signal drops below this level, the grace period begins.", new Font("Segoe UI", 10), secondaryBrush, 330, 485);
                
                // Value Badge
                var badgeRect = new Rectangle(840, 440, 80, 30);
                FillRoundedRect(g, CardInnerColor, badgeRect, 6);
                // Visa positivt värde i badge (t.ex. 75 istället för -75)
                g.DrawString($"{Math.Abs(_monitoringService.Threshold)} dBm", new Font("Segoe UI", 9, FontStyle.Bold), secondaryBrush, 850, 447);

                // Slider
                DrawCustomSlider(g, 330, 550, 580, (_monitoringService.Threshold + 100) / 60f, "Far (100)", "Close (40)");

                // 3. Grace Period Card
                var cardGrace = new Rectangle(300, 630, 640, 190);
                FillRoundedRect(g, CardColor, cardGrace, 12);
                g.DrawString("Grace Period", new Font("Segoe UI", 13, FontStyle.Bold), primaryBrush, 330, 665);
                g.DrawString("Time to wait before locking to prevent accidental triggers.", new Font("Segoe UI", 10), secondaryBrush, 330, 705);

                // Value Badge
                var badgeGrace = new Rectangle(880, 660, 40, 30);
                FillRoundedRect(g, CardInnerColor, badgeGrace, 6);
                g.DrawString($"{_monitoringService.GracePeriodSeconds}s", new Font("Segoe UI", 9, FontStyle.Bold), secondaryBrush, 888, 667);

                // Slider
                DrawCustomSlider(g, 330, 770, 580, _monitoringService.GracePeriodSeconds / 30f, "Instant (0s)", "30s");
            }
        }

        private void DrawCustomSlider(Graphics g, int x, int y, int width, float percentage, string leftLabel, string rightLabel)
        {
            // Track
            FillRoundedRect(g, CardInnerColor, new Rectangle(x, y, width, 10), 5);
            // Progress
            FillRoundedRect(g, Color.DodgerBlue, new Rectangle(x, y, (int)(width * percentage), 10), 5);
            // Thumb
            g.FillEllipse(Brushes.DodgerBlue, x + (int)(width * percentage) - 10, y - 5, 20, 20);
            
            // Labels
            using (var secondaryBrush = new SolidBrush(SecondaryTextColor))
            {
                g.DrawString(leftLabel, new Font("Segoe UI", 8), secondaryBrush, x, y + 25);
                var sizeR = g.MeasureString(rightLabel, new Font("Segoe UI", 8));
                g.DrawString(rightLabel, new Font("Segoe UI", 8), secondaryBrush, x + width - sizeR.Width, y + 25);
            }
        }

        private void DrawToggle(Graphics g, int x, int y, bool on) { FillRoundedRect(g, on ? Color.DodgerBlue : Color.FromArgb(45, 55, 75), new Rectangle(x, y, 60, 30), 15); g.FillEllipse(Brushes.White, on ? x + 35 : x + 5, y + 5, 20, 20); }
        private void FillRoundedRect(Graphics g, Color color, Rectangle rect, int radius) {
            using (var brush = new SolidBrush(color)) {
                var path = new GraphicsPath();
                path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
                path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
                path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }
}
