using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace y2akio.Services.Downloads;

/// <summary>
/// Resume data for one segmented download, stored next to the partial file as
/// <c>&lt;target&gt;.y2a.json</c>. It records which byte ranges are already on disk plus the
/// validators (size, ETag, Last-Modified) used to make sure the remote file did not change.
/// </summary>
public sealed class DownloadState
{
    public int Version { get; set; } = 1;
    public string Url { get; set; } = "";
    public long TotalSize { get; set; } = -1;
    public bool RangeSupported { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<SegmentRecord> Segments { get; set; } = new();

    public long DownloadedBytes
    {
        get
        {
            long sum = 0;
            foreach (var s in Segments) sum += s.Downloaded;
            return sum;
        }
    }

    public static string PartPath(string targetPath) => targetPath + ".y2apart";
    public static string StatePath(string targetPath) => targetPath + ".y2a.json";

    /// <summary>Loads the resume state for <paramref name="targetPath"/>, or null when there is none or it is unusable.</summary>
    public static DownloadState? TryLoad(string targetPath)
    {
        try
        {
            var statePath = StatePath(targetPath);
            var partPath = PartPath(targetPath);
            if (!File.Exists(statePath) || !File.Exists(partPath)) return null;

            var state = JsonSerializer.Deserialize(File.ReadAllText(statePath), DownloadStateJsonContext.Default.DownloadState);
            if (state is null || state.Version != 1 || state.TotalSize <= 0 || !state.RangeSupported) return null;
            if (new FileInfo(partPath).Length != state.TotalSize) return null;

            // Segments must be inside the file and not claim more bytes than they span.
            foreach (var s in state.Segments)
            {
                if (s.Start < 0 || s.End > state.TotalSize || s.Start > s.End || s.Downloaded < 0 || s.Downloaded > s.End - s.Start)
                    return null;
            }
            return state;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Atomically writes the state (temp file + rename), so a crash never leaves a torn JSON file.</summary>
    public void Save(string targetPath)
    {
        var statePath = StatePath(targetPath);
        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, DownloadStateJsonContext.Default.DownloadState));
        File.Move(tmp, statePath, overwrite: true);
    }

    /// <summary>True when a partial download with resume data exists for <paramref name="targetPath"/>.</summary>
    public static bool Exists(string targetPath) => File.Exists(StatePath(targetPath)) && File.Exists(PartPath(targetPath));

    /// <summary>Deletes the partial file and its resume data (cancel / restart from zero).</summary>
    public static void Delete(string targetPath)
    {
        foreach (var path in new[] { PartPath(targetPath), StatePath(targetPath), StatePath(targetPath) + ".tmp" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }
}

public sealed class SegmentRecord
{
    public long Start { get; set; }
    public long End { get; set; }          // exclusive
    public long Downloaded { get; set; }   // bytes on disk, starting at Start
}

// Source-generated (trim-safe) serializer: Release builds of the app are trimmed.
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DownloadState))]
internal partial class DownloadStateJsonContext : JsonSerializerContext
{
}
