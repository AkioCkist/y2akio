using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>
/// Settings page: language toggle, theme selector (Light/Dark/System), and separate
/// default save-folder pickers for video and audio downloads.
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>Prevents Checked event handlers from firing during initial setup.</summary>
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        ApplyLanguage();
        _loading = false;

        // Refresh labels if the language is changed from another page (or by this page itself).
        LocalizationService.LanguageChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(ApplyLanguage);
    }

    // ─── Initialization ──────────────────────────────────────────────────────

    private void LoadSettings()
    {
        var s = AppSettingsService.Instance;

        LangEnRadio.IsChecked  = s.Language == AppLanguage.English;
        LangViRadio.IsChecked  = s.Language == AppLanguage.Vietnamese;

        ThemeLightRadio.IsChecked  = s.Theme == AppTheme.Light;
        ThemeDarkRadio.IsChecked   = s.Theme == AppTheme.Dark;
        ThemeSystemRadio.IsChecked = s.Theme == AppTheme.System;

        VideoFolderBox.Text = s.VideoSaveFolder;
        AudioFolderBox.Text = s.AudioSaveFolder;
        UpdateCookiesState();
    }

    // ─── Localization ────────────────────────────────────────────────────────

    private void ApplyLanguage()
    {
        PageTitle.Text              = L("Settings.Title");
        LangSectionHeader.Text      = L("Settings.Language");
        LanguageLabel.Text          = L("Settings.LanguageLabel");
        LanguageDesc.Text           = L("Settings.LanguageDesc");
        AppearanceSectionHeader.Text= L("Settings.Appearance");
        ThemeLabel.Text             = L("Settings.ThemeLabel");
        ThemeDesc.Text              = L("Settings.ThemeDesc");
        ThemeLightText.Text         = L("Settings.ThemeLight");
        ThemeDarkText.Text          = L("Settings.ThemeDark");
        ThemeSystemText.Text        = L("Settings.ThemeSystem");
        DownloadSectionHeader.Text  = L("Settings.Download");
        VideoFolderTitle.Text       = L("Settings.VideoFolder");
        VideoFolderDesc.Text        = L("Settings.VideoFolderDesc");
        AudioFolderTitle.Text       = L("Settings.AudioFolder");
        AudioFolderDesc.Text        = L("Settings.AudioFolderDesc");
        BrowseVideoButton.Content   = L("Settings.Browse");
        BrowseAudioButton.Content   = L("Settings.Browse");
        CookiesTitle.Text           = L("Settings.Cookies");
        CookiesDesc.Text            = L("Settings.CookiesDesc");
        ImportCookiesButton.Content = L("Settings.CookiesImport");
        DeleteCookiesButton.Content = L("Settings.CookiesDelete");
        UpdateCookiesState();
        AboutSectionHeader.Text     = L("Settings.About");
    }

    private static string L(string key) => LocalizationService.Get(key);

    // ─── Language toggle ─────────────────────────────────────────────────────

    private void LangRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var lang = LangEnRadio.IsChecked == true ? AppLanguage.English : AppLanguage.Vietnamese;
        LocalizationService.SetLanguage(lang);  // raises LanguageChanged → ApplyLanguage on all subscribed pages
    }

    // ─── Theme selector ──────────────────────────────────────────────────────

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton rb) return;

        var themeTag = rb.Tag as string;
        var theme = themeTag switch
        {
            "Light" => AppTheme.Light,
            "Dark"  => AppTheme.Dark,
            _       => AppTheme.System,
        };

        AppSettingsService.Instance.SetTheme(theme);  // MainWindow listens and applies ElementTheme
    }

    // ─── Folder pickers ──────────────────────────────────────────────────────

    private async void BrowseVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is null) return;
        VideoFolderBox.Text = folder.Path;
        AppSettingsService.Instance.SetVideoSaveFolder(folder.Path);
    }

    private async void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is null) return;
        AudioFolderBox.Text = folder.Path;
        AppSettingsService.Instance.SetAudioSaveFolder(folder.Path);
    }

    /// <summary>Switches the row between "import" and "imported (encrypted) + delete" states.</summary>
    private void UpdateCookiesState()
    {
        var s = AppSettingsService.Instance;
        var has = s.HasCookies;

        ImportCookiesButton.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        DeleteCookiesButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CookiesImportedStatus.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CookiesImportedStatus.Text = L("Settings.CookiesImported")
            + (string.IsNullOrEmpty(s.CookiesSourceName) ? "" : $" · {s.CookiesSourceName}");
    }

    private async void ImportCookiesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add("*"); // any file — the name doesn't matter, we read the content
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var text = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(text))
                return; // empty file — nothing to import

            await AppSettingsService.Instance.SetCookiesDataAsync(text, file.Name);
            UpdateCookiesState();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = L("Settings.CookiesImportError"),
                Content = ex.Message,
                CloseButtonText = L("Settings.Ok"),
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private void DeleteCookiesButton_Click(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Instance.ClearCookiesData();
        UpdateCookiesState();
    }

    private static async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        return await picker.PickSingleFolderAsync().AsTask();
    }
}
