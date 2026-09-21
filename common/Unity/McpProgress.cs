using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol;

namespace Unity;

sealed class UnityMcpProgress : IAsyncDisposable {
    internal static readonly TimeSpan defaultHeartbeatInterval = TimeSpan.FromMinutes(1);

    readonly Func<TimeSpan, CancellationToken, Task> delay;
    readonly IProgress<ProgressNotificationValue> destination;
    readonly Func<TimeSpan> elapsed;
    readonly object gate = new();
    readonly TimeSpan heartbeatInterval;
    readonly Task heartbeatTask;
    readonly CancellationTokenSource stopping;
    int disposed;
    TimeSpan lastReport;
    string? phase;
    int sequence;

    internal UnityMcpProgress(
        IProgress<ProgressNotificationValue> destination,
        CancellationToken cancellationToken,
        TimeSpan? heartbeatInterval = null,
        Func<TimeSpan>? elapsed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) {
        this.destination = destination;
        this.heartbeatInterval = heartbeatInterval ?? defaultHeartbeatInterval;
        if (this.heartbeatInterval <= TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "The heartbeat interval must be positive.");
        }

        if (elapsed is null) {
            var stopwatch = Stopwatch.StartNew();
            this.elapsed = () => stopwatch.Elapsed;
        } else {
            this.elapsed = elapsed;
        }

        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lastReport = this.elapsed();
        heartbeatTask = RunHeartbeatAsync();
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) {
            return;
        }

        await stopping.CancelAsync();
        try {
            await heartbeatTask;
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
        } finally {
            stopping.Dispose();
        }
    }

    internal void ReportPhase(string message) {
        if (stopping.IsCancellationRequested || Volatile.Read(ref disposed) != 0) {
            return;
        }

        lock (gate) {
            if (stopping.IsCancellationRequested || disposed != 0 || phase == message) {
                return;
            }

            phase = message;
            lastReport = elapsed();
            ReportLocked(message);
        }
    }

    async Task RunHeartbeatAsync() {
        var nextDelay = heartbeatInterval;
        while (true) {
            await delay(nextDelay, stopping.Token);
            lock (gate) {
                if (stopping.IsCancellationRequested || disposed != 0) {
                    return;
                }

                var now = elapsed();
                var quiet = now - lastReport;
                if (phase is null || quiet < heartbeatInterval) {
                    nextDelay = phase is null ? heartbeatInterval : heartbeatInterval - quiet;
                    continue;
                }

                ReportLocked($"{phase} ({FormatElapsed(now)} elapsed)");
                lastReport = now;
                nextDelay = heartbeatInterval;
            }
        }
    }

    void ReportLocked(string message) =>
        destination.Report(new ProgressNotificationValue { Progress = ++sequence, Message = message });

    internal static string FormatElapsed(TimeSpan elapsed) {
        if (elapsed < TimeSpan.Zero) {
            elapsed = TimeSpan.Zero;
        }

        int hours = (int)elapsed.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s");
    }
}