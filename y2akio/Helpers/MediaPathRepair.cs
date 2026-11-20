using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace y2akio.Helpers;

/// <summary>
/// Finds the real file behind a path whose non-ASCII characters were lost. yt-dlp used to print paths
/// in the ANSI code page, so "a little candy freestyle 🍬.mp4" came back as "a little candy freestyle .mp4"
/// and Vietnamese titles lost their letters; history entries and "Saved:" paths then pointed nowhere.
/// </summary>
internal static class MediaPathRepair
{
    private static readonly HashSet<string> NotMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        ".part", ".ytdl", ".y2apart", ".json", ".tmp", ".temp", ".srt", ".vtt", ".ass", ".ssa", ".sub",
        ".jpg", ".jpeg", ".png", ".webp", ".description", ".lrc",
    };

    /// <summary>Letters and digits only, ASCII only, lower case: what survives the lossy printing on both sides.</summary>
    public static string Loose(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in Path.GetFileNameWithoutExtension(fileName))
        {
            if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.Append('|').Append(Path.GetExtension(fileName).ToLowerInvariant()).ToString();
    }

    /// <summary>
    /// The existing file in <paramref name="folder"/> that <paramref name="brokenPath"/> was meant to be
    /// (same name once non-ASCII is ignored), preferring the one written closest to <paramref name="near"/>.
    /// </summary>
    public static string? FindByLooseName(string? brokenPath, string? folder = null, DateTime? near = null)
    {
        if (string.IsNullOrWhiteSpace(brokenPath)) return null;
        folder ??= Path.GetDirectoryName(brokenPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;

        var key = Loose(Path.GetFileName(brokenPath));
        try
        {
            var matches = new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => Loose(f.Name) == key)
                .ToList();
            if (matches.Count == 0) return null;
            var target = near ?? DateTime.Now;
            return matches.OrderBy(f => Math.Abs((f.LastWriteTime - target).TotalSeconds)).First().FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The download's real output: <paramref name="parsedPath"/> when it exists, else the same name with
    /// lost characters restored, else the newest media file this run created in <paramref name="folder"/>.
    /// </summary>
    public static string? ResolveOutput(string? parsedPath, string folder, ISet<string> filesBefore, DateTime startedAt)
    {
        if (!string.IsNullOrWhiteSpace(parsedPath) && File.Exists(parsedPath)) return parsedPath;
        if (FindByLooseName(parsedPath, folder, DateTime.Now) is { } byName) return byName;

        try
        {
            return new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => !filesBefore.Contains(f.FullName) && f.LastWriteTime >= startedAt.AddSeconds(-5)
                            && !NotMedia.Contains(f.Extension) && !f.Name.Contains(".y2a", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName ?? parsedPath;
        }
        catch
        {
            return parsedPath;
        }
    }
}
