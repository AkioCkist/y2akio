using System;
using System.IO;
using System.Reflection;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Win32;

namespace y2akio.Services;

/// <summary>
/// Shows Windows toast notifications via the modern AppNotificationManager API.
/// For unpackaged apps Windows App SDK requires: an AUMID registered in the registry, a Start-menu
/// shortcut carrying that AUMID, and a registered COM notification activator (ToastActivatorCLSID) —
/// otherwise Register() throws "No COM servers are registered for this app". All three are set up
/// here, best-effort; failures are surfaced through <see cref="Log"/> and never break the download.
/// </summary>
public static class ToastService
{
    public const string AppUserModelId = "Y2AkioMediaDownloader";
    private static readonly string ActivatorClsid = typeof(NotificationActivator).GUID.ToString("B").ToUpperInvariant();

    /// <summary>Optional sink so callers can surface toast setup problems into their UI log.</summary>
    public static Action<string>? Log { get; set; }

    private static bool _registered;

    public static void ShowDownloadComplete(string title, string savedPath)
    {
        try
        {
            EnsureRegistered();

            var notification = new AppNotificationBuilder()
                .AddText("Download complete")
                .AddText(title)
                .AddText(savedPath)
                .SetTag("download-complete")
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            Log?.Invoke("Toast shown");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Toast failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers the app with the Windows notification platform: AUMID registry key, Start-menu
    /// shortcut with that AUMID, and the COM activator CLSID that Register() verifies exists.
    /// Never throws — toast setup is cosmetic, so any failure just logs and skips.
    /// </summary>
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

            // 2) COM activator server registration: CLSID -> this exe, in-proc apartment.
            using (var clsidKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + ActivatorClsid))
            {
                clsidKey.SetValue("", "Y2Akio NotificationActivator");
                using var serverKey = clsidKey.CreateSubKey("InprocServer32");
                serverKey.SetValue("", exePath);
                serverKey.SetValue("ThreadingModel", "Apartment");
            }

            // 3) Start-menu shortcut carrying the AUMID — required for unpackaged toasts.
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Y2Akio");
            Directory.CreateDirectory(startMenuDir);
            var lnkPath = Path.Combine(startMenuDir, "Y2Akio Media Downloader.lnk");
            ShortcutHelper.Create(lnkPath, exePath, AppUserModelId);

            // 4) Register with the OS notification platform.
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Toast registration failed: {ex.Message}");
        }
    }
}
