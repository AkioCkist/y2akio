using System.Diagnostics;
using System.IO;

namespace y2akio.Helpers;

/// <summary>
/// Opens files and folders with the user's default apps. <c>Launcher.LaunchUriAsync(file:///...)</c>
/// does nothing: Windows refuses the <c>file:</c> scheme there, so Play / Open buttons built on it
/// were dead. The shell (<c>UseShellExecute</c>) opens a path with its associated app.
/// </summary>
internal static class ShellLauncher
{
    /// <summary>Opens <paramref name="path"/> in its default app (video player, image viewer...). False when it is gone.</summary>
    public static bool OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens Explorer with <paramref name="filePath"/> selected, or just <paramref name="folder"/> when the file is gone.</summary>
    public static bool ShowInFolder(string? filePath, string? folder = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false });
                return true;
            }
            folder ??= string.IsNullOrWhiteSpace(filePath) ? null : Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return true;
            }
        }
        catch
        {
            // nothing sensible to do
        }
        return false;
    }
}
