# Architecture

## Overview

Y2Akio is a single WinUI 3 desktop project. Pages contain the presentation and interaction logic, service classes own external processes and persistence, and model classes represent parsed or displayed data.

```text
App startup
  -> MainWindow navigation shell
     -> HomePage ------------> YtDlpService ------> yt-dlp / FFmpeg
          |                  -> TikTokPhotoService -> TikTok HTTP endpoints
          |                  -> HistoryStore ------> history.json
          `------------------> AppSettingsService -> settings.json / cookies.txt
     -> ImageResizePage -----> ImageResizeService -> Windows Imaging Component
     -> MediaCompressPage ------------------------> FFmpeg process
     -> SettingsPage --------> AppSettingsService / CookiesProtector
```

## Application shell

`App` initializes toast registration, creates `MainWindow`, and exposes the window for picker initialization. `MainWindow` owns the title bar, NavigationView, page frame, saved theme application, popup cursor workaround, and cookie-file cleanup on close.

## Downloader flow

1. `HomePage` validates the URL and asks `AppSettingsService` for an on-demand cookie path.
2. Definite single-video YouTube URLs skip the playlist probe. A best-effort oEmbed request can populate an instant card while full analysis runs.
3. Ambiguous URLs use a flat `yt-dlp` probe. Multiple entries become `PlaylistInfo`; otherwise a full video analysis follows.
4. `VideoInfo` parses formats and subtitle maps from `yt-dlp` JSON.
5. `HomePage` builds a `DownloadRequest` from the controls.
6. `YtDlpService` builds the command line, starts `yt-dlp`, parses progress, streams logs, and returns the best-known output path.
7. `HomePage` stores the completed item in history and asks `ToastService` to notify the user.

YouTube bot-check-like failures are retried with the Android player client. Facebook story URLs are normalized to watch URLs, and Facebook requests use browser impersonation.

## TikTok photo flow

`TikTokPhotoService` resolves short URLs, requests post data, extracts image resources into `TikTokPhotoPost`, and downloads selected images over HTTP. `HomePage` renders the grid and writes results to a directory or ZIP archive. Imported cookie text can be included in requests for authenticated posts.

## Resize flow

`ImageResizePage` collects source, target dimensions, format, and quality. `ImageResizeService` decodes with `BitmapDecoder`, calculates output dimensions, applies a `BitmapTransform` with Fant interpolation, and encodes through Windows Imaging Component.

## Compression flow

`MediaCompressPage` selects the media type, previews the source, constructs FFmpeg arguments, and owns the child process. Video mode can detect the GPU vendor through Windows CIM, try the matching hardware encoder, and fall back to a software encoder. Image mode uses format-specific FFmpeg arguments. Standard error is streamed to the on-page log.

## Persistence

| Data | Location | Owner |
|---|---|---|
| Language, theme, folders, encrypted cookie blob | `%LocalAppData%\Y2Akio\settings.json` | `AppSettingsService` |
| Download history | `%LocalAppData%\Y2Akio\history.json` | `HistoryStore` |
| Temporary plaintext cookie jar | `%LocalAppData%\Y2Akio\cookies.txt` | `AppSettingsService` |
| Downloaded and generated media | User-selected folders | Feature page/service |

The `Y2Akio`/`y2akio` path casing differs between settings and history source code, but Windows resolves both to the same case-insensitive directory.

## Notifications

The unpackaged app registers an AppUserModelID and Start-menu shortcut. `ToastService`, `ShortcutHelper`, and `NotificationActivator` provide completion notifications and handle activation that opens the saved folder.

## Localization and theme

`AppSettingsService` publishes change events. `MainWindow` applies theme changes; `HomePage` and `SettingsPage` apply localized strings from `LocalizationService`. Not every secondary page is fully localized yet.
