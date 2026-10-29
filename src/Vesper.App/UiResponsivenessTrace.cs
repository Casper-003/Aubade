using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Vesper.App;

// Optional developer trace: measures queue latency, not frame rate. Contains no library paths or song data.
internal sealed class UiResponsivenessTrace : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly string path;
    private readonly Timer timer;
    private readonly object sync = new();
    private readonly List<double> delays = [];
    private readonly int initialGen0 = GC.CollectionCount(0);
    private long lastWrite = Stopwatch.GetTimestamp();
    private int pending, writing, disposed;
    private double peak;
    private long samples;
    private double lastCpuMilliseconds;
    private readonly Func<object>? contextProvider;
    private object? latestContext;
    private readonly List<object> stalls = [];
    private long lastContextAt;

    public UiResponsivenessTrace(DispatcherQueue dispatcher, string path, Func<object>? contextProvider = null)
    {
        this.dispatcher = dispatcher; this.path = path;
        this.contextProvider = contextProvider;
        using var process = Process.GetCurrentProcess();
        lastCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
        timer = new Timer(Poll, null, 32, 32);
    }
    private void Poll(object? state)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
        {
            long sent = Stopwatch.GetTimestamp();
            if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                double delay = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                object? context = null;
                if (contextProvider is not null && (delay > 50 || Stopwatch.GetElapsedTime(lastContextAt).TotalSeconds >= 1))
                {
                    try { context = contextProvider(); } catch (Exception) { }
                    lastContextAt = Stopwatch.GetTimestamp();
                }
                lock (sync)
                {
                    delays.Add(delay); peak = Math.Max(peak, delay); samples++;
                    if (context is not null) latestContext = context;
                    if (delay > 50)
                    {
                        stalls.Add(new { Utc = DateTimeOffset.UtcNow, QueueMilliseconds = Math.Round(delay, 2), Context = latestContext });
                        if (stalls.Count > 64) stalls.RemoveAt(0);
                    }
                }
                Interlocked.Exchange(ref pending, 0);
            })) Interlocked.Exchange(ref pending, 0);
        }
        if (Stopwatch.GetElapsedTime(lastWrite).TotalSeconds >= 5 && Interlocked.CompareExchange(ref writing, 1, 0) == 0)
        {
            try
            {
                double[] window; double maximum; long count; object? context; object[] recentStalls;
                lock (sync) { window = delays.ToArray(); delays.Clear(); maximum = peak; count = samples; context = latestContext; recentStalls = stalls.ToArray(); }
                Array.Sort(window);
                using var process = Process.GetCurrentProcess();
                double cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                var snapshot = new
                {
                    UpdatedUtc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
                    Kind = "UI dispatcher queue latency; not rendering frame time", PollMilliseconds = 32,
                    TotalSamples = count, PeakQueueMilliseconds = Math.Round(maximum, 2),
                    WindowSamples = window.Length,
                    WindowP95Milliseconds = window.Length == 0 ? 0 : Math.Round(window[(int)((window.Length - 1) * .95)], 2),
                    WindowOver50ms = window.Count(value => value > 50), WindowOver100ms = window.Count(value => value > 100),
                    Gen0CollectionsSinceStart = GC.CollectionCount(0) - initialGen0,
                    WindowProcessCpuMilliseconds = Math.Round(cpuMilliseconds - lastCpuMilliseconds, 2),
                    Context = context, RecentStalls = recentStalls
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, true);
                string history = path + ".history.jsonl";
                if (File.Exists(history) && new FileInfo(history).Length > 5 * 1024 * 1024) File.Move(history, history + ".previous", true);
                File.AppendAllText(history, JsonSerializer.Serialize(snapshot) + Environment.NewLine);
                lastCpuMilliseconds = cpuMilliseconds;
            }
            catch (Exception) { /* Diagnostics never interrupt the app. */ }
            finally { lastWrite = Stopwatch.GetTimestamp(); Interlocked.Exchange(ref writing, 0); }
        }
    }
    public void Dispose() { Interlocked.Exchange(ref disposed, 1); timer.Dispose(); }
}
