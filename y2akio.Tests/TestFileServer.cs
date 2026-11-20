using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace y2akio.Tests;

/// <summary>What the server should do wrong for one request.</summary>
public sealed record Fault(
    int? Status = null,          // answer with this status and no body
    long? DropAfterBytes = null, // send this many body bytes, then kill the connection
    long? StallAfterBytes = null,// send this many body bytes, then go silent until the client gives up
    int DelayPerChunkMs = 0);    // slow server

public sealed record RequestInfo(int Index, string PathAndQuery, long From, long? To);

/// <summary>
/// Minimal HTTP file server on localhost for the download engine tests: optional Range support,
/// ETag, and per-request fault injection (status codes, dropped / stalled connections, slowness).
/// </summary>
public sealed class TestFileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private int _requests;
    private int _concurrent;
    private int _maxConcurrent;
    private long _bytesServed;

    public byte[] Data { get; set; }
    public string? ETag { get; set; } = "\"v1\"";
    public bool SupportRanges { get; set; } = true;
    public bool SendContentLength { get; set; } = true;
    public Func<RequestInfo, Fault?>? Faults { get; set; }

    public string BaseUrl { get; }
    public string Url => BaseUrl + "file.bin";
    public int Requests => Volatile.Read(ref _requests);
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public TestFileServer(byte[] data)
    {
        Data = data;
        var port = FreePort();
        BaseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _maxConcurrent, 0);
        Interlocked.Exchange(ref _bytesServed, 0);
    }

    public static byte[] RandomData(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var index = Interlocked.Increment(ref _requests) - 1;
        var now = Interlocked.Increment(ref _concurrent);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, now, seen) != seen) { }

        var response = ctx.Response;
        try
        {
            var data = Data;
            long from = 0, to = data.Length - 1;
            long? requestedTo = null;
            var rangeHeader = ctx.Request.Headers["Range"];
            var ranged = false;
            if (SupportRanges && rangeHeader is not null)
            {
                var m = Regex.Match(rangeHeader, @"bytes=(\d+)-(\d*)");
                if (m.Success)
                {
                    from = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Value.Length > 0) requestedTo = long.Parse(m.Groups[2].Value);
                    to = Math.Min(requestedTo ?? data.Length - 1, data.Length - 1);
                    ranged = true;
                }
            }

            var fault = Faults?.Invoke(new RequestInfo(index, ctx.Request.Url!.PathAndQuery, from, requestedTo));
            if (fault?.Status is { } status)
            {
                response.StatusCode = status;
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            if (ranged && from >= data.Length)
            {
                response.StatusCode = 416;
                response.AddHeader("Content-Range", $"bytes */{data.Length}");
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            if (ETag is not null) response.AddHeader("ETag", ETag);
            if (SupportRanges) response.AddHeader("Accept-Ranges", "bytes");
            if (ranged)
            {
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {from}-{to}/{data.Length}");
            }
            else
            {
                response.StatusCode = 200;
            }

            var length = to - from + 1;
            if (SendContentLength) response.ContentLength64 = length;
            else response.SendChunked = true;

            var output = response.OutputStream;
            long sent = 0;
            const int chunk = 16 * 1024;
            while (sent < length)
            {
                if (fault?.DropAfterBytes is { } drop && sent >= drop)
                {
                    response.Abort();
                    return;
                }
                if (fault?.StallAfterBytes is { } stall && sent >= stall)
                {
                    await output.FlushAsync();
                    try { await Task.Delay(Timeout.Infinite, _stop.Token); } catch { }
                    response.Abort();
                    return;
                }
                if (fault?.DelayPerChunkMs > 0) await Task.Delay(fault.DelayPerChunkMs);

                var n = (int)Math.Min(chunk, length - sent);
                await output.WriteAsync(data.AsMemory((int)(from + sent), n));
                sent += n;
                Interlocked.Add(ref _bytesServed, n);
            }
            response.Close();
        }
        catch
        {
            try { response.Abort(); } catch { }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
