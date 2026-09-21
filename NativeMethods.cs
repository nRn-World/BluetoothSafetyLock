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

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_MOVE = 0x0001;

        /// <summary>
        /// Wakes the display via a tiny synthetic mouse movement (SendInput).
        /// NOTE: Windows deliberately does not allow programmatic unlock of a locked
        /// session — this only wakes the screen; the user still authenticates.
        /// </summary>
        public static void WakeScreen()
        {
            try
            {
                var inputs = new INPUT[2];
                inputs[0].type = INPUT_MOUSE;
                inputs[0].mi.dwFlags = MOUSEEVENTF_MOVE;
                inputs[0].mi.dy = 1;
                inputs[1].type = INPUT_MOUSE;
                inputs[1].mi.dwFlags = MOUSEEVENTF_MOVE;
                inputs[1].mi.dy = -1;
                uint sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
                if (sent == 0u)
                    Logger.Warn("SendInput failed to wake the screen.");
            }
            catch (Exception ex)
            {
                Logger.Error("WakeScreen failed.", ex);
            }
        }

        public static void ClearClipboard()
        {
            try
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    EmptyClipboard();
                    CloseClipboard();
                    Logger.Info("Clipboard cleared.");
                }
                else
                {
                    Logger.Warn("OpenClipboard failed; clipboard not cleared.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ClearClipboard failed.", ex);
            }
        }

        public static bool IsWindowsInDarkMode()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read dark-mode theme from registry; defaulting to dark. " + ex.Message);
            }
            return true; // Default to dark if we can't read registry
        }

        public static bool IsInStartup()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("BluetoothSafetyLock") != null;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read startup registry key. " + ex.Message);
                return false;
            }
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (enable)
                {
                    string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (exePath != null)
                        key.SetValue("BluetoothSafetyLock", $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue("BluetoothSafetyLock", false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("SetStartup failed.", ex);
            }
        }
    }
}
