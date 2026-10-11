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

/// <summary>Download history commands.</summary>
public sealed partial class HomePage : Page
{
    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        await _historyStore.SaveAsync(_history);
    }

    private void PlayHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item && File.Exists(item.FilePath))
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(item.FilePath));
        }
    }

    private async void CopyPathHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
        {
            var package = new DataPackage();
            package.SetText(item.FilePath);
            Clipboard.SetContent(package);
        }
        await Task.CompletedTask;
    }

    private void OpenFolderHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item && Directory.Exists(item.FolderPath))
        {
            _ = Windows.System.Launcher.LaunchFolderPathAsync(item.FolderPath);
        }
    }

    private async void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
        {
            _history.Remove(item);
            await _historyStore.SaveAsync(_history);
        }
    }
}
