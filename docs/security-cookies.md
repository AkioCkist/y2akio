# Cookie Storage and Security

Browser cookies can grant access to logged-in sessions. Treat exported cookie files like passwords.

## Import

Use **Settings > Download > Browser cookies > Import** and select a Netscape-format `cookies.txt` file. The app reads the file and immediately encrypts its contents with Windows DPAPI-NG through `DataProtectionProvider` using the `LOCAL=user` descriptor.

Only the encrypted base64 value and the original filename are stored in `%LocalAppData%\Y2Akio\settings.json`. The original file selected by the user is not managed or deleted by Y2Akio.

## Use

When an operation needs cookies, the app:

1. Decrypts the stored blob in memory.
2. Writes `%LocalAppData%\Y2Akio\cookies.txt`.
3. Passes the path to the relevant downloader or uses the content for a TikTok request.
4. Attempts to delete the temporary file when the operation ends.

The file is also removed when cookies are deleted in Settings and when the main window closes.

## Site behavior

The `yt-dlp` integration sends imported cookies to non-YouTube sites that may need authentication, such as X/Twitter or private Facebook content. It deliberately does not send the imported whole-browser cookie jar to YouTube because that can trigger Google's bot checks. TikTok photo analysis can use the decrypted cookie content.

## Security boundaries

- Encryption is bound to the current Windows user on the current machine. Moving `settings.json` to another user or computer requires a fresh import.
- While an operation is active, the temporary plaintext file can be read by other processes running as the same Windows user.
- Cleanup is best-effort. A crash or forced termination can leave `cookies.txt` behind until the next cleanup attempt.
- Y2Akio does not rotate, refresh, or validate exported cookies.
- Network transport relies on the destination site's HTTPS connection.

## Recommended practices

- Export only the cookies needed for the target site when your browser extension supports filtering.
- Re-export when a site session expires.
- Delete imported cookies in Settings when they are no longer needed.
- Do not commit or share `cookies.txt`, `settings.json`, logs containing private URLs, or authenticated output.
