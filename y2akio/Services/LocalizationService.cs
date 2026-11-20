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
        ["Home.VideoCodec"]        = D("Video Codec",       "Bộ giải mã video"),
        ["Home.CodecAuto"]         = D("Auto",              "Tự động"),
        ["Home.CodecH264"]         = D("H.264 (AVC)",       "H.264 (AVC)"),
        ["Home.CodecHevc"]         = D("HEVC (H.265)",      "HEVC (H.265)"),
        ["Home.CodecAv1"]          = D("AV1",               "AV1"),
        ["Home.CodecVp9"]          = D("VP9",               "VP9"),
        ["Home.CodecMpeg4"]        = D("MPEG-4 (Part 2)",   "MPEG-4 (Part 2)"),
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
        ["Home.GalleryPhotoCount"]  = D("{0} photo(s)",      "{0} ảnh"),
        ["Home.GalleryVideoCount"]  = D("{0} video(s)",      "{0} video"),
        ["Home.GalleryHint"]        = D("Click items to select them · double-click to view full size", "Nhấn vào để chọn · nhấn đúp để xem kích thước đầy đủ"),
        ["Home.GallerySaveLocation"]= D("Save Location",     "Thư mục lưu"),
        ["Home.GallerySaveAs"]      = D("Save as",           "Lưu dạng"),
        ["Home.GallerySaveFolder"]  = D("Files in a folder", "File trong thư mục"),
        ["Home.GallerySaveZip"]     = D("One ZIP file",      "Một file ZIP"),
        ["Home.GalleryDownloadAll"] = D("Download All",      "Tải tất cả"),
        ["Home.GalleryFetching"]    = D("Downloading item {0} of {1}...", "Đang tải mục {0}/{1}..."),
        ["Home.GalleryWriting"]     = D("Saving...",         "Đang lưu..."),
        ["Home.GalleryDone"]        = D("Done",              "Hoàn tất"),
        ["Home.GallerySaved"]       = D("{0} file(s) saved to {1}", "Đã lưu {0} file vào {1}"),
        ["Home.GalleryBusy"]        = D("Wait for the current gallery download to finish.", "Vui lòng đợi tải hiện tại hoàn tất."),

        ["Home.DownloadFailedTitle"] = D("Download failed",   "Tải xuống thất bại"),
        ["Home.ViewAll"]            = D("View all ({0})",     "Xem tất cả ({0})"),
        ["Home.ShowLess"]           = D("Show less",          "Thu gọn"),
        ["Home.FileMissingTitle"]   = D("File not found (moved or deleted)", "Không tìm thấy file (đã bị chuyển hoặc xóa)"),
        ["Home.SavedPrefix"]        = D("Saved:",             "Đã lưu:"),
        ["Home.BatchSaved"]         = D("{0} of {1} videos downloaded", "Đã tải {0}/{1} video"),
        ["Home.BatchFailed"]        = D("{0} download(s) failed: {1}", "{0} video tải lỗi: {1}"),
        ["Home.ClipboardTitle"]     = D("Link copied — analyze it?", "Đã copy link — phân tích luôn?"),

        // ── DownloadsPage / download manager ──
        ["Nav.Downloads"]                    = D("Downloads",            "Tải xuống"),
        ["Downloads.Title"]                  = D("Downloads",            "Trình quản lý tải"),
        ["Downloads.ResumeAll"]              = D("Resume all",           "Tiếp tục tất cả"),
        ["Downloads.PauseAll"]               = D("Pause all",            "Tạm dừng tất cả"),
        ["Downloads.ClearFinished"]          = D("Clear finished",       "Xóa mục đã xong"),
        ["Downloads.SpeedLimit"]             = D("Speed limit (KB/s, 0 = unlimited)", "Giới hạn tốc độ (KB/s, 0 = không giới hạn)"),
        ["Downloads.StartAt"]                = D("Start queue at",       "Bắt đầu hàng đợi lúc"),
        ["Downloads.WhenDone"]               = D("When all downloads finish", "Khi tải xong tất cả"),
        ["Downloads.WhenDoneNothing"]        = D("Do nothing",           "Không làm gì"),
        ["Downloads.WhenDoneSleep"]          = D("Sleep",                "Ngủ (Sleep)"),
        ["Downloads.WhenDoneShutdown"]       = D("Shut down",            "Tắt máy"),
        ["Downloads.Empty"]                  = D("No downloads yet. Analyze a link on Home and press Download.", "Chưa có lượt tải nào. Phân tích link ở Trang chủ rồi bấm Tải xuống."),
        ["Downloads.Summary"]                = D("{0} downloading · {1} waiting · {2} finished", "{0} đang tải · {1} đang chờ · {2} đã xong"),
        ["Downloads.ScheduledFor"]           = D("starts at {0}",        "bắt đầu lúc {0}"),
        ["Downloads.JobSpeedLimit"]          = D("Speed limit",          "Giới hạn tốc độ"),
        ["Downloads.Unlimited"]              = D("Unlimited",            "Không giới hạn"),
        ["Downloads.CopyLink"]               = D("Copy link",            "Copy link"),
        ["Downloads.Remove"]                 = D("Remove from list",     "Xóa khỏi danh sách"),
        ["Downloads.Starting"]               = D("Starting...",          "Đang bắt đầu..."),
        ["Downloads.WaitingNetwork"]         = D("Waiting for the network...", "Đang chờ có mạng..."),
        ["Downloads.RetryIn"]                = D("Network error — retrying in {0}s ({1}/{2})", "Lỗi mạng — thử lại sau {0}s ({1}/{2})"),
        ["Downloads.ShutdownSoonTitle"]      = D("Downloads finished",   "Đã tải xong"),
        ["Downloads.ShutdownSoon"]           = D("Windows shuts down in 60 seconds. Run 'shutdown /a' to cancel.", "Windows sẽ tắt sau 60 giây. Chạy 'shutdown /a' để hủy."),
        ["Downloads.Phase.Processing"]       = D("Processing...",        "Đang xử lý..."),
        ["Downloads.Phase.Merging"]          = D("Merging video and audio...", "Đang ghép video và âm thanh..."),
        ["Downloads.Phase.Metadata"]         = D("Writing metadata...",  "Đang ghi metadata..."),
        ["Downloads.Phase.Thumbnail"]        = D("Embedding thumbnail...", "Đang nhúng ảnh thu nhỏ..."),
        ["Downloads.Phase.Audio"]            = D("Converting audio...",  "Đang chuyển đổi âm thanh..."),
        ["Downloads.Phase.Video"]            = D("Converting video...",  "Đang chuyển mã video..."),
        ["Downloads.Phase.Subtitles"]        = D("Adding subtitles...",  "Đang thêm phụ đề..."),
        ["Downloads.Phase.Fixup"]            = D("Fixing the container...", "Đang sửa container..."),
        ["Downloads.StartAtLabel"]           = D("Scheduled start",      "Hẹn giờ bắt đầu"),
        ["Downloads.Status.Queued"]          = D("Queued",               "Đang chờ"),
        ["Downloads.Status.Downloading"]     = D("Downloading",          "Đang tải"),
        ["Downloads.Status.Paused"]          = D("Paused",               "Tạm dừng"),
        ["Downloads.Status.WaitingForNetwork"] = D("Waiting",            "Chờ mạng"),
        ["Downloads.Status.Completed"]       = D("Completed",            "Hoàn tất"),
        ["Downloads.Status.Failed"]          = D("Failed",               "Lỗi"),
        ["Downloads.Status.Canceled"]        = D("Canceled",             "Đã hủy"),

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
        ["Settings.Cookies"]          = D("Browser cookies (cookies.txt)", "Cookie trình duyệt (cookies.txt)"),
        ["Settings.CookiesDesc"]      = D("Login-gated content (X/Twitter, private reels). Stored encrypted.", "Nội dung yêu cầu đăng nhập (X/Twitter, reel riêng tư). Được mã hóa lưu trữ."),
        ["Settings.CookiesImport"]    = D("Import...",           "Nhập..."),
        ["Settings.CookiesImported"]  = D("Imported · encrypted", "Đã nhập · đã mã hóa"),
        ["Settings.CookiesDelete"]    = D("Delete",              "Xóa"),
        ["Settings.CookiesImportError"] = D("Couldn't import cookies", "Không thể nhập cookie"),
        ["Settings.Ok"]               = D("OK",                  "OK"),
        ["Settings.Browse"]           = D("Browse...",           "Duyệt..."),
        ["Settings.About"]            = D("About",               "Thông tin"),
        ["Settings.Manager"]          = D("Download manager",    "Trình quản lý tải"),
        ["Settings.MultiConn"]        = D("Multi-connection downloads", "Tải đa kết nối"),
        ["Settings.MultiConnDesc"]    = D("Split each file across several connections (IDM-style) with resume and automatic retries. Off = yt-dlp downloads on its own.", "Chia mỗi file ra nhiều kết nối (kiểu IDM), tải tiếp được và tự thử lại. Tắt = để yt-dlp tự tải."),
        ["Settings.Connections"]      = D("Connections per file", "Số kết nối mỗi file"),
        ["Settings.ConnectionsDesc"]  = D("1–16. YouTube is capped at 6 to avoid blocks. Also used for HLS/DASH fragments.", "1–16. YouTube tối đa 6 để tránh bị chặn. Cũng dùng cho mảnh HLS/DASH."),
        ["Settings.Parallel"]         = D("Parallel downloads",  "Số lượt tải cùng lúc"),
        ["Settings.ParallelDesc"]     = D("How many queued downloads run at the same time.", "Số lượt trong hàng đợi chạy cùng một lúc."),
        ["Settings.SpeedLimit"]       = D("Speed limit (KB/s)",  "Giới hạn tốc độ (KB/s)"),
        ["Settings.SpeedLimitDesc"]   = D("Shared by all downloads. 0 = unlimited.", "Dùng chung cho mọi lượt tải. 0 = không giới hạn."),
        ["Settings.ResumeStartup"]    = D("Resume unfinished downloads on startup", "Tự tải tiếp khi mở app"),
        ["Settings.ResumeStartupDesc"]= D("Downloads interrupted by closing the app continue where they stopped.", "Lượt tải bị ngắt do đóng app sẽ tiếp tục từ chỗ dừng."),
        ["Settings.Clipboard"]        = D("Watch clipboard for links", "Theo dõi clipboard"),
        ["Settings.ClipboardDesc"]    = D("When you copy a link, Home offers to analyze it.", "Khi bạn copy link, Trang chủ sẽ hỏi có phân tích luôn không."),
        ["Settings.SiteFolders"]      = D("Subfolder per website", "Thư mục con theo trang web"),
        ["Settings.SiteFoldersDesc"]  = D(@"Save into …\YouTube, …\TikTok, … inside the save folder.", @"Lưu vào …\YouTube, …\TikTok, … trong thư mục lưu."),
        ["Settings.AppVersion"]       = D("Version",             "Phiên bản"),
    };

    // Helper: build a two-language sub-dictionary inline.
    private static Dictionary<AppLanguage, string> D(string en, string vi) =>
        new() { [AppLanguage.English] = en, [AppLanguage.Vietnamese] = vi };
}
