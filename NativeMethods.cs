using System;
using System.Runtime.InteropServices;

namespace HardwareAnchor
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
    }
}
