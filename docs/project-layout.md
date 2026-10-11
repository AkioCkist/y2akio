# Project Layout

```text
y2akio.sln
y2akio/
├── App.xaml(.cs)          Application startup and shared main window
├── Views/                 WinUI pages plus feature-focused code-behind partials
├── Services/              Processes, persistence, security, media operations
│   └── YtDlp/             yt-dlp contracts and focused service partials
├── Models/                Parsed and displayed data structures
├── Helpers/               Small UI helpers
├── Assets/                Icons, logos, and images
├── Properties/            Launch settings
├── app.manifest           Unpackaged executable manifest
├── Package.appxmanifest   Package metadata/tooling input
└── y2akio.csproj          Build, runtime, package, and publish configuration
```

## Views

| Path | Responsibility |
|---|---|
| `Views/MainWindow.xaml(.cs)` | Window shell, navigation, theme application, picker owner, shutdown cleanup |
| `Views/HomePage.xaml(.cs)` | Downloader composition, startup state, localization, and analyze orchestration |
| `Views/HomePage.*.cs` | Feature-focused partials for playlist, gallery, options, presentation, and history |
| `Views/ImageResizePage.xaml(.cs)` | Resize inputs, preview, output selection, and result reporting |
| `Views/MediaCompressPage.xaml(.cs)` | Compression-page composition, drag/drop, and preview |
| `Views/MediaCompressPage.*.cs` | Focused partials for workflow, FFmpeg execution, and control helpers |
| `Views/SettingsPage.xaml(.cs)` | Language, theme, save folders, and encrypted cookie import/delete |

## Services

| Path | Responsibility |
|---|---|
| `Services/YtDlpService.cs` | `yt-dlp` service composition and executable configuration |
| `Services/YtDlp/` | Contracts plus focused analysis, download, and child-process partials |
| `Services/TikTokPhotoService.cs` | TikTok short-link resolution, post parsing, and image downloads |
| `Services/ImageResizeService.cs` | Windows Imaging Component decode, scale, and encode pipeline |
| `Services/AppSettingsService.cs` | Settings persistence and temporary cookie-file lifecycle |
| `Services/CookiesProtector.cs` | DPAPI-NG protect/unprotect operations |
| `Services/HistoryStore.cs` | Download history JSON persistence |
| `Services/LocalizationService.cs` | English and Vietnamese string lookup |
| `Services/ToastService.cs` | Completion toast construction and display |
| `Services/ShortcutHelper.cs` | Start-menu shortcut and AppUserModelID setup |
| `Services/NotificationActivator.cs` | Activation callback for unpackaged notifications |

## Models and helpers

| Path | Responsibility |
|---|---|
| `Models/VideoInfo.cs` | Parsed video metadata, formats, and subtitle maps |
| `Models/VideoFormat.cs` | One `yt-dlp` format entry |
| `Models/PlaylistInfo.cs` | Analyze result, playlist entries, and subtitle selection state |
| `Models/TikTokPhotoPost.cs` | TikTok post and selectable photo items |
| `Models/DownloadHistoryItem.cs` | Persistent history record and media kind |
| `Helpers/ThumbnailHelper.cs` | Thumbnail URL to `BitmapImage` conversion |
| `Helpers/PopupCursorFix.cs` | WinUI popup/drop-down cursor workaround |

See [Architecture](architecture.md) for how these components interact.
