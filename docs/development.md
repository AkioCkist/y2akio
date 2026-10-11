# Developer Guide

## 1. Prerequisites

Install:

- Windows 10 1809+ or Windows 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- Visual Studio 2022 with .NET desktop development and Windows App SDK tooling, if using the IDE.
- `yt-dlp` (onedir `yt-dlp_win.zip`), `deno.exe` and a full FFmpeg build.

Confirm the SDK:

```powershell
dotnet --info
```

## 2. Add external binaries

Follow [Requirements and setup](requirements-and-setup.md). The required source-tree paths are:

```text
y2akio\bin\yt-dlp\yt-dlp.exe
y2akio\bin\deno\deno.exe
y2akio\bin\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe
```

These files are copied to the build and publish output by `y2akio.csproj`. Do not commit downloaded binaries.

## 3. Restore and build

Run from the repository root:

```powershell
dotnet restore .\y2akio.sln
dotnet build .\y2akio.sln -c Debug -p:Platform=x64
```

The default x64 executable is expected at:

```text
y2akio\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\y2akio.exe
```

Run it directly:

```powershell
.\y2akio\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\y2akio.exe
```

The app is configured as an unpackaged WinUI 3 application for local execution. It registers its toast notification identity and Start-menu shortcut when it starts.

## 4. Visual Studio workflow

1. Open `y2akio.sln`.
2. Select `Debug` and the intended architecture, normally `x64`.
3. Set `y2akio` as the startup project if needed.
4. Press F5 to debug or Ctrl+F5 to run without the debugger.

## 5. Development map

- UI pages and event handlers live under `y2akio/Views`.
- Command/process integrations and persistence live under `y2akio/Services`.
- Parsed media, playlist, TikTok, and history data live under `y2akio/Models`.
- Small UI adapters live under `y2akio/Helpers`.
- Application startup and the shared window handle live in `App.xaml.cs`.

See [Architecture](architecture.md) and [Project layout](project-layout.md) before making cross-cutting changes.

## 6. Validation checklist

There is currently no automated test project in the solution. At minimum:

1. Build the architecture you changed.
2. Launch the app and navigate to every edited page.
3. For downloader changes, test a single video, audio extraction, and a playlist.
4. For cookie changes, test import, one authenticated operation, deletion, and app-close cleanup.
5. For FFmpeg changes, test both success and cancellation; test CPU fallback when touching hardware encoding.
6. For image resize changes, test pixel mode, percentage mode, aspect-ratio behavior, and each affected output format.
7. Confirm `%LocalAppData%\Y2Akio` data remains readable after the change.

## 7. Common development constraints

- `Windows.Storage.ApplicationData` is not used because the unpackaged app lacks package identity; settings and history use normal JSON files.
- File and folder pickers must be initialized with the WinUI window handle.
- UI updates from process output must be marshalled through `DispatcherQueue`.
- A non-Auto downloader codec always produces MP4 after transcoding, regardless of the selected container.
- YouTube deliberately does not receive the imported whole-browser cookie jar because it can trigger Google's bot checks.

## 8. Publish

Follow [Build and publish](build-and-publish.md) for release output and deployment requirements.
