using System.Runtime.InteropServices;

namespace MihomoTray;

internal static class NativeMethods
{
    public const int InternetOptionSettingsChanged = 39;
    public const int InternetOptionRefresh = 37;
    public const uint NimAdd = 0x00000000;
    public const uint NimModify = 0x00000001;
    public const uint NimDelete = 0x00000002;
    public const uint NifMessage = 0x00000001;
    public const uint NifIcon = 0x00000002;
    public const uint NifTip = 0x00000004;
    public const int WmApp = 0x8000;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonUp = 0x0205;
    public const int WmDestroy = 0x0002;
    public const int CallbackMessage = WmApp + 1;

    [DllImport("wininet.dll", SetLastError = true)]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public const uint ImageIcon = 1;
    public const uint LrLoadFromFile = 0x00000010;
    public const uint LrDefaultSize = 0x00000040;
    public const uint MessageBoxInfo = 0x00000040;
    public const uint MessageBoxError = 0x00000010;
    public const uint MessageBoxWarning = 0x00000030;
    public const uint MessageBoxYesNo = 0x00000004;
    public const int IdYes = 6;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }
}

internal static class NativeDialog
{
    public static void Info(string message)
    {
        NativeMethods.MessageBox(IntPtr.Zero, message, "Mihomo", NativeMethods.MessageBoxInfo);
    }

    public static void Error(string message)
    {
        NativeMethods.MessageBox(IntPtr.Zero, message, "Mihomo", NativeMethods.MessageBoxError);
    }
}
