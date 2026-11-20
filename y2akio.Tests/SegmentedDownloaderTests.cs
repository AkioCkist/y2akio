using System.Diagnostics;
using System.Security.Cryptography;
using y2akio.Services.Downloads;
using Xunit;

namespace y2akio.Tests;

public sealed class SegmentedDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "y2akio-tests-" + Guid.NewGuid().ToString("N"));

    public SegmentedDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Target(string name = "out.bin") => Path.Combine(_dir, name);

    private static SegmentedDownloadOptions FastOptions(int connections = 8) => new()
    {
        MaxConnections = connections,
        MinSplitSize = 256 * 1024,
        RequestWindow = 2L << 20,
        InitialBackoff = TimeSpan.FromMilliseconds(10),
        MaxBackoff = TimeSpan.FromMilliseconds(100),
        StallTimeout = TimeSpan.FromSeconds(5),
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        StateSaveInterval = TimeSpan.FromMilliseconds(50),
    };

    private static void AssertSameBytes(byte[] expected, string path)
    {
        Assert.True(File.Exists(path), "target file missing");
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void AssertNoResumeFiles(string target)
    {
        Assert.False(File.Exists(DownloadState.PartPath(target)));
        Assert.False(File.Exists(DownloadState.StatePath(target)));
    }

    [Fact]
    public async Task Downloads_with_several_connections_and_correct_bytes()
    {
        var data = TestFileServer.RandomData(24 << 20, 1);
        // A touch of latency so the download outlives a few progress ticks.
        using var server = new TestFileServer(data) { Faults = _ => new Fault(DelayPerChunkMs: 1) };
        var maxActive = 0;
        var progress = new SyncProgress<SegmentedProgress>(p => maxActive = Math.Max(maxActive, p.ActiveConnections));

        await new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target(), progress);

        AssertSameBytes(data, Target());
        AssertNoResumeFiles(Target());
        Assert.True(server.MaxConcurrent > 1, $"expected parallel connections, saw {server.MaxConcurrent}");
        Assert.True(maxActive > 1, $"progress never showed more than {maxActive} active connection(s)");
    }

    [Fact]
    public async Task Server_without_ranges_uses_one_connection()
    {
        var data = TestFileServer.RandomData(3 << 20, 2);
        using var server = new TestFileServer(data) { SupportRanges = false };

        await new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
        Assert.Equal(1, server.MaxConcurrent);
    }

    [Fact]
    public async Task Unknown_length_without_ranges_still_downloads()
    {
        var data = TestFileServer.RandomData(1_500_000, 3);
        using var server = new TestFileServer(data) { SupportRanges = false, SendContentLength = false };

        await new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
    }

    [Fact]
    public async Task Dropped_connections_are_retried_without_corruption()
    {
        var data = TestFileServer.RandomData(12 << 20, 4);
        using var server = new TestFileServer(data)
        {
            // Every one of the first 10 requests dies after 300 KB.
            Faults = r => r.Index < 10 ? new Fault(DropAfterBytes: 300 * 1024) : null,
        };

        await new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
        Assert.True(server.Requests > 10);
    }

    [Fact]
    public async Task Stalled_connection_times_out_and_recovers()
    {
        var data = TestFileServer.RandomData(4 << 20, 5);
        using var server = new TestFileServer(data)
        {
            Faults = r => r.Index == 0 ? new Fault(StallAfterBytes: 100 * 1024) : null,
        };
        var options = new SegmentedDownloadOptions
        {
            MaxConnections = 4,
            MinSplitSize = 256 * 1024,
            RequestWindow = 1L << 20,
            StallTimeout = TimeSpan.FromSeconds(1),
            InitialBackoff = TimeSpan.FromMilliseconds(10),
            MaxBackoff = TimeSpan.FromMilliseconds(50),
        };

        await new SegmentedDownloader(options).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
    }

    [Fact]
    public async Task Expired_link_is_refreshed_and_download_continues()
    {
        var data = TestFileServer.RandomData(10 << 20, 6);
        using var server = new TestFileServer(data)
        {
            // The original link "expires" after its first 3 requests; the refreshed one (?fresh=1) works.
            Faults = r => !r.PathAndQuery.Contains("fresh") && r.Index >= 3 ? new Fault(Status: 403) : null,
        };
        var refreshes = 0;

        await new SegmentedDownloader(FastOptions()).DownloadAsync(
            new DownloadSource(server.Url),
            Target(),
            refreshSource: _ =>
            {
                refreshes++;
                return Task.FromResult<DownloadSource?>(new DownloadSource(server.Url + "?fresh=1"));
            });

        AssertSameBytes(data, Target());
        Assert.True(refreshes >= 1);
    }

    [Fact]
    public async Task Expired_link_without_refresh_fails_and_keeps_partial_file()
    {
        var data = TestFileServer.RandomData(10 << 20, 7);
        using var server = new TestFileServer(data)
        {
            Faults = r => r.Index >= 2 ? new Fault(Status: 403) : null,
        };

        await Assert.ThrowsAsync<DownloadUrlExpiredException>(() =>
            new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target()));

        Assert.True(DownloadState.Exists(Target()));
    }

    [Fact]
    public async Task Pause_then_resume_does_not_download_finished_bytes_again()
    {
        var data = TestFileServer.RandomData(16 << 20, 8);
        using var server = new TestFileServer(data)
        {
            Faults = _ => new Fault(DelayPerChunkMs: 2), // slow enough to pause midway
        };
        using var pause = new CancellationTokenSource();
        var progress = new SyncProgress<SegmentedProgress>(p =>
        {
            if (p.Total > 0 && p.Downloaded > p.Total / 2) pause.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedDownloader(FastOptions(4)).DownloadAsync(new DownloadSource(server.Url), Target(), progress, ct: pause.Token));

        Assert.True(DownloadState.Exists(Target()));
        var saved = DownloadState.TryLoad(Target());
        Assert.NotNull(saved);
        Assert.True(saved!.DownloadedBytes > data.Length / 3, $"only {saved.DownloadedBytes} bytes recorded");

        server.Faults = null;
        server.ResetCounters();
        await new SegmentedDownloader(FastOptions(4)).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
        AssertNoResumeFiles(Target());
        Assert.True(server.BytesServed < data.Length * 0.75,
            $"resume re-downloaded {server.BytesServed} of {data.Length} bytes");
    }

    [Fact]
    public async Task Resume_restarts_when_remote_file_changed()
    {
        var oldData = TestFileServer.RandomData(8 << 20, 9);
        using var server = new TestFileServer(oldData) { Faults = _ => new Fault(DelayPerChunkMs: 2) };
        using var pause = new CancellationTokenSource();
        var progress = new SyncProgress<SegmentedProgress>(p =>
        {
            if (p.Downloaded > 2 << 20) pause.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedDownloader(FastOptions(4)).DownloadAsync(new DownloadSource(server.Url), Target(), progress, ct: pause.Token));

        var newData = TestFileServer.RandomData(8 << 20, 10); // same size, different content + ETag
        server.Data = newData;
        server.ETag = "\"v2\"";
        server.Faults = null;

        await new SegmentedDownloader(FastOptions(4)).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(newData, Target());
    }

    [Fact]
    public async Task Rate_limit_is_respected()
    {
        var data = TestFileServer.RandomData(3 << 20, 11);
        using var server = new TestFileServer(data);
        var options = new SegmentedDownloadOptions
        {
            MaxConnections = 4,
            MinSplitSize = 256 * 1024,
            RateLimiters = new[] { new RateLimiter(2L << 20) }, // 2 MB/s
        };

        var sw = Stopwatch.StartNew();
        await new SegmentedDownloader(options).DownloadAsync(new DownloadSource(server.Url), Target());
        sw.Stop();

        AssertSameBytes(data, Target());
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.0), $"3 MB at 2 MB/s took only {sw.Elapsed.TotalSeconds:0.00}s");
    }

    [Fact]
    public async Task Server_errors_5xx_are_retried()
    {
        var data = TestFileServer.RandomData(2 << 20, 12);
        using var server = new TestFileServer(data)
        {
            Faults = r => r.Index is >= 1 and < 5 ? new Fault(Status: 503) : null,
        };

        await new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
    }

    [Fact]
    public async Task Existing_target_is_overwritten()
    {
        var data = TestFileServer.RandomData(1 << 20, 13);
        using var server = new TestFileServer(data);
        await File.WriteAllTextAsync(Target(), "old");

        await new SegmentedDownloader(FastOptions()).DownloadAsync(new DownloadSource(server.Url), Target());

        AssertSameBytes(data, Target());
    }

    /// <summary>IProgress that runs inline (Progress&lt;T&gt; would post to the thread pool and race the assertions).</summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
