using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unity;

sealed class DockerEngineClient : IAsyncDisposable {
    const string DEFAULT_UNIX_SOCKET_PATH = "/var/run/docker.sock";
    const string DEFAULT_WINDOWS_PIPE_NAME = "docker_engine";
    static readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    readonly HttpClient client;
    readonly string unixSocketPath;
    readonly string windowsPipeName;

    internal DockerEngineClient() : this(DEFAULT_WINDOWS_PIPE_NAME, DEFAULT_UNIX_SOCKET_PATH) {
    }

    internal DockerEngineClient(string windowsPipeName, string unixSocketPath) {
        if (string.IsNullOrWhiteSpace(windowsPipeName)) {
            throw new ArgumentException("The Docker Engine pipe name must not be empty.", nameof(windowsPipeName));
        }

        if (string.IsNullOrWhiteSpace(unixSocketPath)) {
            throw new ArgumentException("The Docker Engine socket path must not be empty.", nameof(unixSocketPath));
        }

        this.windowsPipeName = windowsPipeName;
        this.unixSocketPath = unixSocketPath;
        var handler = new SocketsHttpHandler { ConnectCallback = ConnectAsync };
        client = new HttpClient(handler) { BaseAddress = new Uri("http://docker"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public ValueTask DisposeAsync() {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    internal async Task<JsonObject> VersionAsync(CancellationToken cancellationToken) =>
        await GetObjectAsync("/version", cancellationToken);

    internal async Task<JsonObject> InspectContainerAsync(string idOrName, CancellationToken cancellationToken) =>
        await GetObjectAsync($"/containers/{Escape(idOrName)}/json", cancellationToken);

    internal async Task<JsonObject?> TryInspectContainerAsync(string idOrName, CancellationToken cancellationToken) {
        try {
            return await InspectContainerAsync(idOrName, cancellationToken);
        } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.NotFound) {
            return null;
        }
    }

    internal async Task<JsonObject> InspectSelfAsync(CancellationToken cancellationToken) {
        foreach (string candidate in SelfInspectionCandidates(
                     Environment.GetEnvironmentVariable("HOSTNAME"),
                     Environment.MachineName)) {
            var inspected = await TryInspectContainerAsync(candidate, cancellationToken);
            if (inspected is not null) {
                return inspected;
            }
        }

        throw new InvalidOperationException(
            "Docker Engine access is available, but the sidecar could not inspect its own container. " +
            "Mount the platform Docker socket/pipe and do not override the container hostname.");
    }

    internal static IReadOnlyList<string> SelfInspectionCandidates(string? hostname, string machineName) =>
        new[] { hostname, machineName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => new[] { value!.ToLowerInvariant(), value })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal async Task<string> CreateContainerAsync(string name, JsonObject configuration, CancellationToken cancellationToken) {
        var result = await SendJsonAsync(
            HttpMethod.Post,
            $"/containers/create?name={Uri.EscapeDataString(name)}",
            configuration,
            cancellationToken);
        return result["Id"]?.GetValue<string>()
               ?? throw new InvalidOperationException("Docker did not return the created container ID.");
    }

    internal async Task StartContainerAsync(string idOrName, CancellationToken cancellationToken) =>
        await SendNoContentAsync(HttpMethod.Post, $"/containers/{Escape(idOrName)}/start", null, cancellationToken);

    internal async Task StopContainerAsync(string idOrName, TimeSpan timeout, CancellationToken cancellationToken) {
        int seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        try {
            await SendNoContentAsync(
                HttpMethod.Post,
                $"/containers/{Escape(idOrName)}/stop?t={seconds}",
                null,
                cancellationToken);
        } catch (DockerApiException exception) when (exception.statusCode is HttpStatusCode.NotFound or HttpStatusCode.NotModified) {
        }
    }

    internal async Task<int> WaitContainerAsync(string idOrName, CancellationToken cancellationToken) {
        var result = await SendJsonAsync(
            HttpMethod.Post,
            $"/containers/{Escape(idOrName)}/wait?condition=not-running",
            null,
            cancellationToken);
        return result["StatusCode"]?.GetValue<int>()
               ?? throw new InvalidOperationException("Docker did not return the container exit status.");
    }

    internal async Task RemoveContainerAsync(string idOrName, bool force, bool removeVolumes, CancellationToken cancellationToken) {
        string path = $"/containers/{Escape(idOrName)}?force={(force ? 1 : 0)}&v={(removeVolumes ? 1 : 0)}";
        try {
            await SendNoContentAsync(HttpMethod.Delete, path, null, cancellationToken);
        } catch (DockerApiException exception) when (exception.statusCode == HttpStatusCode.NotFound) {
        }
    }

    internal async Task<CapturedOutput> ContainerLogsAsync(string idOrName, CancellationToken cancellationToken) {
        using var response = await client.GetAsync(
            $"/containers/{Escape(idOrName)}/logs?stdout=1&stderr=1",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadMultiplexedAsync(stream, cancellationToken);
    }

    internal async Task<ExecResult> ExecAsync(
        string container,
        string workingDirectory,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken) {
        return await ExecAsync(container, workingDirectory, command, [], cancellationToken);
    }

    internal async Task<ExecResult> ExecAsync(
        string container,
        string workingDirectory,
        IReadOnlyList<string> command,
        IReadOnlyList<string> environment,
        CancellationToken cancellationToken) {
        var configuration = new JsonObject {
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = false,
            ["WorkingDir"] = workingDirectory,
            ["Cmd"] = ToArray(command)
        };
        if (environment.Count > 0) {
            configuration["Env"] = ToArray(environment);
        }
        var created = await SendJsonAsync(
            HttpMethod.Post,
            $"/containers/{Escape(container)}/exec",
            configuration,
            cancellationToken);
        string execId = created["Id"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("Docker did not return the exec ID.");

        using var request = CreateRequest(HttpMethod.Post, $"/exec/{Escape(execId)}/start", new JsonObject { ["Detach"] = false, ["Tty"] = false });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var output = await ReadMultiplexedAsync(stream, cancellationToken);
        var inspected = await GetObjectAsync($"/exec/{Escape(execId)}/json", cancellationToken);
        int exitCode = inspected["ExitCode"]?.GetValue<int>()
                       ?? throw new InvalidOperationException("Docker did not return the exec exit status.");
        return new ExecResult(
            execId,
            exitCode,
            output.standardOutput,
            output.standardError,
            output.combinedOutput);
    }

    internal async Task ExtractArchiveAsync(
        string container,
        string path,
        string destination,
        CancellationToken cancellationToken) {
        using var response = await client.GetAsync(
            $"/containers/{Escape(container)}/archive?path={Uri.EscapeDataString(path)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await TarFile.ExtractToDirectoryAsync(stream, destination, false, cancellationToken);
    }

    async Task<JsonObject> GetObjectAsync(string path, CancellationToken cancellationToken) {
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return node as JsonObject ?? throw new InvalidOperationException("Docker returned an unexpected JSON response.");
    }

    async Task<JsonObject> SendJsonAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken cancellationToken) {
        using var request = CreateRequest(method, path, body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) {
            return new JsonObject();
        }

        return JsonNode.Parse(content) as JsonObject
               ?? throw new InvalidOperationException("Docker returned an unexpected JSON response.");
    }

    async Task SendNoContentAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken cancellationToken) {
        using var request = CreateRequest(method, path, body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    static HttpRequestMessage CreateRequest(HttpMethod method, string path, JsonObject? body) {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) {
            request.Content = new StringContent(body.ToJsonString(jsonOptions), Encoding.UTF8, "application/json");
        }

        return request;
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken) {
        if (response.IsSuccessStatusCode) {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        try {
            detail = JsonNode.Parse(detail)?["message"]?.GetValue<string>() ?? detail;
        } catch (JsonException) {
        }

        throw new DockerApiException(response.StatusCode, string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
    }

    async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken) {
        _ = context;
        if (OperatingSystem.IsWindows()) {
            var pipe = new NamedPipeClientStream(
                ".",
                windowsPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(unixSocketPath), cancellationToken);
            return new NetworkStream(socket, true);
        } catch {
            socket.Dispose();
            throw;
        }
    }

    internal static async Task<CapturedOutput> ReadMultiplexedAsync(Stream stream, CancellationToken cancellationToken) {
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        using var combinedOutput = new MemoryStream();
        byte[] header = new byte[8];
        byte? previousStream = null;

        while (true) {
            int headerBytes = await ReadAtMostAsync(stream, header, cancellationToken);
            if (headerBytes == 0) {
                break;
            }

            if (headerBytes != header.Length || header[1] != 0 || header[2] != 0 || header[3] != 0) {
                await WriteStreamMarkerAsync(combinedOutput, 1, previousStream, cancellationToken);
                await standardOutput.WriteAsync(header.AsMemory(0, headerBytes), cancellationToken);
                await combinedOutput.WriteAsync(header.AsMemory(0, headerBytes), cancellationToken);
                await CopyAsync(stream, standardOutput, combinedOutput, cancellationToken);
                break;
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            if (length < 0) {
                throw new InvalidOperationException("Docker returned an invalid output frame.");
            }

            byte streamType = header[0];
            var target = streamType == 2 ? standardError : standardOutput;
            await WriteStreamMarkerAsync(combinedOutput, streamType, previousStream, cancellationToken);
            await CopyFrameAsync(stream, target, combinedOutput, length, cancellationToken);
            previousStream = streamType;
        }

        return new CapturedOutput(
            Encoding.UTF8.GetString(standardOutput.ToArray()),
            Encoding.UTF8.GetString(standardError.ToArray()),
            Encoding.UTF8.GetString(combinedOutput.ToArray()));
    }

    static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) {
        int offset = 0;
        while (offset < buffer.Length) {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) {
                break;
            }

            offset += read;
        }

        return offset;
    }

    static async Task CopyFrameAsync(
        Stream source,
        Stream destination,
        Stream combinedOutput,
        int length,
        CancellationToken cancellationToken) {
        byte[] buffer = new byte[Math.Min(16 * 1024, Math.Max(1, length))];
        int remaining = length;
        while (remaining > 0) {
            int read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) {
                throw new EndOfStreamException("Docker output ended in the middle of a frame.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await combinedOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    static async Task CopyAsync(
        Stream source,
        Stream destination,
        Stream combinedOutput,
        CancellationToken cancellationToken) {
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await combinedOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    static async Task WriteStreamMarkerAsync(
        Stream destination,
        byte streamType,
        byte? previousStream,
        CancellationToken cancellationToken) {
        if (streamType == previousStream) {
            return;
        }

        string name = streamType == 2 ? "stderr" : "stdout";
        byte[] marker = Encoding.UTF8.GetBytes($"{(destination.Length == 0 ? string.Empty : "\n")}[{name}]\n");
        await destination.WriteAsync(marker, cancellationToken);
    }

    internal static JsonArray ToArray(IEnumerable<string> values) {
        var result = new JsonArray();
        foreach (string value in values) {
            result.Add(value);
        }

        return result;
    }

    static string Escape(string value) => Uri.EscapeDataString(value);
}

sealed class DockerApiException(HttpStatusCode statusCode, string? message)
    : InvalidOperationException($"Docker Engine returned {(int)statusCode}: {message}") {
    internal HttpStatusCode statusCode { get; } = statusCode;
}

sealed record CapturedOutput(string standardOutput, string standardError, string combinedOutput);

sealed record ExecResult(
    string id,
    int exitCode,
    string standardOutput,
    string standardError,
    string combinedOutput);
