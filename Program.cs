using System;
using System.Threading;
using System.Windows.Forms;

namespace BluetoothSafetyLock;

internal static class Program
{
    private const string SingleInstanceMutexName = "Local\\BluetoothSafetyLock.SingleInstance";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        Logger.Initialize();

        // Auto-updater: apply a staged update BEFORE the mutex and anything else.
        // If one was applied, a fresh process with the new version was started.
        if (UpdaterService.ApplyPendingInstall())
        {
            return;
        }

        // FAS 1.3: single-instance. A second copy must never run: two watchers would
        // double-poll the same device and could lock the workstation in conflict.
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        bool hasHandle = createdNew;
        if (!hasHandle)
        {
            try
            {
                hasHandle = mutex.WaitOne(TimeSpan.FromSeconds(3), false);
            }
            catch (AbandonedMutexException)
            {
                hasHandle = true;
            }
        }

        if (!hasHandle)
        {
            Logger.Info("Second instance detected; exiting.");
            MessageBox.Show(
                "BluetoothSafetyLock is already running.\nCheck the system tray (near the clock) for its icon.",
                "BluetoothSafetyLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Logger.Info("Application starting.");

        // FAS 1.1: load persisted settings before anything uses them.
        SettingsStore.Load();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());

        Logger.Info("Application exited cleanly.");
    }
}
