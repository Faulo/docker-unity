using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Unity.DaemonTests;

[NUnit.Framework.Category("DockerDaemon")]
[TestFixture("linux", "linux")]
[TestFixture("windows", "windows")]
[CancelAfter(10 * 60 * 1000)]
public sealed partial class DockerDaemonTests(string expectedOs, string dockerContext) {
    [OneTimeSetUp]
    public async Task SetUpDaemonAsync() {
        string? requiredOs = Environment.GetEnvironmentVariable(REQUIRED_OS_ENVIRONMENT);
        if (!string.IsNullOrEmpty(requiredOs)
            && !requiredOs.Equals("linux", StringComparison.OrdinalIgnoreCase)
            && !requiredOs.Equals("windows", StringComparison.OrdinalIgnoreCase)) {
            Assert.Fail($"{REQUIRED_OS_ENVIRONMENT} must be 'linux', 'windows', or unset, but was '{requiredOs}'.");
        }

        repository = FindRepository();
        project = Path.Combine(repository, "common", "Unity.Tests", "test-files", "ValidProject");
        staging = Path.Combine(Path.GetTempPath(), $"unity-daemon-tests-{id}");
        image = $"tmp/unity-daemon-tests:{id}";
        container = $"tmp-unity-daemon-{id}";

        var inspection = await RunAsync("docker", ["context", "inspect", dockerContext, "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(5));
        if (inspection.exitCode != 0) {
            IgnoreOrFail($"Docker context '{dockerContext}' is unavailable: {Detail(inspection)}");
        }

        string contextEndpoint = inspection.standardOutput.Trim();
        if (!IsLocalEndpoint(contextEndpoint)) {
            IgnoreOrFail($"Docker context '{dockerContext}' is not local: {contextEndpoint}");
        }

        var version = await RunDockerAsync(["version", "--format", "{{.Server.Os}}"], TimeSpan.FromSeconds(5));
        if (version.exitCode != 0) {
            IgnoreOrFail($"Docker context '{dockerContext}' is offline: {Detail(version)}");
        }

        Assert.That(version.standardOutput.Trim(), Is.EqualTo(expectedOs).IgnoreCase,
            $"Docker context '{dockerContext}' does not target the expected container OS.");

        try {
            Directory.CreateDirectory(Path.Combine(staging, "controller"));
            Directory.CreateDirectory(Path.Combine(staging, "backend"));
            string runtime = expectedOs == "windows" ? "win-x64" : "linux-x64";
            await PublishAsync(Path.Combine(repository, "common", "Unity", "Unity.csproj"), runtime, Path.Combine(staging, "controller"));
            await PublishAsync(Path.Combine(repository, "common", "Unity.DaemonTests.Backend", "Unity.DaemonTests.Backend.csproj"), runtime, Path.Combine(staging, "backend"));

            string dockerfile = Path.Combine(repository, "common", "Unity.DaemonTests", $"Dockerfile.{expectedOs}");
            File.Copy(dockerfile, Path.Combine(staging, "Dockerfile"));
            AssertDockerSucceeded(await RunDockerAsync(["build", "--tag", image, staging], TimeSpan.FromMinutes(5)), "build daemon-test image");
            imageBuilt = true;

            string dockerMount = expectedOs == "windows"
                ? WindowsDockerMount(contextEndpoint)
                : "type=bind,source=/var/run/docker.sock,target=/var/run/docker.sock";
            var runArguments = new List<string> {
                "run",
                "--detach",
                "--name",
                container,
                "--env",
                "UNITY_MCP=1",
                "--env",
                "UNITY_CREDENTIALS_USR=daemon-unity-user",
                "--env",
                "UNITY_CREDENTIALS_PSW=daemon-unity-password",
                "--env",
                "EMAIL_CREDENTIALS_USR=daemon-email-user",
                "--env",
                "EMAIL_CREDENTIALS_PSW=daemon-email-password"
            };
            if (expectedOs == "linux") {
                runArguments.AddRange(["--publish", "127.0.0.1::8080"]);
            }

            runArguments.AddRange(["--mount", dockerMount, image]);
            AssertDockerSucceeded(await RunDockerAsync(runArguments, TimeSpan.FromMinutes(1)), "start daemon-test controller");
            controllerStarted = true;

            var controllerInspection = await DockerCheckedAsync(["inspect", "--format", "{{.Id}}", container]);
            controllerId = controllerInspection.standardOutput.Trim();
            await WaitForHealthyControllerAsync();
            await ConfigureMcpClientAsync();
            await InitializeMcpAsync();
        } catch {
            await CleanupAsync();
            throw;
        }
    }

    [OneTimeTearDown]
    public async Task TearDownDaemonAsync() {
        httpClient?.Dispose();
        await CleanupAsync();
    }

    const string REQUIRED_OS_ENVIRONMENT = "UNITY_DAEMON_TEST_REQUIRED_OS";
    readonly string id = Guid.NewGuid().ToString("N");
    string repository = string.Empty;
    string project = string.Empty;
    string staging = string.Empty;
    string image = string.Empty;
    string container = string.Empty;
    string? controllerId;
    HttpClient? httpClient;
    Uri? endpoint;
    int requestId;
    bool imageBuilt;
    bool controllerStarted;

    [Test]
    public async Task AdvertisesToolsAndReportsProjectInformation() {
        var tools = await InvokeMcpAsync("tools/list", new JsonObject());
        string[] names = tools["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.That(names, Is.EqualTo(new[] { "build_and_serve_webgl", "execute_method", "get_project_info", "run_tests" }));
        var webGlTool = tools["result"]!["tools"]!.AsArray().Single(tool => tool!["name"]!.GetValue<string>() == "build_and_serve_webgl")!;
        Assert.That(webGlTool["inputSchema"]!["properties"]!.AsObject().Select(property => property.Key), Is.EqualTo(new[] { "projectRoot" }));

        var response = await CallToolAsync("get_project_info", new JsonObject { ["projectRoot"] = project });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        using (Assert.EnterMultipleScope()) {
            Assert.That(ExpectedProjectRoots(project, expectedOs), Does.Contain(result["projectRoot"]!.GetValue<string>()));
            Assert.That(result["projectName"]!.GetValue<string>(), Is.EqualTo("Example Game"));
            Assert.That(result["editor"]!["version"]!.GetValue<string>(), Is.EqualTo("6000.3.13f1"));
            Assert.That(result["rendering"]!["renderPipeline"]!.GetValue<string>(), Is.EqualTo("Universal"));
            Assert.That(result["packages"]!["custom"]!.GetValue<string>(), Is.EqualTo("1.2.3"));
        }
    }

    [Test]
    public async Task PreservesMethodArgumentsOutputAndExitStatus() {
        string[] arguments = [string.Empty, "two words", "--", "\"quoted\""];
        var response = await CallToolAsync("execute_method",
            new JsonObject { ["projectRoot"] = project, ["method"] = "DaemonTests.Arguments", ["arguments"] = JsonSerializer.SerializeToNode(arguments) });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        var backend = JsonNode.Parse(result["output"]!.GetValue<string>())!;
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["exitStatus"]!.GetValue<int>(), Is.EqualTo(7));
            Assert.That(result["errorOutput"]!.GetValue<string>(), Is.EqualTo("daemon-test stderr"));
            Assert.That(backend["method"]!.GetValue<string>(), Is.EqualTo("DaemonTests.Arguments"));
            Assert.That(backend["arguments"]!.Deserialize<string[]>(), Is.EqualTo(arguments));
            Assert.That(backend["credentials"]!["unity"]!.GetValue<bool>(), Is.True);
            Assert.That(backend["credentials"]!["email"]!.GetValue<bool>(), Is.True);
        }
    }

    [Test]
    public async Task IsolatesCredentialsForMultipleClientsUsingTheSameRetainedWorker() {
        await ExecuteMethodAsync("DaemonTests.Fallback");
        string worker = await SingleWorkerAsync();

        var clientA = CallToolAsync("execute_method", new JsonObject {
            ["projectRoot"] = project,
            ["method"] = "DaemonTests.ClientA",
            ["arguments"] = new JsonArray()
        }, CredentialHeaders("client-a"));
        var clientB = CallToolAsync("execute_method", new JsonObject {
            ["projectRoot"] = project,
            ["method"] = "DaemonTests.ClientB",
            ["arguments"] = new JsonArray()
        }, CredentialHeaders("client-b"));

        foreach (var response in await Task.WhenAll(clientA, clientB)) {
            Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
            var backend = JsonNode.Parse(ToolResult(response)["output"]!.GetValue<string>())!;
            Assert.Multiple(() => {
                Assert.That(backend["credentials"]!["unity"]!.GetValue<bool>(), Is.True);
                Assert.That(backend["credentials"]!["email"]!.GetValue<bool>(), Is.True);
            });
        }

        Assert.That(await SingleWorkerAsync(), Is.EqualTo(worker));
    }

    [Test]
    public async Task ExecutesWithAnIncompleteRequestCredentialSet() {
        var response = await CallToolAsync("execute_method", new JsonObject {
            ["projectRoot"] = project,
            ["method"] = "DaemonTests.Partial",
            ["arguments"] = new JsonArray()
        }, new Dictionary<string, string> { ["X-Unity-Credentials-Usr"] = string.Empty });

        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var backend = JsonNode.Parse(ToolResult(response)["output"]!.GetValue<string>())!;
        Assert.Multiple(() => {
            Assert.That(backend["credentials"]!["unity"]!.GetValue<bool>(), Is.False);
            Assert.That(backend["credentials"]!["email"]!.GetValue<bool>(), Is.True);
        });
    }

    [Test]
    public async Task StreamsCorrelatedProgressBeforeTheFinalToolResult() {
        string progressToken = $"daemon-progress-{expectedOs}";
        var events = await InvokeMcpEventsAsync("tools/call",
            new JsonObject {
                ["name"] = "execute_method",
                ["arguments"] = new JsonObject { ["projectRoot"] = project, ["method"] = "DaemonTests.Progress", ["arguments"] = new JsonArray() },
                ["_meta"] = new JsonObject { ["progressToken"] = progressToken }
            });
        var notifications = events
            .Where(item => item["method"]?.GetValue<string>() == "notifications/progress")
            .ToArray();
        int finalIndex = events.FindIndex(item => item["id"] is not null);
        float[] values = notifications
            .Select(item => item["params"]!["progress"]!.GetValue<float>())
            .ToArray();

        Assert.That(notifications, Is.Not.Empty, $"No progress notifications were received: {JsonSerializer.Serialize(events)}");
        using (Assert.EnterMultipleScope()) {
            Assert.That(finalIndex, Is.EqualTo(events.Count - 1), "The final tool response must follow every progress notification.");
            Assert.That(notifications.All(item => item["params"]!["progressToken"]!.GetValue<string>() == progressToken), Is.True);
            Assert.That(values.Zip(values.Skip(1), (first, second) => second > first), Is.All.True);
            Assert.That(notifications.All(item => item["params"]!["total"] is null), Is.True);
        }

        var response = events[finalIndex];
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["exitStatus"]!.GetValue<int>(), Is.EqualTo(7));
            Assert.That(result["errorOutput"]!.GetValue<string>(), Is.EqualTo("daemon-test stderr"));
            Assert.That(JsonNode.Parse(result["output"]!.GetValue<string>())!["method"]!.GetValue<string>(), Is.EqualTo("DaemonTests.Progress"));
        }
    }

    [Test]
    public async Task NativeLauncherPreservesWorkingDirectory() {
        string workingDirectory = expectedOs == "windows" ? @"C:\Windows\Temp" : "/tmp";
        string executable = expectedOs == "windows"
            ? @"C:\Windows\unity.exe"
            : "/usr/local/bin/unity";
        var invocation = await RunDockerAsync([
            "exec",
            "--workdir",
            workingDirectory,
            container,
            executable,
            "method",
            workingDirectory,
            "DaemonTests.Arguments",
            "--"
        ], TimeSpan.FromSeconds(30));
        Assert.That(invocation.exitCode, Is.EqualTo(7), invocation.standardError);
        var backend = JsonNode.Parse(invocation.standardOutput)!;
        using (Assert.EnterMultipleScope()) {
            Assert.That(backend["workingDirectory"]!.GetValue<string>(), Is.EqualTo(workingDirectory));
            Assert.That(backend["composerProject"]!.GetValue<string>(), Is.EqualTo(expectedOs == "windows"
                ? @"C:\unity\composer.json"
                : "/unity/composer.json"));
            Assert.That(backend["composerVendorDirectory"]!.GetValue<string>(), Is.EqualTo(expectedOs == "windows"
                ? @"C:\unity\vendor"
                : "/unity/vendor"));
            Assert.That(backend["credentials"]!["unity"]!.GetValue<bool>(), Is.True);
            Assert.That(backend["credentials"]!["email"]!.GetValue<bool>(), Is.True);
        }
    }

    [Test]
    public async Task ReusesWorkerAndMountsOnlyProjectDirectories() {
        await ExecuteMethodAsync("DaemonTests.First");
        string workerBefore = await SingleWorkerAsync();
        string equivalentProjectRoot = await NormalizedProjectRootAsync();
        await ExecuteMethodAsync("DaemonTests.Reuse", equivalentProjectRoot);
        string workerAfter = await SingleWorkerAsync();
        Assert.That(workerAfter, Is.EqualTo(workerBefore), "The retained worker was not reused.");

        var mounts = JsonNode.Parse((await DockerCheckedAsync(["inspect", "--format", "{{json .Mounts}}", workerBefore])).standardOutput)!.AsArray();
        string fingerprint = (await DockerCheckedAsync([
            "inspect", "--format", "{{index .Config.Labels \"net.slothsoft.unity.worker-configuration\"}}", workerBefore
        ])).standardOutput.Trim();
        string workerConfiguration = (await DockerCheckedAsync([
            "inspect", "--format", "{{json .Config}}", workerBefore
        ])).standardOutput;
        string[] destinations = mounts.Select(mount => mount!["Destination"]!.GetValue<string>()).ToArray();
        int projectMounts = destinations.Count(destination => ProjectDirectoryRegex().IsMatch(destination));
        int dockerMounts = destinations.Count(destination => destination.Equals("/var/run/docker.sock", StringComparison.Ordinal)
                                                             || destination.Equals(@"\\.\pipe\docker_engine", StringComparison.OrdinalIgnoreCase));
        using (Assert.EnterMultipleScope()) {
            Assert.That(projectMounts, Is.EqualTo(3));
            Assert.That(dockerMounts, Is.Zero, "The worker must not receive the controller's Docker endpoint.");
            Assert.That(fingerprint, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(workerConfiguration, Does.Not.Contain("daemon-unity-user"));
            Assert.That(workerConfiguration, Does.Not.Contain("daemon-unity-password"));
            Assert.That(workerConfiguration, Does.Not.Contain("daemon-email-user"));
            Assert.That(workerConfiguration, Does.Not.Contain("daemon-email-password"));
        }
    }

    [Test]
    public async Task KeepsRequestCredentialsOutOfControllerConfiguration() {
        string configuration = (await DockerCheckedAsync([
            "inspect", "--format", "{{json .Config.Env}}", container
        ])).standardOutput;

        Assert.Multiple(() => {
            Assert.That(configuration, Does.Not.Contain("client-a-unity-user"));
            Assert.That(configuration, Does.Not.Contain("client-a-unity-password"));
            Assert.That(configuration, Does.Not.Contain("client-a-email-user"));
            Assert.That(configuration, Does.Not.Contain("client-a-email-password"));
            Assert.That(configuration, Does.Not.Contain("client-b-unity-user"));
            Assert.That(configuration, Does.Not.Contain("client-b-unity-password"));
            Assert.That(configuration, Does.Not.Contain("client-b-email-user"));
            Assert.That(configuration, Does.Not.Contain("client-b-email-password"));
        });
    }

    [Test]
    public async Task ParsesTestResultsFromWorker() {
        var response = await CallToolAsync("run_tests", new JsonObject { ["projectRoot"] = project, ["modes"] = new JsonArray("EditMode", "Play Mode") });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["outcome"]!.GetValue<string>(), Is.EqualTo("passed"));
            Assert.That(result["counts"]!["total"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(result["counts"]!["passed"]!.GetValue<int>(), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task BuildsAndServesUnityWebGlContent() {
        var response = await CallToolAsync("build_and_serve_webgl", new JsonObject { ["projectRoot"] = project });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        var result = ToolResult(response);
        string buildId = result["buildId"]!.GetValue<string>();
        string url = result["url"]!.GetValue<string>();
        using (Assert.EnterMultipleScope()) {
            Assert.That(result["projectSlug"]!.GetValue<string>(), Is.EqualTo("example-game"));
            Assert.That(buildId, Does.Match(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z$"));
            Assert.That(result["path"]!.GetValue<string>(), Is.EqualTo($"/webgl/example-game/{buildId}/"));
            Assert.That(url, Does.StartWith(endpoint!.GetLeftPart(UriPartial.Authority) + "/webgl/example-game/"));
        }

        using var root = await httpClient!.GetAsync(new Uri(endpoint, "/webgl/"));
        string rootListing = await root.Content.ReadAsStringAsync();
        using var projectListingResponse = await httpClient.GetAsync(new Uri(endpoint, "/webgl/example-game/"));
        string projectListing = await projectListingResponse.Content.ReadAsStringAsync();
        using var index = await httpClient.GetAsync(url);
        string indexContent = await index.Content.ReadAsStringAsync();
        using var encodedRequest = new HttpRequestMessage(HttpMethod.Head, url + "Build/game.wasm.br");
        using var encoded = await httpClient.SendAsync(encodedRequest);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url + "Build/game.data");
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
        using var range = await httpClient.SendAsync(rangeRequest);
        string rangeContent = await range.Content.ReadAsStringAsync();

        using (Assert.EnterMultipleScope()) {
            Assert.That(rootListing, Does.Contain("example-game"));
            Assert.That(projectListing, Does.Contain(buildId));
            Assert.That(indexContent, Does.Contain("Daemon WebGL Build"));
            Assert.That(index.Headers.GetValues("Cross-Origin-Opener-Policy"), Does.Contain("same-origin"));
            Assert.That(index.Headers.GetValues("Cross-Origin-Embedder-Policy"), Does.Contain("require-corp"));
            Assert.That(index.Headers.GetValues("Cross-Origin-Resource-Policy"), Does.Contain("cross-origin"));
            Assert.That(encoded.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/wasm"));
            Assert.That(encoded.Content.Headers.ContentEncoding, Does.Contain("br"));
            Assert.That(range.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(rangeContent, Is.EqualTo("2345"));
        }
    }

    async Task PublishAsync(string projectFile, string runtime, string output) {
        var result = await RunAsync("dotnet", [
            "publish", projectFile, "--nologo", "--configuration", "Release", "--runtime", runtime,
            "--self-contained", "true", "--output", output
        ], TimeSpan.FromMinutes(3));
        Assert.That(result.exitCode, Is.Zero, () => $"dotnet publish failed.\nstdout:\n{result.standardOutput}\nstderr:\n{result.standardError}");
    }

    async Task WaitForHealthyControllerAsync() {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        string health = string.Empty;
        while (DateTime.UtcNow < deadline) {
            var result = await DockerCheckedAsync(["inspect", "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}", container]);
            health = result.standardOutput.Trim();
            if (health == "healthy") {
                return;
            }

            if (health is "unhealthy" or "exited" or "dead") {
                throw new InvalidOperationException($"Daemon-test controller became {health}.\n{await ControllerLogsAsync()}");
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Daemon-test controller did not become healthy within two minutes (last state: {health}).\n{await ControllerLogsAsync()}");
    }

    async Task ConfigureMcpClientAsync() {
        if (expectedOs == "windows") {
            string address = (await DockerCheckedAsync(["inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", container])).standardOutput.Trim();
            Assert.That(IpAddressRegex().IsMatch(address), Is.True, $"Could not determine the Windows container address: {address}");
            var handler = new SocketsHttpHandler {
                ConnectCallback = async (context, cancellationToken) => {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try {
                        await socket.ConnectAsync(IPAddress.Parse(address), context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, true);
                    } catch {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            endpoint = new Uri("http://localhost:8080/mcp");
        } else {
            string published = (await DockerCheckedAsync(["port", container, "8080/tcp"])).standardOutput.Trim();
            var match = PublishedPortRegex().Match(published);
            Assert.That(match.Success, Is.True, $"Could not parse the published MCP port: {published}");
            endpoint = new Uri($"http://127.0.0.1:{match.Groups["port"].Value}/mcp");
            httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        }

        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    }

    async Task InitializeMcpAsync() {
        var response = await InvokeMcpAsync("initialize",
            new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unity-daemon-tests", ["version"] = "1" } });
        Assert.That(response["result"]!["serverInfo"]!["name"]!.GetValue<string>(), Is.EqualTo("unity"));
    }

    async Task<JsonNode> CallToolAsync(
        string name,
        JsonObject arguments,
        IReadOnlyDictionary<string, string>? headers = null) =>
        await InvokeMcpAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, headers);

    async Task<JsonNode> InvokeMcpAsync(
        string method,
        JsonObject parameters,
        IReadOnlyDictionary<string, string>? headers = null) {
        var events = await InvokeMcpEventsAsync(method, parameters, headers);
        Assert.That(events, Has.Count.EqualTo(1), $"Unexpected MCP events: {JsonSerializer.Serialize(events)}");
        return events[0];
    }

    async Task<List<JsonNode>> InvokeMcpEventsAsync(
        string method,
        JsonObject parameters,
        IReadOnlyDictionary<string, string>? headers = null) {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref requestId), ["method"] = method, ["params"] = parameters };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = content;
        if (headers is not null) {
            foreach (var header in headers) {
                Assert.That(request.Headers.TryAddWithoutValidation(header.Key, header.Value), Is.True);
            }
        }
        using var response = await httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var events = new List<JsonNode>();
        while (await reader.ReadLineAsync() is { } line) {
            if (line.StartsWith("data: ", StringComparison.Ordinal)) {
                events.Add(JsonNode.Parse(line[6..])!);
            }
        }

        Assert.That(events, Is.Not.Empty, "The MCP response contained no SSE data events.");
        return events;
    }

    async Task ExecuteMethodAsync(string method, string? projectRoot = null) {
        var response = await CallToolAsync("execute_method", new JsonObject { ["projectRoot"] = projectRoot ?? project, ["method"] = method, ["arguments"] = new JsonArray() });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
    }

    static IReadOnlyDictionary<string, string> CredentialHeaders(string client) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["X-Unity-Credentials-Usr"] = $"{client}-unity-user",
            ["X-Unity-Credentials-Psw"] = $"{client}-unity-password",
            ["X-Email-Credentials-Usr"] = $"{client}-email-user",
            ["X-Email-Credentials-Psw"] = $"{client}-email-password"
        };

    async Task<string> SingleWorkerAsync() {
        var result = await DockerCheckedAsync([
            "ps", "--all", "--quiet",
            "--filter", "label=net.slothsoft.unity.kind=worker",
            "--filter", $"label=net.slothsoft.unity.controller={controllerId}"
        ]);
        string[] workers = Lines(result.standardOutput);
        Assert.That(workers, Has.Length.EqualTo(1), $"Expected one retained worker, found {workers.Length}.");
        return workers[0];
    }

    async Task<ProcessResult> DockerCheckedAsync(IReadOnlyList<string> arguments, TimeSpan? timeout = null) {
        var result = await RunDockerAsync(arguments, timeout ?? TimeSpan.FromSeconds(30));
        AssertDockerSucceeded(result, string.Join(' ', arguments));
        return result;
    }

    Task<ProcessResult> RunDockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout) =>
        RunAsync("docker", ["--context", dockerContext, .. arguments], timeout);

    async Task<string> ControllerLogsAsync() {
        var result = await RunDockerAsync(["logs", container], TimeSpan.FromSeconds(30));
        return result.standardOutput + result.standardError;
    }

    async Task CleanupAsync() {
        if (!string.IsNullOrWhiteSpace(controllerId)) {
            var owned = await RunDockerAsync(["ps", "--all", "--quiet", "--filter", $"label=net.slothsoft.unity.controller={controllerId}"], TimeSpan.FromSeconds(30));
            foreach (string ownedContainer in Lines(owned.standardOutput)) {
                await RunDockerAsync(["rm", "--force", ownedContainer], TimeSpan.FromSeconds(30));
            }
        }

        if (controllerStarted) {
            await RunDockerAsync(["rm", "--force", container], TimeSpan.FromSeconds(30));
            controllerStarted = false;
        }

        if (imageBuilt) {
            await RunDockerAsync(["image", "rm", "--force", image], TimeSpan.FromSeconds(30));
            imageBuilt = false;
        }

        if (staging.Length > 0 && Directory.Exists(staging)) {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            string fullStaging = Path.GetFullPath(staging);
            if (!fullStaging.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullStaging).StartsWith("unity-daemon-tests-", StringComparison.Ordinal)) {
                throw new InvalidOperationException($"Refusing to remove unexpected staging directory: {fullStaging}");
            }

            Directory.Delete(fullStaging, true);
        }
    }

    static JsonNode ToolResult(JsonNode response) => response["result"]!["structuredContent"]!["result"]!;

    async Task<string> NormalizedProjectRootAsync() {
        var response = await CallToolAsync("get_project_info", new JsonObject { ["projectRoot"] = project });
        Assert.That(response["result"]!["isError"]?.GetValue<bool>(), Is.Not.True, response.ToJsonString());
        return ToolResult(response)["projectRoot"]!.GetValue<string>();
    }

    static string[] ExpectedProjectRoots(string value, string expectedOs) {
        if (expectedOs != "linux" || value.Length < 3 || value[1] != ':' || value[2] is not '\\' and not '/') {
            return [value];
        }

        string suffix = value[3..].Replace('\\', '/').TrimEnd('/');
        char drive = char.ToLowerInvariant(value[0]);
        return [
            $"/mnt/{drive}/{suffix}".TrimEnd('/'),
            $"/run/desktop/mnt/host/{drive}/{suffix}".TrimEnd('/'),
            $"/host_mnt/{drive}/{suffix}".TrimEnd('/')
        ];
    }

    void IgnoreOrFail(string message) {
        if (Environment.GetEnvironmentVariable(REQUIRED_OS_ENVIRONMENT)?.Equals(expectedOs, StringComparison.OrdinalIgnoreCase) == true) {
            Assert.Fail($"The required {expectedOs} daemon fixture could not run. {message}");
        }

        Assert.Ignore(message);
    }

    static bool IsLocalEndpoint(string value) => value.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)
                                                 || value.StartsWith("unix://", StringComparison.OrdinalIgnoreCase);

    static string WindowsDockerMount(string endpoint) {
        var match = WindowsPipeRegex().Match(endpoint);
        Assert.That(match.Success, Is.True, $"Windows daemon tests require a named-pipe context, but '{endpoint}' uses another endpoint.");
        return $@"type=npipe,source=\\.\pipe\{match.Groups["pipe"].Value},target=\\.\pipe\docker_engine";
    }

    static string FindRepository() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-unity.sln"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    static void AssertDockerSucceeded(ProcessResult result, string operation) =>
        Assert.That(result.exitCode, Is.Zero, () => $"Docker failed to {operation}.\nstdout:\n{result.standardOutput}\nstderr:\n{result.standardError}");

    static string Detail(ProcessResult result) {
        string value = string.IsNullOrWhiteSpace(result.standardError) ? result.standardOutput : result.standardError;
        value = value.Trim();
        return value.Length == 0 ? $"exit code {result.exitCode}" : value;
    }

    static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout) {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(startInfo);
            if (process is null) {
                return new ProcessResult(-1, string.Empty, $"Could not start {executable}.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(timeout);
            try {
                await process.WaitForExitAsync(cancellation.Token);
            } catch (OperationCanceledException) {
                process.Kill(true);
                await process.WaitForExitAsync();
                return new ProcessResult(-1, await standardOutput, $"Timed out after {timeout}.\n{await standardError}");
            }

            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        } catch (Exception exception) when (exception is Win32Exception or FileNotFoundException) {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }

    [GeneratedRegex(@"^npipe:/{4}\./pipe/(?<pipe>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPipeRegex();

    [GeneratedRegex(@":(?<port>\d+)$")]
    private static partial Regex PublishedPortRegex();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(@"(?:^|[\\/])(Assets|Packages|ProjectSettings)$")]
    private static partial Regex ProjectDirectoryRegex();

    sealed record ProcessResult(int exitCode, string standardOutput, string standardError);
}
