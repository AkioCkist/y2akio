# Y2Akio

<p align="center">
  <img src="https://media.thuvien.org/photo/3929/image/71d0e35d391ee4323089fd1b97d45835.jpg" alt="Y2Akio banner" />
</p>

Y2Akio is a Windows desktop media toolkit built with .NET 8 and WinUI 3. It can download media with `yt-dlp`, save TikTok photo posts, resize images, and compress images or videos with FFmpeg.

## Table of contents

- [What the app can do](#what-the-app-can-do)
- [Requirements](#requirements)
- [Quick start for developers](#quick-start-for-developers)
- [Documentation](#documentation)
- [Technology](#technology)
- [Data and security](#data-and-security)
- [Legal note](#legal-note)

## What the app can do

- Download a single video, audio track, or selected entries from a playlist.
- Choose quality, container, codec, audio format, subtitles, thumbnails, metadata, and filename options.
- Save all or selected images from TikTok photo posts to a folder or ZIP archive.
- Resize images by dimensions or percentage and convert between PNG, JPEG, BMP, TIFF, and GIF.
- Compress video with H.264, HEVC, or AV1 using CPU or supported NVIDIA, Intel, or AMD hardware encoders.
- Compress and convert images to WebP, JPEG, PNG, GIF, or BMP.
- Keep download history, show live process logs, and send Windows completion notifications.
- Use English or Vietnamese and follow the system, light, or dark theme.

See [Features](docs/features.md) for the complete capability and format reference.

## Requirements

- Windows 10 version 1809 or later, or Windows 11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for development.
- [`yt-dlp.exe`](https://github.com/yt-dlp/yt-dlp/releases) for media analysis and downloads.
- [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) for merging, conversion, embedding, and compression.
- Visual Studio 2022 with the Windows App SDK workload is optional but recommended.

Y2Akio does not download `yt-dlp` or FFmpeg automatically. Follow [Requirements and setup](docs/requirements-and-setup.md) before the first run.

## Quick start for developers

From the repository root:

```powershell
# Put yt-dlp.exe and the extracted FFmpeg build under y2akio\bin first.
dotnet restore .\y2akio.sln
dotnet build .\y2akio.sln -c Debug -p:Platform=x64
.\y2akio\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\y2akio.exe
```

The exact dependency layout and Visual Studio workflow are documented in the [Developer guide](docs/development.md).

## Documentation

### Users

1. [Documentation index](docs/README.md)
2. [Features and supported formats](docs/features.md)
3. [Requirements and setup](docs/requirements-and-setup.md)
4. [User guide](docs/usage.md)
5. [Settings, files, and local data](docs/configuration-and-data.md)
6. [Cookie security](docs/security-cookies.md)
7. [Troubleshooting](docs/troubleshooting.md)

### Contributors

1. [Developer guide](docs/development.md)
2. [Architecture and runtime flows](docs/architecture.md)
3. [Project layout](docs/project-layout.md)
4. [Build and publish](docs/build-and-publish.md)
5. [Analyze optimization notes](docs/analyze-optimization.md)

## Technology

- .NET 8 and C#
- WinUI 3 / Windows App SDK
- `yt-dlp` for extraction and downloading
- FFmpeg for media processing
- Windows Imaging Component for image resizing
- Windows DPAPI-NG for user-bound cookie encryption

## Data and security

Settings, encrypted cookies, and download history are stored locally under `%LocalAppData%\Y2Akio`. Imported browser cookies are encrypted for the current Windows user and decrypted to a temporary file only while an operation needs them. See [Settings, files, and local data](docs/configuration-and-data.md) and [Cookie security](docs/security-cookies.md).

## Legal note

Downloading media may be restricted by a site's terms of service, copyright, or local law. Only download content you are authorized to access and use.
