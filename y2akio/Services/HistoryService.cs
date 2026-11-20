using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>
/// App-wide download history. Downloads now finish in the background queue, possibly while the Home
/// page is not even alive, so the list lives here instead of on the page. UI thread only.
/// </summary>
public sealed class HistoryService
{
    public static HistoryService Instance { get; } = new();

    private readonly HistoryStore _store = new();
    private Task? _loading;

    public ObservableCollection<DownloadHistoryItem> Items { get; } = new();

    private HistoryService() { }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        var items = await _store.LoadAsync();
        foreach (var item in items.OrderByDescending(i => i.CompletedAt))
            Items.Add(item);
    }

    public async Task AddAsync(DownloadHistoryItem item)
    {
        await EnsureLoadedAsync();
        Items.Insert(0, item);
        await SaveAsync();
    }

    public Task SaveAsync() => _store.SaveAsync(Items.ToList());
}
