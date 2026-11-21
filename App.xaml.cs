using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace NetPrio;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            MessageBox(IntPtr.Zero,
                "NetPrio requires Windows 11 (build 22000 or later).",
                "Unsupported OS",
                0x10 /*MB_ICONERROR*/);
            Exit();
            return;
        }

        bool startMinimized = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow(startMinimized);
        if (!startMinimized)
            _window.Activate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
