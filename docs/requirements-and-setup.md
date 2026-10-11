# Requirements and Setup

## Platform requirements

- Windows 10 version 1809 (build 17763) or later, or Windows 11.
- x64, x86, or ARM64 is declared by the solution; x64 is the default runtime used by the project file and the documented path below.
- .NET 8 SDK for source builds.
- Visual Studio 2022 with .NET desktop and Windows App SDK tooling is recommended for XAML development.

## External tools

Y2Akio does not download its command-line dependencies automatically.

- `yt-dlp.exe` is required by the Home downloader for analysis and downloads.
- `ffmpeg.exe` is required for stream merging, audio extraction, conversion, subtitle/thumbnail embedding, video transcoding, and the Compress page.

Download them from the official [`yt-dlp` releases](https://github.com/yt-dlp/yt-dlp/releases) and a trusted [FFmpeg Windows build](https://www.gyan.dev/ffmpeg/builds/).

## Development dependency layout

Place dependencies inside the **project** directory, not the repository root:

```text
y2akio/
└── bin/
    ├── yt-dlp.exe
    └── ffmpeg-9.0.1-full_build/
        └── bin/
            ├── ffmpeg.exe
            ├── ffprobe.exe
            └── ffplay.exe
```

Create the directory and download `yt-dlp` from the repository root:

```powershell
New-Item -ItemType Directory -Force .\y2akio\bin | Out-Null
Invoke-WebRequest `
  "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" `
  -OutFile ".\y2akio\bin\yt-dlp.exe"
```

Download and extract the FFmpeg full build manually so `y2akio\bin\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe` exists. The current project file explicitly copies that versioned path to build and publish output. If the extracted directory uses another version, rename it or update `y2akio.csproj` and the FFmpeg lookup code together.

Verify the tools:

```powershell
.\y2akio\bin\yt-dlp.exe --version
.\y2akio\bin\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe -version
```

## Runtime lookup behavior

The project copies both executables to the application output. The downloader is constructed with the copied `bin\yt-dlp.exe` and FFmpeg directory. The Compress page searches these runtime locations:

1. `bin\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe`
2. `bin\ffmpeg.exe`
3. `ffmpeg.exe` beside the app

## Next steps

- Contributors: continue with the [Developer guide](development.md).
- Users: continue with the [User guide](usage.md).
