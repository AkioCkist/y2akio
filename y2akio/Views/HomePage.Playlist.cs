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

/// <summary>Playlist analysis, selection, and batch downloads.</summary>
public sealed partial class HomePage : Page
{
    private void PopulatePlaylistPanel(PlaylistInfo playlist)
    {
        PlaylistTitleText.Text = playlist.Title;
        var meta = new List<string> { $"{playlist.Count} videos" };
        if (!string.IsNullOrWhiteSpace(playlist.Uploader)) meta.Add(playlist.Uploader);
        PlaylistMetaText.Text = string.Join(" · ", meta);

        PlaylistListView.ItemsSource = playlist.Entries;
        UpdateDownloadPlaylistButton();
    }

    private void PlaylistEntryCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateDownloadPlaylistButton();

    private void UpdateDownloadPlaylistButton()
    {
        var selected = _currentPlaylist?.Entries.Count(e => e.Selected) ?? 0;
        var baseText = L("Home.DownloadSelected");
        DownloadPlaylistButton.Content = selected > 0 ? $"{baseText} ({selected})" : baseText;
        DownloadPlaylistButton.IsEnabled = selected > 0;
    }

    private async void PlaylistListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaylistEntry entry || string.IsNullOrWhiteSpace(entry.Url))
            return;
        if (_analyzeInProgress) return;
        _analyzeInProgress = true;

        ErrorBar.IsOpen = false;
        AnalyzingRing.IsActive = true;
        DownloadPlaylistButton.IsEnabled = false;

        // Paint the options card instantly from the track's own metadata (title/thumb/duration are already
        // in hand at zero cost), then one full dump fills quality/res/subtitle options.
        ShowInstantCard(entry.Title, "", entry.DurationDisplay, ResolveEntryThumbnail(entry));

        string cookiesPath = "";
        try
        {
            cookiesPath = await AppSettingsService.Instance.GetCookiesFilePathAsync();
            var video = await _ytDlp.AnalyzeVideoAsync(entry.Url, cookiesPath);
            FinishVideoOptions(video);
        }
        catch (Exception ex)
        {
            HidePreviewOnError();
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (!string.IsNullOrEmpty(cookiesPath)) AppSettingsService.Instance.CleanupCookiesFile();
            AnalyzingRing.IsActive = false;
            DownloadPlaylistButton.IsEnabled = true;
            _analyzeInProgress = false;
        }
    }

    private async void DownloadPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is null) return;

        var selected = _currentPlaylist.Entries.Where(en => en.Selected).ToList();
        if (selected.Count == 0) return;

        var isVideo = VideoFormatToggle.IsChecked == true;
        var qualitySelection = QualityComboBox.SelectedItem as string ?? "";

        _activeDownloads++;
        DownloadPlaylistButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadStatusText.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        _downloadCts = new CancellationTokenSource();

        ShowLogPanel();
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadProgressBar.Value = p.Percent;
            DownloadStatusText.Text = p.StatusText;
        });

        string cookiesPath = "";
        try
        {
            cookiesPath = await AppSettingsService.Instance.GetCookiesFilePathAsync();
            var completed = 0;
            foreach (var entry in selected)
            {
                completed++;
                DownloadStatusText.Text = $"({completed}/{selected.Count}) Downloading: {entry.Title}";
                DownloadProgressBar.Value = 0;
                AppendLog($"=== ({completed}/{selected.Count}) {entry.Title} ===");

                var request = BuildDownloadRequest(entry.Url, isVideo, qualitySelection, cookiesPath);
                var savedPath = await _ytDlp.DownloadAsync(request, progress, _downloadCts.Token, AppendLog);
                AppendLog($"Saved: {savedPath}");

                _history.Insert(0, new DownloadHistoryItem
                {
                    Title = entry.Title,
                    Kind = request.Kind,
                    QualityLabel = qualitySelection,
                    FilePath = savedPath,
                    FolderPath = request.SaveFolder,
                    CompletedAt = DateTime.Now,
                });
            }
            await _historyStore.SaveAsync(_history);
            ToastService.ShowDownloadComplete($"{selected.Count} videos downloaded", _currentPlaylist.Title);
            ShowDownloadComplete($"{selected.Count} videos downloaded from playlist");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (!string.IsNullOrEmpty(cookiesPath)) AppSettingsService.Instance.CleanupCookiesFile();
            _activeDownloads--;
            DownloadPlaylistButton.IsEnabled = true;
        }
    }

    // ─── Gallery posts (TikTok photo posts, Facebook / Instagram posts + stories) ─

    /// <summary>
    /// Fetches the link as a gallery post when it is one (TikTok photo post, Facebook / Instagram post
    /// or story), else returns null so the normal yt-dlp flow handles it. TikTok share short links
    /// (vt./vm.tiktok.com) only reveal whether they point at a photo post after their redirect resolves.
    /// </summary>
}
