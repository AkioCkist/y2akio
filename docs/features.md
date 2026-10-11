# Features and Supported Formats

## Media downloader

- Analyzes a single media URL or a playlist supported by `yt-dlp`.
- Shows an instant YouTube title, author, and thumbnail preview while full format analysis continues.
- Allows playlist entry selection and batch downloads; opening one entry loads its individual formats.
- Shows raw `yt-dlp` and FFmpeg output in a live log panel.
- Reports progress, stores completed downloads in history, and sends a Windows completion notification.
- Retries selected YouTube bot-check failures with the Android player client. This fallback can expose fewer formats.
- Normalizes Facebook story URLs and uses Chrome impersonation for Facebook extraction.

### Video output

| Option | Values | Notes |
|---|---|---|
| Quality | Best or an available height | Derived from the analyzed media formats |
| Container | MP4, MKV, WebM | Used when codec is set to Auto |
| Codec | Auto, H.264, HEVC, AV1, VP9, MPEG-4 Part 2 | A non-Auto codec forces a full FFmpeg transcode and MP4 output |
| Subtitles | Author-uploaded, auto-generated, or both | SRT or VTT; embedded or saved separately |
| Thumbnail | Embed, save separately, or skip | Embedding requires FFmpeg for some output types |
| Metadata | Optional embedded title/artist metadata | Implemented with `yt-dlp --add-metadata` |

Forced video transcoding uses these application defaults:

| Codec | Encoder and quality setting |
|---|---|
| H.264 | `libx264`, medium preset, CRF 18 |
| HEVC | `libx265`, medium preset, CRF 23 |
| AV1 | `libsvtav1`, preset 6, CRF 30 |
| VP9 | `libvpx-vp9`, CRF 31, zero target bitrate |
| MPEG-4 Part 2 | `mpeg4`, quality scale 3 |

### Audio output

Supported formats are MP3, M4A, AAC, FLAC, Opus, Vorbis/OGG, WAV, ALAC, and WMA. Lossy formats offer 320, 256, 192, and 128 kbps choices; lossless formats use the best mode and hide the bitrate selector.

### File naming

The output name can include the download date and time, channel/uploader, and media title. If every option is cleared, the title is still used to prevent an empty filename.

## TikTok photo posts

- Detects TikTok photo post URLs, including resolved short links.
- Displays post metadata and a selectable thumbnail grid.
- Opens an image full-size on double-click.
- Saves all or selected images either into a folder or into one ZIP archive.
- Can use imported browser cookies when the post requires authentication.

## Image resize

- Accepts common image files through the Windows file picker.
- Resizes by explicit pixel dimensions or by a percentage.
- Preserves aspect ratio when requested; one pixel dimension can be left blank in that mode.
- Uses high-quality Fant interpolation and respects EXIF orientation.
- Outputs PNG, JPEG, BMP, TIFF, or GIF.
- Exposes an image-quality control for JPEG and GIF.
- Uses Windows Imaging Component and does not require FFmpeg.

## Media compression

### Video

- Inputs: MP4, MKV, WebM, AVI, MOV, WMV, and FLV.
- Output: MP4 with AAC audio at 128 kbps and `yuv420p` pixel format.
- Codecs: H.264, HEVC, and AV1.
- Acceleration: automatic detection, NVIDIA NVENC, Intel Quick Sync, AMD AMF, or CPU-only.
- Automatically falls back to CPU encoding if a selected hardware encoder fails.
- Supports drag-and-drop, preview, cancellation, an FFmpeg log, and before/after size comparison.

### Image

- Inputs: JPEG, PNG, BMP, WebP, TIFF, and GIF.
- Outputs: WebP, JPEG, PNG, GIF, or BMP.
- Provides a quality control where supported and shows the resulting file size.

## Application experience

- English and Vietnamese UI on the downloader and settings pages.
- System, light, and dark themes.
- Separate default video and audio download folders.
- Persistent download history with play, copy path, open folder, and delete actions.
- Mica window backdrop and custom navigation shell.

> Some secondary tool pages currently contain English-only labels even when Vietnamese is selected.
