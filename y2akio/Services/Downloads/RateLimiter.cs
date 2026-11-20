using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace y2akio.Services.Downloads;

/// <summary>
/// Token-bucket speed limiter shared by every connection that should count against the same limit
/// (one per job, plus one global). <see cref="BytesPerSecond"/> can change at any time; 0 = unlimited.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _tokens;
    private double _lastRefillSeconds;
    private long _bytesPerSecond;

    public RateLimiter(long bytesPerSecond = 0) => _bytesPerSecond = bytesPerSecond;

    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set
        {
            Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
            lock (_lock) _tokens = 0; // start the new rate from an empty bucket
        }
    }

    /// <summary>Takes <paramref name="bytes"/> from the bucket, waiting when the bucket is in debt.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var limit = BytesPerSecond;
        if (limit <= 0 || bytes <= 0) return;

        double delaySeconds;
        lock (_lock)
        {
            var now = _clock.Elapsed.TotalSeconds;
            // Burst capacity: a quarter second of traffic keeps the rate smooth without stalling small reads.
            var capacity = Math.Max(limit / 4.0, 64 * 1024);
            _tokens = Math.Min(capacity, _tokens + (now - _lastRefillSeconds) * limit);
            _lastRefillSeconds = now;
            _tokens -= bytes;
            delaySeconds = _tokens < 0 ? -_tokens / limit : 0;
        }

        if (delaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySeconds, 5)), ct);
    }
}
