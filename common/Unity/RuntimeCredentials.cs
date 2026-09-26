using System.Diagnostics;
using System.Security;

namespace Unity;

sealed class RuntimeCredentials {
    internal const string UNITY_USERNAME_HEADER = "X-Unity-Credentials-Usr";
    internal const string UNITY_PASSWORD_HEADER = "X-Unity-Credentials-Psw";
    internal const string EMAIL_USERNAME_HEADER = "X-Email-Credentials-Usr";
    internal const string EMAIL_PASSWORD_HEADER = "X-Email-Credentials-Psw";

    static readonly CredentialPair[] pairs = [
        new("UNITY_CREDENTIALS_USR", UNITY_USERNAME_HEADER),
        new("UNITY_CREDENTIALS_PSW", UNITY_PASSWORD_HEADER),
        new("EMAIL_CREDENTIALS_USR", EMAIL_USERNAME_HEADER),
        new("EMAIL_CREDENTIALS_PSW", EMAIL_PASSWORD_HEADER)
    ];

    readonly IReadOnlyDictionary<string, string> values;

    RuntimeCredentials(IReadOnlyDictionary<string, string> values) {
        this.values = values;
    }

    internal static RuntimeCredentials empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    internal static RuntimeCredentials Resolve() =>
        Resolve(Environment.GetEnvironmentVariable, File.ReadAllText);

    internal static RuntimeCredentials Resolve(
        Func<string, string?> readEnvironment,
        Func<string, string> readFile) {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs) {
            ResolveValue(pair.environment, values, readEnvironment, readFile);
        }

        return values.Count == 0 ? empty : new RuntimeCredentials(values);
    }

    internal RuntimeCredentials WithRequestHeaders(Func<string, string?> readHeader) {
        var merged = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var pair in pairs) {
            string? value = readHeader(pair.header);
            if (value is null) {
                continue;
            }

            if (value.Length == 0) {
                merged.Remove(pair.environment);
            } else {
                merged[pair.environment] = value;
            }
        }

        return merged.Count == 0 ? empty : new RuntimeCredentials(merged);
    }

    internal void ApplyTo(ProcessStartInfo startInfo) {
        foreach (var pair in pairs) {
            ApplyValue(startInfo.Environment, pair.environment);
        }
    }

    internal IReadOnlyList<string> WorkerEnvironment() {
        var environment = new List<string>();
        foreach (var pair in pairs) {
            AddEnvironmentValue(environment, pair.environment);
        }

        return environment;
    }

    static void ResolveValue(
        string name,
        IDictionary<string, string> values,
        Func<string, string?> readEnvironment,
        Func<string, string> readFile) {
        string fileName = name + "_FILE";
        string? directValue = readEnvironment(name);
        string? path = readEnvironment(fileName);
        if (directValue is not null && path is not null) {
            throw new InvalidOperationException($"{name} and {fileName} cannot both be set.");
        }

        string? value = directValue;
        if (path is not null) {
            if (string.IsNullOrWhiteSpace(path)) {
                throw new InvalidOperationException($"{fileName} must name a credential file.");
            }

            try {
                value = readFile(path).TrimEnd('\r', '\n');
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException) {
                throw new InvalidOperationException(
                    $"Could not read the credential file configured by {fileName} at '{Program.SanitizeText(path, 512)}'.",
                    exception);
            }

            if (value.Length == 0) {
                throw new InvalidOperationException($"The credential file configured by {fileName} is empty.");
            }
        }

        if (!string.IsNullOrEmpty(value)) {
            values[name] = value;
        }
    }

    void ApplyValue(IDictionary<string, string?> environment, string name) {
        environment.Remove(name);
        environment.Remove(name + "_FILE");
        if (values.TryGetValue(name, out string? value)) {
            environment[name] = value;
        }
    }

    void AddEnvironmentValue(ICollection<string> environment, string name) {
        if (values.TryGetValue(name, out string? value)) {
            environment.Add($"{name}={value}");
        }
    }

    sealed record CredentialPair(string environment, string header);
}
