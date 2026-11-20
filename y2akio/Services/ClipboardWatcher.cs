using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace y2akio.Services;

/// <summary>
/// "Watch clipboard for links" (IDM-style): when a new http(s) link lands on the clipboard, the Home
/// page offers to analyze it. Checked on clipboard changes and whenever the window is activated (the
/// usual flow: copy in the browser, switch back to the app). UI thread only.
/// </summary>
public static class ClipboardWatcher
{
    private static bool _started;
    private static string _lastSeen = "";

    /// <summary>A link copied since the last time one was offered; consumed by the Home page.</summary>
    public static string? PendingUrl { get; private set; }

    public static event Action<string>? LinkDetected;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Clipboard.ContentChanged += (_, _) => _ = CheckAsync();
    }

    public static void Consume() => PendingUrl = null;

    /// <summary>Text the app itself put on the clipboard; never offered as a new link.</summary>
    public static void MarkSeen(string text) => _lastSeen = text.Trim();

    /// <summary>Reads the clipboard and raises <see cref="LinkDetected"/> for a new link (setting permitting).</summary>
    public static async Task CheckAsync()
    {
        if (!AppSettingsService.Instance.WatchClipboard) return;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = (await content.GetTextAsync())?.Trim() ?? "";
            if (text.Length is 0 or > 2048 || text == _lastSeen) return;
            _lastSeen = text;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;

            PendingUrl = text;
            LinkDetected?.Invoke(text);
        }
        catch
        {
            // clipboard busy / locked by another app: try again on the next change
        }
    }
}
