using System.Runtime.InteropServices;

namespace NetPrio.Services;

public sealed class TrayIconService : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    private const int ID_OPEN = 1001;
    private const int ID_TOGGLE = 1002;
    private const int ID_EXIT = 1003;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly SubclassProc _subclassDelegate;
    private NOTIFYICONDATA _nid;
    private bool _added;

    public Func<(string OpenText, string ToggleText, string ExitText)>? MenuTextProvider { get; set; }
    public event Action? RestoreRequested;
    public event Action? ToggleShaperRequested;
    public event Action? ExitRequested;

    public TrayIconService(IntPtr hwnd, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _hIcon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x0010 /*LR_LOADFROMFILE*/);
        _subclassDelegate = WindowSubclassProc;
        SetWindowSubclass(_hwnd, _subclassDelegate, 1, IntPtr.Zero);

        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref _nid);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added) return;
        _nid.szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
            {
                RestoreRequested?.Invoke();
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            return IntPtr.Zero;
        }
        if (msg == WM_COMMAND)
        {
            int id = (int)(wParam.ToInt64() & 0xFFFF);
            if (id == ID_OPEN) RestoreRequested?.Invoke();
            else if (id == ID_TOGGLE) ToggleShaperRequested?.Invoke();
            else if (id == ID_EXIT) ExitRequested?.Invoke();
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var (openText, toggleText, exitText) = MenuTextProvider?.Invoke() ?? ("Open", "Start/Stop", "Exit");
        IntPtr hMenu = CreatePopupMenu();
        AppendMenu(hMenu, 0, (UIntPtr)ID_OPEN, openText);
        AppendMenu(hMenu, 0, (UIntPtr)ID_TOGGLE, toggleText);
        AppendMenu(hMenu, 0x0800 /*MF_SEPARATOR*/, UIntPtr.Zero, string.Empty);
        AppendMenu(hMenu, 0, (UIntPtr)ID_EXIT, exitText);

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(hMenu, 0x0100, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(hMenu);

        if (cmd == ID_OPEN) RestoreRequested?.Invoke();
        else if (cmd == ID_TOGGLE) ToggleShaperRequested?.Invoke();
        else if (cmd == ID_EXIT) ExitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _added = false;
        }
        RemoveWindowSubclass(_hwnd, _subclassDelegate, 1);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);
}
