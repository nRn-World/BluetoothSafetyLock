using System;
using System.Timers;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BluetoothSafetyLock
{
    public class MonitoringService : IDisposable
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly System.Timers.Timer _timer;
        public DateTime LastUpdateReceived { get; private set; } = DateTime.MinValue;
        private DateTime _monitoringStartTime = DateTime.MinValue;
        public string MonitoredDeviceName { get; set; } = "None";
        
        public short CurrentRssi { get; private set; } = -100;
        public short Threshold { get; set; } = -100;
        public int GracePeriodSeconds { get; set; } = 15;
        public bool IsPaused { get; set; } = true;
        public bool IsAutoUnlockEnabled { get; set; } = true;
        private bool _isLocked = false;
        /// <summary>
        /// True efter Windows IsConnected (-50) eller efter tillräckligt många BLE-RSSI-uppdateringar (telefonen syns i luften).
        /// Många mobiler rapporterar aldrig IsConnected=true mot PC; då räcker närvaro via annonser.
        /// </summary>
        private bool _hasConfirmedConnection = false;
        private int _realRssiSamples;
        private const int RealRssiSamplesRequired = 2;
        /// <summary>Vänta 10 sekunder efter start innan vi ens kollar efter frånkoppling.</summary>
        private const double MinSecondsBeforeAnyLock = 10.0;
        /// <summary>Vid enbart BLE-annonser, vänta 20 sekunder efter start innan vi tillåter låsning vid tystnad.</summary>
        private const double MinMonitoringSecondsBeforeSilenceLock = 20.0;
        /// <summary>
        /// Öka toleransen för signalförlust till 25 sekunder. Detta förhindrar slumpmässiga utloggningar
        /// vid tillfälliga störningar, men loggar fortfarande ut om kontakten tappas helt.
        /// </summary>
        private const double AdvertisementSilenceSeconds = 5.0;
        private const int LockDelayMsWindowsDisconnect = 100; // Snabbare respons (0.1s)
        private const int LockDelayMsSilenceInferred = 500;   // Snabbare respons (0.5s)

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
            _timer = new System.Timers.Timer(500); // Kontrollera status var 500ms istället för 1000ms
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();

            LastUpdateReceived = DateTime.MinValue;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _isLocked = false;
                _monitoringStartTime = DateTime.Now;
                LastUpdateReceived = DateTime.Now;
                StatusChanged?.Invoke("Manual login detected.");
            }
        }

        private System.Timers.Timer? _lockDelayTimer;
        private bool _pendingLock = false;
        private int _violationStrikes = 0;
        private const int MaxViolationStrikes = 2; // Sänk till 2 för snabbare respons vid tystnad

        private void CancelPendingLock()
        {
            _pendingLock = false;
            _violationStrikes = 0;
            _lockDelayTimer?.Stop();
            _lockDelayTimer?.Dispose();
            _lockDelayTimer = null;
        }

        /// <param name="fromWindowsDisconnect">Sant när Windows rapporterar frånkoppling (-128); kort fördröjning.</param>
        private void ScheduleDisconnectLock(bool fromWindowsDisconnect)
        {
            if (IsPaused || _isLocked || !_hasConfirmedConnection) return;
            if ((DateTime.Now - _monitoringStartTime).TotalSeconds < MinSecondsBeforeAnyLock) return;
            if (!fromWindowsDisconnect && (DateTime.Now - _monitoringStartTime).TotalSeconds < MinMonitoringSecondsBeforeSilenceLock) return;
            if (_pendingLock) return;

            // Kräv flera "strikes" vid signalförlust för att undvika slumpmässiga lås.
            // Vid Windows explicit frånkoppling låser vi direkt (strike 3).
            if (!fromWindowsDisconnect)
            {
                _violationStrikes++;
                if (_violationStrikes < MaxViolationStrikes) return;
            }

            _pendingLock = true;
            int delayMs = fromWindowsDisconnect ? LockDelayMsWindowsDisconnect : LockDelayMsSilenceInferred;
            _lockDelayTimer?.Stop();
            _lockDelayTimer?.Dispose();
            _lockDelayTimer = new System.Timers.Timer(delayMs) { AutoReset = false };
            _lockDelayTimer.Elapsed += (s, e) => {
                if (_pendingLock && !IsPaused && !_isLocked)
                {
                    _pendingLock = false;
                    TriggerLock();
                }
            };
            _lockDelayTimer.Start();
        }

        private void OnRssiUpdated(short rssi)
        {
            if (rssi == ConnectionWatcherDisconnected)
            {
                CurrentRssi = ConnectionWatcherDisconnected;
                ScheduleDisconnectLock(fromWindowsDisconnect: true);
                return;
            }

            // Återställ strikes vid varje lyckad signaluppdatering
            if (rssi > Threshold || rssi == ConnectionWatcherConnected)
            {
                _violationStrikes = 0;
                if (_pendingLock) CancelPendingLock();
            }

            if (rssi == ConnectionWatcherConnected)
            {
                _hasConfirmedConnection = true;
                LastUpdateReceived = DateTime.Now;
                CurrentRssi = rssi;
            }
            else
            {
                CurrentRssi = rssi;
                LastUpdateReceived = DateTime.Now;
                _realRssiSamples++;
                if (_realRssiSamples >= RealRssiSamplesRequired)
                    _hasConfirmedConnection = true;
                
                // Om signalen är under tröskelvärdet, räkna som strike
                if (rssi < Threshold)
                {
                    ScheduleDisconnectLock(fromWindowsDisconnect: false);
                }
            }

            if (IsPaused) return;

            if (_isLocked && IsAutoUnlockEnabled && rssi == ConnectionWatcherConnected)
            {
                _isLocked = false;
                _monitoringStartTime = DateTime.Now;
                NativeMethods.WakeScreen();
                StatusChanged?.Invoke("Välkommen tillbaka!");
            }
        }

        private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (IsPaused || _isLocked || !_hasConfirmedConnection) return;
            
            // Om vi precis startat (inom 10s), lås aldrig.
            if ((DateTime.Now - _monitoringStartTime).TotalSeconds < MinSecondsBeforeAnyLock) return;

            // Om Windows säger att vi är anslutna, lita på det – men bara om vi också 
            // sett en signal nyligen (inom 5s). Windows kan vara segt på att upptäcka 
            // att en BLE-enhet stängts av.
            if (_bluetoothManager.IsDeviceConnected && (DateTime.Now - LastUpdateReceived).TotalSeconds < 5.0)
            {
                LastUpdateReceived = DateTime.Now;
                return;
            }
            
            // Om vi inte hört något på 10 sekunder (AdvertisementSilenceSeconds), lås.
            if ((DateTime.Now - LastUpdateReceived).TotalSeconds < AdvertisementSilenceSeconds) return;
            
            ScheduleDisconnectLock(fromWindowsDisconnect: false);
        }

        private void TriggerLock()
        {
            _isLocked = true;
            Locked?.Invoke();
            NativeMethods.ClearClipboard();
            NativeMethods.LockWorkStation();
            StatusChanged?.Invoke("Workstation locked.");
        }

        public async Task StartMonitoringAsync(string deviceId)
        {
            _monitoringStartTime = DateTime.Now;
            _realRssiSamples = 0;
            _hasConfirmedConnection = false;
            _pendingLock = false;
            _violationStrikes = 0;
            _isLocked = false;
            LastUpdateReceived = DateTime.Now;
            
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
