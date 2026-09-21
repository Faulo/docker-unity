using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Unity;

static class McpActivation {
    internal static bool Parse() =>
        Parse(
            Environment.GetEnvironmentVariable("UNITY_MCP"),
            warning => Console.Error.WriteLine($"unity-sidecar: warning: {warning}"));

    internal static bool Parse(string? value, Action<string>? warn = null) {
        if (value is null or "0") {
            return false;
        }

        if (value == "1") {
            return true;
        }

        warn?.Invoke("ignoring invalid UNITY_MCP value; use 1 to enable MCP or 0 to disable it.");
        return false;
    }
}

sealed class McpServerRuntime : IAsyncDisposable {
    readonly WebApplication application;
    readonly UnityMcpController controller;
    readonly CancellationTokenSource stopping;
    int stopped;

    McpServerRuntime(
        WebApplication application,
        UnityMcpController controller,
        CancellationTokenSource stopping,
        Task completion) {
        this.application = application;
        this.controller = controller;
        this.stopping = stopping;
        this.completion = completion;
    }

    internal Task completion { get; }

    public async ValueTask DisposeAsync() {
        await StopAsync();
        await application.DisposeAsync();
        await controller.DisposeAsync();
        stopping.Dispose();
    }

    internal static async Task<McpServerRuntime> StartAsync(
        RuntimeCredentials credentials,
        CancellationToken sidecarStopping) {
        var stopping = CancellationTokenSource.CreateLinkedTokenSource(sidecarStopping);
        UnityMcpController? controller = null;
        WebApplication? application = null;
        try {
            controller = await UnityMcpController.CreateAsync(credentials, stopping.Token);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(McpServerRuntime).Assembly.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://0.0.0.0:8080");
            builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
            builder.Services.AddSingleton(controller);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDirectoryBrowser();
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools<UnityMcpTools>();

            application = builder.Build();
            application.UseHostFiltering();
            application.Use(async (context, next) => {
                if (context.Request.Headers.TryGetValue("Origin", out var values)
                    && values.Any(value => !IsLoopbackOrigin(value))) {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Origin is not allowed.");
                    return;
                }

                await next(context);
            });
            Directory.CreateDirectory(WebGlHosting.documentRoot);
            application.UseWhen(
                context => context.Request.Path.StartsWithSegments("/webgl"),
                webGl => webGl.Use(async (context, next) => {
                    WebGlHosting.ApplyUnityHeaders(context);
                    await next(context);
                }));
            application.UseFileServer(new FileServerOptions {
                FileProvider = new PhysicalFileProvider(WebGlHosting.documentRoot),
                RequestPath = "/webgl",
                EnableDefaultFiles = true,
                EnableDirectoryBrowsing = true,
                StaticFileOptions = {
                    ContentTypeProvider = new UnityWebContentTypeProvider(),
                    ServeUnknownFileTypes = true,
                    DefaultContentType = "application/octet-stream",
                    OnPrepareResponse = WebGlHosting.PrepareStaticResponse
                }
            });
            application.MapMcp("/mcp");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            application.Lifetime.ApplicationStopped.Register(completion.SetResult);
            await application.StartAsync(stopping.Token);
            return new McpServerRuntime(application, controller, stopping, completion.Task);
        } catch {
            if (application is not null) {
                await application.DisposeAsync();
            }

            if (controller is not null) {
                await controller.DisposeAsync();
            }

            stopping.Dispose();
            throw;
        }
    }

    internal static async Task<bool> CheckHealthAsync() {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, 8080, cancellation.Token);
            await using var docker = new DockerEngineClient();
            await docker.VersionAsync(cancellation.Token);
            await docker.InspectSelfAsync(cancellation.Token);
            return true;
        } catch {
            return false;
        }
    }

    internal async Task StopAsync() {
        if (Interlocked.Exchange(ref stopped, 1) != 0) {
            return;
        }

        application.Lifetime.StopApplication();
        stopping.Cancel();
        await controller.StopActiveWorkersAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            await application.StopAsync(timeout.Token);
        } catch (OperationCanceledException) {
            Console.Error.WriteLine("unity-sidecar: MCP shutdown exceeded 30 seconds");
        }
    }

    internal static bool IsLoopbackOrigin(string? value) {
        return Uri.TryCreate(value, UriKind.Absolute, out var origin)
               && origin.IsLoopback
               && origin.Scheme is "http" or "https";
    }
}

[McpServerToolType]
sealed class UnityMcpTools(UnityMcpController controller, IHttpContextAccessor contexts) {
    [McpServerTool(
        Name = "configure_credentials",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Atomically replace the in-memory credentials used by subsequent Unity worker executions. " +
        "Unity credentials are required; email credentials must be omitted or supplied as a complete pair. " +
        "Credential values are never returned or persisted.")]
    public object ConfigureCredentials(
        [Description("Unity account username.")]
        string unityUsername,
        [Description("Unity account password.")]
        string unityPassword,
        [Description("Optional email account username; requires emailPassword when supplied.")]
        string? emailUsername = null,
        [Description("Optional email account password; requires emailUsername when supplied.")]
        string? emailPassword = null) =>
        controller.ConfigureCredentials(unityUsername, unityPassword, emailUsername, emailPassword);

    [McpServerTool(
        Name = "get_project_info",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Read a Docker-host Unity project's YAML and JSON metadata and return its root, product metadata, editor and project versions, major code and rendering settings, input handling, and complete package manifest without installing or launching Unity.")]
    public async Task<object> ProjectInfoAsync(
        [Description("Absolute host path to an existing Unity project.")]
        string projectRoot,
        CancellationToken cancellationToken) =>
        await InvokeAsync(() => controller.ProjectInfoAsync(projectRoot, cancellationToken));

    [McpServerTool(
        Name = "run_tests",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Run Unity Test Runner modes for a Docker-host Unity project. Cold imports can take 30-60 minutes; " +
        "progress notifications report liveness, so await the original call instead of restarting it or launching parallel diagnostics. " +
        "Valid JUnit reports return compact pass/fail counts and failure details; infrastructure errors return the complete ordered Unity log.")]
    public async Task<object> RunTestsAsync(
        [Description("Absolute host path to an existing Unity project.")]
        string projectRoot,
        [Description("One or more Unity test modes, such as EditMode, PlayMode, or a platform.")]
        string[] modes,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) =>
        await InvokeAsync(() => controller.RunTestsAsync(projectRoot, modes, progress, cancellationToken));

    [McpServerTool(
        Name = "execute_method",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Run a fully qualified static Unity editor method and ask Unity to quit afterward. Cold imports can take 30-60 minutes; " +
        "progress notifications report liveness, so await the original call instead of restarting it or launching parallel diagnostics.")]
    public async Task<object> ExecuteMethodAsync(
        [Description("Absolute host path to an existing Unity project.")]
        string projectRoot,
        [Description("Fully qualified static Unity editor method name.")]
        string method,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken,
        [Description("Optional arguments forwarded without shell reinterpretation.")]
        string[]? arguments = null) =>
        await InvokeAsync(() => controller.ExecuteMethodAsync(projectRoot, method, arguments, progress, cancellationToken));

    [McpServerTool(
        Name = "build_and_serve_webgl",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description(
        "Build a Docker-host Unity project for WebGL and serve the immutable result from this MCP listener. Cold imports can take 30-60 minutes; " +
        "progress notifications report liveness, so await the original call instead of restarting it or launching parallel diagnostics.")]
    public async Task<object> BuildAndServeWebGlAsync(
        [Description("Absolute host path to an existing Unity project.")]
        string projectRoot,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken) {
        var request = contexts.HttpContext?.Request
                      ?? throw new McpException("The WebGL build tool requires an HTTP request context.");
        return await InvokeAsync(() => controller.BuildAndServeWebGlAsync(
            projectRoot,
            request.Scheme,
            request.Host.Value ?? string.Empty,
            progress,
            cancellationToken));
    }

    static async Task<object> InvokeAsync(Func<Task<object>> action) {
        try {
            return await action();
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception exception) {
            throw new McpException(exception.Message, exception);
        }
    }
}
