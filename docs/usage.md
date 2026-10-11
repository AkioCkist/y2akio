# User Guide

## Navigation

Use the left navigation menu to open:

- **Home** for downloads and TikTok photo posts.
- **Image Resize** for image scaling and format conversion.
- **Compress** for FFmpeg-based image and video compression.
- **Settings** for language, theme, default folders, and browser cookies.

## Download a video or audio track

1. Paste a supported URL into the Home page and select **Analyze**, or press Enter.
2. Wait for the options panel. YouTube may show a preview card before all available formats finish loading.
3. Choose **Video** or **Audio**.
4. Configure the output:
   - Video: quality, MP4/MKV/WebM container, and optional codec transcode.
   - Audio: MP3, M4A, AAC, FLAC, Opus, Vorbis, WAV, ALAC, or WMA and an available bitrate.
   - Optional filename prefixes, subtitles, metadata, thumbnail behavior, and save folder.
5. Select **Download Now**.
6. Follow progress in the live log. On success, use the Windows notification or the history action buttons to open the result.

Choosing a non-Auto video codec performs a full re-encode. It can take much longer than a normal download, especially at high resolution.

## Download a playlist

1. Paste the playlist URL and select **Analyze**.
2. Select the entries to download from the checklist.
3. Select **Download Selected** for a batch download.
4. To customize one entry, click its row. Y2Akio first shows the entry card, then loads that video's full options.

Playlist analysis is capped at the first 500 entries by the current `yt-dlp` command.

## Download subtitles

1. Select **Video**, then enable **Download subtitles**.
2. Choose author-uploaded tracks, auto-generated tracks, or both.
3. Select the language or languages exposed by the analyzed video.
4. Choose SRT or VTT.
5. Choose whether to embed subtitles into the video or save sidecar files.

Subtitle conversion and embedding require FFmpeg. Subtitle controls are not used in audio-only mode.

## Save a TikTok photo post

1. Paste a TikTok photo-post URL or supported short link and select **Analyze**.
2. Select images in the thumbnail grid. Use **Select all** or **Deselect all** when useful.
3. Double-click an image to open the full-size source.
4. Choose the destination and select either **Images in a folder** or **One ZIP file**.
5. Select **Download All** or **Download Selected**.

If TikTok requires a logged-in session, import a current Netscape-format `cookies.txt` file in Settings and retry.

## Resize an image

1. Open **Image Resize** and choose a source image.
2. Select **By pixel** or **By percentage**.
3. For pixel mode:
   - With **Keep aspect ratio** enabled, enter either width or height; the other dimension is calculated.
   - With it disabled, enter both dimensions.
4. For percentage mode, use `50` for half-size, `100` for unchanged dimensions, or `200` for double-size.
5. Choose PNG, JPEG, BMP, TIFF, or GIF. JPEG and GIF expose a quality slider.
6. Set the output filename and folder, then select **Resize Image**.

The output extension is normalized to match the selected format.

## Compress a video

1. Open **Compress** and select **Video**.
2. Browse for a file or drag a supported video onto the drop zone.
3. Choose H.264, HEVC, or AV1.
4. Choose automatic GPU detection, a specific NVIDIA/Intel/AMD encoder, or CPU-only.
5. Set the quality value. Lower CRF/CQ values generally mean higher quality and larger output.
6. Choose the output folder and select **Start Compression**.

The output is MP4 with AAC audio. Automatic mode detects a GPU vendor and falls back to CPU if hardware encoding is unavailable or fails. Use **Cancel** to stop the running FFmpeg process.

## Compress an image

1. Open **Compress** and select **Image**.
2. Browse or drag in a supported image.
3. Choose WebP, JPEG, PNG, GIF, or BMP and adjust quality.
4. Choose the output folder and start compression.

The result is named `<original-name>_compressed.<extension>`. If it already exists, the app asks before overwriting it.

## Download history

Completed media downloads appear on Home. For each item, you can play/open the file, copy its path, open its containing folder, or delete the history entry. **Clear All** removes the history list, not the downloaded media files.

History currently tracks downloader results; image resize and compression results are not added to it.

## Settings

- **Language:** English or Vietnamese.
- **Theme:** Light, Dark, or System default.
- **Video save folder:** default destination for video downloads and the initial Compress destination.
- **Audio save folder:** default destination for audio downloads.
- **Browser cookies:** import or delete a Netscape-format cookie file for login-gated sites.

See [Settings, files, and local data](configuration-and-data.md) for persistence paths.
