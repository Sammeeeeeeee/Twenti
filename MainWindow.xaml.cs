using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Twenti.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Twenti;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "20/20";

        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);

        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsMinimizable = false;
            p.IsMaximizable = false;
            p.IsResizable = false;
        }

        // Park the owner window off-screen at 1×1: it exists only so the tray
        // and popup windows have a parent in the message hierarchy.
        appWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
        appWindow.IsShownInSwitchers = false;

        Win32Helper.HideFromAltTab(hwnd);
    }
}
