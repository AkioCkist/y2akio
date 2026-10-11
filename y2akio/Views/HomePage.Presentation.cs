using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using y2akio;
using y2akio.Models;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Shared downloader presentation state, logging, and preview helpers.</summary>
public sealed partial class HomePage : Page
{
    private void ShowLogPanel()
    {
        LogTextBlock.Text = "";
        LogPanel.Visibility = Visibility.Visible;
    }

    private void AppendLog(string line)
    {
        // onLogLine is raised on yt-dlp's process thread; UI must be touched on the dispatcher.
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        DispatcherQueue.TryEnqueue(() =>
        {
            LogTextBlock.Text += text;
            // Keep the newest lines in view.
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBlock.Text = "";
    }

    private void ShowDownloadComplete(string message)
    {
        SuccessBar.Message = message;
        SuccessBar.IsOpen = true;
    }

    // ─── Options panel ────────────────────────────────────────────────────────

    /// <summary>Resets every options control so a fresh analyze never shows stale state from the previous video.</summary>
    private void ClearOptionsState()
    {
        _currentVideo = null;
        QualityComboBox.Items.Clear();
        SubtitleLanguageComboBox.Items.Clear();
        _autoSubtitleLangs = null;
        AutoLanguageList.ItemsSource = null;
        DownloadNowButton.IsEnabled = false;
    }

    /// <summary>
    /// Shows the options panel immediately with whatever title/thumb is already in hand (oEmbed for a typed
    /// URL, playlist-entry data for a track click). Quality/subtitle controls stay empty and Download is
    /// disabled until the full dump lands and <see cref="FinishVideoOptions"/> fills them.
    /// </summary>
    private void ShowInstantCard(string title, string uploader, string durationDisplay, string? thumbUrl)
    {
        ClearOptionsState();
        _previewActive = true;

        VideoTitleText.Text = title;
        UploaderText.Text = uploader;
        DurationText.Text = durationDisplay;
        if (!string.IsNullOrWhiteSpace(thumbUrl) && Uri.TryCreate(thumbUrl, UriKind.Absolute, out var thumbUri))
            ThumbnailImage.Source = new BitmapImage(thumbUri);

        OptionsPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Full dump arrived: swap the instant card for the complete, populated options panel.</summary>
    private void FinishVideoOptions(VideoInfo video)
    {
        _currentVideo = video;
        PopulateOptionsPanel(video); // overwrites the card + fills subtitle languages + quality list
        _previewActive = false;
        OptionsPanel.Visibility = Visibility.Visible;
        DownloadNowButton.IsEnabled = _activeDownloads == 0;
    }

    /// <summary>An analyze failed while only the instant card was showing — hide it rather than leave a half-loaded panel.</summary>
    private void HidePreviewOnError()
    {
        if (!_previewActive) return;
        OptionsPanel.Visibility = Visibility.Collapsed;
        _previewActive = false;
        DownloadNowButton.IsEnabled = _activeDownloads == 0;
    }

    /// <summary>
    /// Fire-and-forget: paint the card from YouTube oEmbed (~0.2s) while the full dump is still running.
    /// No-ops if the preview already ended (dump beat it) or oEmbed returned nothing. Continuation must
    /// resume on the UI thread, so no ConfigureAwait(false) here.
    /// </summary>
    private async Task TryPaintYouTubePreviewAsync(string url)
    {
        try
        {
            var preview = await _ytDlp.FetchYouTubePreviewAsync(url);
            if (preview is not null && _previewActive)
                ShowInstantCard(preview.Title, preview.Author, "", preview.ThumbnailUrl);
        }
        catch
        {
            // best effort — the full dump paints the card anyway
        }
    }

    /// <summary>Playlist flat entries often omit the thumbnail; derive the standard YouTube one from the id.</summary>
    private static string? ResolveEntryThumbnail(PlaylistEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ThumbnailUrl)) return entry.ThumbnailUrl;
        if (!string.IsNullOrWhiteSpace(entry.Id)) return $"https://i.ytimg.com/vi/{entry.Id}/hqdefault.jpg";
        return null;
    }

}
