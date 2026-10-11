# Settings, Files, and Local Data

## Settings

The Settings page persists changes immediately.

| Setting | Values/default | Effect |
|---|---|---|
| Language | English (default), Vietnamese | Updates localized downloader and settings labels |
| Theme | System (default), Light, Dark | Updates the window theme |
| Video save folder | `%USERPROFILE%\Downloads` | Default for video downloads; initial Compress output folder |
| Audio save folder | `%USERPROFILE%\Downloads` | Default for audio downloads |
| Browser cookies | Not imported | Enables selected authenticated non-YouTube operations |

## Local application data

Y2Akio uses regular files because its development/runtime configuration is unpackaged and `Windows.Storage.ApplicationData` requires package identity.

```text
%LocalAppData%\Y2Akio\
├── settings.json
├── history.json
└── cookies.txt       # temporary; only present while needed
```

### `settings.json`

Stores language, theme, video folder, audio folder, the cookie source filename, and an encrypted cookie blob. If the file is missing or cannot be parsed, the app falls back to defaults. Settings writes are best-effort so a filesystem failure does not crash the app.

### `history.json`

Stores the Home page download history. If it is missing or invalid, history starts empty. Clearing history or deleting a history row removes records only; it does not delete downloaded files.

### `cookies.txt`

Created on demand by decrypting the stored cookie blob so external tools can read it. The app attempts to delete it after analyze/download work, when cookies are removed, during startup when no cookie blob exists, and when the main window closes.

See [Cookie security](security-cookies.md) for the full lifecycle and limitations.

## Output files

- Downloader output uses the selected video or audio folder.
- TikTok photos use the chosen folder or ZIP destination.
- Image Resize uses the selected output folder and filename.
- Compress defaults to the configured video folder, but its page-level folder change is not persisted as a new global setting.

## Resetting local state

Close Y2Akio before manually changing its local files. To reset preferences and cookie state, remove `settings.json`. To reset the history list, remove `history.json`. Media output is stored elsewhere and is unaffected.
