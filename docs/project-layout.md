# Project Layout

| Path | Purpose |
|---|---|
| `y2akio/Views/MainWindow.xaml` | Main window shell and navigation |
| `y2akio/Views/HomePage.xaml` | Downloader UI: URL, options, playlist, log and history |
| `y2akio/Views/HomePage.xaml.cs` | Analyze, playlist, options, downloads, log and history behavior |
| `y2akio/Models/PlaylistInfo.cs` | Analyze result, playlist entries and subtitle selection models |
| `y2akio/Models/VideoInfo.cs` | Parsed yt-dlp JSON and subtitle language maps |
| `y2akio/Models/VideoFormat.cs` | One entry from the yt-dlp `formats[]` array |
| `y2akio/Models/DownloadHistoryItem.cs` | Download history row model |
| `y2akio/Services/YtDlpService.cs` | Runs yt-dlp, parses JSON/progress and emits log lines |
| `y2akio/Services/HistoryStore.cs` | Persists history under `%LocalAppData%\\Y2Akio` |
| `y2akio/Services/ToastService.cs` | Windows toast notifications |
| `y2akio/Services/ShortcutHelper.cs` | Start-menu shortcut and AUMID registration |
| `y2akio/Services/NotificationActivator.cs` | COM activator for unpackaged toasts |
| `y2akio/Helpers/ThumbnailHelper.cs` | Playlist thumbnail URL to `BitmapImage` helper |

For the Analyze performance work, see [Analyze optimization](analyze-optimization.md).
