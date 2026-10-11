# Troubleshooting

## The app does not build

- Run `dotnet --info` and confirm a .NET 8 SDK is installed.
- Restore with `dotnet restore .\y2akio.sln`.
- Build with an explicit platform: `dotnet build .\y2akio.sln -c Debug -p:Platform=x64`.
- If MSBuild reports missing `y2akio\bin\yt-dlp.exe` or FFmpeg content, complete [Requirements and setup](requirements-and-setup.md).

## The app opens but analysis fails immediately

Confirm `yt-dlp.exe` exists in the built app's `bin` directory and runs from PowerShell. Update it to a current release because site extractors change frequently.

The red InfoBar contains the user-facing failure. Expand the Home page log for raw `yt-dlp` output.

## A YouTube video plays in the browser but is unavailable in Y2Akio

Y2Akio retries bot-check-like failures through the Android player client. That fallback can be limited to lower resolutions, sometimes 360p. If both attempts fail, update `yt-dlp` and check whether the video is private, age-restricted, region-restricted, or requires a browser session.

Imported whole-browser cookies are intentionally not sent to YouTube by the downloader.

## X/Twitter, Facebook, or TikTok requires login

Export a fresh Netscape-format cookie file from a logged-in browser and import it under **Settings > Download > Browser cookies**. Sessions expire, so an old export can stop working without an app change.

For Facebook parser failures, update `yt-dlp`. Y2Akio uses Chrome impersonation and rewrites supported story URLs, but private content still needs valid access.

## TikTok photo analysis fails

- Confirm the URL points to a photo post, not a normal video.
- Retry with the expanded post URL if a short link cannot be resolved.
- Import fresh TikTok cookies for login-gated posts.
- Network/API changes on TikTok can break the post-data endpoint; inspect the displayed error before retrying.

## Downloaded video has no audio, merging fails, or `.part` files remain

FFmpeg is missing, inaccessible, or incompatible. Verify the runtime `bin` layout from [Requirements and setup](requirements-and-setup.md), then run `ffmpeg.exe -version` directly.

Loose subtitle or thumbnail sidecars are expected when **save separately** is selected. For embedded output, the app removes sidecars created by the current run on a best-effort basis.

## Compression says FFmpeg was not found

The Compress page only checks FFmpeg beside the running application in its known runtime paths. It does not currently fall back to the system `PATH`. Ensure the publish/build output contains `bin\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe`, `bin\ffmpeg.exe`, or `ffmpeg.exe` beside the app.

## Hardware video compression fails

Use **Auto** first. Y2Akio detects the GPU vendor and falls back to CPU if the hardware encoder fails. A vendor match does not guarantee that a specific GPU and driver support H.264, HEVC, or AV1 encoding.

Update the GPU driver, try another codec, or select **None (CPU Only)**. The FFmpeg log contains the encoder error.

## Image resize output looks wrong

- With aspect ratio enabled, enter one dimension and leave the other blank.
- With aspect ratio disabled, enter both dimensions.
- Percentage must be greater than zero.
- JPEG and GIF quality settings are lossy; PNG, BMP, and TIFF do not use that slider.
- The resizer respects EXIF orientation but does not perform color management.

## Completion notifications do not appear

Launch the app once so toast registration and the Start-menu shortcut can be created. Check Windows notification permissions and look for `Toast registration failed` in diagnostics. Clicking a successful completion toast should open the save folder.

## Settings or history reset unexpectedly

Y2Akio falls back safely when `settings.json` or `history.json` is corrupt. Close the app, inspect `%LocalAppData%\Y2Akio`, and restore or remove the invalid file. See [Settings, files, and local data](configuration-and-data.md).

## Published app fails at startup

For `Unable to load resource dll. Microsoft.WindowsAppRuntime.Insights.Resource.dll`, confirm the DLL exists beside `y2akio.exe`. A missing file usually means the publish folder is incomplete or the custom extraction target did not run.

Use the event-log and Release-debugging steps in [Build and publish](build-and-publish.md).
