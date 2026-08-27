using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>Persists the "Download History" list to a small JSON file in LocalAppData.</summary>
public class HistoryStore
{
    private readonly string _filePath;

    public HistoryStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "y2akio");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "history.json");
    }

    public async Task<List<DownloadHistoryItem>> LoadAsync()
    {
        if (!File.Exists(_filePath)) return new List<DownloadHistoryItem>();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var items = await JsonSerializer.DeserializeAsync<List<DownloadHistoryItem>>(stream);
            return items ?? new List<DownloadHistoryItem>();
        }
        catch
        {
            return new List<DownloadHistoryItem>(); // corrupted/old file — start fresh rather than crash
        }
    }

    public async Task SaveAsync(IEnumerable<DownloadHistoryItem> items)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, items, new JsonSerializerOptions { WriteIndented = true });
    }
}
