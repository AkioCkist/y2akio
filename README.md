# Y2Akio — Media Downloader (WinUI 3)

Y2Akio is a WinUI 3 media downloader for Windows, built with .NET 8, Windows App SDK,
`yt-dlp`, and `ffmpeg`. Paste a supported media URL, analyze it, choose video or audio
options, and download it with live progress and history.

## Overview

- Supports individual videos and playlists.
- Downloads video or audio with configurable quality, containers, codecs, subtitles,
  thumbnails, metadata, and filename options.
- Provides live logs, Windows completion notifications, and persistent download history.
- Supports encrypted browser-cookie storage for login-gated media.

## Documentation

Use the table of contents below to open the detailed documentation:

1. [Features](docs/features.md)
2. [Requirements and setup](docs/requirements-and-setup.md)
3. [How to use](docs/usage.md)
4. [Build and publish](docs/build-and-publish.md)
5. [Security: cookie storage and encryption](docs/security-cookies.md)
6. [Troubleshooting](docs/troubleshooting.md)
7. [Project layout](docs/project-layout.md)
8. [Analyze optimization notes](docs/analyze-optimization.md)

## Features

- **Single video** — paste a YouTube (or any yt-dlp-supported site) URL, analyze, download.
- **Playlist support** — analyze a playlist URL and get a checklist panel: tick which
  videos to download, or click a single track to see *that* video's own options
  (thumbnail, title, formats). Batch-download all checked.
- **Video or Audio** — toggle MP4 video vs audio-only extraction.
- **Audio formats** — MP3, M4A, AAC, FLAC, Opus, Vorbis (OGG), WAV, ALAC, WMA.
  Lossless formats (FLAC/WAV/ALAC) auto-hide bitrate options.
- **Subtitles** — download author-uploaded and/or YouTube auto-generated subs, pick
  language (single-select for manual, multi-select checklist for auto), SRT/VTT,
  embed into the file or save separately.
- **Container / quality** — MP4/MKV/WEBM, height-based quality from the video's
  actual formats, audio bitrate (320/256/192/128).
- **Video codec (transcode)** — force the output codec: **H.264 (AVC)**, **HEVC (H.265)**,
  **AV1**, **VP9**, or **MPEG-4 (Part 2)**. Whatever yt-dlp picks (2K/4K YouTube streams are
  usually AV1/VP9), the saved file is re-encoded to the chosen codec in MP4 via ffmpeg.
  Slow for high resolutions — it's a full encode, and quality is capped by CRF (H.264
  `crf 18`, HEVC `crf 23`, AV1 `crf 30`, VP9 `crf 31`, MPEG-4 `-q:v 3`). AV1 uses the
  fast SVT-AV1 encoder.
- **Thumbnails** — embed into the file, save alongside, or skip.
- **Metadata** — embed title/artist metadata (`--add-metadata`).
- **Filename options** — prefix with date+time and/or channel, include title.
- **Live download log** — collapsible panel under the options showing raw yt-dlp
  output (progress, ffmpeg, merge, errors) while downloading.
- **Windows toast on completion** — click the toast to open the save folder in
  Explorer.
- **Persistent history** — download history saved to `%LocalAppData%\Y2Akio\history.json`.
- **Browser cookies (cookies.txt)** — import once in Settings → Download; the content is
  **encrypted at rest** (DPAPI-NG, current Windows user only) and passed to yt-dlp via
  `--cookies` for login-gated content (X/Twitter videos, private/age-restricted media).

## Requirements

- Windows 10 1809+ / Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build)
- [yt-dlp.exe](https://github.com/yt-dlp/yt-dlp/releases) (auto-downloaded on first run, or see below)
- [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) (required for audio extraction, format
  conversion, subtitle/thumbnail embedding, and merging video+audio streams)

## Setting up yt-dlp and ffmpeg (dev)

The app looks for the binaries in this order:

1. `bin\yt-dlp.exe` next to the app (`AppContext.BaseDirectory\bin`)
2. `yt-dlp.exe` next to the app
3. `bin` on the system `PATH` (last resort)

ffmpeg is searched in `bin`, `bin\ffmpeg-9.0.1-full_build\bin`, then `PATH`.

For local dev, put them in the project `bin` folder (already in `.gitignore`,
so the repo stays clean):

```powershell
# 1. yt-dlp (single .exe, self-contained)
#    Download the "yt-dlp.exe" asset from:
#    https://github.com/yt-dlp/yt-dlp/releases
Invoke-WebRequest "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile "bin\yt-dlp.exe"

# 2. ffmpeg (full build, includes ffprobe/ffplay)
#    Download from: https://www.gyan.dev/ffmpeg/builds/
#    Extract so that bin\ffmpeg-<version>-full_build\bin\ffmpeg.exe exists.
#    The app already scans bin\ffmpeg-*-full_build\bin automatically.
```

Layout after setup:

```
bin/
├── yt-dlp.exe
└── ffmpeg-9.0.1-full_build/
    └── bin/
        ├── ffmpeg.exe
        ├── ffprobe.exe
        └── ffplay.exe
```

## Repository

- Solution: `y2akio.sln`
- Application project: `y2akio/y2akio.csproj`
- Detailed documentation: `docs/`
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
