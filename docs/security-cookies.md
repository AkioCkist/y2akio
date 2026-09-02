# Cookie Storage and Encryption

Browser cookies can grant access to logged-in sessions, so Y2Akio never stores the imported file as plain text.

## Import

**Settings > Download > Import...** reads the selected file and encrypts its contents immediately with Windows DPAPI-NG through `Windows.Security.Cryptography.DataProtection.DataProtectionProvider` using the `LOCAL=user` descriptor. Only base64 ciphertext is stored in `%LocalAppData%\\Y2Akio\\settings.json` as `CookiesEncrypted`.

The encryption is tied to the current Windows user on the current machine. There is no passphrase or key file to back up.

## Use

During Analyze or Download, the app decrypts the content in memory, writes a temporary plaintext file to `%LocalAppData%\\Y2Akio\\cookies.txt`, passes it to yt-dlp with `--cookies`, and deletes it when the operation finishes. The file is also removed when the app closes or when cookies are deleted in Settings.

## Security boundaries

- The encrypted settings value works only for the importing Windows user on the same machine. Moving `settings.json` elsewhere requires a new import.
- During an active operation, the temporary cookies file is readable by processes running as the same user. This is required because yt-dlp accepts a file path.
- Network protection is provided by the destination site's normal HTTPS connection; the app does not add separate transport encryption.

Use fresh cookies for private or login-gated content and delete them in Settings when they are no longer needed.
