using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using System.Linq;

namespace HardwareAnchor
{
    public class BluetoothDeviceModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsPaired { get; set; }
        public bool IsNearby { get; set; }
        public byte? BatteryLevel { get; set; }
        public string Category { get; set; } = "Other";
        public bool IsConnected { get; set; }
    }

    public class BluetoothManager : IDisposable
    {
        private BluetoothLEAdvertisementWatcher? _watcher;
        private DeviceWatcher? _deviceDiscoveryWatcher;
        private DeviceWatcher? _connectionWatcher;
        private string? _targetDeviceId;
        private ulong _targetBluetoothAddress;
        private BluetoothLEDevice? _monitoredLeDevice;
        private BluetoothDevice? _monitoredClassicDevice;

        public event Action<BluetoothDeviceModel>? DeviceDiscovered;
        public event Action? DiscoveryCompleted;
        public event Action<short>? RssiUpdated;
        public event Action<string, Action<bool>>? PairingConfirmationRequested;
        /// <summary>Anropas när batteriprocent för övervakad enhet uppdaterats.</summary>
        public event Action? MonitoredBatteryChanged;

        public byte? MonitoredBatteryLevel { get; private set; }
        public bool IsDeviceConnected { get; private set; } = false;
        /// <summary>Enhets-ID för pågående övervakning (null om ingen).</summary>
        public string? MonitoredDeviceId => _targetDeviceId;

        /// <summary>Sentinel till MonitoringService: Windows IsConnected true för vald enhet.</summary>
        public const short RssiSentinelConnected = -50;
        /// <summary>Sentinel: ingen BT-anslutning / IsConnected false.</summary>
        public const short RssiSentinelDisconnected = -128;

        private System.Timers.Timer? _batteryRefreshTimer;
        private System.Timers.Timer? _connectionPollTimer;
        private readonly Dictionary<string, bool> _targetEndpointIdCache = new();

        public BluetoothManager() { }

        private void NotifyConnectionState(bool connected)
        {
            if (IsDeviceConnected == connected) return;
            IsDeviceConnected = connected;
            RssiUpdated?.Invoke(connected ? RssiSentinelConnected : RssiSentinelDisconnected);
        }

        private async Task<bool> IsUpdateForTargetDeviceAsync(string id)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(_targetDeviceId)) return false;
            if (id == _targetDeviceId) return true;
            if (_targetBluetoothAddress == 0) return false;
            if (_targetEndpointIdCache.TryGetValue(id, out var cached)) return cached;

            bool isTarget = false;
            try {
                var le = await BluetoothLEDevice.FromIdAsync(id);
                if (le != null) {
                    try {
                        isTarget = le.BluetoothAddress == _targetBluetoothAddress;
                    } finally { le.Dispose(); }
                }
            } catch { }
            if (!isTarget) {
                try {
                    var classic = await BluetoothDevice.FromIdAsync(id);
                    if (classic != null) {
                        try {
                            isTarget = classic.BluetoothAddress == _targetBluetoothAddress;
                        } finally { classic.Dispose(); }
                    }
                } catch { }
            }
            _targetEndpointIdCache[id] = isTarget;
            return isTarget;
        }

        private void PublishNativeConnectionState()
        {
            if (_monitoredLeDevice == null && _monitoredClassicDevice == null) return;
            bool leConnected = _monitoredLeDevice != null && _monitoredLeDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
            bool classicConnected = _monitoredClassicDevice != null && _monitoredClassicDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
            NotifyConnectionState(leConnected || classicConnected);
        }

        private void OnMonitoredLeConnectionStatusChanged(BluetoothLEDevice sender, object _)
        {
            PublishNativeConnectionState();
        }

        private void OnMonitoredClassicConnectionStatusChanged(BluetoothDevice sender, object _)
        {
            PublishNativeConnectionState();
        }

        private void AttachConnectionStatusHandlers()
        {
            if (_monitoredLeDevice != null)
                _monitoredLeDevice.ConnectionStatusChanged += OnMonitoredLeConnectionStatusChanged;
            if (_monitoredClassicDevice != null)
                _monitoredClassicDevice.ConnectionStatusChanged += OnMonitoredClassicConnectionStatusChanged;
        }

        private void DetachConnectionStatusHandlers()
        {
            if (_monitoredLeDevice != null)
                _monitoredLeDevice.ConnectionStatusChanged -= OnMonitoredLeConnectionStatusChanged;
            if (_monitoredClassicDevice != null)
                _monitoredClassicDevice.ConnectionStatusChanged -= OnMonitoredClassicConnectionStatusChanged;
        }

        private void StartConnectionPollTimer()
        {
            StopConnectionPollTimer();
            _connectionPollTimer = new System.Timers.Timer(300) { AutoReset = true }; // Ännu snabbare polling (300ms) för omedelbar respons
            _connectionPollTimer.Elapsed += async (_, _) => await PollConnectionStateAsync();
            _connectionPollTimer.Start();
        }

        private void StopConnectionPollTimer()
        {
            if (_connectionPollTimer == null) return;
            try { _connectionPollTimer.Stop(); } catch { }
            _connectionPollTimer.Dispose();
            _connectionPollTimer = null;
        }

        private async Task PollConnectionStateAsync()
        {
            if (string.IsNullOrEmpty(_targetDeviceId)) return;
            try {
                if (_monitoredLeDevice != null || _monitoredClassicDevice != null) {
                    PublishNativeConnectionState();
                    return;
                }
            } catch { }
            await QueryInitialConnectionStateAsync();
        }

        public void StartDiscovery()
        {
            try {
                StopDiscovery();
                string aqsFilter = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
                                   "System.Devices.Aep.ProtocolId:=\"{bb7bb58e-4e49-42f4-88af-48f1ea746d46}\")";
                string[] requestedProperties = { "System.Devices.Aep.IsPaired", "System.Devices.Aep.IsConnected" };
                _deviceDiscoveryWatcher = DeviceInformation.CreateWatcher(aqsFilter, requestedProperties, DeviceInformationKind.AssociationEndpoint);
                _deviceDiscoveryWatcher.Added += (s, di) => { if (!string.IsNullOrEmpty(di.Name)) { DeviceDiscovered?.Invoke(CreateSimpleModel(di, true)); FetchDetailsAsync(di.Id); } };
                _deviceDiscoveryWatcher.Updated += (s, update) => { FetchDetailsAsync(update.Id); };
                _deviceDiscoveryWatcher.Start();

                Task.Run(async () => {
                    try {
                        var paired = await DeviceInformation.FindAllAsync(aqsFilter, requestedProperties);
                        foreach (var di in paired) if (!string.IsNullOrEmpty(di.Name)) { DeviceDiscovered?.Invoke(CreateSimpleModel(di, false)); FetchDetailsAsync(di.Id); }
                    } catch { }
                });
            } catch { }
        }

        private BluetoothDeviceModel CreateSimpleModel(DeviceInformation di, bool nearby)
        {
            var model = new BluetoothDeviceModel { Id = di.Id, Name = di.Name, IsPaired = di.Pairing.IsPaired, IsNearby = nearby, Category = GuessCategory(di.Name) };
            if (di.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var ic)) model.IsConnected = ic is bool b && b;
            return model;
        }

        private string GuessCategory(string name)
        {
            string n = name.ToLower();
            if (n.Contains("s25") || n.Contains("phone") || n.Contains("galaxy")) return "Phone";
            if (n.Contains("wh-") || n.Contains("airpod") || n.Contains("head") || n.Contains("bud")) return "Audio";
            return "Other";
        }

        private async void FetchDetailsAsync(string id)
        {
            try {
                var di = await DeviceInformation.CreateFromIdAsync(id, new[] { "System.Devices.Aep.Category", "System.Devices.Aep.Bluetooth.BatteryLevel", "{104E0000-B531-4F39-8C00-2053070759E0} 2", "{6196DF38-F020-410E-8F14-88A9620E83F0} 8" });
                var model = CreateSimpleModel(di, true);
                if (di.Properties.TryGetValue("System.Devices.Aep.Category", out var category)) {
                    if (category is string[] cats && cats.Length > 0) model.Category = cats[0].Split('.').Last();
                    else if (category is string cat) model.Category = cat.Split('.').Last();
                }
                object? batVal = null;
                if (di.Properties.TryGetValue("System.Devices.Aep.Bluetooth.BatteryLevel", out batVal)) { }
                else if (di.Properties.TryGetValue("{104E0000-B531-4F39-8C00-2053070759E0} 2", out batVal)) { }
                else if (di.Properties.TryGetValue("{6196DF38-F020-410E-8F14-88A9620E83F0} 8", out batVal)) { }
                if (batVal != null) model.BatteryLevel = (byte)Convert.ToInt32(batVal);
                DeviceDiscovered?.Invoke(model);
                if (model.Id == _targetDeviceId && model.BatteryLevel.HasValue)
                    MonitoredBatteryLevel = model.BatteryLevel;

                if (model.BatteryLevel == null) {
                    var leDevice = await BluetoothLEDevice.FromIdAsync(id);
                    if (leDevice != null) {
                        var services = await leDevice.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
                        if (services.Status == GattCommunicationStatus.Success) {
                            foreach (var service in services.Services) {
                                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                                if (chars.Status == GattCommunicationStatus.Success) {
                                    var res = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                                    if (res.Status == GattCommunicationStatus.Success) {
                                        model.BatteryLevel = Windows.Storage.Streams.DataReader.FromBuffer(res.Value).ReadByte();
                                        if (model.Id == _targetDeviceId)
                                            MonitoredBatteryLevel = model.BatteryLevel;
                                        DeviceDiscovered?.Invoke(model);
                                    }
                                }
                            }
                        }
                    }
                }
            } catch { }
        }

        public void StopDiscovery()
        {
            if (_deviceDiscoveryWatcher != null) {
                try { _deviceDiscoveryWatcher.Stop(); } catch { }
                _deviceDiscoveryWatcher = null;
            }
        }

        public async Task<bool> PairDeviceAsync(string deviceId)
        {
            try {
                var di = await DeviceInformation.CreateFromIdAsync(deviceId);
                di.Pairing.Custom.PairingRequested += (s, args) => args.Accept();
                var result = await di.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin);
                return result.Status == DevicePairingResultStatus.Paired || result.Status == DevicePairingResultStatus.AlreadyPaired;
            } catch { return false; }
        }

        public async Task<bool> UnpairDeviceAsync(string deviceId)
        {
            try { var di = await DeviceInformation.CreateFromIdAsync(deviceId); var result = await di.Pairing.UnpairAsync(); return result.Status == DeviceUnpairingResultStatus.Unpaired; } catch { return false; }
        }

        public async Task StartMonitoringAsync(string deviceId)
        {
            _targetDeviceId = deviceId;
            MonitoredBatteryLevel = null;
            IsDeviceConnected = false;
            StopMonitoring();

            // Hämta BT-adress via klassisk BT
            try {
                _monitoredClassicDevice = await BluetoothDevice.FromIdAsync(deviceId);
                if (_monitoredClassicDevice != null)
                    _targetBluetoothAddress = _monitoredClassicDevice.BluetoothAddress;
            } catch { }

            // Hämta BT-adress via BLE om klassisk misslyckades
            try {
                _monitoredLeDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
                if (_monitoredLeDevice != null && _targetBluetoothAddress == 0)
                    _targetBluetoothAddress = _monitoredLeDevice.BluetoothAddress;
            } catch { }

            AttachConnectionStatusHandlers();
            StartConnectionWatcher();
            await QueryInitialConnectionStateAsync();

            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += (s, e) => {
                if (e.BluetoothAddress == _targetBluetoothAddress)
                    RssiUpdated?.Invoke(e.RawSignalStrengthInDBm);
            };
            _watcher.Start();

            await ReadBatteryForDeviceIdAsync(deviceId);
            StartBatteryRefreshTimer();
            StartConnectionPollTimer();
        }

        private void StartConnectionWatcher()
        {
            try {
                string aqs = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
                             "System.Devices.Aep.ProtocolId:=\"{bb7bb58e-4e49-42f4-88af-48f1ea746d46}\") AND " +
                             "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

                string[] props = { "System.Devices.Aep.IsConnected" };
                _connectionWatcher = DeviceInformation.CreateWatcher(aqs, props, DeviceInformationKind.AssociationEndpoint);

                _connectionWatcher.Added += (s, di) => {
                    _ = Task.Run(async () => {
                        try {
                            if (!await IsUpdateForTargetDeviceAsync(di.Id)) return;
                            if (di.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var val)) {
                                bool connected = val is bool b && b;
                                NotifyConnectionState(connected);
                            }
                        } catch { }
                    });
                };

                _connectionWatcher.Updated += (s, update) => {
                    _ = Task.Run(async () => {
                        try {
                            if (!await IsUpdateForTargetDeviceAsync(update.Id)) return;
                            if (update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var val)) {
                                bool connected = val is bool b && b;
                                NotifyConnectionState(connected);
                            }
                        } catch { }
                    });
                };

                _connectionWatcher.Start();
            } catch {
            }
        }

        private async Task QueryInitialConnectionStateAsync()
        {
            if (string.IsNullOrEmpty(_targetDeviceId)) return;
            try {
                if (_monitoredLeDevice != null || _monitoredClassicDevice != null) {
                    PublishNativeConnectionState();
                    return;
                }
            } catch { }
            try {
                var di = await DeviceInformation.CreateFromIdAsync(_targetDeviceId, new[] { "System.Devices.Aep.IsConnected" });
                if (di.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var val)) {
                    bool connected = val is bool b && b;
                    NotifyConnectionState(connected);
                }
            } catch { }
        }

        private void StartBatteryRefreshTimer()
        {
            StopBatteryRefreshTimer();
            _batteryRefreshTimer = new System.Timers.Timer(30000) { AutoReset = true };
            _batteryRefreshTimer.Elapsed += async (_, _) => await RefreshMonitoredBatteryAsync();
            _batteryRefreshTimer.Start();
        }

        private void StopBatteryRefreshTimer()
        {
            if (_batteryRefreshTimer == null) return;
            try { _batteryRefreshTimer.Stop(); } catch { }
            _batteryRefreshTimer.Dispose();
            _batteryRefreshTimer = null;
        }

        private async Task RefreshMonitoredBatteryAsync()
        {
            if (string.IsNullOrEmpty(_targetDeviceId)) return;
            try {
                await ReadBatteryForDeviceIdAsync(_targetDeviceId);
            } catch { }
        }

        private async Task ReadBatteryForDeviceIdAsync(string id)
        {
            byte? level = null;
            try {
                var di = await DeviceInformation.CreateFromIdAsync(id, new[] {
                    "System.Devices.Aep.Bluetooth.BatteryLevel",
                    "{104E0000-B531-4F39-8C00-2053070759E0} 2",
                    "{6196DF38-F020-410E-8F14-88A9620E83F0} 8"
                });
                object? batVal = null;
                if (di.Properties.TryGetValue("System.Devices.Aep.Bluetooth.BatteryLevel", out batVal)) { }
                else if (di.Properties.TryGetValue("{104E0000-B531-4F39-8C00-2053070759E0} 2", out batVal)) { }
                else if (di.Properties.TryGetValue("{6196DF38-F020-410E-8F14-88A9620E83F0} 8", out batVal)) { }
                if (batVal != null)
                    level = (byte)Convert.ToInt32(batVal);

                if (!level.HasValue) {
                    var leDevice = await BluetoothLEDevice.FromIdAsync(id);
                    if (leDevice != null) {
                        var services = await leDevice.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
                        if (services.Status == GattCommunicationStatus.Success) {
                            foreach (var service in services.Services) {
                                var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                                if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Count > 0) {
                                    var res = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
                                    if (res.Status == GattCommunicationStatus.Success) {
                                        level = Windows.Storage.Streams.DataReader.FromBuffer(res.Value).ReadByte();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            } catch { }

            if (level.HasValue && (MonitoredBatteryLevel != level || !MonitoredBatteryLevel.HasValue)) {
                MonitoredBatteryLevel = level;
                MonitoredBatteryChanged?.Invoke();
            }
        }

        public void StopMonitoring()
        {
            StopBatteryRefreshTimer();
            StopConnectionPollTimer();
            DetachConnectionStatusHandlers();
            _targetEndpointIdCache.Clear();
            if (_monitoredLeDevice != null) { _monitoredLeDevice.Dispose(); _monitoredLeDevice = null; }
            if (_monitoredClassicDevice != null) { _monitoredClassicDevice.Dispose(); _monitoredClassicDevice = null; }
            if (_watcher != null) { _watcher.Stop(); _watcher = null; }
            if (_connectionWatcher != null) {
                try { _connectionWatcher.Stop(); } catch { }
                _connectionWatcher = null;
            }
        }

        public void Dispose() { StopMonitoring(); StopDiscovery(); }
    }
}
