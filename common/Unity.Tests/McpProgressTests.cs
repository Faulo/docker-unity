using System.Collections.Concurrent;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Unity.Tests;

public sealed class McpProgressTests {
    [TestCase(nameof(UnityMcpTools.RunTestsAsync), new[] { "projectRoot", "modes" })]
    [TestCase(nameof(UnityMcpTools.ExecuteMethodAsync), new[] { "projectRoot", "method", "arguments" })]
    [TestCase(nameof(UnityMcpTools.BuildAndServeWebGlAsync), new[] { "projectRoot" })]
    public void ProgressReporterIsInjectedWithoutChangingToolSchema(string methodName, string[] expectedProperties) {
        var method = typeof(UnityMcpTools).GetMethod(methodName)
                     ?? throw new InvalidOperationException($"Could not find {methodName}.");
        var target = new UnityMcpTools(null!, null!);
        var tool = McpServerTool.Create(method, target);

        string[] properties = tool.ProtocolTool.InputSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        var progressParameters = method.GetParameters()
            .Where(parameter => parameter.ParameterType == typeof(IProgress<ProgressNotificationValue>))
            .ToArray();

        using (Assert.EnterMultipleScope()) {
            Assert.That(progressParameters, Has.Length.EqualTo(1));
            Assert.That(properties, Is.EqualTo(expectedProperties));
            Assert.That(properties, Does.Not.Contain("progress"));
        }
    }

    [Test]
    public void ProjectInformationDoesNotAcceptProgress() {
        var method = typeof(UnityMcpTools).GetMethod(nameof(UnityMcpTools.ProjectInfoAsync))
                     ?? throw new InvalidOperationException("Could not find ProjectInfoAsync.");

        Assert.That(
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(IProgress<ProgressNotificationValue>)),
            Is.False);
    }

    [Test]
    public async Task ReportsImmediatePhasesWithMonotonicSequenceAndNoTotal() {
        var destination = new RecordingProgress();
        await using (var progress = new UnityMcpProgress(destination, CancellationToken.None)) {
            progress.ReportPhase("Validating Unity project");
            progress.ReportPhase("Validating Unity project");
            progress.ReportPhase("Preparing or reusing Unity worker");
        }

        var values = destination.Snapshot();
        using (Assert.EnterMultipleScope()) {
            Assert.That(values.Select(value => value.Message), Is.EqualTo(new[] { "Validating Unity project", "Preparing or reusing Unity worker" }));
            Assert.That(values.Select(value => value.Progress), Is.EqualTo(new[] { 1F, 2F }));
            Assert.That(values.Select(value => value.Total), Is.All.Null);
        }
    }

    [Test]
    public async Task EmitsHeartbeatOnlyAfterAFullQuietIntervalAndResetsOnPhaseChange() {
        var timer = new ManualProgressTimer();
        var destination = new RecordingProgress();
        await using var progress = new UnityMcpProgress(
            destination,
            CancellationToken.None,
            TimeSpan.FromMinutes(1),
            timer.Elapsed,
            timer.DelayAsync);
        progress.ReportPhase("Building WebGL - step 2 of 3");

        await timer.AdvanceNextDelayAsync(TimeSpan.FromSeconds(30));
        await timer.WaitForScheduledDelayAsync();
        progress.ReportPhase("Publishing WebGL build - step 3 of 3");
        await timer.AdvanceNextDelayAsync(TimeSpan.FromSeconds(30));
        await timer.WaitForScheduledDelayAsync();
        Assert.That(destination.Snapshot(), Has.Length.EqualTo(2), "A recent phase transition must suppress the heartbeat.");

        await timer.AdvanceNextDelayAsync(TimeSpan.FromSeconds(30));
        await destination.WaitForCountAsync(3);

        var heartbeat = destination.Snapshot()[2];
        using (Assert.EnterMultipleScope()) {
            Assert.That(heartbeat.Progress, Is.EqualTo(3F));
            Assert.That(heartbeat.Total, Is.Null);
            Assert.That(heartbeat.Message, Is.EqualTo("Publishing WebGL build - step 3 of 3 (1m 30s elapsed)"));
        }
    }

    [Test]
    public async Task StopsReportingAfterCompletionOrCancellation() {
        var completedDestination = new RecordingProgress();
        var completed = new UnityMcpProgress(completedDestination, CancellationToken.None);
        completed.ReportPhase("Running Unity tests");
        await completed.DisposeAsync();
        completed.ReportPhase("Parsing Unity test result");

        using var stopping = new CancellationTokenSource();
        var cancelledDestination = new RecordingProgress();
        await using var cancelled = new UnityMcpProgress(cancelledDestination, stopping.Token);
        cancelled.ReportPhase("Invoking Unity editor method");
        stopping.Cancel();
        cancelled.ReportPhase("Preparing method result");

        using (Assert.EnterMultipleScope()) {
            Assert.That(completedDestination.Snapshot().Select(value => value.Message), Is.EqualTo(new[] { "Running Unity tests" }));
            Assert.That(cancelledDestination.Snapshot().Select(value => value.Message), Is.EqualTo(new[] { "Invoking Unity editor method" }));
        }
    }

    [TestCase(0, 0, 0, "0m 00s")]
    [TestCase(0, 18, 0, "18m 00s")]
    [TestCase(1, 2, 3, "1h 02m 03s")]
    public void FormatsElapsedTime(int hours, int minutes, int seconds, string expected) =>
        Assert.That(UnityMcpProgress.FormatElapsed(new TimeSpan(hours, minutes, seconds)), Is.EqualTo(expected));
}

sealed class RecordingProgress : IProgress<ProgressNotificationValue> {
    readonly SemaphoreSlim changed = new(0);
    readonly ConcurrentQueue<ProgressNotificationValue> values = new();

    public void Report(ProgressNotificationValue value) {
        values.Enqueue(value);
        changed.Release();
    }

    internal ProgressNotificationValue[] Snapshot() => values.ToArray();

    internal async Task WaitForCountAsync(int count) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (values.Count < count) {
            await changed.WaitAsync(timeout.Token);
        }
    }
}

sealed class ManualProgressTimer {
    readonly ConcurrentQueue<DelayRequest> requests = new();
    readonly SemaphoreSlim scheduled = new(0);
    TimeSpan elapsed;

    internal TimeSpan Elapsed() => elapsed;

    internal Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
        var request = new DelayRequest(delay, cancellationToken);
        requests.Enqueue(request);
        scheduled.Release();
        return request.task;
    }

    internal async Task AdvanceNextDelayAsync(TimeSpan elapsedBy) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await scheduled.WaitAsync(timeout.Token);
        Assert.That(requests.TryDequeue(out var request), Is.True);
        elapsed += elapsedBy;
        request!.Complete();
    }

    internal async Task WaitForScheduledDelayAsync() {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await scheduled.WaitAsync(timeout.Token);
        scheduled.Release();
    }

    sealed class DelayRequest {
        readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration registration;

        internal DelayRequest(TimeSpan delay, CancellationToken cancellationToken) {
            _ = delay;
            registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        internal Task task {
            get => completion.Task;
        }

        internal void Complete() {
            registration.Dispose();
            completion.TrySetResult();
        }
    }
}