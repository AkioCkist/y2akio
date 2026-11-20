# Analyze Optimization Notes

This document records the design behind the faster YouTube analysis path.

## Original problem

A single YouTube video previously performed two full-cost extraction operations:

1. A flat playlist probe used only to distinguish playlists.
2. A full video dump used to load formats, quality choices, and subtitles.

The probe returned complete format data for a single video, so the second extraction made the first one wasted work. Opening a playlist track also delayed every visible detail even though the flat playlist entry already contained basic metadata.

## Current approach

- URLs that are definitely one YouTube video skip the flat playlist probe and run one full dump.
- A YouTube oEmbed request loads title, author, and thumbnail as a best-effort preview while the full dump runs.
- Playlist entries display known title, duration, and a derived thumbnail immediately; full per-video analysis continues in the background.
- Subtitle maps remain part of the full dump. Fetching them lazily would require another expensive extraction.
- Ambiguous and non-YouTube URLs retain the conservative flat-probe path.

## Further speedups (2026-11)

Measured on the bundled yt-dlp 2026.08.19 (YouTube "Me at the zoo"): `--version` 0.4s, full dump 2.8–4.2s, `--load-info-json` simulate 0.6s versus 2.1s from the URL.

- **Single non-YouTube videos reuse the flat probe.** `--flat-playlist` only affects playlist entries; on a single TikTok/Dailymotion/Facebook video the probe already carries `formats`, so `AnalyzeAsync` parses it directly instead of running a second full extraction. Results without `formats` (Vimeo, `_type: url`) still get the full dump.
- **Downloads reuse the analyze JSON.** `VideoInfo.InfoJson` is passed to `DownloadRequest.InfoJson` and handed to yt-dlp with `--load-info-json`, which skips page/player extraction and the JS challenge. JSON older than 3 hours is ignored (signed URLs expire), and a failed cached attempt falls back to the normal URL attempts.
- **`--concurrent-fragments 4`** for HLS/DASH formats.
- **Prewarm.** `HomePage` runs `yt-dlp --version`, `deno --version` and `ffmpeg -version` in the background at startup so the first analyze does not pay for a cold file cache and antivirus scan.
- **No cookies for YouTube.** `YtDlpService.NeedsCookies` skips decrypting and writing `cookies.txt` for YouTube links, which yt-dlp never sends cookies to.
- **Facebook/X post links analyze in parallel.** The yt-dlp analysis starts at the same time as the gallery page fetch and is cancelled when the post turns out to be a photo post.
- **TikTok WAF memo.** After HttpClient gets the bot challenge once, TikTok photo posts go straight to the impersonated fetch for 30 minutes.
- **Playlist downloads run 3 at a time**, and one failed entry no longer stops the rest.
- **GPU recode.** A forced codec (H.264/HEVC/AV1) uses NVENC, Quick Sync or AMF when a test encode proves the encoder works, otherwise falls back to the CPU encoder. The probe result is cached per app run.

## URL classification

`YtDlpService.LooksLikeSingleVideoUrl` returns true only for known single-video forms without a `list` query:

- `youtu.be/<id>`
- `youtube.com/watch?v=<id>`
- `/shorts/<id>`
- `/embed/<id>`
- `/live/<id>`
- `/v/<id>`

Channels, playlist URLs, URLs with `list=`, non-YouTube hosts, and ambiguous forms remain on the playlist-detection path.

## UI state model

| Phase | Current video | Preview active | Options | Download button |
|---|---|---|---|---|
| Analysis starts | Empty | Yes | Hidden or temporary card | Disabled |
| oEmbed/entry preview arrives | Empty | Yes | Basic card, no format controls | Disabled |
| Full dump completes | Set | No | Fully populated | Enabled when no download is active |
| Analysis fails | Empty | No | Temporary card removed | Depends on other active work |

The full `PopulateOptionsPanel` call is the final writer. The preview path checks whether preview state is still active, so a late oEmbed response cannot overwrite complete format data.

## Relevant code

- `Services/YtDlpService.cs`
  - `LooksLikeSingleVideoUrl`
  - fast path in `AnalyzeAsync`
  - `FetchYouTubePreviewAsync`
- `Views/HomePage.xaml.cs`
  - `ShowInstantCard`
  - `FinishVideoOptions`
  - `TryPaintYouTubePreviewAsync`
  - playlist item-click flow

## Manual regression checks

1. Normal YouTube watch URL: preview first, then one complete format load.
2. Shorts and `youtu.be` URL: same fast path.
3. YouTube URL containing `list=`: playlist probe remains active.
4. Real playlist: checklist appears; clicking one row gives an immediate card and later full options.
5. YouTube bot-check failure: Android player retry still runs.
6. Facebook story and other non-YouTube URLs: existing normalization, cookies, and impersonation remain active.
7. oEmbed failure: no preview card, but the full analysis can still complete normally.

Run the build and validation steps in the [Developer guide](development.md) after changing this flow.
