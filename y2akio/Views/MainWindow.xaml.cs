using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>App window shell: title bar + sidebar (NavigationView) + page host (Frame).</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // ── Window sizing & centering ──────────────────────────────────────
        var hWnd      = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId  = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        var iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var centerX = (displayArea.WorkArea.Width  - 1280) / 2;
            var centerY = (displayArea.WorkArea.Height - 800)  / 2;
            appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
        }

        Title = "YT-DLP Media Downloader";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // ── Default page ───────────────────────────────────────────────────
        ContentFrame.Navigate(typeof(HomePage));

        // ── Theme: apply saved preference, then react to changes made in SettingsPage ──
        ApplyTheme(AppSettingsService.Instance.Theme);
        AppSettingsService.Instance.SettingsChanged += (_, args) =>
        {
            if (args.ThemeChanged)
                DispatcherQueue.TryEnqueue(() => ApplyTheme(AppSettingsService.Instance.Theme));
        };
    }

    // ─── Theme helper ────────────────────────────────────────────────────────

    private void ApplyTheme(AppTheme theme)
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark  => ElementTheme.Dark,
            _              => ElementTheme.Default,   // follows Windows system setting
        };
    }

    // ─── Navigation ──────────────────────────────────────────────────────────

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
                ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.InvokedItemContainer?.Tag is not string tag) return;

        if (tag == "home")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(HomePage))
                ContentFrame.Navigate(typeof(HomePage));
        }
        else if (tag == "imageResize")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(ImageResizePage))
                ContentFrame.Navigate(typeof(ImageResizePage));
        }
    }
}
