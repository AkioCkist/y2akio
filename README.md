# Y2Akio — Media Downloader (WinUI 3)

Y2Mate-style downloader built on `yt-dlp`. Paste a link → Analyze → pick options →
download. Fluent/WinUI 3 UI, .NET 8, Windows App SDK.

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
- **Thumbnails** — embed into the file, save alongside, or skip.
- **Metadata** — embed title/artist metadata (`--add-metadata`).
- **Filename options** — prefix with date+time and/or channel, include title.
- **Live download log** — collapsible panel under the options showing raw yt-dlp
  output (progress, ffmpeg, merge, errors) while downloading.
- **Windows toast on completion** — click the toast to open the save folder in
  Explorer.
- **Persistent history** — download history saved to `%LocalAppData%\Y2Akio\history.json`.
- **Browser cookies (cookies.txt)** — set in Settings → Download. Passed to yt-dlp via
  `--cookies`, needed for login-gated content (X/Twitter videos, private/age-restricted
  media).

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

Verify:

```powershell
.\bin\yt-dlp.exe --version
.\bin\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe -version
```

## Build & run

```powershell
dotnet build -c Debug -p:Platform=x64
# or from Visual Studio: open the .sln/.csproj, F5
```

The app is **unpackaged** — no MSIX install needed, it runs straight from the build
output. First launch registers a toast AUMID + Start-menu shortcut so download
notifications work.

## Build a standalone .exe (publish)

Produces a self-contained Windows 11/10 x64 build (no .NET needed on the target
machine):

```powershell
dotnet publish -c Release -p:Platform=x64 -p:PublishTrimmed=false
```

> `PublishTrimmed=false` is required — WinUI 3 apps break when trimming is enabled.

Output goes to:

```
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
├── y2akio.exe                  # the app
├── ...                         # WinUI/WinAppSDK runtime DLLs
└── bin\                        # copy yt-dlp + ffmpeg here (see below)
```

WinUI 3 unpackaged apps **cannot be a single .exe file** — the runtime DLLs in the
publish folder are required. Ship/zip the whole `publish` folder together.

The app looks for yt-dlp/ffmpeg in `publish\bin\` next to the exe, so copy them
over (or point `ResolveYtDlpPath`/`ResolveFfmpegDirectory` at your own paths):

```powershell
cd bin\Release\net8.0-windows10.0.19041.0\win-x64\publish
mkdir bin
# 1. yt-dlp: single exe — grab from https://github.com/yt-dlp/yt-dlp/releases
copy ..\..\..\..\..\bin\yt-dlp.exe .\bin\        # or wherever you keep it
# 2. ffmpeg full build — extract so bin\ffmpeg-<ver>-full_build\bin\ffmpeg.exe exists
xcopy ..\..\..\..\..\bin\ffmpeg-9.0.1-full_build .\bin\ /E
```

Then double-click `y2akio.exe`. First run registers the toast AUMID + Start-menu
shortcut; repeat the toast step on the new machine if notifications don't show.

## Standard build (dev)

```powershell
# Debug build (fastest iteration)
dotnet build -c Debug -p:Platform=x64

# Run the built exe
.\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\y2akio.exe
```

Or open the `.sln`/`.csproj` in Visual Studio and press **F5**. Visual Studio
launches the unpackaged app directly — no MSIX install step.

## Debugging after build

If the app builds but crashes / won't open, walk through in order:

**1. Run from a terminal and check the exit code.**
GUI apps produce no console output, but a crash still returns a code:

```powershell
# PowerShell
.\y2akio.exe; echo "exit=$LASTEXITCODE"
```

- `exit=127` in Git Bash → loader failed (missing DLL next to the exe).
- Process starts then disappears within seconds → runtime exception, go to step 2.

**2. Read the Windows Application event log.** The actual fault details land here:

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1026} -MaxEvents 20 |
  Format-List TimeCreated, ProviderName, Message
```

- **Id 1000** (Application Error) → shows the faulting module + exception code.
- **Id 1026** (.NET Runtime) → the full managed stack trace.
- `0xc000027b` (STATUS_STOWED_EXCEPTION) in `Microsoft.UI.Xaml.dll` → an unhandled
  exception during XAML load or navigation.

**3. Known unpackaged-app gotchas** (all already handled in this codebase — don't
reintroduce them):

- `Windows.Storage.ApplicationData` **requires an MSIX package identity** and throws
  in unpackaged apps. Persist to a file instead — see `AppSettingsService`
  (`%LocalAppData%\Y2Akio\settings.json`).
- `x:Bind`/`StaticResource` errors only surface at **runtime**, not compile time.
  An undefined `Style="{StaticResource ...}"` or `Icon="..."` that isn't a real
  `Symbol` name crashes the window at startup (e.g. `Failed to create
  'IconElement' from the text '...'`). Use a `FontIcon` with a glyph for custom icons.
- Event handlers that fire during `InitializeComponent` (a `ComboBox`
  `SelectionChanged`, a `Slider` `ValueChanged`, a `CheckBox` `Checked`) can run
  before other named controls are wired → **NullReferenceException**. Guard with
  `if (SomeControl is null) return;` at the top of the handler.

**4. Reproduce under the debugger.** In Visual Studio: set a breakpoint on the
`App` constructor, step through, and let VS surface the exception. For a crash
that only happens in the published (Release) exe, switch to Release config and
F5 — trimming is disabled, so Debug/Release behavior should match.

## How to use

1. **Paste a link** into the URL box and hit **Analyze** (or press Enter).
   - Single video → options panel appears (thumbnail, title, formats).
   - Playlist → playlist checklist panel appears. Tick the videos you want, or
     click one track to load its own download options.
2. **Pick Video or Audio**.
   - Video: choose container (MP4/MKV/WEBM) and quality (Best or a height like 1080p).
   - Audio: choose format (MP3/FLAC/etc.) and bitrate (hidden for lossless).
3. **Options**
   - Subtitles (video only): enable, choose author/auto, language, SRT/VTT, embed/separate.
   - File name prefix: date+time, channel, title.
   - Thumbnail: embed / save alongside / don't download.
   - Save Location: default `%USERPROFILE%\Downloads`, change via "Choose Folder".
4. **Download Now**. Watch the progress bar and live log panel. On completion you
   get a Windows toast — clicking it opens the save folder.
5. **Download history** persists below; replay/`copy path`/open folder/delete from
   the per-row buttons.

## Project layout

```
MainWindow.xaml        UI: header, URL box, options, playlist panel, log, history
MainWindow.xaml.cs     Wiring: analyze, playlist click, options, downloads, log, toast
PlaylistInfo.cs        AnalyzeResult / PlaylistInfo / PlaylistEntry (+ SubtitleSelection)
VideoInfo.cs           Parsed yt-dlp --dump-json result (+ subtitle language maps)
VideoFormat.cs         One entry from the formats[] array
DownloadHistoryItem.cs History row model
Services/YtDlpService.cs  Shells out to yt-dlp, parses progress, live log lines
Services/HistoryStore.cs  Persists history.json in %LocalAppData%\Y2Akio
Services/ToastService.cs  Windows toast notifications (AppNotificationManager)
Services/ShortcutHelper.cs  Start-menu shortcut + AUMID for unpackaged toasts
Services/NotificationActivator.cs  COM activator so toasts work unpackaged
Helpers/ThumbnailHelper.cs  URL -> BitmapImage for playlist rows
```

## Troubleshooting

- **"Couldn't analyze that link"** — yt-dlp error shown in the red InfoBar; full
  stderr also lands in the log panel. Check you're online and the URL is valid.
- **X/Twitter: `No video could be found in this tweet`** — the tweet's video is
  login-gated; anonymous yt-dlp can't see it. Export your X session cookies to a
  Netscape-format `cookies.txt` (browser extension such as "Get cookies.txt LOCALLY",
  or `yt-dlp --cookies-from-browser edge` from a logged-in machine) and set it in
  **Settings → Download → Browser cookies**. The app then passes `--cookies` on
  analyze and download.
- **No audio / no merge / leftover `.part`** — ffmpeg not found. Confirm it's in
  `bin` (see above).
- **No toast on download** — run the app once more after launch; registration
  (AUMID + Start-menu shortcut) happens on first run. Check the log panel for
  `Toast registration failed: ...` and open
  `Windows Settings → System → Notifications` to make sure notifications are allowed.
- **Leftover `.srt` / `.webp`** — when embedding is selected the app auto-deletes
  loose sidecar files created by that run.

## A note on legality

`yt-dlp` is a legitimate open-source tool, but downloading media can violate a
site's Terms of Service or copyright law depending on the content and your
jurisdiction/use case. Use responsibly.
