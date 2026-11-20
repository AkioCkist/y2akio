using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace y2akio.Services.Downloads;

/// <summary>Where to download from: the media URL plus the HTTP headers the site expects (from yt-dlp's <c>http_headers</c>).</summary>
public sealed record DownloadSource(string Url, IReadOnlyDictionary<string, string> Headers)
{
    public DownloadSource(string url) : this(url, new Dictionary<string, string>()) { }
}

public readonly record struct SegmentSnapshot(long Start, long End, long Position, bool Active);

/// <summary>Progress of one segmented download. <see cref="Total"/> is -1 when the server did not say.</summary>
public sealed record SegmentedProgress(
    long Downloaded,
    long Total,
    double BytesPerSecond,
    TimeSpan? Eta,
    int ActiveConnections,
    IReadOnlyList<SegmentSnapshot> Segments)
{
    public double Percent => Total > 0 ? Downloaded * 100.0 / Total : 0;
}

public sealed class SegmentedDownloadOptions
{
    /// <summary>Most connections used at once for one file.</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>A running segment is only split when at least twice this much is left in it.</summary>
    public long MinSplitSize { get; init; } = 1L << 20;

    /// <summary>Most bytes asked for in one HTTP request. Long requests get throttled by googlevideo and some CDNs.</summary>
    public long RequestWindow { get; init; } = 10L << 20;

    /// <summary>A connection that delivers no byte for this long is dropped and retried.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Consecutive failures (without any byte of progress) one connection tolerates.</summary>
    public int MaxRetries { get; init; } = 10;

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times an expired (403/410) URL may be refreshed for one download.</summary>
    public int MaxSourceRefreshes { get; init; } = 3;

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan StateSaveInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int BufferSize { get; init; } = 64 * 1024;

    /// <summary>Speed limiters this download counts against (job limit, global limit, ...).</summary>
    public IReadOnlyList<RateLimiter> RateLimiters { get; init; } = Array.Empty<RateLimiter>();
}

/// <summary>The server rejected the (signed, time-limited) URL with 403 / 410.</summary>
public sealed class DownloadUrlExpiredException : Exception
{
    public DownloadUrlExpiredException(HttpStatusCode status)
        : base($"The download link was rejected ({(int)status} {status}); it has probably expired.") { }
}

/// <summary>The remote file is not the one the partial download started from (size / ETag / range support changed).</summary>
public sealed class RemoteFileChangedException : Exception
{
    public RemoteFileChangedException(string message) : base(message) { }
}

/// <summary>
/// Multi-connection HTTP downloader built around dynamic file segmentation, the technique download
/// managers such as IDM are known for:
/// <list type="number">
/// <item>The first request doubles as the probe and becomes connection #1 for the whole file.</item>
/// <item>Whenever a connection slot is free, the running segment with the most bytes left is cut in half
/// and the new connection takes the upper half. A connection that finishes immediately does the same,
/// so no connection idles near the end while a slow one finishes alone.</item>
/// <item>Every connection writes straight into a preallocated (sparse) <c>.y2apart</c> file at its own
/// offset; nothing is concatenated at the end.</item>
/// <item>Progress per segment is saved to <c>.y2a.json</c> about once a second, so a pause, crash, network
/// loss or app restart resumes without fetching a byte twice.</item>
/// </list>
/// Transient failures retry per connection with exponential backoff; a 403/410 asks the caller for a
/// fresh URL and continues from the saved offsets when the file is unchanged.
/// </summary>
public sealed class SegmentedDownloader
{
    private static readonly Lazy<HttpClient> SharedClient = new(CreateHttpClient);

    private readonly HttpClient _http;
    private readonly SegmentedDownloadOptions _options;

    public SegmentedDownloader(SegmentedDownloadOptions? options = null, HttpClient? http = null)
    {
        _options = options ?? new SegmentedDownloadOptions();
        if (_options.MaxConnections < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxConnections must be >= 1.");
        if (_options.MinSplitSize < _options.BufferSize * 2L)
            throw new ArgumentOutOfRangeException(nameof(options), "MinSplitSize must be at least twice BufferSize.");
        _http = http ?? SharedClient.Value;
    }

    /// <summary>
    /// HTTP client tuned for range downloads: no automatic decompression (byte ranges must address the
    /// stored bytes), no overall timeout (stalls are detected per read), many connections per host.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            UseCookies = false,
            MaxConnectionsPerServer = 64,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Downloads <paramref name="source"/> to <paramref name="targetPath"/>, resuming from an existing
    /// <c>.y2apart</c> / <c>.y2a.json</c> pair when the remote file is unchanged. Cancelling pauses:
    /// the partial file and its state stay on disk for the next call.
    /// </summary>
    /// <param name="refreshSource">Called when the server answers 403/410 (expired signed URL); returns a new
    /// URL for the same file, or null to give up. Null = no refresh.</param>
    public async Task DownloadAsync(
        DownloadSource source,
        string targetPath,
        IProgress<SegmentedProgress>? progress = null,
        Func<CancellationToken, Task<DownloadSource?>>? refreshSource = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var refreshes = 0;
        var restarts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await new Run(this, source, targetPath, progress).ExecuteAsync(ct);
                return;
            }
            catch (DownloadUrlExpiredException) when (refreshSource is not null && refreshes < _options.MaxSourceRefreshes)
            {
                refreshes++;
                source = await refreshSource(ct)
                    ?? throw new InvalidOperationException("The download link expired and no fresh link could be obtained.");
            }
            catch (RemoteFileChangedException) when (restarts < 2)
            {
                // The file behind the URL changed (or stopped honoring ranges) — the bytes on disk are
                // worthless, start over.
                restarts++;
                DownloadState.Delete(targetPath);
            }
        }
    }

    // ─── One attempt: probe/validate, then run connections until done or stopped ──────────────

    private sealed class Segment
    {
        public long Start;
        public long End;       // exclusive; only ever shrinks (when split)
        public long Position;  // next byte to write
        public bool Active;
        public long Remaining => End - Position;
    }

    private sealed class Run
    {
        private readonly SegmentedDownloader _owner;
        private readonly SegmentedDownloadOptions _o;
        private readonly DownloadSource _source;
        private readonly string _targetPath;
        private readonly string _partPath;
        private readonly IProgress<SegmentedProgress>? _progress;

        private readonly object _lock = new();
        private readonly List<Segment> _segments = new();
        private DownloadState _state = new();
        private SafeFileHandle? _file;
        private long _downloaded;
        private int _active;

        public Run(SegmentedDownloader owner, DownloadSource source, string targetPath, IProgress<SegmentedProgress>? progress)
        {
            _owner = owner;
            _o = owner._options;
            _source = source;
            _targetPath = targetPath;
            _partPath = DownloadState.PartPath(targetPath);
            _progress = progress;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = roundCts.Token;

            HttpResponseMessage? first = null;
            Segment? firstSegment = null;
            Task? reporter = null;
            var reporterStop = new CancellationTokenSource();
            try
            {
                var resume = DownloadState.TryLoad(_targetPath);
                if (resume is not null)
                {
                    (first, firstSegment) = await TryResumeAsync(resume, token);
                }

                if (firstSegment is null)
                {
                    DownloadState.Delete(_targetPath);
                    (first, firstSegment) = await StartFreshAsync(token);
                }

                reporter = ReportLoopAsync(reporterStop.Token);

                if (!_state.RangeSupported)
                {
                    await SingleStreamAsync(first!, token);
                    first = null;
                }
                else
                {
                    await RunConnectionsAsync(firstSegment, first, roundCts);
                    first = null;
                }

                reporterStop.Cancel();
                if (reporter is not null) await reporter;
                reporter = null;

                Complete();
            }
            finally
            {
                first?.Dispose();
                reporterStop.Cancel();
                if (reporter is not null)
                {
                    try { await reporter; } catch { /* ignore */ }
                }
                reporterStop.Dispose();

                if (_file is not null)
                {
                    // Paused / failed: keep what we have for the next attempt.
                    try { SaveState(); } catch { /* best-effort */ }
                    _file.Dispose();
                    _file = null;
                }
            }
        }

        // ── Probe ──────────────────────────────────────────────────────────

        private async Task<(HttpResponseMessage?, Segment?)> StartFreshAsync(CancellationToken ct)
        {
            var response = await SendWithRetryAsync(0, _o.RequestWindow - 1, ct);
            try
            {
                long total;
                if (response.StatusCode == HttpStatusCode.PartialContent && ParseContentRange(response) is { } range
                    && range.Start == 0 && range.Total > 0)
                {
                    total = range.Total;
                    _state = NewState(response, total, rangeSupported: true);
                }
                else if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignores Range: one connection, no resume.
                    total = response.Content.Headers.ContentLength ?? -1;
                    _state = NewState(response, total, rangeSupported: false);
                }
                else
                {
                    throw new HttpRequestException($"Unexpected response {(int)response.StatusCode} {response.StatusCode}.");
                }

                OpenPartFile(total, create: true);

                var seg = new Segment { Start = 0, End = total > 0 ? total : long.MaxValue, Position = 0 };
                lock (_lock) _segments.Add(seg);
                if (_state.RangeSupported) SaveState();
                return (response, seg);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        /// <summary>Validates the saved state against the server with the first request of the first unfinished segment.</summary>
        private async Task<(HttpResponseMessage?, Segment?)> TryResumeAsync(DownloadState saved, CancellationToken ct)
        {
            var segments = saved.Segments
                .Select(r => new Segment { Start = r.Start, End = r.End, Position = r.Start + r.Downloaded })
                .OrderBy(s => s.Start)
                .ToList();
            var firstOpen = segments.FirstOrDefault(s => s.Remaining > 0);

            if (firstOpen is null)
            {
                // Everything is on disk already; only the final rename was missing.
                _state = saved;
                lock (_lock) _segments.AddRange(segments);
                OpenPartFile(saved.TotalSize, create: false);
                Interlocked.Exchange(ref _downloaded, segments.Sum(s => s.Position - s.Start));
                return (null, segments.Count > 0 ? segments[0] : new Segment());
            }

            var response = await SendWithRetryAsync(firstOpen.Position, Math.Min(firstOpen.End, firstOpen.Position + _o.RequestWindow) - 1, ct);
            var range = ParseContentRange(response);
            var etag = response.Headers.ETag?.Tag;
            var lastModified = response.Content.Headers.LastModified?.ToString("R");

            var unchanged = response.StatusCode == HttpStatusCode.PartialContent
                && range is { } r && r.Start == firstOpen.Position && r.Total == saved.TotalSize
                && (saved.ETag is null || etag is null || saved.ETag == etag)
                && (saved.LastModified is null || lastModified is null || saved.LastModified == lastModified);

            if (!unchanged)
            {
                response.Dispose();
                return (null, null); // caller starts fresh
            }

            _state = saved;
            _state.Url = _source.Url;
            lock (_lock) _segments.AddRange(segments);
            OpenPartFile(saved.TotalSize, create: false);
            Interlocked.Exchange(ref _downloaded, segments.Sum(s => s.Position - s.Start));
            return (response, firstOpen);
        }

        private DownloadState NewState(HttpResponseMessage response, long total, bool rangeSupported) => new()
        {
            Url = _source.Url,
            TotalSize = total,
            RangeSupported = rangeSupported && total > 0,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified?.ToString("R"),
        };

        private void OpenPartFile(long total, bool create)
        {
            if (create)
            {
                if (total > 0) EnsureFreeSpace(total);
                using (var fs = new FileStream(_partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    if (total > 0)
                    {
                        SparseFile.TrySetSparse(fs.SafeFileHandle, true);
                        fs.SetLength(total);
                    }
                }
            }
            _file = File.OpenHandle(_partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        }

        private void EnsureFreeSpace(long needed)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(_partPath));
                if (string.IsNullOrEmpty(root)) return;
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < needed)
                    throw new IOException($"Not enough disk space: {FormatBytes(needed)} needed, {FormatBytes(drive.AvailableFreeSpace)} free on {root}.");
                if (drive.IsReady && needed >= 4L * 1024 * 1024 * 1024
                    && string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"{root} is FAT32, which cannot hold files of 4 GB or more ({FormatBytes(needed)}).");
            }
            catch (IOException) { throw; }
            catch { /* network drives etc.: let the write itself fail if it must */ }
        }

        // ── Multi-connection scheduler ─────────────────────────────────────

        private async Task RunConnectionsAsync(Segment? firstSegment, HttpResponseMessage? firstResponse, CancellationTokenSource roundCts)
        {
            var token = roundCts.Token;
            var running = new List<Task>();
            Exception? failure = null;

            void Start(Segment seg, HttpResponseMessage? initial)
            {
                lock (_lock) seg.Active = true;
                Interlocked.Increment(ref _active);
                running.Add(ConnectionAsync(seg, initial, token));
            }

            if (firstSegment is not null && firstSegment.Remaining > 0) Start(firstSegment, firstResponse);
            else firstResponse?.Dispose();

            while (true)
            {
                if (failure is null && !token.IsCancellationRequested)
                {
                    while (running.Count < _o.MaxConnections && NextSegment() is { } next)
                        Start(next, null);
                }

                if (running.Count == 0) break;

                var done = await Task.WhenAny(running);
                running.Remove(done);
                Interlocked.Decrement(ref _active);

                if (done.IsFaulted && failure is null)
                {
                    failure = done.Exception!.GetBaseException();
                    roundCts.Cancel(); // stop the other connections; their progress is kept
                }
            }

            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            token.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (_segments.Any(s => s.Remaining > 0))
                    throw new IOException("Download stopped before every segment finished.");
            }
        }

        /// <summary>
        /// The next piece of work for a free connection: an unfinished segment nobody is working on
        /// (left over from a resume), otherwise the upper half of the active segment with the most bytes left.
        /// </summary>
        private Segment? NextSegment()
        {
            lock (_lock)
            {
                var idle = _segments.FirstOrDefault(s => !s.Active && s.Remaining > 0);
                if (idle is not null) return idle;

                Segment? largest = null;
                foreach (var s in _segments)
                {
                    if (s.Active && (largest is null || s.Remaining > largest.Remaining)) largest = s;
                }
                if (largest is null || largest.Remaining < 2 * _o.MinSplitSize) return null;

                var mid = largest.Position + largest.Remaining / 2;
                var upper = new Segment { Start = mid, End = largest.End, Position = mid };
                largest.End = mid;
                _segments.Add(upper);
                return upper;
            }
        }

        /// <summary>One connection: requests windows of its segment until the segment is done (it may shrink meanwhile).</summary>
        private async Task ConnectionAsync(Segment seg, HttpResponseMessage? initial, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(_o.BufferSize);
            var failures = 0;
            try
            {
                while (true)
                {
                    long position, end;
                    lock (_lock) { position = seg.Position; end = seg.End; }
                    if (position >= end) return;

                    var response = initial;
                    initial = null;
                    try
                    {
                        response ??= await SendAsync(position, Math.Min(end, position + _o.RequestWindow) - 1, ct);
                        using (response)
                        {
                            CheckRangeResponse(response, position);
                            var wrote = await CopyResponseAsync(response, seg, buffer, ct);
                            if (wrote > 0) failures = 0;
                        }
                    }
                    catch (Exception ex) when (IsTransient(ex, ct))
                    {
                        failures++;
                        if (failures > _o.MaxRetries)
                            throw new IOException($"Connection failed {failures} times in a row: {ex.Message}", ex);
                        await Task.Delay(Backoff(failures), ct);
                    }
                }
            }
            finally
            {
                lock (_lock) seg.Active = false;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Streams one response into the segment's range; stops at the segment's (possibly shrunk) end.</summary>
        private async Task<long> CopyResponseAsync(HttpResponseMessage response, Segment seg, byte[] buffer, CancellationToken ct)
        {
            long total = 0;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            while (true)
            {
                var read = await ReadWithStallTimeoutAsync(stream, buffer, ct);
                if (read == 0) return total;

                long writePos, writeLen;
                lock (_lock)
                {
                    writePos = seg.Position;
                    writeLen = Math.Min(read, seg.End - seg.Position);
                }
                if (writeLen <= 0) return total;

                foreach (var limiter in _o.RateLimiters) await limiter.WaitAsync((int)writeLen, ct);

                // Safe without holding the lock: a split never moves End closer than MinSplitSize
                // (>= 2 buffers) to the Position it saw, so this write cannot cross the new End.
                await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, (int)writeLen), writePos, ct);

                bool segmentDone;
                lock (_lock)
                {
                    seg.Position += writeLen;
                    segmentDone = seg.Position >= seg.End;
                }
                Interlocked.Add(ref _downloaded, writeLen);
                total += writeLen;

                if (writeLen < read || segmentDone) return total; // the rest belongs to another connection
            }
        }

        // ── Single connection (server without Range support) ───────────────

        private async Task SingleStreamAsync(HttpResponseMessage first, CancellationToken ct)
        {
            var seg = _segments[0];
            var buffer = ArrayPool<byte>.Shared.Rent(_o.BufferSize);
            var failures = 0;
            var response = first;
            Interlocked.Increment(ref _active);
            try
            {
                while (true)
                {
                    try
                    {
                        if (response is null)
                        {
                            // No ranges: a dropped connection means starting the file over.
                            lock (_lock) seg.Position = 0;
                            Interlocked.Exchange(ref _downloaded, 0);
                            response = await SendAsync(0, -1, ct);
                            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
                                ThrowForStatus(response);
                        }
                        using (response)
                        {
                            await CopyResponseAsync(response, seg, buffer, ct);
                        }
                        response = null;

                        long written;
                        lock (_lock) written = seg.Position;
                        if (_state.TotalSize > 0 && written < _state.TotalSize)
                            throw new IOException("Connection closed before the whole file arrived.");

                        if (_state.TotalSize <= 0)
                        {
                            // Unknown length: the file (written sequentially from 0) is exactly what arrived.
                            _state.TotalSize = written;
                            lock (_lock) seg.End = written;
                        }
                        return;
                    }
                    catch (Exception ex) when (IsTransient(ex, ct))
                    {
                        response?.Dispose();
                        response = null;
                        if (++failures > _o.MaxRetries)
                            throw new IOException($"Download failed {failures} times in a row: {ex.Message}", ex);
                        await Task.Delay(Backoff(failures), ct);
                    }
                }
            }
            finally
            {
                response?.Dispose();
                Interlocked.Decrement(ref _active);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ── Completion, state, progress ────────────────────────────────────

        private void Complete()
        {
            lock (_lock)
            {
                var have = _segments.Sum(s => s.Position - s.Start);
                if (_state.TotalSize > 0 && have != _state.TotalSize)
                    throw new IOException($"Download incomplete: {have} of {_state.TotalSize} bytes.");
            }

            SparseFile.TrySetSparse(_file!, false);
            _file!.Dispose();
            _file = null;

            File.Move(_partPath, _targetPath, overwrite: true);
            try { File.Delete(DownloadState.StatePath(_targetPath)); } catch { /* best-effort */ }

            _progress?.Report(Snapshot(0, TimeSpan.Zero));
        }

        private void SaveState()
        {
            if (!_state.RangeSupported) return; // nothing resumable
            lock (_lock)
            {
                _state.Segments = _segments
                    .OrderBy(s => s.Start)
                    .Select(s => new SegmentRecord { Start = s.Start, End = s.End, Downloaded = s.Position - s.Start })
                    .ToList();
            }
            _state.Save(_targetPath);
        }

        private async Task ReportLoopAsync(CancellationToken stop)
        {
            var clock = Stopwatch.StartNew();
            var lastBytes = Interlocked.Read(ref _downloaded);
            var lastTime = 0.0;
            var lastSave = 0.0;
            double ema = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(_o.ProgressInterval, stop);
                    var now = clock.Elapsed.TotalSeconds;
                    var bytes = Interlocked.Read(ref _downloaded);
                    var instant = (bytes - lastBytes) / Math.Max(now - lastTime, 0.001);
                    ema = ema == 0 ? instant : 0.3 * instant + 0.7 * ema;
                    lastBytes = bytes;
                    lastTime = now;

                    TimeSpan? eta = null;
                    if (_state.TotalSize > 0 && ema > 1)
                        eta = TimeSpan.FromSeconds(Math.Min((_state.TotalSize - bytes) / ema, TimeSpan.FromDays(30).TotalSeconds));
                    _progress?.Report(Snapshot(ema, eta));

                    if (now - lastSave >= _o.StateSaveInterval.TotalSeconds)
                    {
                        lastSave = now;
                        try { SaveState(); } catch { /* disk hiccup: try again next tick */ }
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        private SegmentedProgress Snapshot(double speed, TimeSpan? eta)
        {
            List<SegmentSnapshot> segs;
            lock (_lock)
            {
                segs = _segments.OrderBy(s => s.Start)
                    .Select(s => new SegmentSnapshot(s.Start, s.End == long.MaxValue ? s.Position : s.End, s.Position, s.Active))
                    .ToList();
            }
            return new SegmentedProgress(Interlocked.Read(ref _downloaded), _state.TotalSize, speed, eta, Volatile.Read(ref _active), segs);
        }

        // ── HTTP helpers ───────────────────────────────────────────────────

        private async Task<HttpResponseMessage> SendWithRetryAsync(long from, long to, CancellationToken ct)
        {
            var failures = 0;
            while (true)
            {
                try
                {
                    var response = await SendAsync(from, to, ct);
                    if ((int)response.StatusCode >= 400)
                    {
                        try { ThrowForStatus(response); }
                        finally { response.Dispose(); }
                    }
                    return response;
                }
                catch (Exception ex) when (IsTransient(ex, ct))
                {
                    if (++failures > _o.MaxRetries) throw;
                    await Task.Delay(Backoff(failures), ct);
                }
            }
        }

        /// <param name="to">Inclusive end, or -1 for no Range header.</param>
        private async Task<HttpResponseMessage> SendAsync(long from, long to, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _source.Url);
            foreach (var (name, value) in _source.Headers)
            {
                if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            request.Headers.AcceptEncoding.Clear();
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (to >= 0) request.Headers.Range = new RangeHeaderValue(from, to);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_o.StallTimeout);
            try
            {
                return await _owner._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The server did not answer in time.");
            }
        }

        private void CheckRangeResponse(HttpResponseMessage response, long expectedStart)
        {
            if ((int)response.StatusCode >= 400) ThrowForStatus(response);

            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new RemoteFileChangedException("The server stopped honoring byte ranges.");

            if (ParseContentRange(response) is not { } range || range.Start != expectedStart)
                throw new RemoteFileChangedException("The server returned an unexpected byte range.");
            if (range.Total > 0 && _state.TotalSize > 0 && range.Total != _state.TotalSize)
                throw new RemoteFileChangedException($"The remote file size changed ({_state.TotalSize} → {range.Total} bytes).");
        }

        private static void ThrowForStatus(HttpResponseMessage response)
        {
            var status = response.StatusCode;
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
                throw new DownloadUrlExpiredException(status);
            if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
                throw new RemoteFileChangedException("The server rejected the byte range (416); the file changed.");
            throw new HttpRequestException($"HTTP {(int)status} {status}", null, status);
        }

        private async Task<int> ReadWithStallTimeoutAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(_o.StallTimeout);
            try
            {
                return await stream.ReadAsync(buffer.AsMemory(0, _o.BufferSize), stallCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data for {_o.StallTimeout.TotalSeconds:0}s.");
            }
        }

        private static (long Start, long End, long Total)? ParseContentRange(HttpResponseMessage response)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || !range.HasRange || range.From is null || range.To is null) return null;
            return (range.From.Value, range.To.Value, range.Length ?? -1);
        }

        /// <summary>Network trouble worth retrying, as opposed to cancellation, expired links or a changed file.</summary>
        private static bool IsTransient(Exception ex, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return false;
            return ex switch
            {
                DownloadUrlExpiredException or RemoteFileChangedException => false,
                HttpRequestException { StatusCode: { } status } =>
                    (int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout,
                HttpRequestException or IOException or TimeoutException => !IsDiskFull(ex),
                OperationCanceledException => true, // internal timeouts, not the caller's token
                _ => false,
            };
        }

        private static bool IsDiskFull(Exception ex) =>
            ex is IOException io && (io.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL

        private TimeSpan Backoff(int failures)
        {
            var seconds = _o.InitialBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 10));
            return TimeSpan.FromSeconds(Math.Min(seconds, _o.MaxBackoff.TotalSeconds));
        }
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
    }
}
