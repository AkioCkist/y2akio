# YT-DLP Media Downloader (WinUI 3)

A Y2Mate-style shell around `yt-dlp`: paste a link → Analyze → a Fluent-styled
options panel drops in (format, quality, filename, save location) → Download
Now → the result lands in a persisted Download History list.

## Files

```
MainWindow.xaml        UI: URL box, options panel, history list
MainWindow.xaml.cs      All the wiring: analyze, populate options, download, history CRUD
Models/VideoInfo.cs      Parsed yt-dlp --dump-json result
Models/VideoFormat.cs   One entry from the formats[] array
Models/DownloadHistoryItem.cs
Services/YtDlpService.cs  Shells out to yt-dlp.exe, parses progress
Services/HistoryStore.cs  Persists history.json in %LocalAppData%
```

## Build a standalone `.exe` (publish)

Produces a self-contained Windows 11/10 x64 build (no .NET needed on the target machine):

```powershell
dotnet publish -c Release -p:Platform=x64 -p:PublishTrimmed=false
```

> **Note:** `PublishTrimmed=false` is required — WinUI 3 apps break when trimming is enabled.

Output goes to:

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
├── y2akio.exe                  # the app
├── ...                         # WinUI/WinAppSDK runtime DLLs
└── bin\                        # yt-dlp + ffmpeg
```

WinUI 3 unpackaged apps **cannot be a single `.exe` file** — the runtime DLLs in the `publish` folder are required. Ship or zip the entire `publish` folder together.

The app looks for `yt-dlp` and `ffmpeg` in `publish\bin\` next to the `.exe`, so copy them there (or point `ResolveYtDlpPath` / `ResolveFfmpegDirectory` at your own paths).

```powershell
cd bin\Release\net8.0-windows10.0.19041.0\win-x64\publish

mkdir bin

# 1. yt-dlp: single executable
# Download from:
# https://github.com/yt-dlp/yt-dlp/releases
copy ..\..\..\..\..\bin\yt-dlp.exe .\bin\

# 2. FFmpeg: extract the full build
# so bin\ffmpeg-<ver>-full_build\bin\ffmpeg.exe exists
xcopy ..\..\..\..\..\bin\ffmpeg-9.0.1-full_build .\bin\ /E
```

After copying the dependencies, the folder should look roughly like this:

```text
publish\
├── y2akio.exe
├── *.dll
├── ...
└── bin\
    ├── yt-dlp.exe
    └── ffmpeg-9.0.1-full_build\
        └── bin\
            └── ffmpeg.exe
```

Then double-click `y2akio.exe` to launch the application.

On first run, the app registers the toast AUMID and Start-menu shortcut. If notifications do not appear on another machine, repeat the toast registration step there.

## Getting it running

1. **Create the project.** In Visual Studio, create a new *"Blank App, Packaged
   (WinUI 3 in Desktop)"* project (or unpackaged, if you prefer — nothing here
   depends on packaging). Target .NET 8.
2. Drop these files into the project at matching paths/namespaces (the
   namespace `YtDlpDownloader` is assumed — rename to match your project, or
   `dotnet new` your project with that name).
3. **Get yt-dlp and ffmpeg.**
   - Download `yt-dlp.exe` from https://github.com/yt-dlp/yt-dlp/releases
   - Download an ffmpeg build (e.g. from https://www.gyan.dev/ffmpeg/builds/)
   - Either put both on your system `PATH`, or ship them next to the app
     (e.g. a `bin/` folder marked "Copy if newer") and construct the service
     with explicit paths:
     ```csharp
     private readonly YtDlpService _ytDlp = new(
         ytDlpPath: Path.Combine(AppContext.BaseDirectory, "bin", "yt-dlp.exe"),
         ffmpegDirectory: Path.Combine(AppContext.BaseDirectory, "bin"));
     ```
4. **Enable network access for images** — the thumbnail loads over http(s) via
   `BitmapImage`, which just works in an unpackaged app. For a *packaged*
   app, make sure the `internetClient` capability is declared in
   `Package.appxmanifest`.
5. Build and run. Paste a URL, hit Analyze, pick your options, hit Download Now.

## How the pieces fit together

- **Analyze** runs `yt-dlp --dump-single-json --no-playlist <url>` and parses
  title/uploader/thumbnail/duration/available formats out of the JSON
  (`VideoInfo.FromJson`).
- **Quality dropdown** is rebuilt from the parsed formats whenever you flip
  the Video/Audio toggle (`RefreshQualityOptions`) — it lists actual
  available heights instead of hardcoding a list that might not exist for a
  given video.
- **Download** builds a yt-dlp command line from `DownloadRequest`:
  - Video: `-f "bestvideo[height<=H]+bestaudio/best[height<=H]" --merge-output-format mp4`
  - Audio: `-x --audio-format mp3 --audio-quality <kbps>k`
  - Filename prefix checkboxes control the `-o` output template
    (`%(uploader)s`, `%(title)s`, plus a literal timestamp).
  - `--embed-thumbnail` / `--write-thumbnail`, `--add-metadata`, and
    `--write-subs --embed-subs` map to the corresponding checkboxes.
- **Progress** is parsed line-by-line from yt-dlp's `--newline` stdout
  (`[download]  42.1% ...`) and pushed to a `ProgressBar` via `IProgress<T>`.
- **History** is an `ObservableCollection<DownloadHistoryItem>` bound to an
  `ItemsControl`, persisted as JSON in
  `%LocalAppData%\YtDlpDownloader\history.json` so it survives restarts, just
  like the reference screenshots.

## Things you'll likely want to add next

- A real Settings dialog (currently just a stub) for custom `yt-dlp.exe` /
  ffmpeg paths and a default save folder.
- Cancel button wired to `_downloadCts.Cancel()` (the field already exists).
- Queueing multiple downloads instead of one at a time.
- Validating the pasted URL before enabling the Analyze button.

## A note on legality

`yt-dlp` is a legitimate open-source tool, but downloading media can violate
a site's Terms of Service or copyright law depending on the content and your
jurisdiction/use case. Worth a mention in your app's UI or README if you
plan to distribute this.