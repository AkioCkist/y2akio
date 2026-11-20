using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using y2akio.Models;
using y2akio.Services.Downloads;

namespace y2akio.Services;

public enum WhenDoneAction { None, Sleep, Shutdown }

/// <summary>
/// The download manager: a persistent queue of <see cref="DownloadJob"/>s with a parallel-download
/// limit, pause / resume / cancel, resume after restart, waiting for the network, automatic retries,
/// a global speed limit, a scheduled start and an optional "when done" action. UI thread only;
/// the downloads themselves run on the thread pool.
/// </summary>
public sealed class DownloadQueueService
{
    public static DownloadQueueService Instance { get; } = new();

    private static readonly string QueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Y2Akio", "downloads.json");

    private const int MaxAutoRetries = 3;
    private static readonly TimeSpan AutoRetryDelay = TimeSpan.FromSeconds(30);
    private const int KeepFinishedJobs = 200;

    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;
    private YtDlpService? _ytDlp;
    private bool _shuttingDown;
    private int _completedSinceArmed;
    private readonly Dictionary<string, (int Count, DateTime At)> _retries = new();

    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    /// <summary>Global speed limit shared by every job (Settings → speed limit).</summary>
    public RateLimiter GlobalLimiter { get; } = new();

    /// <summary>Raised when a job reaches Completed, Failed or Canceled.</summary>
    public event Action<DownloadJob>? JobFinished;

    /// <summary>Raised whenever the number of running / waiting jobs may have changed.</summary>
    public event Action? QueueChanged;

    public DateTime? ScheduledStart { get; private set; }

    private WhenDoneAction _whenDone;
    public WhenDoneAction WhenDone
    {
        get => _whenDone;
        set { _whenDone = value; _completedSinceArmed = 0; }
    }

    public int ActiveCount => Jobs.Count(j => j.Status == DownloadJobStatus.Downloading);
    public int PendingCount => Jobs.Count(j => j.Status is DownloadJobStatus.Queued or DownloadJobStatus.Downloading or DownloadJobStatus.WaitingForNetwork);

    private DownloadQueueService() { }

    // ─── Setup / teardown ────────────────────────────────────────────────────

    public void Initialize(DispatcherQueue dispatcher, YtDlpService ytDlp)
    {
        if (_dispatcher is not null) return;
        _dispatcher = dispatcher;
        _ytDlp = ytDlp;

        ApplySettings();
        AppSettingsService.Instance.SettingsChanged += (_, e) =>
        {
            if (e.DownloadSettingsChanged) dispatcher.TryEnqueue(() => { ApplySettings(); Pump(); });
            if (e.LanguageChanged) dispatcher.TryEnqueue(() => { foreach (var j in Jobs) j.RefreshLabels(); });
        };

        NetworkChange.NetworkAvailabilityChanged += (_, e) => dispatcher.TryEnqueue(() => OnNetworkChanged(e.IsAvailable));

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();

        Load();
        Pump();
    }

    /// <summary>App is closing: stop running jobs (their progress is kept) and persist the queue.</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        foreach (var job in Jobs.Where(j => j.Status == DownloadJobStatus.Downloading))
        {
            job.StopReason = StopReason.Shutdown;
            try { job.Cts?.Cancel(); } catch { /* ignore */ }
        }
        Save();
    }

    private void ApplySettings() =>
        GlobalLimiter.BytesPerSecond = AppSettingsService.Instance.SpeedLimitKBps * 1024L;

    // ─── Public commands ─────────────────────────────────────────────────────

    public DownloadJob Enqueue(DownloadRequest request, string title, string thumbnailUrl, string qualityLabel, string? batchId = null)
    {
        var job = new DownloadJob
        {
            Request = request,
            Title = string.IsNullOrWhiteSpace(title) ? request.Url : title,
            Site = SiteNames.For(request.Url),
            ThumbnailUrl = thumbnailUrl,
            QualityLabel = qualityLabel,
            BatchId = batchId,
            StatusText = LocalizationService.Get("Downloads.Status.Queued"),
        };
        Jobs.Insert(0, job); // newest on top
        Save();
        Pump();
        return job;
    }

    public void Pause(DownloadJob job)
    {
        switch (job.Status)
        {
            case DownloadJobStatus.Downloading:
                job.StopReason = StopReason.Pause;
                job.Cts?.Cancel();
                break;
            case DownloadJobStatus.Queued:
            case DownloadJobStatus.WaitingForNetwork:
                job.Status = DownloadJobStatus.Paused;
                Save();
                Changed();
                break;
        }
    }

    public void Resume(DownloadJob job)
    {
        if (job.Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed or DownloadJobStatus.Canceled)
        {
            _retries.Remove(job.Id);
            job.Error = "";
            job.Status = DownloadJobStatus.Queued;
            Save();
            Pump();
        }
    }

    public void Cancel(DownloadJob job)
    {
        if (job.Status == DownloadJobStatus.Downloading)
        {
            job.StopReason = StopReason.Cancel;
            job.Cts?.Cancel();
            return;
        }
        if (job.Status is DownloadJobStatus.Completed or DownloadJobStatus.Canceled) return;
        DeletePartials(job);
        job.Status = DownloadJobStatus.Canceled;
        job.Percent = 0;
        job.Segments = null;
        job.StatusText = "";
        Save();
        Changed();
    }

    public void Remove(DownloadJob job)
    {
        if (job.Status == DownloadJobStatus.Downloading) return;
        if (job.Status != DownloadJobStatus.Completed) DeletePartials(job);
        Jobs.Remove(job);
        Save();
        Changed();
    }

    public void PauseAll()
    {
        foreach (var job in Jobs.ToList()) Pause(job);
    }

    public void ResumeAll()
    {
        foreach (var job in Jobs.Where(j => j.Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed).ToList())
        {
            _retries.Remove(job.Id);
            job.Error = "";
            job.Status = DownloadJobStatus.Queued;
        }
        Save();
        Pump();
    }

    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.Status is DownloadJobStatus.Completed or DownloadJobStatus.Canceled).ToList())
            Jobs.Remove(job);
        Save();
        Changed();
    }

    /// <summary>Holds queued jobs until <paramref name="at"/> (null = start immediately).</summary>
    public void Schedule(DateTime? at)
    {
        ScheduledStart = at is { } t && t > DateTime.Now ? t : null;
        Pump();
        Changed();
    }

    // ─── Scheduling ──────────────────────────────────────────────────────────

    private void Pump()
    {
        if (_dispatcher is null || _ytDlp is null || _shuttingDown) return;
        if (ScheduledStart is { } at && DateTime.Now < at) { Changed(); return; }

        var running = ActiveCount;
        var limit = AppSettingsService.Instance.ParallelDownloads;
        foreach (var job in Jobs.Where(j => j.Status == DownloadJobStatus.Queued).OrderBy(j => j.CreatedAt).ToList())
        {
            if (running >= limit) break;
            running++;
            _ = RunJobAsync(job);
        }
        Changed();
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        job.Status = DownloadJobStatus.Downloading;
        job.Error = "";
        job.StopReason = StopReason.None;
        job.StatusText = LocalizationService.Get("Downloads.Starting");
        var cts = new CancellationTokenSource();
        job.Cts = cts;
        Save();

        var settings = AppSettingsService.Instance;
        var cookiesLeased = false;
        try
        {
            var cookiesPath = "";
            if (YtDlpService.NeedsCookies(job.Request.Url))
            {
                cookiesPath = await settings.AcquireCookiesFileAsync();
                cookiesLeased = cookiesPath.Length > 0;
            }
            job.Request.CookiesPath = cookiesPath;

            // Progress<T> was created on the UI thread, so job updates land there.
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (job.Status != DownloadJobStatus.Downloading) return;
                job.Percent = p.Percent;
                job.StatusText = p.StatusText;
                job.Segments = p.Segments;
                job.SegmentsTotal = p.SegmentsTotal;
            });
            var dispatcher = _dispatcher!;
            var engine = new DownloadEngineOptions
            {
                Enabled = settings.MultiConnectionEnabled,
                MaxConnections = settings.ConnectionsPerFile,
                RateLimiters = new[] { GlobalLimiter, job.Limiter },
                OnPartialFile = path => dispatcher.TryEnqueue(() =>
                {
                    if (!job.PartialFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) job.PartialFiles.Add(path);
                    Save();
                }),
            };
            var request = job.Request;
            var ytDlp = _ytDlp!;

            // Thread pool: the engine's connections must not run their continuations on the UI thread.
            var saved = await Task.Run(() => ytDlp.DownloadAsync(
                request, progress, cts.Token, line => dispatcher.TryEnqueue(() => job.RaiseLog(line)), engine));

            job.SavedPath = saved;
            job.Percent = 100;
            job.Segments = null;
            job.StatusText = "";
            job.CompletedAt = DateTime.Now;
            job.PartialFiles.Clear();
            job.Request.InfoJson = ""; // signed links are useless now; keep downloads.json small
            job.Status = DownloadJobStatus.Completed;
            _retries.Remove(job.Id);
            if (WhenDone != WhenDoneAction.None) _completedSinceArmed++;

            await HistoryService.Instance.AddAsync(new DownloadHistoryItem
            {
                Title = job.Title,
                Kind = job.Request.Kind,
                QualityLabel = job.QualityLabel,
                FilePath = saved,
                FolderPath = job.Request.SaveFolder,
                CompletedAt = DateTime.Now,
            });
            ToastService.ShowDownloadComplete(job.Title, saved);
            JobFinished?.Invoke(job);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            switch (job.StopReason)
            {
                case StopReason.Cancel:
                    DeletePartials(job);
                    job.Percent = 0;
                    job.Segments = null;
                    job.StatusText = "";
                    job.Status = DownloadJobStatus.Canceled;
                    JobFinished?.Invoke(job);
                    break;
                case StopReason.Network:
                    job.StatusText = LocalizationService.Get("Downloads.WaitingNetwork");
                    job.Status = DownloadJobStatus.WaitingForNetwork;
                    break;
                case StopReason.Shutdown:
                    break; // keeps "Downloading" in downloads.json → resumes on next start
                default:
                    job.Status = DownloadJobStatus.Paused;
                    break;
            }
        }
        catch (Exception ex)
        {
            var message = FirstLine(ex.Message);
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                job.StatusText = LocalizationService.Get("Downloads.WaitingNetwork");
                job.Status = DownloadJobStatus.WaitingForNetwork;
            }
            else if (LooksTransient(message) && NextRetry(job) is { } retry)
            {
                // IDM-style: a network-ish failure is retried automatically a few times.
                job.StatusText = string.Format(LocalizationService.Get("Downloads.RetryIn"), (int)AutoRetryDelay.TotalSeconds, retry, MaxAutoRetries);
                job.Status = DownloadJobStatus.WaitingForNetwork;
            }
            else
            {
                job.Error = message;
                job.Status = DownloadJobStatus.Failed;
                JobFinished?.Invoke(job);
            }
        }
        finally
        {
            if (cookiesLeased) settings.ReleaseCookiesFile();
            job.Request.CookiesPath = "";
            job.Cts = null;
            cts.Dispose();
            if (!_shuttingDown)
            {
                Save();
                Pump();
                CheckWhenDone();
            }
        }
    }

    private int? NextRetry(DownloadJob job)
    {
        _retries.TryGetValue(job.Id, out var r);
        if (r.Count >= MaxAutoRetries) return null;
        _retries[job.Id] = (r.Count + 1, DateTime.Now + AutoRetryDelay);
        return r.Count + 1;
    }

    private static bool LooksTransient(string message)
    {
        string[] markers =
        {
            "timed out", "timeout", "connection", "network", "temporarily", "reset", "unreachable",
            "HTTP 5", "HTTP Error 5", "No data for", "Name or service", "getaddrinfo", "too many times",
        };
        return markers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private void OnTimer()
    {
        if (ScheduledStart is { } at && DateTime.Now >= at)
        {
            ScheduledStart = null;
            Pump();
        }

        // Jobs waiting for the network / a retry go back to the queue when it is time.
        if (!NetworkInterface.GetIsNetworkAvailable()) return;
        var requeued = false;
        foreach (var job in Jobs.Where(j => j.Status == DownloadJobStatus.WaitingForNetwork).ToList())
        {
            if (_retries.TryGetValue(job.Id, out var r) && DateTime.Now < r.At) continue;
            job.Status = DownloadJobStatus.Queued;
            requeued = true;
        }
        if (requeued) { Save(); Pump(); }
    }

    private void OnNetworkChanged(bool available)
    {
        if (!available)
        {
            foreach (var job in Jobs.Where(j => j.Status == DownloadJobStatus.Downloading))
            {
                job.StopReason = StopReason.Network;
                job.Cts?.Cancel();
            }
            return;
        }

        foreach (var job in Jobs.Where(j => j.Status == DownloadJobStatus.WaitingForNetwork).ToList())
        {
            _retries.Remove(job.Id);
            job.Status = DownloadJobStatus.Queued;
        }
        Save();
        Pump();
    }

    // ─── "When done" ─────────────────────────────────────────────────────────

    private void CheckWhenDone()
    {
        if (WhenDone == WhenDoneAction.None || _completedSinceArmed == 0 || PendingCount > 0) return;

        var action = WhenDone;
        WhenDone = WhenDoneAction.None;
        Save();
        try
        {
            if (action == WhenDoneAction.Shutdown)
            {
                // 60 s grace period; "shutdown /a" in a terminal cancels it.
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 60 /c \"Y2Akio: downloads finished. Run 'shutdown /a' to cancel.\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                ToastService.ShowInfo(LocalizationService.Get("Downloads.ShutdownSoonTitle"), LocalizationService.Get("Downloads.ShutdownSoon"));
            }
            else if (action == WhenDoneAction.Sleep)
            {
                SetSuspendState(false, false, false);
            }
        }
        catch
        {
            // best-effort power action
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // ─── Files / persistence ─────────────────────────────────────────────────

    private static void DeletePartials(DownloadJob job)
    {
        foreach (var path in job.PartialFiles)
        {
            DownloadState.Delete(path);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
            try { if (File.Exists(path + ".part")) File.Delete(path + ".part"); } catch { /* best-effort */ }
        }
        job.PartialFiles.Clear();
    }

    private void Save()
    {
        try
        {
            var records = Jobs.Select(j => new DownloadJobRecord
            {
                Id = j.Id,
                Request = j.Request,
                Title = j.Title,
                Site = j.Site,
                ThumbnailUrl = j.ThumbnailUrl,
                QualityLabel = j.QualityLabel,
                CreatedAt = j.CreatedAt,
                CompletedAt = j.CompletedAt,
                Status = j.Status,
                Percent = j.Percent,
                StatusText = j.StatusText,
                Error = j.Error,
                SavedPath = j.SavedPath,
                SpeedLimitKBps = j.SpeedLimitKBps,
                PartialFiles = j.PartialFiles.ToList(),
            }).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
            var tmp = QueuePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(records, DownloadQueueJsonContext.Default.ListDownloadJobRecord));
            File.Move(tmp, QueuePath, overwrite: true);
        }
        catch
        {
            // persistence is best-effort
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(QueuePath)) return;
            var records = JsonSerializer.Deserialize(File.ReadAllText(QueuePath), DownloadQueueJsonContext.Default.ListDownloadJobRecord);
            if (records is null) return;

            var resume = AppSettingsService.Instance.ResumeOnStartup;
            var finished = 0;
            foreach (var r in records)
            {
                if (r.Request is null) continue;
                var status = r.Status switch
                {
                    DownloadJobStatus.Downloading or DownloadJobStatus.Queued or DownloadJobStatus.WaitingForNetwork
                        => resume ? DownloadJobStatus.Queued : DownloadJobStatus.Paused,
                    var s => s,
                };
                if (status is DownloadJobStatus.Completed or DownloadJobStatus.Canceled && ++finished > KeepFinishedJobs) continue;

                var job = new DownloadJob
                {
                    Id = r.Id,
                    Request = r.Request,
                    Title = r.Title,
                    Site = r.Site,
                    ThumbnailUrl = r.ThumbnailUrl,
                    QualityLabel = r.QualityLabel,
                    CreatedAt = r.CreatedAt,
                    PartialFiles = r.PartialFiles ?? new List<string>(),
                };
                job.Status = status;
                job.Percent = r.Percent;
                job.StatusText = status == DownloadJobStatus.Completed ? "" : r.StatusText;
                job.Error = r.Error;
                job.SavedPath = r.SavedPath;
                job.CompletedAt = r.CompletedAt;
                job.SpeedLimitKBps = r.SpeedLimitKBps;
                Jobs.Add(job);
            }
        }
        catch
        {
            // corrupt file: start with an empty queue
        }
    }

    private void Changed() => QueueChanged?.Invoke();

    private static string FirstLine(string text)
    {
        var line = (text ?? "").Trim().Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<DownloadJobRecord>))]
internal partial class DownloadQueueJsonContext : JsonSerializerContext
{
}
