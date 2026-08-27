using System;
using System.IO;
using System.Reflection;
using System.Security; // Thêm thư viện này để dùng SecurityElement.Escape
using Microsoft.Windows.AppNotifications;
using Microsoft.Win32;

namespace y2akio.Services;

/// <summary>
/// Shows Windows toast notifications via the modern AppNotificationManager API.
/// </summary>
public static class ToastService
{
    public const string AppUserModelId = "Y2AkioMediaDownloader";
    private static readonly string ActivatorClsid = typeof(NotificationActivator).GUID.ToString("B").ToUpperInvariant();

    public static Action<string>? Log { get; set; }

    private static bool _registered;

    public static void ShowDownloadComplete(string title, string savedPath)
    {
        try
        {
            EnsureRegistered();

            // Lấy đường dẫn thư mục và chuyển đổi sang dạng URI (file:///)
            var folder = Path.GetDirectoryName(savedPath) ?? savedPath;
            var folderUri = new Uri(folder).AbsoluteUri;

            // Xây dựng XML thủ công để ép Windows mở Protocol trực tiếp 
            // (Tự động mở File Explorer, không mở app)
            string xml = $@"
<toast activationType=""protocol"" launch=""{folderUri}"">
    <visual>
        <binding template=""ToastGeneric"">
            <text>Download complete</text>
            <text>{SecurityElement.Escape(title)}</text>
            <text>{SecurityElement.Escape(savedPath)}</text>
        </binding>
    </visual>
</toast>";

            var notification = new AppNotification(xml);
            AppNotificationManager.Default.Show(notification);
            Log?.Invoke("Toast shown");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Toast failed: {ex.Message}");
        }
    }

    public static void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                Log?.Invoke("Toast: couldn't resolve app exe path");
                return;
            }

            // 1) AUMID registry key.
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId))
            {
                key.SetValue("DisplayName", "Y2Akio Media Downloader");
                key.SetValue("IconUri", exePath, RegistryValueKind.String);
                key.SetValue("ShellExecute", "\"" + exePath + "\"");
                key.SetValue("ToastActivatorCLSID", ActivatorClsid);
            }

            // 2) Sửa COM activator server: File .exe bắt buộc phải dùng LocalServer32
            using (var clsidKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + ActivatorClsid))
            {
                clsidKey.SetValue("", "Y2Akio NotificationActivator");
                using var serverKey = clsidKey.CreateSubKey("LocalServer32");
                serverKey.SetValue("", exePath);
            }

            // 3) Start-menu shortcut
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Y2Akio");
            Directory.CreateDirectory(startMenuDir);
            var lnkPath = Path.Combine(startMenuDir, "Y2Akio Media Downloader.lnk");
            ShortcutHelper.Create(lnkPath, exePath, AppUserModelId);

            // 4) Register
            AppNotificationManager.Default.Register();
            _registered = true;

            // Xóa đăng ký sự kiện OnNotificationInvoked vì Windows Shell đã đảm nhận việc mở thư mục
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Toast registration failed: {ex.Message}");
        }
    }
}