using System;
using System.IO;
using System.Text.Json;

namespace BluetoothSafetyLock
{
    /// <summary>
    /// Local, non-cloud application settings. Serialized to %APPDATA%\BluetoothSafetyLock\settings.json.
    /// No network, no telemetry — everything stays on the user's machine.
    /// </summary>
    public class AppSettings
    {
        public short Threshold { get; set; } = -100;
        public int GracePeriodSeconds { get; set; } = 15;
        public bool IsLockWorkstationEnabled { get; set; } = true;
        public bool IsAutoUnlockEnabled { get; set; } = true;
        public bool IsClearClipboardEnabled { get; set; } = true;
        public bool IsPlayWarningEnabled { get; set; } = false;
        public string SelectedDeviceId { get; set; } = string.Empty;
        public string SelectedDeviceName { get; set; } = "None";
        public string AppearanceTheme { get; set; } = "Auto";
    }

    public static class SettingsStore
    {
        private static readonly object _ioLock = new();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BluetoothSafetyLock", "settings.json");

        /// <summary>The current in-memory settings, loaded once at startup.</summary>
        public static AppSettings Current { get; private set; } = new();

        /// <summary>Loads settings from disk into Current. Falls back to safe defaults on any failure.</summary>
        public static void Load()
        {
            try
            {
                lock (_ioLock)
                {
                    if (!File.Exists(FilePath))
                    {
                        Current = new AppSettings();
                        Logger.Info("No settings file found; using defaults.");
                        return;
                    }

                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded == null)
                    {
                        Current = new AppSettings();
                        Logger.Warn("settings.json was empty or invalid; using defaults.");
                        return;
                    }

                    // Validate/clamp so a corrupted or hand-edited file can never produce unsafe values.
                    loaded.Threshold = Math.Clamp(loaded.Threshold, (short)-100, (short)-30);
                    loaded.GracePeriodSeconds = Math.Clamp(loaded.GracePeriodSeconds, 0, 60);
                    if (loaded.AppearanceTheme is not ("Light" or "Dark" or "Auto"))
                        loaded.AppearanceTheme = "Auto";

                    Current = loaded;
                    Logger.Info("Settings loaded.");
                }
            }
            catch (Exception ex)
            {
                // A corrupt settings file must never prevent the app from starting protected.
                Current = new AppSettings();
                Logger.Error("Failed to read settings.json; falling back to safe defaults.", ex);
            }
        }

        /// <summary>Atomically persists Current: writes to a temp file, then moves it into place.</summary>
        public static void SaveCurrent()
        {
            try
            {
                lock (_ioLock)
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, FilePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save settings.json.", ex);
            }
        }
    }
}
