using System;
using System.IO;
using y2akio.Helpers;

namespace y2akio.Services;

/// <summary>App-wide service instances shared by pages that come and go with navigation.</summary>
public static class AppServices
{
    /// <summary>
    /// Bundled yt-dlp first (so it works after you publish/copy the app elsewhere), then PATH. The
    /// bundle is the onedir build (bin\yt-dlp\ with _internal\): the onefile exe re-extracts ~40MB to
    /// %TEMP% on every run, which antivirus rescans each time on machines that haven't seen it.
    /// </summary>
    public static YtDlpService YtDlp { get; } = new(
        ytDlpPath: ExecutableLocator.Find("yt-dlp.exe", Path.Combine("bin", "yt-dlp"), "bin", "") ?? "yt-dlp.exe",
        ffmpegDirectory: ResolveFfmpegDirectory(),
        denoPath: ExecutableLocator.Find("deno.exe", Path.Combine("bin", "deno"), "bin", "") ?? "");

    private static string ResolveFfmpegDirectory()
    {
        var executable = ExecutableLocator.Find(
            "ffmpeg.exe",
            Path.Combine("bin", "ffmpeg-9.0.1-full_build", "bin"),
            "bin",
            "");
        return executable is null ? "" : Path.GetDirectoryName(executable) ?? "";
    }
}

/// <summary>Short, folder-safe site names for links ("YouTube", "TikTok", ...).</summary>
public static class SiteNames
{
    public static string For(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Other";
        var host = uri.Host.ToLowerInvariant();
        static bool Is(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

        if (Is(host, "youtube.com") || Is(host, "youtu.be") || Is(host, "youtube-nocookie.com")) return "YouTube";
        if (Is(host, "tiktok.com")) return "TikTok";
        if (Is(host, "facebook.com") || Is(host, "fb.watch")) return "Facebook";
        if (Is(host, "instagram.com")) return "Instagram";
        if (Is(host, "x.com") || Is(host, "twitter.com")) return "X";
        if (Is(host, "vimeo.com")) return "Vimeo";
        if (Is(host, "dailymotion.com")) return "Dailymotion";
        if (Is(host, "soundcloud.com")) return "SoundCloud";
        if (Is(host, "twitch.tv")) return "Twitch";
        if (Is(host, "reddit.com")) return "Reddit";

        var parts = host.Split('.');
        var name = parts.Length >= 2 ? parts[^2] : host;
        if (string.IsNullOrEmpty(name)) return "Other";
        return MediaGalleryService.SanitizeFileName(char.ToUpperInvariant(name[0]) + name[1..]);
    }

    /// <summary><paramref name="folder"/>\&lt;Site&gt; when the "subfolder per website" setting is on.</summary>
    public static string ApplySubfolder(string folder, string url) =>
        AppSettingsService.Instance.SiteSubfolders ? Path.Combine(folder, For(url)) : folder;
}
