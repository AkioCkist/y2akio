using System;
using Windows.Storage;

namespace y2akio.Services;

public enum AppLanguage { English, Vietnamese }
public enum AppTheme { System, Light, Dark }

public class AppSettingsChangedEventArgs : EventArgs
{
    public bool ThemeChanged { get; init; }
    public bool LanguageChanged { get; init; }
    public bool SaveFolderChanged { get; init; }
}

/// <summary>
/// Singleton that persists user preferences (language, theme, save folders) in
/// ApplicationData.Current.LocalSettings so they survive restarts.
/// </summary>
public class AppSettingsService
{
    private static AppSettingsService? _instance;
    public static AppSettingsService Instance => _instance ??= new();

    private readonly ApplicationDataContainer _store = ApplicationData.Current.LocalSettings;

    public event EventHandler<AppSettingsChangedEventArgs>? SettingsChanged;

    public AppLanguage Language  { get; private set; }
    public AppTheme    Theme     { get; private set; }
    public string VideoSaveFolder { get; private set; }
    public string AudioSaveFolder { get; private set; }

    private AppSettingsService()
    {
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

        Language = Enum.TryParse<AppLanguage>(_store.Values["Language"] as string, out var lang)
            ? lang : AppLanguage.English;

        Theme = Enum.TryParse<AppTheme>(_store.Values["Theme"] as string, out var theme)
            ? theme : AppTheme.System;

        VideoSaveFolder = _store.Values["VideoSaveFolder"] as string ?? downloads;
        AudioSaveFolder = _store.Values["AudioSaveFolder"] as string ?? downloads;
    }

    public void SetLanguage(AppLanguage language)
    {
        Language = language;
        _store.Values["Language"] = language.ToString();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { LanguageChanged = true });
    }

    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        _store.Values["Theme"] = theme.ToString();
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { ThemeChanged = true });
    }

    public void SetVideoSaveFolder(string path)
    {
        VideoSaveFolder = path;
        _store.Values["VideoSaveFolder"] = path;
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { SaveFolderChanged = true });
    }

    public void SetAudioSaveFolder(string path)
    {
        AudioSaveFolder = path;
        _store.Values["AudioSaveFolder"] = path;
        SettingsChanged?.Invoke(this, new AppSettingsChangedEventArgs { SaveFolderChanged = true });
    }
}
