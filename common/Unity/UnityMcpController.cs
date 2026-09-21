using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using ModelContextProtocol;

namespace Unity;

enum EWindowsHostPathStrategy {
    ORIGINAL,
    WSL,
    DOCKER_DESKTOP,
    LEGACY_DESKTOP
}

sealed class UnityMcpController : IAsyncDisposable {
    const string LABEL_PREFIX = "net.slothsoft.unity";
    const string WORKER_CONFIGURATION_LABEL = $"{LABEL_PREFIX}.worker-configuration";
    const string WORKER_DOTNET_GC_HEAP_COUNT = "DOTNET_GCHeapCount=2";
    static readonly TimeSpan workerStopTimeout = TimeSpan.FromSeconds(10);

    static readonly string[] forwardedEnvironmentNames = [
        "UNITY_NO_GRAPHICS",
        "UNITY_ACCELERATOR_ENDPOINT",
        "UNITY_ACCELERATOR_PARAMS",
        "UNITY_LOGGING",
        "UNITY_EMPTY_MANIFEST",
        "UNITY_CALL_TIMEOUT"
    ];

    static readonly string[] inheritedHostConfigurationNames = [
        "Memory",
        "MemorySwap",
        "MemoryReservation",
        "NanoCpus",
        "CpuShares",
        "CpuPeriod",
        "CpuQuota",
        "CpusetCpus",
        "CpusetMems",
        "PidsLimit",
        "OomKillDisable",
        "ShmSize",
        "Ulimits",
        "BlkioWeight",
        "BlkioWeightDevice",
        "BlkioDeviceReadBps",
        "BlkioDeviceWriteBps",
        "BlkioDeviceReadIOps",
        "BlkioDeviceWriteIOps",
        "DeviceRequests",
        "Devices",
        "Isolation"
    ];

    readonly ConcurrentDictionary<string, byte> activeWorkers = new(StringComparer.Ordinal);
    readonly string controllerId;

    readonly DockerEngineClient docker;
    readonly string imageHash;
    readonly string imageId;
    readonly ConcurrentDictionary<string, AsyncFifoLock> lanes = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Lazy<Task<ValidatedProject>>> projects = new(StringComparer.Ordinal);
    readonly JsonObject self;
    readonly CancellationToken stoppingToken;
    readonly RuntimeCredentialStore credentials;
    readonly bool windowsContainers;
    int preferredWindowsHostPathStrategy;

    UnityMcpController(
        DockerEngineClient docker,
        JsonObject self,
        string controllerId,
        string imageId,
        bool windowsContainers,
        RuntimeCredentialStore credentials,
        CancellationToken stoppingToken) {
        this.docker = docker;
        this.self = self;
        this.controllerId = controllerId;
        this.imageId = imageId;
        imageHash = Hash(imageId)[..12];
        this.windowsContainers = windowsContainers;
        this.credentials = credentials;
        this.stoppingToken = stoppingToken;
    }

    string probeProjectRoot {
        get => windowsContainers ? @"C:\unity-probe" : "/unity-probe";
    }

    string workerProjectRoot {
        get => windowsContainers ? @"C:\workspace\project" : "/var/workspace/project";
    }

    string unityExecutable {
        get => windowsContainers ? "unity.exe" : "unity";
    }

    string workerWebGlRoot {
        get => windowsContainers ? @"C:\Windows\Temp\unity-webgl" : "/tmp/unity-webgl";
    }

    public async ValueTask DisposeAsync() {
        await StopActiveWorkersAsync();
        await docker.DisposeAsync();
    }

    internal static Task<UnityMcpController> CreateAsync(
        RuntimeCredentials credentials,
        CancellationToken stoppingToken) =>
        CreateAsync(new DockerEngineClient(), credentials, stoppingToken);

    internal static Task<UnityMcpController> CreateAsync(
        RuntimeCredentials credentials,
        CancellationToken stoppingToken,
        string windowsPipeName,
        string unixSocketPath) =>
        CreateAsync(new DockerEngineClient(windowsPipeName, unixSocketPath), credentials, stoppingToken);

    static async Task<UnityMcpController> CreateAsync(
        DockerEngineClient docker,
        RuntimeCredentials credentials,
        CancellationToken stoppingToken) {
        try {
            JsonObject version;
            try {
                version = await docker.VersionAsync(stoppingToken);
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                string endpoint = OperatingSystem.IsWindows()
                    ? @"\\.\pipe\docker_engine"
                    : "/var/run/docker.sock";
                throw new InvalidOperationException(
                    $"MCP startup requires Docker Engine access at {endpoint}. " +
                    $"Mount the platform Docker socket or named pipe into the sidecar: {exception.Message}",
                    exception);
            }

            string daemonOs = version["Os"]?.GetValue<string>()
                              ?? throw new InvalidOperationException("Docker Engine did not report its container operating system.");
            bool windowsContainers = daemonOs.Equals("windows", StringComparison.OrdinalIgnoreCase);
            var self = await docker.InspectSelfAsync(stoppingToken);
            string controllerId = self["Id"]?.GetValue<string>()
                                  ?? throw new InvalidOperationException("Docker Engine did not report the sidecar container ID.");
            string imageId = self["Image"]?.GetValue<string>()
                             ?? throw new InvalidOperationException("Docker Engine did not report the sidecar image ID.");
            return new UnityMcpController(
                docker,
                self,
                controllerId,
                imageId,
                windowsContainers,
                new RuntimeCredentialStore(credentials),
                stoppingToken);
        } catch {
            await docker.DisposeAsync();
            throw;
        }
    }

    internal object ConfigureCredentials(
        string unityUsername,
        string unityPassword,
        string? emailUsername,
        string? emailPassword) =>
        credentials.Configure(unityUsername, unityPassword, emailUsername, emailPassword);

    internal async Task<object> ProjectInfoAsync(string projectRoot, CancellationToken cancellationToken) {
        var started = DateTimeOffset.UtcNow;
        var project = await GetProjectAsync(projectRoot, cancellationToken);
        LogStart("get_project_info", project.id);
        try {
            return new {
                projectRoot = project.normalizedRoot,
                project.probe.companyName,
                project.probe.projectName,
                project.probe.projectVersion,
                editor = new { version = project.probe.editorVersion, revision = project.probe.editorRevision },
                code = new { project.probe.apiCompatibility, project.probe.allowUnsafeCode, project.probe.scriptingBackendOverrides },
                rendering = new { project.probe.renderPipeline, project.probe.colorSpace, project.probe.graphicsApis },
                project.probe.inputHandling,
                project.probe.packages
            };
        } finally {
            LogEnd("get_project_info", project.id, started);
        }
    }

    internal async Task<object> RunTestsAsync(
        string projectRoot,
        string[] modes,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) {
        if (modes is null || modes.Length == 0 || modes.Any(string.IsNullOrWhiteSpace)) {
            throw new ArgumentException("modes must be a non-empty array of non-empty strings.", nameof(modes));
        }

        if (modes.Any(mode => mode.Length > 128 || mode.Any(char.IsControl))) {
            throw new ArgumentException("Each test mode must be at most 128 characters and contain no control characters.", nameof(modes));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        await using var operationProgress = new UnityMcpProgress(progress, linked.Token);
        operationProgress.ReportPhase("Validating Unity project");
        var project = await GetProjectAsync(projectRoot, linked.Token);
        return await ExecuteSerializedAsync(project, "run_tests", operationProgress, async (worker, token) => {
            operationProgress.ReportPhase("Running Unity tests");
            var command = new List<string> {
                unityExecutable,
                "tests",
                "--junit",
                "-",
                workerProjectRoot
            };
            command.AddRange(modes);
            var result = await ExecuteWorkerAsync(worker, command, token);
            operationProgress.ReportPhase("Parsing Unity test result");
            return BuildTestResult(result);
        }, linked.Token);
    }

    internal async Task<object> ExecuteMethodAsync(
        string projectRoot,
        string method,
        string[]? arguments,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(method) || method.Length > 512 || method.Any(char.IsControl)) {
            throw new ArgumentException("method must be a non-empty static method name of at most 512 characters.", nameof(method));
        }

        arguments ??= [];
        if (arguments.Length > 256 || arguments.Any(argument => argument.Length > 16_384)) {
            throw new ArgumentException("arguments accepts at most 256 values of at most 16384 characters each.", nameof(arguments));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        await using var operationProgress = new UnityMcpProgress(progress, linked.Token);
        operationProgress.ReportPhase("Validating Unity project");
        var project = await GetProjectAsync(projectRoot, linked.Token);
        return await ExecuteSerializedAsync(project, "execute_method", operationProgress, async (worker, token) => {
            operationProgress.ReportPhase("Invoking Unity editor method");
            var command = new List<string> {
                unityExecutable,
                "method",
                workerProjectRoot,
                method,
                "--"
            };
            command.AddRange(arguments);
            var result = await ExecuteWorkerAsync(worker, command, token);
            operationProgress.ReportPhase("Preparing method result");
            return new { exitStatus = result.exitCode, output = RelevantOutput(result.standardOutput), errorOutput = RelevantOutput(result.standardError) };
        }, linked.Token);
    }

    internal async Task<object> BuildAndServeWebGlAsync(
        string projectRoot,
        string scheme,
        string host,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) {
        if (scheme is not "http" and not "https" || string.IsNullOrWhiteSpace(host)) {
            throw new InvalidOperationException("The MCP request did not provide a valid public HTTP origin.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        await using var operationProgress = new UnityMcpProgress(progress, linked.Token);
        operationProgress.ReportPhase("Validating Unity project");
        var project = await GetProjectAsync(projectRoot, linked.Token);
        return await ExecuteSerializedAsync(project, "build_and_serve_webgl", operationProgress, async (worker, token) => {
            operationProgress.ReportPhase("Installing WebGL Build Support - step 1 of 3");
            var moduleResult = await ExecuteWorkerAsync(worker, [
                unityExecutable,
                "module-install",
                workerProjectRoot,
                "webgl"
            ], token);
            EnsureSuccessful(moduleResult, "Unity WebGL Build Support installation");

            string workerOutput = CombineContainerPath(workerWebGlRoot, Guid.NewGuid().ToString("N"), windowsContainers);
            try {
                operationProgress.ReportPhase("Building WebGL - step 2 of 3");
                var buildResult = await ExecuteWorkerAsync(worker, [
                    unityExecutable,
                    "method",
                    workerProjectRoot,
                    "Slothsoft.UnityExtensions.Editor.Build.WebGL",
                    "--",
                    "-buildTarget",
                    "WebGL",
                    workerOutput
                ], token);
                EnsureSuccessful(buildResult, "Unity WebGL build");

                operationProgress.ReportPhase("Publishing WebGL build - step 3 of 3");
                string projectSlug = WebGlHosting.ProjectSlug(project.probe.projectName);
                var build = await WebGlHosting.ClaimBuildDirectoryAsync(WebGlHosting.documentRoot, projectSlug, token);
                await ExtractWorkerArchiveAsync(
                    worker,
                    workerOutput,
                    build.directory,
                    token);
                string path = WebGlHosting.PublicPath(build);
                return new { build.projectSlug, build.buildId, path, url = $"{scheme}://{host}{path}" };
            } finally {
                await RemoveWorkerDirectoryBestEffortAsync(worker, workerOutput);
            }
        }, linked.Token);
    }

    internal async Task StopActiveWorkersAsync() {
        foreach (string worker in activeWorkers.Keys) {
            try {
                await docker.StopContainerAsync(worker, workerStopTimeout, CancellationToken.None);
            } catch (Exception exception) {
                Console.Error.WriteLine($"unity-sidecar: failed to stop active MCP worker: {exception.Message}");
            }
        }
    }

    async Task<object> ExecuteSerializedAsync(
        ValidatedProject project,
        string tool,
        UnityMcpProgress progress,
        Func<WorkerContainer, CancellationToken, Task<object>> operation,
        CancellationToken cancellationToken) {
        var lane = lanes.GetOrAdd(project.id, _ => new AsyncFifoLock());
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stoppingToken);
        await using var laneLease = await lane.AcquireAsync(
            linked.Token,
            () => progress.ReportPhase("Waiting for project access"));
        await using var daemonLease = await AcquireDaemonLockAsync(project, progress, linked.Token);
        var started = DateTimeOffset.UtcNow;
        LogStart(tool, project.id);
        try {
            progress.ReportPhase("Preparing or reusing Unity worker");
            var worker = await EnsureWorkerAsync(project, linked.Token);
            return await operation(worker, linked.Token);
        } finally {
            LogEnd(tool, project.id, started);
        }
    }

    async Task<ExecResult> ExecuteWorkerAsync(
        WorkerContainer worker,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken) {
        activeWorkers.TryAdd(worker.id, 0);
        try {
            return await docker.ExecAsync(
                worker.id,
                workerProjectRoot,
                command,
                credentials.Snapshot().WorkerEnvironment(),
                cancellationToken);
        } catch (OperationCanceledException) {
            await docker.StopContainerAsync(worker.id, workerStopTimeout, CancellationToken.None);
            throw;
        } finally {
            activeWorkers.TryRemove(worker.id, out _);
        }
    }

    async Task ExtractWorkerArchiveAsync(
        WorkerContainer worker,
        string source,
        string destination,
        CancellationToken cancellationToken) {
        if (!windowsContainers) {
            await docker.ExtractArchiveAsync(
                worker.id,
                CombineContainerPath(source, ".", false),
                destination,
                cancellationToken);
            return;
        }

        // Docker cannot access a running Hyper-V container's filesystem through
        // the archive endpoint. Stopping and restarting preserves the retained
        // worker's writable layer, including its imported Unity Library.
        try {
            await docker.StopContainerAsync(worker.id, workerStopTimeout, cancellationToken);
            await docker.ExtractArchiveAsync(worker.id, source, destination, cancellationToken);
            MoveArchiveContentsToRoot(destination, ContainerPathName(source));
        } finally {
            if (!stoppingToken.IsCancellationRequested) {
                try {
                    await docker.StartContainerAsync(worker.id, CancellationToken.None);
                } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.NotModified) {
                }
            }
        }
    }

    internal static string ContainerPathName(string path) {
        int separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return path[(separator + 1)..];
    }

    internal static void MoveArchiveContentsToRoot(string destination, string archiveDirectoryName) {
        string archiveRoot = Path.Combine(destination, archiveDirectoryName);
        if (!Directory.Exists(archiveRoot)) {
            throw new InvalidOperationException($"Docker archive did not contain its expected root directory '{archiveDirectoryName}'.");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(archiveRoot)) {
            string name = Path.GetFileName(entry);
            string target = Path.Combine(destination, name);
            if (Directory.Exists(entry)) {
                Directory.Move(entry, target);
            } else {
                File.Move(entry, target);
            }
        }

        Directory.Delete(archiveRoot);
    }

    async Task RemoveWorkerDirectoryBestEffortAsync(WorkerContainer worker, string path) {
        IReadOnlyList<string> command = windowsContainers
            ? ["cmd.exe", "/d", "/s", "/c", $"rmdir /s /q \"{path}\""]
            : ["rm", "-rf", "--", path];
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await docker.ExecAsync(worker.id, workerProjectRoot, command, timeout.Token);
            if (result.exitCode != 0) {
                Console.Error.WriteLine($"unity-sidecar: failed to remove temporary WebGL output from worker {worker.name}");
            }
        } catch (Exception exception) {
            Console.Error.WriteLine($"unity-sidecar: failed to remove temporary WebGL output from worker {worker.name}: {exception.Message}");
        }
    }

    static void EnsureSuccessful(ExecResult result, string operation) {
        if (result.exitCode == 0) {
            return;
        }

        string detail = RelevantOutput(result.combinedOutput);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"{operation} failed with exit code {result.exitCode}."
            : $"{operation} failed with exit code {result.exitCode}: {detail}");
    }

    async Task<ValidatedProject> GetProjectAsync(string projectRoot, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(projectRoot) || projectRoot.Length > 4096 || projectRoot.Any(char.IsControl)) {
            throw new ArgumentException("projectRoot must be a non-empty absolute host path.", nameof(projectRoot));
        }

        var candidates = DaemonProjectRootCandidates(
            projectRoot,
            windowsContainers,
            (EWindowsHostPathStrategy)Volatile.Read(ref preferredWindowsHostPathStrategy));
        var failures = new List<(string path, Exception exception)>();
        foreach (var candidate in candidates) {
            try {
                var project = await GetOrProbeProjectAsync(projectRoot, candidate.path, cancellationToken);
                if (!windowsContainers && LooksLikeWindowsHostPath(projectRoot)) {
                    Interlocked.Exchange(ref preferredWindowsHostPathStrategy, (int)candidate.strategy);
                }

                return project;
            } catch (Exception exception) when (exception is not OperationCanceledException) {
                if (candidates.Count == 1) {
                    throw;
                }

                failures.Add((candidate.path, exception));
            }
        }

        string details = string.Join("; ", failures.Select(failure => $"'{failure.path}': {failure.exception.GetBaseException().Message}"));
        throw new InvalidOperationException(
            $"Unity project validation failed for '{projectRoot}' using every supported Linux Docker host path layout. {details}",
            new AggregateException(failures.Select(failure => failure.exception)));
    }

    async Task<ValidatedProject> GetOrProbeProjectAsync(
        string suppliedRoot,
        string daemonRoot,
        CancellationToken cancellationToken) {
        var lazy = projects.GetOrAdd(
            daemonRoot,
            value => new Lazy<Task<ValidatedProject>>(
                () => ProbeProjectAsync(suppliedRoot, value, stoppingToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try {
            return await lazy.Value.WaitAsync(cancellationToken);
        } catch {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted) {
                projects.TryRemove(new KeyValuePair<string, Lazy<Task<ValidatedProject>>>(daemonRoot, lazy));
            }

            throw;
        }
    }

    async Task<ValidatedProject> ProbeProjectAsync(
        string suppliedRoot,
        string daemonRoot,
        CancellationToken cancellationToken) {
        string name = $"unity-probe-{Guid.NewGuid():N}";
        string? containerId = null;
        try {
            var configuration = new JsonObject {
                ["Image"] = imageId,
                ["Cmd"] = DockerEngineClient.ToArray([
                    windowsContainers ? "unity-sidecar.exe" : "unity-sidecar",
                    "probe-project",
                    probeProjectRoot
                ]),
                ["Labels"] = Labels("probe", null),
                ["HostConfig"] = new JsonObject {
                    ["Mounts"] = new JsonArray {
                        new JsonObject {
                            ["Type"] = "bind",
                            ["Source"] = daemonRoot,
                            ["Target"] = probeProjectRoot,
                            ["ReadOnly"] = true,
                            ["BindOptions"] = new JsonObject { ["CreateMountpoint"] = false }
                        }
                    }
                }
            };
            containerId = await docker.CreateContainerAsync(name, configuration, cancellationToken);
            await docker.StartContainerAsync(containerId, cancellationToken);
            int exitCode = await docker.WaitContainerAsync(containerId, cancellationToken);
            var inspected = await docker.InspectContainerAsync(containerId, cancellationToken);
            var output = await docker.ContainerLogsAsync(containerId, cancellationToken);
            if (exitCode != 0) {
                string detail = RelevantOutput(output.standardError);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? "the validation probe failed without diagnostic output"
                    : detail);
            }

            var mount = inspected["Mounts"]?.AsArray()
                            .Select(node => node?.AsObject())
                            .FirstOrDefault(mount => mount?["Destination"]?.GetValue<string>() == probeProjectRoot)
                        ?? throw new InvalidOperationException("Docker did not report the project probe bind mount.");
            string normalizedRoot = mount["Source"]?.GetValue<string>()
                                    ?? throw new InvalidOperationException("Docker did not report the normalized project source path.");
            normalizedRoot = NormalizeDaemonPath(normalizedRoot);
            var probe = JsonSerializer.Deserialize<ProjectProbeResult>(
                            output.standardOutput,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidOperationException("the project validation probe returned no project information");
            return new ValidatedProject(normalizedRoot, Hash(ProjectIdentityPath(normalizedRoot, windowsContainers)), probe);
        } catch (Exception exception) when (exception is not OperationCanceledException) {
            throw new InvalidOperationException(
                $"Unity project validation failed for '{suppliedRoot}': {exception.Message}",
                exception);
        } finally {
            if (containerId is not null) {
                await docker.RemoveContainerAsync(containerId, true, true, CancellationToken.None);
            }
        }
    }

    async Task<WorkerContainer> EnsureWorkerAsync(ValidatedProject project, CancellationToken cancellationToken) {
        string name = $"unity-worker-{project.id[..16]}-{imageHash}";
        var configuration = BuildWorkerConfiguration(project);
        string expectedFingerprint = configuration["Labels"]?[WORKER_CONFIGURATION_LABEL]?.GetValue<string>()
                                     ?? throw new InvalidOperationException("The worker configuration fingerprint is missing.");
        var inspected = await docker.TryInspectContainerAsync(name, cancellationToken);
        bool reused = inspected is not null;
        if (inspected is not null) {
            ValidateWorkerIdentity(inspected, project);
            string? actualFingerprint = inspected["Config"]?["Labels"]?[WORKER_CONFIGURATION_LABEL]?.GetValue<string>();
            if (actualFingerprint != expectedFingerprint) {
                await docker.StopContainerAsync(name, workerStopTimeout, cancellationToken);
                await docker.RemoveContainerAsync(name, true, true, cancellationToken);
                inspected = null;
                reused = false;
            }
        }

        if (inspected is null) {
            try {
                await docker.CreateContainerAsync(name, configuration, cancellationToken);
            } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.Conflict) {
            }

            inspected = await docker.InspectContainerAsync(name, cancellationToken);
        }

        ValidateWorker(inspected, project, expectedFingerprint);
        if (inspected["State"]?["Running"]?.GetValue<bool>() != true) {
            try {
                await docker.StartContainerAsync(name, cancellationToken);
            } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.NotModified) {
            }
        }

        return new WorkerContainer(
            inspected["Id"]?.GetValue<string>() ?? name,
            name,
            reused);
    }

    JsonObject BuildWorkerConfiguration(ValidatedProject project) {
        var hostConfiguration = new JsonObject();
        var selfHostConfiguration = self["HostConfig"]?.AsObject() ?? new JsonObject();
        foreach (string name in inheritedHostConfigurationNames) {
            var value = selfHostConfiguration[name];
            if (value is not null) {
                hostConfiguration[name] = value.DeepClone();
            }
        }

        hostConfiguration["Mounts"] = BuildWorkerMounts(project);

        var labels = Labels("worker", project);
        labels[$"{LABEL_PREFIX}.image"] = imageId;
        var environment = ForwardedEnvironment();
        var reuseContract = new JsonObject { ["Image"] = imageId, ["Project"] = project.id, ["Env"] = environment.DeepClone(), ["HostConfig"] = hostConfiguration.DeepClone() };
        labels[WORKER_CONFIGURATION_LABEL] = ConfigurationFingerprint(reuseContract);
        return new JsonObject { ["Image"] = imageId, ["Env"] = environment, ["Labels"] = labels, ["HostConfig"] = hostConfiguration };
    }

    JsonArray BuildWorkerMounts(ValidatedProject project) {
        var mounts = new JsonArray();
        foreach (string directory in new[] { "Assets", "Packages", "ProjectSettings" }) {
            mounts.Add(new JsonObject {
                ["Type"] = "bind",
                ["Source"] = CombineDaemonPath(project.normalizedRoot, directory, windowsContainers),
                ["Target"] = CombineContainerPath(workerProjectRoot, directory, windowsContainers),
                ["ReadOnly"] = false,
                ["BindOptions"] = new JsonObject { ["CreateMountpoint"] = false }
            });
        }

        var inheritedDestinations = windowsContainers
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                @"C:\Program Files\Unity\Hub\Editor",
                @"C:\Users\ContainerAdministrator\AppData\Roaming\Unity",
                @"C:\Users\ContainerAdministrator\AppData\Roaming\UnityHub",
                @"C:\Users\ContainerAdministrator\AppData\Local\Unity",
                @"C:\ProgramData\Unity"
            }
            : new HashSet<string>(StringComparer.Ordinal) {
                "/root/Unity",
                "/root/.config/unity3d",
                "/root/.config/unityhub",
                "/root/.cache/Unity",
                "/root/.local/share/unity3d"
            };

        foreach (var node in self["Mounts"]?.AsArray() ?? []) {
            var mount = node?.AsObject();
            string? destination = mount?["Destination"]?.GetValue<string>();
            string? type = mount?["Type"]?.GetValue<string>();
            if (destination is null || type is null || !inheritedDestinations.Contains(destination)) {
                continue;
            }

            string? source = type.Equals("volume", StringComparison.OrdinalIgnoreCase)
                ? mount?["Name"]?.GetValue<string>()
                : mount?["Source"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(source)) {
                continue;
            }

            var inherited = new JsonObject { ["Type"] = type, ["Source"] = source, ["Target"] = destination, ["ReadOnly"] = mount?["RW"]?.GetValue<bool>() == false };
            string? propagation = mount?["Propagation"]?.GetValue<string>();
            if (type.Equals("bind", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(propagation)) {
                inherited["BindOptions"] = new JsonObject { ["Propagation"] = propagation };
            }

            mounts.Add(inherited);
        }

        return mounts;
    }

    JsonArray ForwardedEnvironment() {
        var allowed = new HashSet<string>(forwardedEnvironmentNames, StringComparer.Ordinal);
        var entries = new List<string>();
        foreach (var node in self["Config"]?["Env"]?.AsArray() ?? []) {
            string? entry = node?.GetValue<string>();
            int separator = entry?.IndexOf('=') ?? -1;
            if (separator > 0 && allowed.Contains(entry![..separator])) {
                entries.Add(entry);
            }
        }

        entries.Sort(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (string entry in entries) {
            result.Add(entry);
        }

        // Unity's Roslyn compiler server otherwise creates one server-GC heap per host CPU and can
        // retain enough memory to crowd IL2CPP out of a Docker Desktop VM during the same build.
        result.Add(WORKER_DOTNET_GC_HEAP_COUNT);

        return result;
    }

    void ValidateWorker(JsonObject worker, ValidatedProject project, string expectedFingerprint) {
        ValidateWorkerIdentity(worker, project);
        string? actualFingerprint = worker["Config"]?["Labels"]?[WORKER_CONFIGURATION_LABEL]?.GetValue<string>();
        if (actualFingerprint != expectedFingerprint) {
            throw new InvalidOperationException("A conflicting container has an incompatible MCP worker configuration.");
        }
    }

    void ValidateWorkerIdentity(JsonObject worker, ValidatedProject project) {
        var labels = worker["Config"]?["Labels"]?.AsObject();
        if (labels?[$"{LABEL_PREFIX}.kind"]?.GetValue<string>() != "worker"
            || labels[$"{LABEL_PREFIX}.project"]?.GetValue<string>() != project.id
            || worker["Image"]?.GetValue<string>() != imageId) {
            throw new InvalidOperationException("A conflicting container occupies the reserved MCP worker name.");
        }
    }

    async ValueTask<IAsyncDisposable> AcquireDaemonLockAsync(
        ValidatedProject project,
        UnityMcpProgress progress,
        CancellationToken cancellationToken) {
        string name = $"unity-lock-{project.id[..32]}";
        while (true) {
            try {
                var configuration = new JsonObject { ["Image"] = imageId, ["Labels"] = Labels("lock", project) };
                await docker.CreateContainerAsync(name, configuration, cancellationToken);
                return new DockerContainerLease(docker, name);
            } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.Conflict) {
                var existing = await docker.TryInspectContainerAsync(name, cancellationToken);
                if (existing is null) {
                    continue;
                }

                var labels = existing["Config"]?["Labels"]?.AsObject();
                if (labels?[$"{LABEL_PREFIX}.kind"]?.GetValue<string>() != "lock"
                    || labels[$"{LABEL_PREFIX}.project"]?.GetValue<string>() != project.id) {
                    throw new InvalidOperationException("A conflicting container occupies the reserved MCP project lock name.");
                }

                string? owner = labels[$"{LABEL_PREFIX}.controller"]?.GetValue<string>();
                if (owner == controllerId) {
                    await docker.RemoveContainerAsync(name, true, true, cancellationToken);
                    continue;
                }

                var ownerContainer = string.IsNullOrWhiteSpace(owner)
                    ? null
                    : await docker.TryInspectContainerAsync(owner, cancellationToken);
                if (ownerContainer?["State"]?["Running"]?.GetValue<bool>() != true) {
                    await docker.RemoveContainerAsync(name, true, true, cancellationToken);
                    continue;
                }

                progress.ReportPhase("Waiting for project access");
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    JsonObject Labels(string kind, ValidatedProject? project) {
        var labels = new JsonObject { [$"{LABEL_PREFIX}.kind"] = kind, [$"{LABEL_PREFIX}.controller"] = controllerId };
        if (project is not null) {
            labels[$"{LABEL_PREFIX}.project"] = project.id;
            labels[$"{LABEL_PREFIX}.project-root"] = project.normalizedRoot;
        }

        return labels;
    }

    internal static object BuildTestResult(ExecResult result) {
        try {
            var document = XDocument.Parse(result.standardOutput, LoadOptions.None);
            var suites = document.Descendants("testsuite").ToList();
            if (suites.Count == 0) {
                throw new InvalidOperationException("The JUnit report contains no test suites.");
            }

            var testCases = document.Descendants("testcase").ToList();
            int total = AttributeSum(suites, "tests", testCases.Count);
            int failureCount = AttributeSum(suites, "failures", testCases.Count(test => test.Element("failure") is not null));
            int errorCount = AttributeSum(suites, "errors", testCases.Count(test => test.Element("error") is not null));
            int skipped = AttributeSum(suites, "skipped", testCases.Count(test => test.Element("skipped") is not null));
            decimal duration = suites.Sum(suite => ParseDecimal(suite.Attribute("time")?.Value));
            var allFailureDetails = testCases
                .Select(test => new { test, failure = test.Element("failure") ?? test.Element("error") })
                .Where(item => item.failure is not null)
                .ToList();
            var failureDetails = allFailureDetails
                .Take(100)
                .Select(item => new {
                    name = item.test.Attribute("name")?.Value,
                    className = item.test.Attribute("classname")?.Value,
                    message = item.failure!.Attribute("message")?.Value,
                    type = item.failure.Attribute("type")?.Value,
                    stackTrace = item.failure.Value
                })
                .ToList();

            if (failureCount == 0 && errorCount == 0 && result.exitCode != 0) {
                return ErrorTestResult(result);
            }

            string outcome = failureCount == 0 && errorCount == 0 ? "passed" : "failed";
            return new {
                outcome,
                result.exitCode,
                counts = new {
                    total,
                    passed = Math.Max(0, total - failureCount - errorCount - skipped),
                    failures = failureCount,
                    errors = errorCount,
                    skipped
                },
                durationSeconds = decimal.Round(duration, 3),
                failures = failureDetails,
                failuresTruncated = failureCount + errorCount > failureDetails.Count
            };
        } catch (Exception exception) when (exception is XmlException or InvalidOperationException) {
            return ErrorTestResult(result);
        }
    }

    static object ErrorTestResult(ExecResult result) => new { outcome = "error", result.exitCode, log = result.combinedOutput };

    static int AttributeSum(IReadOnlyList<XElement> suites, string name, int fallback) {
        if (suites.Count == 0 || suites.Any(suite => suite.Attribute(name) is null)) {
            return fallback;
        }

        return suites.Sum(suite => int.TryParse(suite.Attribute(name)?.Value, out int value) ? value : 0);
    }

    static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result) ? result : 0;

    static string RelevantOutput(string value, int maximumLength = 64 * 1024) {
        string trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : trimmed[..maximumLength] + "\n[output truncated]";
    }

    internal static string NormalizeDaemonPath(string path) {
        int rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        while (path.Length > rootLength && (path.EndsWith('/') || path.EndsWith('\\'))) {
            path = path[..^1];
        }

        return path;
    }

    internal static string ProjectIdentityPath(string path, bool windowsContainers) =>
        windowsContainers || LooksLikeWindowsHostPath(path) ? path.ToUpperInvariant() : path;

    internal static bool LooksLikeWindowsHostPath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && path[2] is '\\' or '/';

    internal static IReadOnlyList<(EWindowsHostPathStrategy strategy, string path)> DaemonProjectRootCandidates(
        string path,
        bool windowsContainers,
        EWindowsHostPathStrategy preferredStrategy) {
        if (windowsContainers || !LooksLikeWindowsHostPath(path)) {
            return [(EWindowsHostPathStrategy.ORIGINAL, path)];
        }

        var strategies = Enum.GetValues<EWindowsHostPathStrategy>();
        if (Enum.IsDefined(preferredStrategy) && preferredStrategy != strategies[0]) {
            int preferredIndex = Array.IndexOf(strategies, preferredStrategy);
            (strategies[0], strategies[preferredIndex]) = (strategies[preferredIndex], strategies[0]);
        }

        return strategies.Select(strategy => (strategy, TranslateWindowsHostPath(path, strategy))).ToArray();
    }

    internal static string TranslateWindowsHostPath(string path, EWindowsHostPathStrategy strategy) {
        if (strategy == EWindowsHostPathStrategy.ORIGINAL) {
            return path;
        }

        char drive = char.ToLowerInvariant(path[0]);
        string root = strategy switch {
            EWindowsHostPathStrategy.WSL => $"/mnt/{drive}",
            EWindowsHostPathStrategy.DOCKER_DESKTOP => $"/run/desktop/mnt/host/{drive}",
            EWindowsHostPathStrategy.LEGACY_DESKTOP => $"/host_mnt/{drive}",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
        };
        string suffix = path[3..].Replace('\\', '/').TrimEnd('/');
        return suffix.Length == 0 ? root : $"{root}/{suffix}";
    }

    internal static string CombineDaemonPath(string root, string child, bool windowsContainers) =>
        windowsContainers ? Path.Combine(root, child) : root + "/" + child;

    internal static string CombineContainerPath(string root, string child, bool windowsContainers) =>
        windowsContainers ? root + "\\" + child : root + "/" + child;

    static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string ConfigurationFingerprint(JsonNode configuration) =>
        Hash(Canonicalize(configuration).ToJsonString());

    static JsonNode Canonicalize(JsonNode node) =>
        node switch {
            JsonObject value => new JsonObject(value
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => KeyValuePair.Create(
                    property.Key,
                    property.Value is null ? null : Canonicalize(property.Value)))),
            JsonArray value => new JsonArray(value
                .Select(item => item is null ? null : Canonicalize(item))
                .ToArray()),
            _ => node.DeepClone()
        };

    static void LogStart(string tool, string project) =>
        Console.WriteLine($"MCP START tool={tool} project={project[..12]}");

    static void LogEnd(string tool, string project, DateTimeOffset started) =>
        Console.WriteLine($"MCP END tool={tool} project={project[..12]} duration={(DateTimeOffset.UtcNow - started).TotalSeconds:F1}s");
}

sealed record ValidatedProject(string normalizedRoot, string id, ProjectProbeResult probe);

sealed record WorkerContainer(string id, string name, bool reused);

sealed class DockerContainerLease(DockerEngineClient docker, string name) : IAsyncDisposable {
    public async ValueTask DisposeAsync() =>
        await docker.RemoveContainerAsync(name, true, true, CancellationToken.None);
}

sealed class AsyncFifoLock {
    readonly object gate = new();
    readonly Queue<Waiter> waiters = new();
    bool held;

    internal ValueTask<IAsyncDisposable> AcquireAsync(
        CancellationToken cancellationToken,
        Action? onWait = null) {
        Waiter? waiter;
        lock (gate) {
            if (!held) {
                held = true;
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
            }

            waiter = new Waiter(this, cancellationToken);
            waiters.Enqueue(waiter);
        }

        onWait?.Invoke();
        return new ValueTask<IAsyncDisposable>(waiter.task);
    }

    void Release() {
        lock (gate) {
            while (waiters.Count > 0) {
                if (waiters.Dequeue().TryAcquire()) {
                    return;
                }
            }

            held = false;
        }
    }

    sealed class Waiter {
        readonly TaskCompletionSource<IAsyncDisposable> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly AsyncFifoLock owner;
        readonly CancellationTokenRegistration registration;

        internal Waiter(AsyncFifoLock owner, CancellationToken cancellationToken) {
            this.owner = owner;
            registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        internal Task<IAsyncDisposable> task {
            get => completion.Task;
        }

        internal bool TryAcquire() {
            bool acquired = completion.TrySetResult(new Lease(owner));
            registration.Dispose();
            return acquired;
        }
    }

    sealed class Lease(AsyncFifoLock owner) : IAsyncDisposable {
        int released;

        public ValueTask DisposeAsync() {
            if (Interlocked.Exchange(ref released, 1) == 0) {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
