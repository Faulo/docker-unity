using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace Unity;

static class WebGlHosting {
    internal static string documentRoot {
        get => OperatingSystem.IsWindows()
            ? @"C:\unity\webgl"
            : "/unity/webgl";
    }

    internal static string ProjectSlug(string? projectName) {
        var result = new StringBuilder();
        bool separator = false;
        foreach (char value in (projectName ?? string.Empty).Normalize(NormalizationForm.FormKC)) {
            if (char.IsLetterOrDigit(value)) {
                if (separator && result.Length > 0) {
                    result.Append('-');
                }

                result.Append(char.ToLowerInvariant(value));
                separator = false;
            } else if (value is '-' or '_') {
                if (result.Length > 0) {
                    separator = true;
                }
            } else if (result.Length > 0) {
                separator = true;
            }

            if (result.Length >= 80) {
                break;
            }
        }

        return result.ToString().TrimEnd('-') is { Length: > 0 } slug ? slug : "project";
    }

    internal static async Task<WebGlBuildDirectory> ClaimBuildDirectoryAsync(
        string documentRootPath,
        string projectSlug,
        CancellationToken cancellationToken) {
        string projectDirectory = Path.Combine(documentRootPath, projectSlug);
        Directory.CreateDirectory(projectDirectory);
        while (true) {
            var now = DateTimeOffset.UtcNow;
            string buildId = now.ToString("yyyy-MM-dd_HH-mm-ss'Z'", CultureInfo.InvariantCulture);
            string directory = Path.Combine(projectDirectory, buildId);
            if (!Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
                return new WebGlBuildDirectory(projectSlug, buildId, directory);
            }

            var nextSecond = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddSeconds(1);
            await Task.Delay(nextSecond - now, cancellationToken);
        }
    }

    internal static string PublicPath(WebGlBuildDirectory build) =>
        $"/webgl/{Uri.EscapeDataString(build.projectSlug)}/{Uri.EscapeDataString(build.buildId)}/";

    internal static void ApplyUnityHeaders(HttpContext context) {
        context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        context.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        context.Response.Headers.CacheControl = "no-cache";
    }

    internal static void PrepareStaticResponse(StaticFileResponseContext context) {
        string path = context.File.Name;
        if (path.EndsWith(".br", StringComparison.OrdinalIgnoreCase)) {
            context.Context.Response.Headers.ContentEncoding = "br";
            context.Context.Response.Headers.Append("Vary", "Accept-Encoding");
        } else if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) {
            context.Context.Response.Headers.ContentEncoding = "gzip";
            context.Context.Response.Headers.Append("Vary", "Accept-Encoding");
        }

        context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    }
}

sealed class UnityWebContentTypeProvider : IContentTypeProvider {
    readonly FileExtensionContentTypeProvider defaults = new();

    public bool TryGetContentType(string subpath, out string contentType) {
        string path = subpath;
        if (path.EndsWith(".br", StringComparison.OrdinalIgnoreCase)) {
            path = path[..^3];
        } else if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) {
            if (path.EndsWith(".data.gz", StringComparison.OrdinalIgnoreCase)) {
                contentType = "application/gzip";
                return true;
            }

            path = path[..^3];
        }

        if (path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)) {
            contentType = "application/wasm";
            return true;
        }

        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) {
            contentType = "application/javascript";
            return true;
        }

        if (path.EndsWith(".data", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".symbols.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".unityweb", StringComparison.OrdinalIgnoreCase)) {
            contentType = "application/octet-stream";
            return true;
        }

        return defaults.TryGetContentType(path, out contentType!);
    }
}

sealed record WebGlBuildDirectory(string projectSlug, string buildId, string directory);