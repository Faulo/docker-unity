using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace Unity.Tests;

public sealed class DockerEngineClientTests {
    static string validProject {
        get => Path.Combine(AppContext.BaseDirectory, "test-files", "ValidProject");
    }

    [Test]
    public void RejectsEmptyEndpointNames() {
        Assert.That(captureArgumentException(string.Empty, "/tmp/docker.sock"), Is.Not.Null);
        Assert.That(captureArgumentException("docker", string.Empty), Is.Not.Null);

        static ArgumentException? captureArgumentException(string windowsPipeName, string unixSocketPath) {
            try {
                var client = new DockerEngineClient(windowsPipeName, unixSocketPath);
                client.DisposeAsync().GetAwaiter().GetResult();
                return null;
            } catch (ArgumentException exception) {
                return exception;
            }
        }
    }

    [Test]
    public async Task ReadsVersionAndInspectsContainers() {
        await using var engine = new FakeDockerEngine((request, _) => Task.FromResult(request.path switch {
            "/version" => FakeDockerResponse.Json("""{"Os":"linux","ApiVersion":"1.47"}"""),
            "/containers/present/json" => FakeDockerResponse.Json("""{"Id":"present"}"""),
            "/containers/missing/json" => FakeDockerResponse.Json("""{"message":"No such container"}""", HttpStatusCode.NotFound),
            _ => FakeDockerResponse.NotFound()
        }));
        await using var client = engine.CreateClient();

        var version = await client.VersionAsync(CancellationToken.None);
        var present = await client.InspectContainerAsync("present", CancellationToken.None);
        var missing = await client.TryInspectContainerAsync("missing", CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(version["Os"]?.GetValue<string>(), Is.EqualTo("linux"));
            Assert.That(present["Id"]?.GetValue<string>(), Is.EqualTo("present"));
            Assert.That(missing, Is.Null);
        });
    }

    [Test]
    public async Task FindsSelfUsingDockerInspectionCandidates() {
        var candidates = DockerEngineClient.SelfInspectionCandidates(
            Environment.GetEnvironmentVariable("HOSTNAME"),
            Environment.MachineName);
        string expected = candidates[^1];
        await using var engine = new FakeDockerEngine((request, _) => {
            string path = $"/containers/{Uri.EscapeDataString(expected)}/json";
            return Task.FromResult(request.path == path
                ? FakeDockerResponse.Json("""{"Id":"self","Image":"sha256:image"}""")
                : FakeDockerResponse.NotFound());
        });
        await using var client = engine.CreateClient();

        var self = await client.InspectSelfAsync(CancellationToken.None);

        Assert.That(self["Id"]?.GetValue<string>(), Is.EqualTo("self"));
    }

    [Test]
    public async Task CreatesStartsWaitsStopsAndRemovesContainers() {
        await using var engine = new FakeDockerEngine((request, _) => Task.FromResult((request.method, request.path) switch {
            ("POST", "/containers/create?name=worker%20name") => FakeDockerResponse.Json("""{"Id":"created-id"}""", HttpStatusCode.Created),
            ("POST", "/containers/created-id/start") => FakeDockerResponse.Empty(HttpStatusCode.NoContent),
            ("POST", "/containers/created-id/wait?condition=not-running") => FakeDockerResponse.Json("""{"StatusCode":17}"""),
            ("POST", "/containers/created-id/stop?t=2") => FakeDockerResponse.Empty(HttpStatusCode.NotModified),
            ("DELETE", "/containers/created-id?force=1&v=0") => FakeDockerResponse.NotFound(),
            _ => FakeDockerResponse.NotFound()
        }));
        await using var client = engine.CreateClient();
        var configuration = new JsonObject { ["Image"] = "sha256:image", ["Cmd"] = DockerEngineClient.ToArray(["command", "two words"]) };

        string id = await client.CreateContainerAsync("worker name", configuration, CancellationToken.None);
        await client.StartContainerAsync(id, CancellationToken.None);
        int exitCode = await client.WaitContainerAsync(id, CancellationToken.None);
        await client.StopContainerAsync(id, TimeSpan.FromMilliseconds(1001), CancellationToken.None);
        await client.RemoveContainerAsync(id, true, false, CancellationToken.None);

        var create = engine.requests.Single(request => request.path == "/containers/create?name=worker%20name");
        var body = JsonNode.Parse(create.body)!.AsObject();
        Assert.Multiple(() => {
            Assert.That(id, Is.EqualTo("created-id"));
            Assert.That(exitCode, Is.EqualTo(17));
            Assert.That(body["Image"]?.GetValue<string>(), Is.EqualTo("sha256:image"));
            Assert.That(body["Cmd"]?.AsArray().Select(node => node!.GetValue<string>()), Is.EqualTo(new[] { "command", "two words" }));
        });
    }

    [Test]
    public async Task ReadsContainerLogsThroughInjectedEndpoint() {
        byte[] frames = Frames((1, "out"), (2, "error"), (1, "done"));
        await using var engine = new FakeDockerEngine((request, _) => Task.FromResult(
            request.path == "/containers/worker/logs?stdout=1&stderr=1"
                ? FakeDockerResponse.Bytes(frames)
                : FakeDockerResponse.NotFound()));
        await using var client = engine.CreateClient();

        var output = await client.ContainerLogsAsync("worker", CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(output.standardOutput, Is.EqualTo("outdone"));
            Assert.That(output.standardError, Is.EqualTo("error"));
            Assert.That(output.combinedOutput, Is.EqualTo("[stdout]\nout\n[stderr]\nerror\n[stdout]\ndone"));
        });
    }

    [Test]
    public async Task StreamsAndExtractsContainerArchive() {
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, true)) {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "index.html") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("webgl fixture")) };
            writer.WriteEntry(entry);
        }

        byte[] bytes = archive.ToArray();
        await using var engine = new FakeDockerEngine((request, _) => Task.FromResult(
            request.path == "/containers/worker/archive?path=%2Ftmp%2Fbuild%2F."
                ? FakeDockerResponse.Bytes(bytes)
                : FakeDockerResponse.NotFound()));
        await using var client = engine.CreateClient();
        string destination = Path.Combine(Path.GetTempPath(), "unity-archive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try {
            await client.ExtractArchiveAsync("worker", "/tmp/build/.", destination, CancellationToken.None);

            Assert.That(await File.ReadAllTextAsync(Path.Combine(destination, "index.html")), Is.EqualTo("webgl fixture"));
        } finally {
            Directory.Delete(destination, true);
        }
    }

    [Test]
    public async Task ExecutesCommandAndReturnsDockerExitStatus() {
        byte[] frames = Frames((1, "standard"), (2, "problem"));
        await using var engine = new FakeDockerEngine((request, _) => Task.FromResult((request.method, request.path) switch {
            ("POST", "/containers/worker/exec") => FakeDockerResponse.Json("""{"Id":"exec-id"}""", HttpStatusCode.Created),
            ("POST", "/exec/exec-id/start") => FakeDockerResponse.Bytes(frames),
            ("GET", "/exec/exec-id/json") => FakeDockerResponse.Json("""{"ExitCode":23}"""),
            _ => FakeDockerResponse.NotFound()
        }));
        await using var client = engine.CreateClient();

        var result = await client.ExecAsync("worker", "/workspace with space", ["tool", "", "two words", "--"], CancellationToken.None);

        var create = engine.requests.Single(request => request.path == "/containers/worker/exec");
        var createBody = JsonNode.Parse(create.body)!.AsObject();
        var start = engine.requests.Single(request => request.path == "/exec/exec-id/start");
        var startBody = JsonNode.Parse(start.body)!.AsObject();
        Assert.Multiple(() => {
            Assert.That(result.id, Is.EqualTo("exec-id"));
            Assert.That(result.exitCode, Is.EqualTo(23));
            Assert.That(result.standardOutput, Is.EqualTo("standard"));
            Assert.That(result.standardError, Is.EqualTo("problem"));
            Assert.That(createBody["WorkingDir"]?.GetValue<string>(), Is.EqualTo("/workspace with space"));
            Assert.That(createBody["Cmd"]?.AsArray().Select(node => node!.GetValue<string>()), Is.EqualTo(new[] { "tool", "", "two words", "--" }));
            Assert.That(createBody["Tty"]?.GetValue<bool>(), Is.False);
            Assert.That(startBody["Detach"]?.GetValue<bool>(), Is.False);
            Assert.That(startBody["Tty"]?.GetValue<bool>(), Is.False);
        });
    }

    [Test]
    public async Task ReportsDockerJsonErrorDetails() {
        await using var engine = new FakeDockerEngine((_, _) => Task.FromResult(
            FakeDockerResponse.Json("""{"message":"specific engine failure"}""", HttpStatusCode.InternalServerError)));
        await using var client = engine.CreateClient();

        DockerApiException? exception = null;
        try {
            await client.VersionAsync(CancellationToken.None);
        } catch (DockerApiException caught) {
            exception = caught;
        }

        Assert.Multiple(() => {
            Assert.That(exception, Is.Not.Null);
            Assert.That(exception?.statusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(exception?.Message, Does.Contain("specific engine failure"));
        });
    }

    [Test]
    public async Task CancelsRequestWaitingForDockerResponse() {
        await using var engine = new FakeDockerEngine(async (_, cancellationToken) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return FakeDockerResponse.Empty(HttpStatusCode.OK);
        });
        await using var client = engine.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        OperationCanceledException? exception = null;
        try {
            await client.VersionAsync(cancellation.Token);
        } catch (OperationCanceledException caught) {
            exception = caught;
        }

        Assert.That(exception, Is.Not.Null);
    }

    [Test]
    public async Task OrchestratesProbeLockWorkerReuseAndExecThroughInjectedEndpoint() {
        bool windowsContainers = OperatingSystem.IsWindows();
        string selfId = "controller-id";
        string imageId = "sha256:image";
        string probeRoot = windowsContainers ? @"C:\unity-probe" : "/unity-probe";
        string workerRoot = windowsContainers ? @"C:\workspace\project" : "/var/workspace/project";
        string unityDestination = windowsContainers ? @"C:\Program Files\Unity\Hub\Editor" : "/root/Unity";
        string dockerDestination = windowsContainers ? @"\\.\pipe\docker_engine" : "/var/run/docker.sock";
        var selfCandidates = DockerEngineClient.SelfInspectionCandidates(
                Environment.GetEnvironmentVariable("HOSTNAME"),
                Environment.MachineName)
            .ToHashSet(StringComparer.Ordinal);
        var configurations = new ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal);
        var running = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        int probeCreates = 0;
        int probeDeletes = 0;
        int lockCreates = 0;
        int lockDeletes = 0;
        int workerCreates = 0;
        int workerDeletes = 0;
        int execCreates = 0;
        JsonObject? probeConfiguration = null;
        JsonObject? workerConfiguration = null;
        string probeOutput = JsonSerializer.Serialize(ProjectProbe.Read(validProject));

        await using var engine = new FakeDockerEngine((request, cancellationToken) => {
            _ = cancellationToken;
            if (request.path == "/version") {
                return Task.FromResult(FakeDockerResponse.Json(new JsonObject { ["Os"] = windowsContainers ? "windows" : "linux" }.ToJsonString()));
            }

            if (request.method == "POST" && request.path.StartsWith("/containers/create?name=", StringComparison.Ordinal)) {
                string name = Uri.UnescapeDataString(request.path["/containers/create?name=".Length..]);
                var configuration = JsonNode.Parse(request.body)!.AsObject();
                configurations[name] = configuration;
                running[name] = false;
                if (name.StartsWith("unity-probe-", StringComparison.Ordinal)) {
                    probeCreates++;
                    probeConfiguration = configuration;
                } else if (name.StartsWith("unity-lock-", StringComparison.Ordinal)) {
                    lockCreates++;
                } else if (name.StartsWith("unity-worker-", StringComparison.Ordinal)) {
                    workerCreates++;
                    workerConfiguration = configuration;
                }

                return Task.FromResult(FakeDockerResponse.Json(new JsonObject { ["Id"] = name }.ToJsonString(), HttpStatusCode.Created));
            }

            if (TryContainerName(request.path, "/json", out string inspectedName)) {
                if (selfCandidates.Contains(inspectedName)) {
                    var self = new JsonObject {
                        ["Id"] = selfId,
                        ["Image"] = imageId,
                        ["Config"] = new JsonObject {
                            ["Env"] = DockerEngineClient.ToArray([
                                "UNITY_NO_GRAPHICS=1",
                                "UNRELATED_SECRET=hidden",
                                "UNITY_CALL_TIMEOUT=30"
                            ])
                        },
                        ["HostConfig"] = new JsonObject { ["Memory"] = 1_048_576, ["ShmSize"] = 67_108_864 },
                        ["Mounts"] = new JsonArray {
                            new JsonObject { ["Type"] = "volume", ["Name"] = "unity-test-volume", ["Destination"] = unityDestination, ["RW"] = true },
                            new JsonObject { ["Type"] = "bind", ["Source"] = dockerDestination, ["Destination"] = dockerDestination, ["RW"] = true }
                        }
                    };
                    return Task.FromResult(FakeDockerResponse.Json(self.ToJsonString()));
                }

                if (!configurations.TryGetValue(inspectedName, out var configuration)) {
                    return Task.FromResult(FakeDockerResponse.NotFound());
                }

                if (inspectedName.StartsWith("unity-probe-", StringComparison.Ordinal)) {
                    var inspected = new JsonObject { ["Id"] = inspectedName, ["Mounts"] = new JsonArray { new JsonObject { ["Source"] = validProject, ["Destination"] = probeRoot } } };
                    return Task.FromResult(FakeDockerResponse.Json(inspected.ToJsonString()));
                }

                var container = new JsonObject {
                    ["Id"] = inspectedName,
                    ["Image"] = imageId,
                    ["Config"] = new JsonObject { ["Labels"] = configuration["Labels"]?.DeepClone() },
                    ["State"] = new JsonObject { ["Running"] = running[inspectedName] }
                };
                return Task.FromResult(FakeDockerResponse.Json(container.ToJsonString()));
            }

            if (TryContainerName(request.path, "/start", out string startedName) && request.method == "POST") {
                running[startedName] = true;
                return Task.FromResult(FakeDockerResponse.Empty(HttpStatusCode.NoContent));
            }

            if (TryContainerName(request.path, "/wait?condition=not-running", out _)) {
                return Task.FromResult(FakeDockerResponse.Json("""{"StatusCode":0}"""));
            }

            if (TryContainerName(request.path, "/logs?stdout=1&stderr=1", out string loggedName)
                && loggedName.StartsWith("unity-probe-", StringComparison.Ordinal)) {
                return Task.FromResult(FakeDockerResponse.Bytes(Frames((1, probeOutput))));
            }

            if (request.method == "DELETE" && request.path.StartsWith("/containers/", StringComparison.Ordinal)) {
                string name = Uri.UnescapeDataString(request.path["/containers/".Length..request.path.IndexOf('?')]);
                if (name.StartsWith("unity-probe-", StringComparison.Ordinal)) {
                    probeDeletes++;
                } else if (name.StartsWith("unity-lock-", StringComparison.Ordinal)) {
                    lockDeletes++;
                } else if (name.StartsWith("unity-worker-", StringComparison.Ordinal)) {
                    workerDeletes++;
                }

                configurations.TryRemove(name, out _);
                running.TryRemove(name, out _);
                return Task.FromResult(FakeDockerResponse.Empty(HttpStatusCode.NoContent));
            }

            if (request.method == "POST"
                && request.path.StartsWith("/containers/unity-worker-", StringComparison.Ordinal)
                && request.path.EndsWith("/exec", StringComparison.Ordinal)) {
                execCreates++;
                return Task.FromResult(FakeDockerResponse.Json(new JsonObject { ["Id"] = $"exec-{execCreates}" }.ToJsonString(), HttpStatusCode.Created));
            }

            if (request.method == "POST" && request.path.StartsWith("/exec/exec-", StringComparison.Ordinal) && request.path.EndsWith("/start", StringComparison.Ordinal)) {
                return Task.FromResult(FakeDockerResponse.Bytes(Frames((1, "method output"), (2, "method warning"))));
            }

            if (request.method == "GET" && request.path.StartsWith("/exec/exec-", StringComparison.Ordinal) && request.path.EndsWith("/json", StringComparison.Ordinal)) {
                return Task.FromResult(FakeDockerResponse.Json("""{"ExitCode":7}"""));
            }

            return Task.FromResult(FakeDockerResponse.NotFound());
        });
        var credentials = RuntimeCredentials.Resolve(
            name => name switch {
                "UNITY_CREDENTIALS_USR" => "worker-unity-user",
                "UNITY_CREDENTIALS_PSW" => "worker-unity-password",
                "EMAIL_CREDENTIALS_USR" => "worker-email-user",
                "EMAIL_CREDENTIALS_PSW" => "worker-email-password",
                _ => null
            },
            _ => throw new InvalidOperationException());
        await using var controller = await engine.CreateControllerAsync(credentials, CancellationToken.None);

        var progress = new RecordingProgress();
        object first = await controller.ExecuteMethodAsync(validProject, "Example.Build", ["", "two words", "--"], progress, CancellationToken.None);
        object second = await controller.ExecuteMethodAsync(validProject, "Example.Build", ["again"], NoProgress.instance, CancellationToken.None);
        int workerCreatesAfterReuse = workerCreates;
        string workerName = configurations.Keys.Single(name => name.StartsWith("unity-worker-", StringComparison.Ordinal));
        configurations[workerName]["Labels"]!["net.slothsoft.unity.worker-configuration"] = "stale";
        object third = await controller.ExecuteMethodAsync(validProject, "Example.Build", ["after-change"], NoProgress.instance, CancellationToken.None);

        var firstResult = JsonNode.Parse(JsonSerializer.Serialize(first))!.AsObject();
        var probeMount = probeConfiguration!["HostConfig"]!["Mounts"]![0]!;
        var workerMounts = workerConfiguration!["HostConfig"]!["Mounts"]!.AsArray();
        string[] workerEnvironment = workerConfiguration["Env"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        var execRequest = engine.requests.First(request => request.path.EndsWith("/exec", StringComparison.Ordinal));
        var execBody = JsonNode.Parse(execRequest.body)!.AsObject();
        string unityExecutable = windowsContainers ? "unity.exe" : "unity";
        Assert.Multiple(() => {
            Assert.That(probeCreates, Is.EqualTo(1), "The validated project should be cached.");
            Assert.That(probeDeletes, Is.EqualTo(1));
            Assert.That(lockCreates, Is.EqualTo(3));
            Assert.That(lockDeletes, Is.EqualTo(3));
            Assert.That(workerCreatesAfterReuse, Is.EqualTo(1), "A matching retained worker should be reused.");
            Assert.That(workerCreates, Is.EqualTo(2), "A worker with a stale configuration fingerprint should be replaced.");
            Assert.That(workerDeletes, Is.EqualTo(1));
            Assert.That(execCreates, Is.EqualTo(3));
            Assert.That(progress.Snapshot().Select(value => value.Message),
                Is.EqualTo(new[] { "Validating Unity project", "Preparing or reusing Unity worker", "Invoking Unity editor method", "Preparing method result" }));
            Assert.That(firstResult["exitStatus"]?.GetValue<int>(), Is.EqualTo(7));
            Assert.That(firstResult["output"]?.GetValue<string>(), Is.EqualTo("method output"));
            Assert.That(firstResult["errorOutput"]?.GetValue<string>(), Is.EqualTo("method warning"));
            Assert.That(JsonNode.Parse(JsonSerializer.Serialize(second))!["exitStatus"]?.GetValue<int>(), Is.EqualTo(7));
            Assert.That(JsonNode.Parse(JsonSerializer.Serialize(third))!["exitStatus"]?.GetValue<int>(), Is.EqualTo(7));
            Assert.That(workerConfiguration["Image"]?.GetValue<string>(), Is.EqualTo(imageId));
            Assert.That(workerConfiguration["Labels"]?["net.slothsoft.unity.worker-configuration"]?.GetValue<string>(),
                Does.Match("^[0-9a-f]{64}$"));
            Assert.That(workerConfiguration["HostConfig"]?["Memory"]?.GetValue<long>(), Is.EqualTo(1_048_576));
            Assert.That(workerConfiguration["HostConfig"]?["ShmSize"]?.GetValue<long>(), Is.EqualTo(67_108_864));
            Assert.That(workerEnvironment, Is.EqualTo(new[] { "UNITY_CALL_TIMEOUT=30", "UNITY_NO_GRAPHICS=1", "DOTNET_GCHeapCount=2" }));
            Assert.That(probeMount["ReadOnly"]?.GetValue<bool>(), Is.True);
            Assert.That(probeMount["BindOptions"]?["CreateMountpoint"]?.GetValue<bool>(), Is.False);
            Assert.That(workerMounts, Has.Count.EqualTo(4));
            Assert.That(workerMounts.Take(3).All(node => node?["ReadOnly"]?.GetValue<bool>() == false), Is.True);
            Assert.That(workerMounts.Any(node => node?["Target"]?.GetValue<string>() == dockerDestination), Is.False);
            Assert.That(workerMounts.Any(node => node?["Target"]?.GetValue<string>() == unityDestination), Is.True);
            Assert.That(execBody["WorkingDir"]?.GetValue<string>(), Is.EqualTo(workerRoot));
            Assert.That(execBody["Cmd"]?.AsArray().Select(node => node!.GetValue<string>()),
                Is.EqualTo(new[] { unityExecutable, "method", workerRoot, "Example.Build", "--", "", "two words", "--" }));
            Assert.That(execBody["Env"]?.AsArray().Select(node => node!.GetValue<string>()), Is.EqualTo(new[] {
                "UNITY_CREDENTIALS_USR=worker-unity-user",
                "UNITY_CREDENTIALS_PSW=worker-unity-password",
                "EMAIL_CREDENTIALS_USR=worker-email-user",
                "EMAIL_CREDENTIALS_PSW=worker-email-password"
            }));
            Assert.That(workerConfiguration.ToJsonString(), Does.Not.Contain("worker-unity-user"));
            Assert.That(workerConfiguration.ToJsonString(), Does.Not.Contain("worker-unity-password"));
            Assert.That(workerConfiguration.ToJsonString(), Does.Not.Contain("worker-email-user"));
            Assert.That(workerConfiguration.ToJsonString(), Does.Not.Contain("worker-email-password"));
        });
    }

    static bool TryContainerName(string path, string suffix, out string name) {
        const string prefix = "/containers/";
        if (path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal)) {
            name = Uri.UnescapeDataString(path[prefix.Length..^suffix.Length]);
            return true;
        }

        name = string.Empty;
        return false;
    }

    static byte[] Frames(params (byte stream, string content)[] values) {
        using var result = new MemoryStream();
        byte[] header = new byte[8];
        foreach (var value in values) {
            byte[] content = Encoding.UTF8.GetBytes(value.content);
            Array.Clear(header);
            header[0] = value.stream;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), content.Length);
            result.Write(header);
            result.Write(content);
        }

        return result.ToArray();
    }
}

sealed record FakeDockerRequest(string method, string path, string body);

sealed class NoProgress : IProgress<ProgressNotificationValue> {
    internal static NoProgress instance { get; } = new();

    public void Report(ProgressNotificationValue value) {
    }
}

sealed record FakeDockerResponse(HttpStatusCode statusCode, string contentType, byte[] body) {
    internal static FakeDockerResponse Bytes(byte[] body) => new(HttpStatusCode.OK, "application/octet-stream", body);

    internal static FakeDockerResponse Empty(HttpStatusCode statusCode) => new(statusCode, "application/json", []);

    internal static FakeDockerResponse Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode, "application/json", Encoding.UTF8.GetBytes(body));

    internal static FakeDockerResponse NotFound() => Json("""{"message":"not found"}""", HttpStatusCode.NotFound);
}

sealed class FakeDockerEngine : IAsyncDisposable {
    readonly Func<FakeDockerRequest, CancellationToken, Task<FakeDockerResponse>> handler;
    readonly string pipeName = "unity-tests-" + Guid.NewGuid().ToString("N");
    readonly Task server;
    readonly CancellationTokenSource stopping = new();
    readonly string unixSocketPath = Path.Combine(Path.GetTempPath(), "cu-" + Guid.NewGuid().ToString("N") + ".sock");
    Socket? listener;

    internal FakeDockerEngine(Func<FakeDockerRequest, CancellationToken, Task<FakeDockerResponse>> handler) {
        this.handler = handler;
        server = OperatingSystem.IsWindows() ? RunWindowsAsync() : RunUnixAsync();
    }

    internal ConcurrentQueue<FakeDockerRequest> requests { get; } = new();

    public async ValueTask DisposeAsync() {
        await stopping.CancelAsync();
        listener?.Dispose();
        try {
            await server;
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
        } catch (ObjectDisposedException) when (stopping.IsCancellationRequested) {
        }

        stopping.Dispose();
        if (File.Exists(unixSocketPath)) {
            File.Delete(unixSocketPath);
        }
    }

    internal DockerEngineClient CreateClient() => new(pipeName, unixSocketPath);

    internal Task<UnityMcpController> CreateControllerAsync(CancellationToken cancellationToken) =>
        CreateControllerAsync(RuntimeCredentials.empty, cancellationToken);

    internal Task<UnityMcpController> CreateControllerAsync(
        RuntimeCredentials credentials,
        CancellationToken cancellationToken) =>
        UnityMcpController.CreateAsync(credentials, cancellationToken, pipeName, unixSocketPath);

    async Task RunWindowsAsync() {
        while (!stopping.IsCancellationRequested) {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(stopping.Token);
            await HandleConnectionAsync(pipe);
        }
    }

    async Task RunUnixAsync() {
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(unixSocketPath));
        listener.Listen();
        while (!stopping.IsCancellationRequested) {
            using var socket = await listener.AcceptAsync(stopping.Token);
            await using var stream = new NetworkStream(socket, false);
            await HandleConnectionAsync(stream);
        }
    }

    async Task HandleConnectionAsync(Stream stream) {
        try {
            await HandleAsync(stream);
        } catch (IOException) {
            // Request cancellation closes the client transport and is not a server failure.
        }
    }

    async Task HandleAsync(Stream stream) {
        string requestLine = await ReadLineAsync(stream, stopping.Token);
        string[] requestParts = requestLine.Split(' ', 3);
        if (requestParts.Length != 3) {
            throw new InvalidOperationException($"Invalid HTTP request line: {requestLine}");
        }

        int contentLength = 0;
        while (true) {
            string header = await ReadLineAsync(stream, stopping.Token);
            if (header.Length == 0) {
                break;
            }

            int separator = header.IndexOf(':');
            if (separator > 0
                && header[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) {
                contentLength = int.Parse(header[(separator + 1)..].Trim());
            }
        }

        byte[] body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, stopping.Token);
        var request = new FakeDockerRequest(requestParts[0], requestParts[1], Encoding.UTF8.GetString(body));
        requests.Enqueue(request);
        var response = await handler(request, stopping.Token);
        string reason = response.statusCode switch {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.Created => "Created",
            HttpStatusCode.NoContent => "No Content",
            HttpStatusCode.NotModified => "Not Modified",
            HttpStatusCode.NotFound => "Not Found",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => response.statusCode.ToString()
        };
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)response.statusCode} {reason}\r\n" +
            $"Content-Type: {response.contentType}\r\n" +
            $"Content-Length: {response.body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, stopping.Token);
        await stream.WriteAsync(response.body, stopping.Token);
        await stream.FlushAsync(stopping.Token);
    }

    static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken) {
        using var line = new MemoryStream();
        int previous = -1;
        while (true) {
            byte[] value = new byte[1];
            int read = await stream.ReadAsync(value, cancellationToken);
            if (read == 0) {
                throw new EndOfStreamException("HTTP request ended before its headers were complete.");
            }

            if (previous == '\r' && value[0] == '\n') {
                byte[] bytes = line.ToArray();
                return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
            }

            line.WriteByte(value[0]);
            previous = value[0];
        }
    }
}
