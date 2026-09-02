# Troubleshooting

## Analysis errors

**Couldn't analyze that link**: check the URL and network connection. The red InfoBar shows the user-facing error, while the log panel contains yt-dlp stderr.

**YouTube says the video is unavailable in the app but plays in a browser**: the app retries affected videos through the Android player client. Some videos may then be limited to 360p.

**X/Twitter says no video could be found**: the post is likely login-gated. Export fresh Netscape-format cookies and import them under **Settings > Download > Browser cookies**.

**Facebook cannot parse data**: update yt-dlp first. Private or friends-only reels also require fresh logged-in cookies. The app uses `--impersonate chrome` for Facebook URLs.

## Download and notification errors

**No audio, no merge, or leftover `.part` files**: ffmpeg was not found. Check the locations in [Requirements and Setup](requirements-and-setup.md).

**No completion toast**: launch the app again so registration can run, then check Windows notification permissions and the log for `Toast registration failed`.

**Leftover `.srt` or `.webp` files**: these are sidecar files. When embedding is selected, the app removes loose files created by that run.

## Published app startup error

For `Unable to load resource dll. Microsoft.WindowsAppRuntime.Insights.Resource.dll`, confirm that the resource DLL exists beside `y2akio.exe` in the publish folder. The project includes a publish target that extracts it from the Windows App Runtime package; a missing file usually means the publish output was incomplete.

See [Build and Publish](build-and-publish.md) for event-log and debugger steps.
