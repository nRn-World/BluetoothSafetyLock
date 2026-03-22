using System;
using System.Runtime.InteropServices;

namespace BluetoothSafetyLock
{
    public static partial class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

        public const int MOUSEEVENTF_MOVE = 0x01;

        public static void WakeScreen()
        {
            // Small mouse move to wake screen
            mouse_event(MOUSEEVENTF_MOVE, 0, 1, 0, 0);
            mouse_event(MOUSEEVENTF_MOVE, 0, -1, 0, 0);
        }

        public static void ClearClipboard()
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                EmptyClipboard();
                CloseClipboard();
            }
        }

        public static bool IsWindowsInDarkMode()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("AppsUseLightTheme");
                        if (val is int i) return i == 0;
                    }
                }
            }
            catch { }
            return true; // Default to dark if we can't read registry
        }

        public static bool IsInStartup()
        {
            try {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false)) {
                    if (key != null) {
                        var val = key.GetValue("BluetoothSafetyLock");
                        return val != null;
                    }
                }
            } catch { }
            return false;
        }

        public static void SetStartup(bool enable)
        {
            try {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                    if (key != null) {
                        if (enable) {
                            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            key.SetValue("BluetoothSafetyLock", $"\"{exePath}\"");
                        } else {
                            key.DeleteValue("BluetoothSafetyLock", false);
                        }
                    }
                }
            } catch { }
        }
    }
}
