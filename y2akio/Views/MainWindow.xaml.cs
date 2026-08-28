using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace y2akio.Views;

/// <summary>App window shell: title bar + sidebar (NavigationView) + page host (Frame).</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Lấy AppWindow của cửa sổ hiện tại
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        // Đặt kích thước lúc khởi động (đơn vị: pixel, chưa scale theo DPI)
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        // (tuỳ chọn) căn giữa màn hình
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var centerX = (displayArea.WorkArea.Width - 1280) / 2;
            var centerY = (displayArea.WorkArea.Height - 800) / 2;
            appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
        }

        Title = "YT-DLP Media Downloader";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Mở trang Home mặc định khi start app.
        ContentFrame.Navigate(typeof(HomePage));
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            // Settings chưa có trang riêng — giữ nguyên trang hiện tại.
            return;
        }

        if (args.InvokedItemContainer?.Tag is not string tag)
            return;

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
