using System;
using System.Collections.Generic;

namespace y2akio.Services;

/// <summary>
/// Dictionary-based EN/VI localization. Pages subscribe to <see cref="LanguageChanged"/>
/// and call <see cref="Get"/> to refresh their text content.
/// </summary>
public static class LocalizationService
{
    public static event EventHandler? LanguageChanged;

    public static AppLanguage CurrentLanguage => AppSettingsService.Instance.Language;

    public static void SetLanguage(AppLanguage language)
    {
        AppSettingsService.Instance.SetLanguage(language);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        if (!_strings.TryGetValue(key, out var dict)) return key;
        if (dict.TryGetValue(CurrentLanguage, out var val)) return val;
        if (dict.TryGetValue(AppLanguage.English, out var en)) return en;
        return key;
    }

    // ─── String table ────────────────────────────────────────────────────────

    private static readonly Dictionary<string, Dictionary<AppLanguage, string>> _strings = new()
    {
        // ── Navigation ──
        ["Nav.Home"]        = D("Home",                      "Trang chủ"),
        ["Nav.ImageResize"] = D("Image Resize",               "Thay đổi kích thước ảnh"),

        // ── HomePage ──
        ["Home.Subtitle"]          = D("Media downloader powered by yt-dlp", "Tải media – powered by yt-dlp"),
        ["Home.UrlPlaceholder"]    = D("Paste YouTube (or other site) link here...", "Dán link YouTube (hoặc website khác) vào đây..."),
        ["Home.Analyze"]           = D("Analyze",          "Phân tích"),
        ["Home.ErrorTitle"]        = D("Couldn't analyze that link", "Không thể phân tích link này"),
        ["Home.SuccessTitle"]      = D("Download complete", "Tải xuống hoàn tất"),
        ["Home.DownloadOptions"]   = D("Download Options",  "Tùy chọn tải xuống"),
        ["Home.FileFormat"]        = D("File Format",       "Định dạng file"),
        ["Home.VideoMp4"]          = D("Video (MP4)",       "Video (MP4)"),
        ["Home.AudioMusic"]        = D("Audio (Music)",     "Âm thanh (Nhạc)"),
        ["Home.Quality"]           = D("Quality",           "Chất lượng"),
        ["Home.VideoFormat"]       = D("Video Format",      "Định dạng video"),
        ["Home.AudioFormat"]       = D("Audio Format",      "Định dạng âm thanh"),
        ["Home.FileNamePrefix"]    = D("File name prefix",  "Tiền tố tên file"),
        ["Home.PrefixDateTime"]    = D("Download date + time", "Ngày + giờ tải xuống"),
        ["Home.PrefixChannel"]     = D("Channel name",      "Tên kênh"),
        ["Home.IncludeTitle"]      = D("Video title",       "Tên video"),
        ["Home.DownloadSubtitles"] = D("Download subtitles","Tải phụ đề"),
        ["Home.SubtitleOptions"]   = D("Subtitle Options",  "Tùy chọn phụ đề"),
        ["Home.SubtitleEmbed"]     = D("Embed into video file", "Nhúng vào file video"),
        ["Home.SubtitleSeparate"]  = D("Save as separate file", "Lưu thành file riêng"),
        ["Home.IncludeTracks"]     = D("Include tracks",    "Bao gồm track"),
        ["Home.ManualSubs"]        = D("Author-uploaded subs", "Phụ đề do tác giả tải lên"),
        ["Home.AutoSubs"]          = D("Auto-generated (YouTube)", "Tự động tạo (YouTube)"),
        ["Home.Languages"]         = D("Languages",         "Ngôn ngữ"),
        ["Home.SelectAll"]         = D("Select all",        "Chọn tất cả"),
        ["Home.DeselectAll"]       = D("Deselect all",      "Bỏ chọn tất cả"),
        ["Home.SubtitleFormat"]    = D("Subtitle format",   "Định dạng phụ đề"),
        ["Home.VideoSaveLocation"] = D("Video Save Location","Thư mục lưu video"),
        ["Home.AudioSaveLocation"] = D("Audio Save Location","Thư mục lưu âm thanh"),
        ["Home.ChooseFolder"]      = D("Choose Folder",     "Chọn thư mục"),
        ["Home.ExtraOptions"]      = D("Extra Options",     "Tùy chọn thêm"),
        ["Home.EmbedMetadata"]     = D("Embed full Metadata (Title, Artist...)", "Nhúng Metadata đầy đủ (Tiêu đề, Nghệ sĩ...)"),
        ["Home.Thumbnail"]         = D("Thumbnail",         "Ảnh thu nhỏ"),
        ["Home.ThumbnailEmbed"]    = D("Embed into media file", "Nhúng vào file media"),
        ["Home.ThumbnailSave"]     = D("Save alongside file",   "Lưu bên cạnh file"),
        ["Home.ThumbnailNone"]     = D("Don't download",        "Không tải"),
        ["Home.DownloadNow"]       = D("Download Now",      "Tải xuống ngay"),
        ["Home.DownloadLog"]       = D("Download Log",      "Nhật ký tải"),
        ["Home.Clear"]             = D("Clear",             "Xóa"),
        ["Home.DownloadHistory"]   = D("Download History",  "Lịch sử tải xuống"),
        ["Home.ClearAll"]          = D("Clear All",         "Xóa tất cả"),
        ["Home.DownloadSelected"]  = D("Download Selected", "Tải các mục đã chọn"),

        // ── SettingsPage ──
        ["Settings.Title"]            = D("Settings",            "Cài đặt"),
        ["Settings.Language"]         = D("Language",            "Ngôn ngữ"),
        ["Settings.LanguageLabel"]    = D("Display language",    "Ngôn ngữ hiển thị"),
        ["Settings.LanguageDesc"]     = D("Sets the language for the entire application", "Đặt ngôn ngữ cho toàn bộ ứng dụng"),
        ["Settings.Appearance"]       = D("Appearance",          "Giao diện"),
        ["Settings.ThemeLabel"]       = D("App theme",           "Giao diện ứng dụng"),
        ["Settings.ThemeDesc"]        = D("Choose Light, Dark, or follow the Windows system setting", "Chọn Sáng, Tối, hoặc theo cài đặt hệ thống Windows"),
        ["Settings.ThemeLight"]       = D("Light",               "Sáng"),
        ["Settings.ThemeDark"]        = D("Dark",                "Tối"),
        ["Settings.ThemeSystem"]      = D("System default",      "Theo hệ thống"),
        ["Settings.Download"]         = D("Download",            "Tải xuống"),
        ["Settings.VideoFolder"]      = D("Video save folder",   "Thư mục lưu video"),
        ["Settings.VideoFolderDesc"]  = D("Default location for video downloads", "Thư mục mặc định để lưu video"),
        ["Settings.AudioFolder"]      = D("Audio save folder",   "Thư mục lưu âm thanh"),
        ["Settings.AudioFolderDesc"]  = D("Default location for audio downloads", "Thư mục mặc định để lưu âm thanh"),
        ["Settings.Browse"]           = D("Browse...",           "Duyệt..."),
        ["Settings.About"]            = D("About",               "Thông tin"),
        ["Settings.AppVersion"]       = D("Version",             "Phiên bản"),
    };

    // Helper: build a two-language sub-dictionary inline.
    private static Dictionary<AppLanguage, string> D(string en, string vi) =>
        new() { [AppLanguage.English] = en, [AppLanguage.Vietnamese] = vi };
}
