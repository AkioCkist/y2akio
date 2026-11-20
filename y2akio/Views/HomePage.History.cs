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
using y2akio.Helpers;
using y2akio.Models;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Download history: a short preview with "View all", and per-item commands.</summary>
public sealed partial class HomePage : Page
{
    /// <summary>Items shown before "View all": a long history used to push the page down endlessly.</summary>
    private const int HistoryPreviewCount = 5;

    /// <summary>Expanded list height; the full history scrolls inside it instead of stretching the page.</summary>
    private const double HistoryExpandedMaxHeight = 560;

    private bool _historyExpanded;
    private readonly ObservableCollection<DownloadHistoryItem> _historyView = new();

    private void InitHistoryView()
    {
        HistoryList.ItemsSource = _historyView;
        _history.CollectionChanged += OnHistoryChanged;
        Unloaded += (_, _) => _history.CollectionChanged -= OnHistoryChanged;
        RefreshHistoryView();
    }

    private void OnHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshHistoryView);

    private void RefreshHistoryView()
    {
        var visible = _historyExpanded ? _history.ToList() : _history.Take(HistoryPreviewCount).ToList();
        _historyView.Clear();
        foreach (var item in visible) _historyView.Add(item);

        HistoryScroller.MaxHeight = _historyExpanded ? HistoryExpandedMaxHeight : double.PositiveInfinity;
        HistoryScroller.VerticalScrollBarVisibility = _historyExpanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        UpdateHistoryLabels();
    }

    private void UpdateHistoryLabels()
    {
        if (HistoryToggleButton is null) return; // ApplyLanguage can run before the template is ready
        var count = _history.Count;
        DownloadHistoryTitle.Text = count > 0 ? $"{L("Home.DownloadHistory")} ({count})" : L("Home.DownloadHistory");
        HistoryToggleButton.Visibility = count > HistoryPreviewCount ? Visibility.Visible : Visibility.Collapsed;
        HistoryToggleButton.Content = _historyExpanded ? L("Home.ShowLess") : string.Format(L("Home.ViewAll"), count);
    }

    private void HistoryToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _historyExpanded = !_historyExpanded;
        RefreshHistoryView();
        if (!_historyExpanded) HistoryScroller.ChangeView(null, 0, null, true);
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        await HistoryService.Instance.SaveAsync();
    }

    private async void PlayHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DownloadHistoryItem item) return;
        if (!ShellLauncher.OpenFile(item.FilePath)
            && !(await HistoryService.Instance.TryRepairAsync(item) && ShellLauncher.OpenFile(item.FilePath)))
        {
            ErrorBar.Title = L("Home.FileMissingTitle");
            ErrorBar.Message = item.FilePath;
            ErrorBar.IsOpen = true;
        }
    }

    private async void CopyPathHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
        {
            var package = new DataPackage();
            package.SetText(item.FilePath);
            ClipboardWatcher.MarkSeen(item.FilePath);
            Clipboard.SetContent(package);
        }
        await Task.CompletedTask;
    }

    private void OpenFolderHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        // Explorer opens with the file selected (or just the folder when the file was moved).
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
            ShellLauncher.ShowInFolder(item.FilePath, item.FolderPath);
    }

    private async void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
        {
            _history.Remove(item);
            await HistoryService.Instance.SaveAsync();
        }
    }
}
