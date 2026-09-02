# Features

Y2Akio is a WinUI 3 media downloader built on `yt-dlp`.

- **Single video**: analyze and download YouTube or any `yt-dlp`-supported URL.
- **Playlist support**: inspect a playlist, select videos, download them in a batch, or open one track's options.
- **Video or audio**: choose MP4 video or audio-only extraction.
- **Audio formats**: MP3, M4A, AAC, FLAC, Opus, Vorbis (OGG), WAV, ALAC and WMA. Lossless formats hide bitrate options.
- **Subtitles**: download author-uploaded and/or auto-generated subtitles, choose languages, use SRT/VTT, and embed or save separately.
- **Quality and containers**: choose MP4/MKV/WEBM, height-based video quality, or 320/256/192/128 kbps audio.
- **Video codec transcoding**: force H.264, HEVC, AV1, VP9 or MPEG-4 output through ffmpeg. Transcoding is performed in MP4 and can be slow at high resolutions.
- **Thumbnails and metadata**: embed a thumbnail, save it alongside the media, skip it, and embed title/artist metadata.
- **Filename options**: prefix names with date/time and/or channel, and include the title.
- **Live download log**: see yt-dlp, ffmpeg, progress and error output while downloading.
- **Windows toast notifications**: receive a completion notification that can open the save folder.
- **Persistent history**: download history is stored under `%LocalAppData%\\Y2Akio\\history.json`.
- **Browser cookies**: import cookies for login-gated content. Cookies are encrypted at rest and passed to yt-dlp only during an operation.

## Codec notes

The selected codec is a full video re-encode. Quality is controlled by codec-specific settings: H.264 uses CRF 18, HEVC CRF 23, AV1 CRF 30, VP9 CRF 31, and MPEG-4 uses `-q:v 3`. AV1 uses the fast SVT-AV1 encoder.

See [How to use](usage.md) for the workflow and [Cookie storage and encryption](security-cookies.md) for the cookie model.

## Legal note

`yt-dlp` is a legitimate open-source tool, but downloading media may violate a site's Terms of Service or copyright law depending on the content and jurisdiction. Use responsibly.
