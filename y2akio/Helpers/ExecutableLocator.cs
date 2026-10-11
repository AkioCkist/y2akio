using System;
using System.Collections.Generic;
using System.IO;

namespace y2akio.Helpers;

/// <summary>Locates bundled command-line tools first, then falls back to the process PATH.</summary>
internal static class ExecutableLocator
{
    public static string? Find(string fileName, params string[] relativeDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        foreach (var directory in CandidateDirectories(relativeDirectories))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories(IEnumerable<string> relativeDirectories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativeDirectory in relativeDirectories)
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativeDirectory));
            if (seen.Add(directory)) yield return directory;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) yield break;

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length > 0 && seen.Add(directory)) yield return directory;
        }
    }
}
