using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace y2akio
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DownloadItem> DownloadHistory { get; set; } = new();

        public MainWindow()
        {
            this.InitializeComponent();
            HistoryList.ItemsSource = DownloadHistory;
        }

        private void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlTextBox.Text;
            if (string.IsNullOrWhiteSpace(url)) return;

            // Simulate UI update
            VideoTitle.Text = "Fetched Video Title";
            ChannelName.Text = "Fetched Channel";

            // Reveal the options panel
            OptionsPanel.Visibility = Visibility.Visible;
        }

        private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.Downloads;
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                SaveLocationBox.Text = folder.Path;
            }
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlTextBox.Text;
            string savePath = SaveLocationBox.Text;

            string arguments = $"-f bestvideo+bestaudio --embed-metadata -o \"{savePath}\\%(title)s.%(ext)s\" \"{url}\"";

            StartDownloadProcess(arguments);

            DownloadHistory.Insert(0, new DownloadItem
            {
                Title = "Downloading: " + VideoTitle.Text,
                Details = $"Video • Best Quality • Saved in: {savePath} • {DateTime.Now:HH:mm:ss dd/MM/yyyy}"
            });
        }

        private void StartDownloadProcess(string arguments)
        {
            try
            {
                ProcessStartInfo processInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                Process process = new Process { StartInfo = processInfo };
                process.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting yt-dlp: {ex.Message}");
            }
        }
    }

    public class DownloadItem
    {
        public string Title { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }
}