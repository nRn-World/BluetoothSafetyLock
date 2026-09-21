using System;
using System.Timers;
using Microsoft.Win32;

namespace BluetoothSafetyLock
{
    public class MonitoringService : IDisposable
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly System.Timers.Timer _timer;
        public DateTime LastUpdateReceived { get; private set; } = DateTime.MinValue;

        // ---- Shared mutable state: all fields below are guarded by _stateLock (FAS 1.5) ----
        private readonly object _stateLock = new();
        private DateTime _monitoringStartTime = DateTime.MinValue;
        private string _monitoredDeviceName = "None";
        private short _currentRssi = -100;
        private short _threshold = -100;
        private int _gracePeriodSeconds = 15;
        private bool _isPaused = true;
        private bool _isLocked;
        private bool _hasConfirmedConnection;
        private int _realRssiSamples;
        private bool _pendingLock;
        private int _violationStrikes;

        public string MonitoredDeviceName { get => Volatile.Read(ref _monitoredDeviceName); set => Volatile.Write(ref _monitoredDeviceName, value); }
        public short CurrentRssi { get { lock (_stateLock) return _currentRssi; } }
        public short Threshold { get { lock (_stateLock) return _threshold; } set { lock (_stateLock) _threshold = value; } }
        public int GracePeriodSeconds { get { lock (_stateLock) return _gracePeriodSeconds; } set { lock (_stateLock) _gracePeriodSeconds = value; } }
        public bool IsPaused { get { lock (_stateLock) return _isPaused; } set { lock (_stateLock) _isPaused = value; } }
        public bool IsLocked { get { lock (_stateLock) return _isLocked; } }

        // Simple boolean toggles; UI writes these from the UI thread only.
        public bool IsAutoUnlockEnabled { get; set; } = true;
        public bool IsLockWorkstationEnabled { get; set; } = true;
        public bool IsClearClipboardEnabled { get; set; } = true;
        public bool IsPlayWarningEnabled { get; set; } = false;

        /// <summary>
        /// True efter Windows IsConnected (-50) eller efter tillräckligt många BLE-RSSI-uppdateringar (telefonen syns i luften).
        /// Många mobiler rapporterar aldrig IsConnected=true mot PC; då räcker närvaro via annonser.
        /// </summary>
        private const int RealRssiSamplesRequired = 2;
        /// <summary>Vänta 10 sekunder efter start innan vi ens kollar efter frånkoppling.</summary>
        private const double MinSecondsBeforeAnyLock = 10.0;
        /// <summary>Vid enbart BLE-annonser, vänta 20 sekunder efter start innan vi tillåter låsning vid tystnad.</summary>
        private const double MinMonitoringSecondsBeforeSilenceLock = 20.0;
        /// <summary>Tillåter upp till 5 sekunders tystnad för att absorbera små dippar.</summary>
        private const double AdvertisementSilenceSeconds = 5.0;
        /// <summary>RSSI är brusigt: glidande medelvärde + hysteres förhindrar falska lås vid gränsvärdet (FAS 3.1).</summary>
        private const double RssiSmoothingAlpha = 0.4; // Vikt för varje ny mätning i EMA (0-1). Lägre = jämnare.
        /// <summary>Hysteres: signalen måste vara så här mycket under tröskeln för att räknas som brott.</summary>
        private const short HysteresisMargin = 5;

        /// <summary>Sentinel från BluetoothManager när System.Devices.Aep.IsConnected är true för målenheten.</summary>
        private const short ConnectionWatcherConnected = -50;
        /// <summary>Sentinel när IsConnected är false / ingen anslutning.</summary>
        private const short ConnectionWatcherDisconnected = -128;

        public event Action<string>? StatusChanged;
        public event Action? Locked;

        public MonitoringService(BluetoothManager bluetoothManager)
        {
            _bluetoothManager = bluetoothManager;
            _bluetoothManager.RssiUpdated += OnRssiUpdated;
            _timer = new System.Timers.Timer(500);
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();

            LastUpdateReceived = DateTime.MinValue;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                lock (_stateLock)
                {
                    _isLocked = false;
                    _monitoringStartTime = DateTime.Now;
                    LastUpdateReceived = DateTime.Now;
                }
                Logger.Info("Manual login detected.");
                StatusChanged?.Invoke("Manual login detected.");
            }
        }

        private System.Timers.Timer? _lockDelayTimer;
        private const int MaxViolationStrikes = 2;

        private void CancelPendingLock()
        {
            _pendingLock = false;
            _violationStrikes = 0;
            _lockDelayTimer?.Stop();
            _lockDelayTimer?.Dispose();
            _lockDelayTimer = null;
        }

        /// <param name="fromWindowsDisconnect">True when Windows reports disconnect (-128); uses grace period instead of instant lock.</param>
        private void ScheduleDisconnectLock(bool fromWindowsDisconnect)
        {
            lock (_stateLock)
            {
                if (_isPaused || _isLocked || !_hasConfirmedConnection) return;
                if ((DateTime.Now - _monitoringStartTime).TotalSeconds < MinSecondsBeforeAnyLock) return;
                if (!fromWindowsDisconnect && (DateTime.Now - _monitoringStartTime).TotalSeconds < MinMonitoringSecondsBeforeSilenceLock) return;
                if (_pendingLock) return;

                if (!fromWindowsDisconnect)
                {
                    _violationStrikes++;
                    if (_violationStrikes < MaxViolationStrikes) return;
                }

                _pendingLock = true;

                // Use the user's Grace Period slider!
                int delayMs = _gracePeriodSeconds * 1000;
                if (delayMs <= 0) delayMs = 500; // minimum

                _lockDelayTimer?.Stop();
                _lockDelayTimer?.Dispose();
                _lockDelayTimer = new System.Timers.Timer(delayMs) { AutoReset = false };
                _lockDelayTimer.Elapsed += (s, e) =>
                {
                    bool doLock = false;
                    lock (_stateLock)
                    {
                        if (_pendingLock && !_isPaused && !_isLocked)
                        {
                            _pendingLock = false;
                            doLock = true;
                        }
                    }
                    if (doLock) TriggerLock();
                };
                _lockDelayTimer.Start();
            }
        }

        private void OnRssiUpdated(short rssi)
        {
            if (rssi == ConnectionWatcherDisconnected)
            {
                // Native connection lost. We allow the silence timeout to handle the UI drop
                // because mobile devices constantly toggle active connection to save power.
                Logger.Info("Connection watcher reported disconnect.");
                ScheduleDisconnectLock(fromWindowsDisconnect: true);
                return;
            }

            if (rssi == ConnectionWatcherConnected)
            {
                // A native connection was established. We DO NOT overwrite CurrentRssi for graph stability.
                lock (_stateLock)
                {
                    _hasConfirmedConnection = true;
                    LastUpdateReceived = DateTime.Now;
                    _violationStrikes = 0;
                    if (_pendingLock) CancelPendingLock();
                }
                if (!IsPaused && IsLocked && IsAutoUnlockEnabled)
                    WakeAndResetLock();
                return;
            }

            // REAL BLE RSSI
            short smoothed;
            lock (_stateLock)
            {
                // Exponentiellt glidande medelvärde (FAS 3.1): dämpar 10-20 dBm-svängningar.
                _currentRssi = _realRssiSamples == 0
                    ? rssi
                    : (short)Math.Round(RssiSmoothingAlpha * rssi + (1 - RssiSmoothingAlpha) * _currentRssi);
                smoothed = _currentRssi;

                LastUpdateReceived = DateTime.Now;
                _realRssiSamples++;
                if (_realRssiSamples >= RealRssiSamplesRequired)
                    _hasConfirmedConnection = true;
            }

            // Reset strikes if signal is good — threshold itself cancels pending locks.
            if (smoothed > Threshold)
            {
                lock (_stateLock)
                {
                    _violationStrikes = 0;
                    if (_pendingLock) CancelPendingLock();
                }
                if (!IsPaused && IsLocked && IsAutoUnlockEnabled)
                    WakeAndResetLock();
            }
            else if (smoothed <= Threshold - HysteresisMargin)
            {
                // Signal is clearly below threshold (hysteresis), count as strike.
                ScheduleDisconnectLock(fromWindowsDisconnect: false);
            }
        }

        private void WakeAndResetLock()
        {
            lock (_stateLock)
            {
                _isLocked = false;
                _monitoringStartTime = DateTime.Now;
            }
            Logger.Info("Device back in range — waking screen.");
            NativeMethods.WakeScreen();
            StatusChanged?.Invoke("Welcome back!");
        }

        private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            lock (_stateLock)
            {
                if (_isPaused || _isLocked || !_hasConfirmedConnection) return;

                // If we just started (within 10s), never lock.
                if ((DateTime.Now - _monitoringStartTime).TotalSeconds < MinSecondsBeforeAnyLock) return;

                // If Windows says we're connected, trust it – but only if we've also
                // seen a signal recently (within 5s). Windows can be slow to detect
                // that a BLE device has been turned off.
                if (_bluetoothManager.IsDeviceConnected && (DateTime.Now - LastUpdateReceived).TotalSeconds < 5.0)
                {
                    LastUpdateReceived = DateTime.Now;
                    return;
                }

                // Drop UI to bottom if silence > 5 seconds to match the silence timeout
                if ((DateTime.Now - LastUpdateReceived).TotalSeconds > 5.0)
                {
                    _currentRssi = -110;
                }

                // If we haven't heard anything for 5 seconds (AdvertisementSilenceSeconds), lock.
                if ((DateTime.Now - LastUpdateReceived).TotalSeconds < AdvertisementSilenceSeconds) return;
            }
            ScheduleDisconnectLock(fromWindowsDisconnect: false);
        }

        private void TriggerLock()
        {
            Logger.Info("Triggering workstation lock.");
            lock (_stateLock)
            {
                _isLocked = true;
            }
            Locked?.Invoke();

            if (IsPlayWarningEnabled)
                System.Media.SystemSounds.Exclamation.Play();

            if (IsClearClipboardEnabled)
                NativeMethods.ClearClipboard();

            if (IsLockWorkstationEnabled)
                NativeMethods.LockWorkStation();

            StatusChanged?.Invoke("Workstation locked.");
        }

        public async Task StartMonitoringAsync(string deviceId)
        {
            lock (_stateLock)
            {
                _monitoringStartTime = DateTime.Now;
                _realRssiSamples = 0;
                _hasConfirmedConnection = false;
                _pendingLock = false;
                _violationStrikes = 0;
                _isLocked = false;
                LastUpdateReceived = DateTime.Now;
            }
            Logger.Info($"StartMonitoringAsync for device {deviceId.Substring(0, Math.Min(8, deviceId.Length))}…");

            await _bluetoothManager.StartMonitoringAsync(deviceId);
            MonitoredDeviceName = _bluetoothManager.MonitoredDeviceId ?? "Device";
            IsPaused = false;
            StatusChanged?.Invoke($"Monitoring started for {MonitoredDeviceName}");
        }

        public void StopMonitoring() { _bluetoothManager.StopMonitoring(); }

        public void Dispose()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _bluetoothManager.RssiUpdated -= OnRssiUpdated;
            CancelPendingLock();
            _timer.Stop();
            _timer.Dispose();
            _bluetoothManager.Dispose();
        }
    }
}
