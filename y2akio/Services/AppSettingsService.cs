using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace y2akio.Services;

public enum AppLanguage { English, Vietnamese }
public enum AppTheme { System, Light, Dark }

public class AppSettingsChangedEventArgs : EventArgs
{
    public bool ThemeChanged { get; init; }
    public bool LanguageChanged { get; init; }
    public bool SaveFolderChanged { get; init; }
    public bool CookiesChanged { get; init; }
}

/// <summary>
/// Singleton that persists user preferences (language, theme, save folders) in a JSON file under
/// %LocalAppData%\Y2Akio\settings.json so they survive restarts.
/// NOTE: storage is file-based on purpose — <c>Windows.Storage.ApplicationData</c> requires an
/// MSIX package identity and throws in this unpackaged app.
/// </summary>
public class AppSettingsService
{
    private static AppSettingsService? _instance;
    public static AppSettingsService Instance => _instance ??= new();

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Y2Akio", "settings.json");

    private AppSettingsData _data = new();

    public event EventHandler<AppSettingsChangedEventArgs>? SettingsChanged;

    public AppLanguage Language { get => _data.Language; private set => _data.Language = value; }
    public AppTheme    Theme    { get => _data.Theme;    private set => _data.Theme = value; }
    public string VideoSaveFolder { get => _data.VideoSaveFolder; private set => _data.VideoSaveFolder = value; }
    public string AudioSaveFolder { get => _data.AudioSaveFolder; private set => _data.AudioSaveFolder = value; }
    public bool HasCookies => !string.IsNullOrWhiteSpace(_data.CookiesEncrypted);
    public string CookiesSourceName => _data.CookiesSourceName ?? "";

    private AppSettingsService()
    {
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(SettingsPath));
                if (loaded != null) _data = loaded;
            }
        }
        catch
        {
            // Corrupt/missing file → fall back to defaults.
        }

        if (string.IsNullOrWhiteSpace(VideoSaveFolder)) VideoSaveFolder = downloads;
        if (string.IsNullOrWhiteSpace(AudioSaveFolder)) AudioSaveFolder = downloads;

        // No cookies configured → make sure no leftover plaintext cookie file survives a past session.
        if (!HasCookies) CleanupCookiesFile();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_data));
        }
        catch
        {
            // Settings persistence is best-effort; never crash over it.
        }
    }

    public void SetLanguage(AppLanguage language)
    {
        Language = language;
        Save();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { LanguageChanged = true });
    }

    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        Save();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { ThemeChanged = true });
    }

    public void SetVideoSaveFolder(string path)
    {
        VideoSaveFolder = path;
        Save();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { SaveFolderChanged = true });
    }

    public void SetAudioSaveFolder(string path)
    {
        AudioSaveFolder = path;
        Save();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { SaveFolderChanged = true });
    }

    /// <summary>
    /// Encrypts the imported cookies.txt content (DPAPI, current Windows user only) and persists
    /// the blob. Plaintext is never written to settings.json.
    /// </summary>
    public async Task SetCookiesDataAsync(string plaintext, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            ClearCookiesData();
            return;
        }

        _data.CookiesEncrypted = await CookiesProtector.ProtectAsync(plaintext);
        _data.CookiesSourceName = sourceName ?? "";
        Save();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { CookiesChanged = true });
    }

    /// <summary>Removes the imported cookie data and any decrypted temp file. State resets to "not imported".</summary>
    public void ClearCookiesData()
    {
        _data.CookiesEncrypted = "";
        _data.CookiesSourceName = "";
        CleanupCookiesFile();
        Save();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { CookiesChanged = true });
    }

    /// <summary>Decrypts the stored blob, or null when there is nothing / decryption fails (other user, other machine).</summary>
    public async Task<string?> DecryptCookiesAsync() =>
        HasCookies ? await CookiesProtector.UnprotectAsync(_data.CookiesEncrypted) : null;

    /// <summary>
    /// Path to a decrypted cookies.txt yt-dlp can consume. Written on demand into the app's own
    /// data folder (not TEMP), and deleted again via <see cref="CleanupCookiesFile"/> once the
    /// analyze/download is done. Empty string = no cookies configured.
    /// </summary>
    public async Task<string> GetCookiesFilePathAsync()
    {
        if (!HasCookies) return "";
        var plain = await DecryptCookiesAsync();
        if (string.IsNullOrWhiteSpace(plain)) return "";

        var path = DecryptedCookiesPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, plain);
        }
        catch { return ""; }
        return path;
    }

    /// <summary>Deletes the plaintext cookie temp file so nothing sensitive lingers on disk.</summary>
    public void CleanupCookiesFile()
    {
        try { if (File.Exists(DecryptedCookiesPath)) File.Delete(DecryptedCookiesPath); } catch { /* best-effort */ }
    }

    private static string DecryptedCookiesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Y2Akio", "cookies.txt");
}

/// <summary>Serializable settings payload. Enum values serialize as strings (System.Text.Json default).</summary>
public class AppSettingsData
{
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public AppTheme    Theme    { get; set; } = AppTheme.System;
    public string VideoSaveFolder { get; set; } = "";
    public string AudioSaveFolder { get; set; } = "";
    public string CookiesEncrypted { get; set; } = "";   // base64 DPAPI blob — never plaintext
    public string CookiesSourceName { get; set; } = "";  // just the filename, for display
}
