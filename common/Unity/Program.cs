using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Unity;

static class Program {
    const long DEFAULT_TIMEOUT_SECONDS = 86_400;
    static readonly TimeSpan terminationGracePeriod = TimeSpan.FromSeconds(10);

    public static async Task<int> Main(string[] args) {
        string executable = ResolveExecutable(
            Environment.ProcessPath,
            Environment.GetCommandLineArgs(),
            ReadKernelExecutableArgument());
        try {
            var command = CommandRouter.Route(executable, args);
            return command.mode == EApplicationMode.SIDECAR
                ? await RunSidecarCommandAsync(command.arguments)
                : await RunUnityAsync(command.arguments, RuntimeCredentials.Resolve());
        } catch (Exception exception) {
            Console.Error.WriteLine($"{executable}: {exception.Message}");
            return 1;
        }
    }

    internal static string ResolveExecutable(
        string? processPath,
        IReadOnlyList<string> commandLineArguments,
        string? kernelExecutableArgument = null) {
        string path = kernelExecutableArgument
                      ?? (commandLineArguments.Count > 0
                          ? commandLineArguments[0]
                          : processPath ?? "unity");
        return Path.GetFileNameWithoutExtension(path);
    }

    static string? ReadKernelExecutableArgument() {
        if (!OperatingSystem.IsLinux()) {
            return null;
        }

        try {
            byte[] commandLine = File.ReadAllBytes("/proc/self/cmdline");
            int terminator = Array.IndexOf(commandLine, (byte)0);
            return Encoding.UTF8.GetString(commandLine, 0, terminator >= 0 ? terminator : commandLine.Length);
        } catch {
            return null;
        }
    }

    static async Task<int> RunSidecarCommandAsync(string[] args) {
        if (args.Length == 0) {
            return await RunSupervisorAsync(RuntimeCredentials.Resolve());
        }

        if (args.Length == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase)) {
            return PrintStatus();
        }

        if (args.Length == 1 && args[0].Equals("health", StringComparison.OrdinalIgnoreCase)) {
            return await CheckHealthAsync() ? 0 : 1;
        }

        if (args.Length == 2 && args[0].Equals("probe-project", StringComparison.Ordinal)) {
            return ProjectProbe.Run(args[1]);
        }

        Console.Error.WriteLine("Usage: unity-sidecar [status|health]");
        return 2;
    }

    static async Task<int> RunUnityAsync(string[] args, RuntimeCredentials credentials) {
        long timeoutSeconds = ParseTimeout();
        var store = new StateStore();
        store.EnsureDirectories();

        string invocationId = Guid.NewGuid().ToString("N")[..12];
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = ResolveDeadline(startedAt, timeoutSeconds);
        string command = SanitizeCommand(args);
        string workingDirectory = SanitizeText(Environment.CurrentDirectory, 512);
        ChildProcess? child = null;
        InvocationRecord? record = null;

        using var cancellation = new CancellationTokenSource();
        using var signals = SignalHandlers.Register(cancellation);

        try {
            string[] composerArguments = ["exec", "unity-command", "--", .. args];
            child = ProcessTree.StartComposer(composerArguments, invocationId, credentials);
            record = new InvocationRecord {
                id = invocationId,
                command = command,
                workingDirectory = workingDirectory,
                startedAtUtc = startedAt,
                timeoutSeconds = timeoutSeconds,
                deadlineUtc = deadline,
                launcher = ProcessIdentity.Current(),
                rootProcess = ProcessIdentity.FromProcess(child.process),
                processGroupId = child.processGroupId,
                jobName = child.jobName
            };
            store.WriteActive(record);
            store.WriteEvent(LifecycleEvent.Start(record));
            child.Resume();

            var processExit = child.process.WaitForExitAsync();
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            var timeoutTask = timeoutSeconds == 0
                ? Task.Delay(Timeout.InfiniteTimeSpan)
                : WaitForTimeoutAsync(timeoutSeconds);
            var completed = await Task.WhenAny(processExit, cancellationTask, timeoutTask);

            if (completed == timeoutTask) {
                store.WriteEvent(LifecycleEvent.Finish("TIMEOUT", record, 124));
                await child.TerminateAsync(terminationGracePeriod);
                return 124;
            }

            if (completed == cancellationTask) {
                store.WriteEvent(LifecycleEvent.Finish("CANCELLED", record, 130));
                await child.TerminateAsync(terminationGracePeriod);
                return 130;
            }

            await processExit;
            int exitCode = child.process.ExitCode;
            store.WriteEvent(LifecycleEvent.Finish(exitCode == 0 ? "END" : "FAILED", record, exitCode));
            return exitCode;
        } catch (Exception exception) {
            if (record is not null) {
                store.WriteEvent(LifecycleEvent.Finish("FAILED", record, 1, exception.Message));
            } else {
                store.WriteEvent(new LifecycleEvent {
                    kind = "FAILED",
                    id = invocationId,
                    command = command,
                    workingDirectory = workingDirectory,
                    startedAtUtc = startedAt,
                    finishedAtUtc = DateTimeOffset.UtcNow,
                    exitCode = 1,
                    message = SanitizeText(exception.Message, 160)
                });
            }

            Console.Error.WriteLine($"unity: {exception.Message}");
            return 1;
        } finally {
            if (record is not null) {
                store.RemoveActive(record.id);
            }

            child?.Dispose();
        }
    }

    static async Task<int> RunSupervisorAsync(RuntimeCredentials credentials) {
        bool mcpEnabled = McpActivation.Parse();
        var store = new StateStore();
        store.EnsureDirectories();

        foreach (var record in store.ReadActive()) {
            await ReconcileOrphanAsync(store, record);
        }

        if (!store.CanWrite() || !await ProbeComposerAsync()) {
            Console.Error.WriteLine("unity-sidecar: readiness prerequisites failed");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        using var signals = SignalHandlers.Register(cancellation);
        McpServerRuntime? mcp = null;
        var lastReconciliation = DateTimeOffset.MinValue;
        int exitCode = 0;

        try {
            if (mcpEnabled) {
                mcp = await McpServerRuntime.StartAsync(credentials, cancellation.Token);
            }

            var ready = new ReadyRecord { supervisor = ProcessIdentity.Current(), startedAtUtc = DateTimeOffset.UtcNow, mcpEnabled = mcpEnabled, mcpReady = mcp is not null };
            store.WriteReady(ready);
            Console.WriteLine(
                $"READY os={RuntimeInformation.OSDescription.Replace(' ', '_')} unity={await ComposerVersionAsync()} mcp={(mcpEnabled ? "http://0.0.0.0:8080/mcp" : "disabled")}");

            while (!cancellation.IsCancellationRequested) {
                if (mcp is not null && mcp.completion.IsCompleted) {
                    Console.Error.WriteLine("unity-sidecar: MCP server stopped unexpectedly");
                    exitCode = 1;
                    cancellation.Cancel();
                    break;
                }

                store.DrainEvents(WriteLifecycleEvent);
                if (DateTimeOffset.UtcNow - lastReconciliation >= TimeSpan.FromSeconds(2)) {
                    foreach (var record in store.ReadActive()) {
                        await ReconcileOrphanAsync(store, record);
                    }

                    lastReconciliation = DateTimeOffset.UtcNow;
                }

                await Task.Delay(200, cancellation.Token);
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } finally {
            if (mcp is not null) {
                await mcp.StopAsync();
            }

            foreach (var record in store.ReadActive()) {
                if (record.rootProcess.IsAlive()) {
                    WriteLifecycleEvent(LifecycleEvent.Finish("CANCELLED", record, 130, "sidecar stopping"));
                    await ProcessTree.TerminateRecordAsync(record, terminationGracePeriod);
                }

                store.RemoveActive(record.id);
            }

            store.RemoveReady();
            store.DrainEvents(WriteLifecycleEvent);
            if (mcp is not null) {
                await mcp.DisposeAsync();
            }
        }

        return exitCode;
    }

    static int PrintStatus() {
        var store = new StateStore();
        var ready = store.ReadReady();
        bool healthy = ready is not null
                       && ready.supervisor.IsAlive()
                       && store.CanWrite()
                       && (!ready.mcpEnabled || ready.mcpReady);
        var active = store.ReadActive()
            .Where(record => record.launcher.IsAlive() || record.rootProcess.IsAlive())
            .OrderBy(record => record.startedAtUtc)
            .ToList();

        Console.WriteLine(healthy ? "healthy" : "unhealthy");
        Console.WriteLine($"mcp: {(ready?.mcpEnabled == true ? ready.mcpReady ? "ready" : "unready" : "disabled")}");
        Console.WriteLine($"active calls: {active.Count}");
        foreach (var record in active) {
            var elapsed = DateTimeOffset.UtcNow - record.startedAtUtc;
            string timeout = record.timeoutSeconds == 0 ? "disabled" : FormatDuration(TimeSpan.FromSeconds(record.timeoutSeconds));
            Console.WriteLine($"{record.id} {record.command} pid={record.rootProcess.pid} elapsed={FormatDuration(elapsed)} timeout={timeout} cwd={record.workingDirectory}");
        }

        return healthy ? 0 : 1;
    }

    static async Task<bool> CheckHealthAsync() {
        try {
            var store = new StateStore();
            var ready = store.ReadReady();
            return ready is not null
                   && ready.supervisor.IsAlive()
                   && store.CanWrite()
                   && await ProbeComposerAsync()
                   && (!ready.mcpEnabled || (ready.mcpReady && await McpServerRuntime.CheckHealthAsync()));
        } catch {
            return false;
        }
    }

    static async Task ReconcileOrphanAsync(StateStore store, InvocationRecord record) {
        if (record.launcher.IsAlive()) {
            return;
        }

        WriteLifecycleEvent(LifecycleEvent.Finish("ORPHANED", record, null, "launcher disappeared"));
        if (record.rootProcess.IsAlive()) {
            await ProcessTree.TerminateRecordAsync(record, terminationGracePeriod);
        }

        store.RemoveActive(record.id);
    }

    static void WriteLifecycleEvent(LifecycleEvent lifecycleEvent) {
        string duration = lifecycleEvent.finishedAtUtc.HasValue
            ? $" duration={FormatDuration(lifecycleEvent.finishedAtUtc.Value - lifecycleEvent.startedAtUtc)}"
            : string.Empty;
        string exit = lifecycleEvent.exitCode.HasValue ? $" exit={lifecycleEvent.exitCode.Value}" : string.Empty;
        string timeout = lifecycleEvent.timeoutSeconds.HasValue
            ? $" timeout={(lifecycleEvent.timeoutSeconds.Value == 0 ? "disabled" : lifecycleEvent.timeoutSeconds.Value + "s")}"
            : string.Empty;
        string message = string.IsNullOrEmpty(lifecycleEvent.message) ? string.Empty : $" message={SanitizeText(lifecycleEvent.message, 160)}";
        Console.WriteLine(
            $"{lifecycleEvent.kind} id={lifecycleEvent.id} pid={lifecycleEvent.pid} command={lifecycleEvent.command} cwd={lifecycleEvent.workingDirectory}{timeout}{exit}{duration}{message}");
    }

    static long ParseTimeout() =>
        ParseTimeout(Environment.GetEnvironmentVariable("UNITY_CALL_TIMEOUT"));

    internal static long ParseTimeout(string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return DEFAULT_TIMEOUT_SECONDS;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            || seconds < 0
            || seconds > (long)TimeSpan.MaxValue.TotalSeconds) {
            throw new ArgumentException($"Invalid UNITY_CALL_TIMEOUT: {SanitizeText(value, 80)}");
        }

        return seconds;
    }

    static async Task WaitForTimeoutAsync(long timeoutSeconds) {
        var remaining = TimeSpan.FromSeconds(timeoutSeconds);
        var maximumDelay = TimeSpan.FromDays(30);
        while (remaining > TimeSpan.Zero) {
            var delay = remaining < maximumDelay ? remaining : maximumDelay;
            await Task.Delay(delay);
            remaining -= delay;
        }
    }

    internal static DateTimeOffset? ResolveDeadline(DateTimeOffset startedAt, long timeoutSeconds) {
        if (timeoutSeconds == 0) {
            return null;
        }

        try {
            return startedAt.AddSeconds(timeoutSeconds);
        } catch (ArgumentOutOfRangeException) {
            throw new ArgumentException("UNITY_CALL_TIMEOUT exceeds the supported deadline range.");
        }
    }

    internal static string SanitizeCommand(string[] args) {
        int index = Array.FindIndex(args, argument => argument.Equals("exec", StringComparison.OrdinalIgnoreCase));
        int commandIndex = index + 1;
        while (commandIndex > 0 && commandIndex < args.Length && args[commandIndex] == "--") {
            commandIndex++;
        }

        string value = index >= 0 && commandIndex < args.Length ? args[commandIndex] : "composer";
        string sanitized = new(value.Take(64)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_')
            .ToArray());
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    internal static string SanitizeText(string? value, int maximumLength) {
        if (string.IsNullOrEmpty(value)) {
            return "-";
        }

        string normalized = value.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    internal static string FormatDuration(TimeSpan duration) {
        if (duration < TimeSpan.Zero) {
            duration = TimeSpan.Zero;
        }

        return $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    static ProcessStartInfo ComposerStartInfo(bool redirectOutput) {
        ProcessStartInfo startInfo;
        string composerProject;
        string composerVendorDirectory;
        if (OperatingSystem.IsWindows()) {
            startInfo = new ProcessStartInfo("php.exe");
            startInfo.ArgumentList.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ComposerSetup", "bin", "composer.phar"));
            composerProject = @"C:\unity\composer.json";
            composerVendorDirectory = @"C:\unity\vendor";
        } else {
            startInfo = new ProcessStartInfo("composer");
            composerProject = "/unity/composer.json";
            composerVendorDirectory = "/unity/vendor";
        }

        startInfo.Environment["COMPOSER"] = composerProject;
        startInfo.Environment["COMPOSER_VENDOR_DIR"] = composerVendorDirectory;
        startInfo.WorkingDirectory = Environment.CurrentDirectory;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = redirectOutput;
        startInfo.RedirectStandardError = redirectOutput;
        startInfo.CreateNoWindow = redirectOutput;
        return startInfo;
    }

    internal static ProcessStartInfo ComposerStartInfo(
        string[] args,
        bool redirectOutput = false,
        RuntimeCredentials? credentials = null) {
        var startInfo = ComposerStartInfo(redirectOutput);
        credentials?.ApplyTo(startInfo);
        foreach (string argument in args) {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    static async Task<bool> ProbeComposerAsync() {
        try {
            using var process = Process.Start(ComposerStartInfo(["--version"], true));
            if (process is null) {
                return false;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        } catch {
            return false;
        }
    }

    static async Task<string> ComposerVersionAsync() {
        try {
            using var process = Process.Start(ComposerStartInfo(["--version"], true));
            if (process is null) {
                return "unknown";
            }

            string? output = await process.StandardOutput.ReadLineAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return SanitizeText(output, 80).Replace(' ', '_');
        } catch {
            return "unknown";
        }
    }
}

enum EApplicationMode {
    UNITY,
    SIDECAR
}

sealed record ApplicationCommand(EApplicationMode mode, string[] arguments);

static class CommandRouter {
    internal static ApplicationCommand Route(string executable, string[] arguments) {
        if (executable.Equals("unity-sidecar", StringComparison.OrdinalIgnoreCase)) {
            return new ApplicationCommand(EApplicationMode.SIDECAR, arguments);
        }

        return new ApplicationCommand(EApplicationMode.UNITY, arguments);
    }
}

sealed class StateStore {
    static readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly string activeDirectory;
    readonly string eventDirectory;
    readonly string readyPath;
    readonly string root;

    internal StateStore(string? rootOverride = null) {
        root = rootOverride
               ?? Environment.GetEnvironmentVariable("UNITY_STATE_DIRECTORY")
               ?? (OperatingSystem.IsWindows()
                   ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "unity")
                   : "/run/unity");
        activeDirectory = Path.Combine(root, "active");
        eventDirectory = Path.Combine(root, "events");
        readyPath = Path.Combine(root, "ready.json");
    }

    internal void EnsureDirectories() {
        Directory.CreateDirectory(activeDirectory);
        Directory.CreateDirectory(eventDirectory);
    }

    internal bool CanWrite() {
        try {
            EnsureDirectories();
            string probe = Path.Combine(root, $".write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        } catch {
            return false;
        }
    }

    internal void WriteActive(InvocationRecord record) => WriteAtomic(Path.Combine(activeDirectory, record.id + ".json"), record);
    internal void RemoveActive(string id) => DeleteIfExists(Path.Combine(activeDirectory, id + ".json"));
    internal void WriteReady(ReadyRecord record) => WriteAtomic(readyPath, record);
    internal void RemoveReady() => DeleteIfExists(readyPath);

    internal ReadyRecord? ReadReady() => Read<ReadyRecord>(readyPath);

    internal IReadOnlyList<InvocationRecord> ReadActive() {
        try {
            EnsureDirectories();
            return Directory.EnumerateFiles(activeDirectory, "*.json")
                .Select(Read<InvocationRecord>)
                .Where(record => record is not null)
                .Cast<InvocationRecord>()
                .ToList();
        } catch {
            return [];
        }
    }

    internal void WriteEvent(LifecycleEvent lifecycleEvent) {
        EnsureDirectories();
        string name = $"{DateTimeOffset.UtcNow.UtcTicks:D19}-{Guid.NewGuid():N}.json";
        WriteAtomic(Path.Combine(eventDirectory, name), lifecycleEvent);
    }

    internal void DrainEvents(Action<LifecycleEvent> consumer) {
        try {
            EnsureDirectories();
            foreach (string path in Directory.EnumerateFiles(eventDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal)) {
                var lifecycleEvent = Read<LifecycleEvent>(path);
                if (lifecycleEvent is not null) {
                    consumer(lifecycleEvent);
                }

                DeleteIfExists(path);
            }
        } catch (IOException) {
        }
    }

    static T? Read<T>(string path) {
        try {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), jsonOptions);
        } catch {
            return default;
        }
    }

    static void WriteAtomic<T>(string path, T value) {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, jsonOptions));
        File.Move(temporaryPath, path, true);
    }

    static void DeleteIfExists(string path) {
        try {
            File.Delete(path);
        } catch (FileNotFoundException) {
        }
    }
}

sealed class InvocationRecord {
    public string id { get; set; } = string.Empty;
    public string command { get; set; } = string.Empty;
    public string workingDirectory { get; set; } = string.Empty;
    public DateTimeOffset startedAtUtc { get; set; }
    public long timeoutSeconds { get; set; }
    public DateTimeOffset? deadlineUtc { get; set; }
    public ProcessIdentity launcher { get; set; } = new();
    public ProcessIdentity rootProcess { get; set; } = new();
    public int processGroupId { get; set; }
    public string? jobName { get; set; }
}

sealed class ReadyRecord {
    public ProcessIdentity supervisor { get; set; } = new();
    public DateTimeOffset startedAtUtc { get; set; }
    public bool mcpEnabled { get; set; }
    public bool mcpReady { get; set; }
}

sealed class ProcessIdentity {
    public int pid { get; set; }
    public long startMarker { get; set; }

    internal static ProcessIdentity Current() => FromProcess(Process.GetCurrentProcess());

    internal static ProcessIdentity FromProcess(Process process) => new() { pid = process.Id, startMarker = ReadStartMarker(process) };

    internal bool IsAlive() {
        if (pid <= 0 || startMarker <= 0) {
            return false;
        }

        try {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && ReadStartMarker(process) == startMarker;
        } catch {
            return false;
        }
    }

    static long ReadStartMarker(Process process) {
        if (OperatingSystem.IsWindows()) {
            return process.StartTime.ToUniversalTime().Ticks;
        }

        string stat = File.ReadAllText($"/proc/{process.Id}/stat");
        int commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0) {
            throw new InvalidDataException($"Invalid process stat for PID {process.Id}.");
        }

        string[] fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[19], CultureInfo.InvariantCulture);
    }
}

sealed class LifecycleEvent {
    public string kind { get; set; } = string.Empty;
    public string id { get; set; } = string.Empty;
    public int pid { get; set; }
    public string command { get; set; } = string.Empty;
    public string workingDirectory { get; set; } = string.Empty;
    public DateTimeOffset startedAtUtc { get; set; }
    public DateTimeOffset? finishedAtUtc { get; set; }
    public long? timeoutSeconds { get; set; }
    public int? exitCode { get; set; }
    public string? message { get; set; }

    internal static LifecycleEvent Start(InvocationRecord record) => new() {
        kind = "START",
        id = record.id,
        pid = record.rootProcess.pid,
        command = record.command,
        workingDirectory = record.workingDirectory,
        startedAtUtc = record.startedAtUtc,
        timeoutSeconds = record.timeoutSeconds
    };

    internal static LifecycleEvent Finish(string kind, InvocationRecord record, int? exitCode, string? message = null) => new() {
        kind = kind,
        id = record.id,
        pid = record.rootProcess.pid,
        command = record.command,
        workingDirectory = record.workingDirectory,
        startedAtUtc = record.startedAtUtc,
        finishedAtUtc = DateTimeOffset.UtcNow,
        timeoutSeconds = kind == "TIMEOUT" ? record.timeoutSeconds : null,
        exitCode = exitCode,
        message = message
    };
}

sealed class ChildProcess : IDisposable {
    readonly IDisposable? nativeResource;
    readonly Action resume;
    bool resumed;

    internal ChildProcess(Process process, int processGroupId, string? jobName, Action resume, IDisposable? nativeResource) {
        this.process = process;
        this.processGroupId = processGroupId;
        this.jobName = jobName;
        this.resume = resume;
        this.nativeResource = nativeResource;
    }

    internal Process process { get; }
    internal int processGroupId { get; }
    internal string? jobName { get; }

    public void Dispose() {
        process.Dispose();
        nativeResource?.Dispose();
    }

    internal void Resume() {
        if (!resumed) {
            resume();
            resumed = true;
        }
    }

    internal Task TerminateAsync(TimeSpan gracePeriod) => ProcessTree.TerminateChildAsync(this, gracePeriod);
}

static class ProcessTree {
    internal static ChildProcess StartComposer(string[] args, string invocationId, RuntimeCredentials credentials) {
        return OperatingSystem.IsWindows()
            ? WindowsProcessTree.Start(Program.ComposerStartInfo(args, credentials: credentials), invocationId)
            : UnixProcessTree.Start(Program.ComposerStartInfo(args, credentials: credentials));
    }

    internal static Task TerminateChildAsync(ChildProcess child, TimeSpan gracePeriod) => OperatingSystem.IsWindows()
        ? WindowsProcessTree.TerminateAsync(child.process, child.jobName, gracePeriod)
        : UnixProcessTree.TerminateAsync(child.process, child.processGroupId, gracePeriod);

    internal static async Task TerminateRecordAsync(InvocationRecord record, TimeSpan gracePeriod) {
        if (!record.rootProcess.IsAlive()) {
            return;
        }

        try {
            using var process = Process.GetProcessById(record.rootProcess.pid);
            if (OperatingSystem.IsWindows()) {
                await WindowsProcessTree.TerminateAsync(process, record.jobName, gracePeriod);
            } else {
                await UnixProcessTree.TerminateAsync(process, record.processGroupId, gracePeriod);
            }
        } catch (ArgumentException) {
        } catch (InvalidOperationException) {
        }
    }
}

static class UnixProcessTree {
    const int SIG_TERM = 15;
    const int SIG_KILL = 9;

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int signal);

    internal static ChildProcess Start(ProcessStartInfo composer) {
        var startInfo = new ProcessStartInfo("/usr/bin/setsid") { UseShellExecute = false, WorkingDirectory = composer.WorkingDirectory };
        startInfo.Environment.Clear();
        foreach (var variable in composer.Environment) {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add(composer.FileName);
        foreach (string argument in composer.ArgumentList) {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Composer process group.");
        return new ChildProcess(process, process.Id, null, () => { }, null);
    }

    internal static async Task TerminateAsync(Process? process, int processGroupId, TimeSpan gracePeriod) {
        if (processGroupId <= 0) {
            return;
        }

        kill(-processGroupId, SIG_TERM);
        if (process is not null && await WaitForExitAsync(process, gracePeriod)) {
            return;
        }

        await Task.Delay(gracePeriod);
        kill(-processGroupId, SIG_KILL);
        if (process is not null) {
            await WaitForExitAsync(process, TimeSpan.FromSeconds(2));
        }
    }

    static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout) {
        if (process.HasExited) {
            return true;
        }

        var wait = process.WaitForExitAsync();
        return await Task.WhenAny(wait, Task.Delay(timeout)) == wait;
    }
}

static class WindowsProcessTree {
    const uint CREATE_SUSPENDED = 0x00000004;
    const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint STARTF_USE_STD_HANDLES = 0x00000100;
    const uint JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const uint JOB_OBJECT_TERMINATE = 0x0008;
    const uint JOB_OBJECT_QUERY = 0x0004;
    const uint CTRL_BREAK_EVENT = 1;
    const int STD_INPUT_HANDLE = -10;
    const int STD_OUTPUT_HANDLE = -11;
    const int STD_ERROR_HANDLE = -12;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, uint informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenJobObject(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int standardHandle);

    internal static ChildProcess Start(ProcessStartInfo startInfo, string invocationId) {
        string jobName = "unity-" + invocationId;
        IntPtr job = CreateJobObject(IntPtr.Zero, jobName);
        if (job == IntPtr.Zero) {
            throw NativeError("Failed to create Windows Job Object");
        }

        try {
            ConfigureKillOnClose(job);
            var startupInfo = new StartupInfo {
                size = Marshal.SizeOf<StartupInfo>(),
                flags = STARTF_USE_STD_HANDLES,
                standardInput = GetStdHandle(STD_INPUT_HANDLE),
                standardOutput = GetStdHandle(STD_OUTPUT_HANDLE),
                standardError = GetStdHandle(STD_ERROR_HANDLE)
            };
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            IntPtr environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo));
            ProcessInformation processInformation;
            try {
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        CREATE_SUSPENDED | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT,
                        environment, startInfo.WorkingDirectory, ref startupInfo, out processInformation)) {
                    throw NativeError("Failed to create suspended Composer process");
                }
            } finally {
                Marshal.FreeHGlobal(environment);
            }

            if (!AssignProcessToJobObject(job, processInformation.process)) {
                CloseHandle(processInformation.thread);
                CloseHandle(processInformation.process);
                throw NativeError("Failed to assign Composer to Windows Job Object");
            }

            var process = Process.GetProcessById((int)processInformation.processId);
            CloseHandle(processInformation.process);
            var jobHandle = new NativeHandle(job);
            job = IntPtr.Zero;
            return new ChildProcess(process, 0, jobName, () => {
                if (ResumeThread(processInformation.thread) == uint.MaxValue) {
                    throw NativeError("Failed to resume Composer process");
                }

                CloseHandle(processInformation.thread);
            }, jobHandle);
        } finally {
            if (job != IntPtr.Zero) {
                CloseHandle(job);
            }
        }
    }

    internal static async Task TerminateAsync(Process? process, string? jobName, TimeSpan gracePeriod) {
        if (string.IsNullOrWhiteSpace(jobName)) {
            return;
        }

        IntPtr job = OpenJobObject(JOB_OBJECT_TERMINATE | JOB_OBJECT_QUERY, false, jobName);
        if (job == IntPtr.Zero) {
            return;
        }

        try {
            if (process is not null && !process.HasExited) {
                GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)process.Id);
                if (await WaitForExitAsync(process, gracePeriod)) {
                    return;
                }
            }

            TerminateJobObject(job, 124);
            if (process is not null) {
                await WaitForExitAsync(process, TimeSpan.FromSeconds(2));
            }
        } finally {
            CloseHandle(job);
        }
    }

    static void ConfigureKillOnClose(IntPtr job) {
        var information = new JobObjectExtendedLimitInformation { basicLimitInformation = new JobObjectBasicLimitInformation { limitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION_CLASS, pointer, (uint)size)) {
                throw NativeError("Failed to configure Windows Job Object");
            }
        } finally {
            Marshal.FreeHGlobal(pointer);
        }
    }

    static string BuildCommandLine(ProcessStartInfo startInfo) {
        var values = new List<string> { startInfo.FileName };
        values.AddRange(startInfo.ArgumentList);
        return string.Join(" ", values.Select(QuoteArgument));
    }

    static string BuildEnvironmentBlock(ProcessStartInfo startInfo) =>
        string.Join('\0', startInfo.Environment
            .OrderBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase)
            .Select(variable => $"{variable.Key}={variable.Value}")) + "\0\0";

    static string QuoteArgument(string argument) {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"')) {
            return argument;
        }

        var result = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument) {
            if (character == '\\') {
                backslashes++;
            } else if (character == '"') {
                result.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
            } else {
                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout) {
        if (process.HasExited) {
            return true;
        }

        var wait = process.WaitForExitAsync();
        return await Task.WhenAny(wait, Task.Delay(timeout)) == wait;
    }

    static Exception NativeError(string message) => new InvalidOperationException($"{message}: {Marshal.GetLastWin32Error()}");

    sealed class NativeHandle(IntPtr handle) : IDisposable {
        public void Dispose() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo {
        public int size;
        public string? reserved;
        public string? desktop;
        public string? title;
        public uint x;
        public uint y;
        public uint xSize;
        public uint ySize;
        public uint xCountChars;
        public uint yCountChars;
        public uint fillAttribute;
        public uint flags;
        public ushort showWindow;
        public ushort reserved2;
        public IntPtr reserved2Pointer;
        public IntPtr standardInput;
        public IntPtr standardOutput;
        public IntPtr standardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation {
        public IntPtr process;
        public IntPtr thread;
        public uint processId;
        public uint threadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation {
        public long perProcessUserTimeLimit;
        public long perJobUserTimeLimit;
        public uint limitFlags;
        public UIntPtr minimumWorkingSetSize;
        public UIntPtr maximumWorkingSetSize;
        public uint activeProcessLimit;
        public UIntPtr affinity;
        public uint priorityClass;
        public uint schedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters {
        public ulong readOperationCount;
        public ulong writeOperationCount;
        public ulong otherOperationCount;
        public ulong readTransferCount;
        public ulong writeTransferCount;
        public ulong otherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation {
        public JobObjectBasicLimitInformation basicLimitInformation;
        public IoCounters ioInfo;
        public UIntPtr processMemoryLimit;
        public UIntPtr jobMemoryLimit;
        public UIntPtr peakProcessMemoryUsed;
        public UIntPtr peakJobMemoryUsed;
    }
}

static class SignalHandlers {
    internal static IDisposable Register(CancellationTokenSource cancellation) {
        ConsoleCancelEventHandler consoleHandler = (_, eventArgs) => {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += consoleHandler;

        PosixSignalRegistration? terminate = null;
        PosixSignalRegistration? interrupt = null;
        if (!OperatingSystem.IsWindows()) {
            terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => {
                context.Cancel = true;
                cancellation.Cancel();
            });
            interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => {
                context.Cancel = true;
                cancellation.Cancel();
            });
        }

        return new Registration(() => {
            Console.CancelKeyPress -= consoleHandler;
            terminate?.Dispose();
            interrupt?.Dispose();
        });
    }

    sealed class Registration(Action dispose) : IDisposable {
        public void Dispose() => dispose();
    }
}
