# Requirements and Setup

## Requirements

- Windows 10 1809+ or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- [yt-dlp.exe](https://github.com/yt-dlp/yt-dlp/releases)
- [ffmpeg](https://www.gyan.dev/ffmpeg/builds/), required for audio extraction, format conversion, subtitle/thumbnail embedding, and merging video/audio streams

## Set up yt-dlp and ffmpeg for development

The app searches for yt-dlp in this order:

1. `bin\\yt-dlp.exe` next to the app
2. `yt-dlp.exe` next to the app
3. `yt-dlp.exe` on `PATH`

ffmpeg is searched in `bin`, `bin\\ffmpeg-<version>-full_build\\bin`, and `PATH`.

Put the binaries in the project's `bin` folder:

```powershell
# yt-dlp
Invoke-WebRequest "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile "bin\\yt-dlp.exe"

# ffmpeg
# Download and extract a full build so this file exists:
# bin\\ffmpeg-<version>-full_build\\bin\\ffmpeg.exe
```

Expected layout:

```text
bin/
├── yt-dlp.exe
└── ffmpeg-9.0.1-full_build/
    └── bin/
        ├── ffmpeg.exe
        ├── ffprobe.exe
        └── ffplay.exe
```

Verify the tools:

```powershell
.\\bin\\yt-dlp.exe --version
.\\bin\\ffmpeg-9.0.1-full_build\\bin\\ffmpeg.exe -version
```

The `bin` folder is ignored by git so local binaries do not enter the repository.
