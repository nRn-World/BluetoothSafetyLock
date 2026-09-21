using System;
using System.Drawing;

namespace BluetoothSafetyLock
{
    /// <summary>
    /// Central access to the embedded application icon (BluetoothSafetyLock.ico,
    /// 10 sizes 16–256 px) so the tray, forms and dialogs share one sharp source.
    /// Icons created here are owned by the recipient and must be disposed
    /// (or released with NativeMethods.DestroyIcon for Icon.FromHandle results).
    /// </summary>
    internal static class AppIcons
    {
        /// <summary>Manifest name of the embedded .ico file.</summary>
        private const string ResourceName = "BluetoothSafetyLock.BluetoothSafetyLock.ico";

        /// <summary>Shared clone that each caller must Dispose (forms do this automatically).</summary>
        private static Icon? _master;

        /// <summary>Loads a fresh Icon over all sizes from the embedded resource. Throws on failure.</summary>
        private static Icon LoadMaster()
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded icon resource '{ResourceName}' was not found.");
            return new Icon(stream); // keeps every frame of the multi-size .ico
        }

        /// <summary>
        /// Returns a new Icon instance with the frame that best matches the requested size.
        /// The caller owns and must dispose the returned icon.
        /// </summary>
        internal static Icon GetIcon(Size size)
        {
            _master ??= LoadMaster();
            return new Icon(_master, size.Width == 0 ? 32 : size.Width, size.Height == 0 ? 32 : size.Height);
        }

        /// <summary>Icon sized for the system tray (16 px at 100 % scaling, 32 px at 200 %).</summary>
        internal static Icon GetSmallIcon() => GetIcon(SystemInformation.SmallIconSize);

        /// <summary>Icon for window title bars and the taskbar (32 px at 100 % scaling, 64 px at 200 %).</summary>
        internal static Icon GetWindowIcon() => GetIcon(SystemInformation.IconSize);

        /// <summary>Embedded window icon as a fresh stream, for System.Drawing constructors that own the stream.</summary>
        internal static System.IO.Stream OpenIconStream() =>
            typeof(Program).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded icon resource '{ResourceName}' was not found.");
    }
}
