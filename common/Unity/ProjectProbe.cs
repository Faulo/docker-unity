using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unity;

static class ProjectProbe {
    static readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static int Run(string projectRoot) {
        try {
            Console.WriteLine(JsonSerializer.Serialize(Read(projectRoot), jsonOptions));
            return 0;
        } catch (Exception exception) {
            Console.Error.WriteLine($"unity-sidecar: {exception.Message}");
            return 1;
        }
    }

    internal static ProjectProbeResult Read(string projectRoot) {
        foreach (string directory in new[] { "Assets", "Packages", "ProjectSettings" }) {
            string path = Path.Combine(projectRoot, directory);
            if (!Directory.Exists(path)) {
                throw new InvalidOperationException($"Required Unity project directory is missing: {directory}");
            }
        }

        string versionPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionPath)) {
            throw new InvalidOperationException("Required Unity version file is missing: ProjectSettings/ProjectVersion.txt");
        }

        string manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(manifestPath)) {
            throw new InvalidOperationException("Required package manifest is missing: Packages/manifest.json");
        }

        string settingsPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset");
        if (!File.Exists(settingsPath)) {
            throw new InvalidOperationException("Required player settings file is missing: ProjectSettings/ProjectSettings.asset");
        }

        string[] versionLines = File.ReadAllLines(versionPath);
        string editorVersion = ReadValue(versionLines, "m_EditorVersion:")
                               ?? throw new InvalidOperationException("ProjectSettings/ProjectVersion.txt does not contain m_EditorVersion");
        string? editorVersionWithRevision = ReadValue(versionLines, "m_EditorVersionWithRevision:");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))
                       ?? throw new InvalidOperationException("Packages/manifest.json is empty");
        string[] settings = File.ReadAllLines(settingsPath);

        return new ProjectProbeResult {
            companyName = ReadRequiredSetting(settings, "companyName"),
            projectName = ReadRequiredSetting(settings, "productName"),
            projectVersion = ReadRequiredSetting(settings, "bundleVersion"),
            editorVersion = editorVersion,
            editorRevision = ReadRevision(editorVersionWithRevision),
            apiCompatibility = ApiCompatibility(
                ReadRequiredIntegerSetting(settings, "apiCompatibilityLevel"),
                editorVersion),
            allowUnsafeCode = ReadRequiredIntegerSetting(settings, "allowUnsafeCode") != 0,
            scriptingBackendOverrides = ReadEnumMap(settings, "scriptingBackend", ScriptingBackend),
            renderPipeline = RenderPipeline(projectRoot, manifest),
            colorSpace = ColorSpace(ReadRequiredIntegerSetting(settings, "m_ActiveColorSpace")),
            graphicsApis = ReadGraphicsApis(settings),
            inputHandling = InputHandling(ReadRequiredIntegerSetting(settings, "activeInputHandler")),
            packages = manifest
        };
    }

    static string ReadRequiredSetting(IReadOnlyList<string> lines, string name) =>
        ReadSetting(lines, name)
        ?? throw new InvalidOperationException($"ProjectSettings/ProjectSettings.asset does not contain {name}");

    static int ReadRequiredIntegerSetting(IReadOnlyList<string> lines, string name) {
        string value = ReadRequiredSetting(lines, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new InvalidOperationException($"ProjectSettings/ProjectSettings.asset contains an invalid {name} value");
    }

    static string? ReadSetting(IEnumerable<string> lines, string name) {
        string prefix = $"  {name}:";
        string? line = lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? null : DecodeYamlScalar(line[prefix.Length..]);
    }

    static Dictionary<string, string> ReadEnumMap(
        IReadOnlyList<string> lines,
        string name,
        Func<int, string> format) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int section = FindSetting(lines, name);
        if (section < 0 || !string.IsNullOrWhiteSpace(lines[section][$"  {name}:".Length..])) {
            return result;
        }

        for (int index = section + 1; index < lines.Count && Indentation(lines[index]) > 2; index++) {
            string line = lines[index].Trim();
            int separator = line.LastIndexOf(':');
            if (separator <= 0
                || !int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) {
                continue;
            }

            result[DecodeYamlScalar(line[..separator])] = format(value);
        }

        return result;
    }

    static Dictionary<string, GraphicsApiSettings> ReadGraphicsApis(IReadOnlyList<string> lines) {
        var result = new Dictionary<string, GraphicsApiSettings>(StringComparer.Ordinal);
        int section = FindSetting(lines, "m_BuildTargetGraphicsAPIs");
        if (section < 0) {
            return result;
        }

        string? buildTarget = null;
        string? serializedApis = null;
        bool? automatic = null;
        for (int index = section + 1; index < lines.Count; index++) {
            if (Indentation(lines[index]) <= 2
                && !lines[index].StartsWith("  - ", StringComparison.Ordinal)) {
                break;
            }

            string line = lines[index].TrimStart();
            if (line.StartsWith("- m_BuildTarget:", StringComparison.Ordinal)) {
                AddGraphicsApis(result, buildTarget, serializedApis, automatic);
                buildTarget = DecodeYamlScalar(line["- m_BuildTarget:".Length..]);
                serializedApis = null;
                automatic = null;
            } else if (line.StartsWith("m_APIs:", StringComparison.Ordinal)) {
                serializedApis = line["m_APIs:".Length..].Trim();
            } else if (line.StartsWith("m_Automatic:", StringComparison.Ordinal)
                       && int.TryParse(line["m_Automatic:".Length..].Trim(), out int value)) {
                automatic = value != 0;
            }
        }

        AddGraphicsApis(result, buildTarget, serializedApis, automatic);
        return result;
    }

    static void AddGraphicsApis(
        IDictionary<string, GraphicsApiSettings> result,
        string? buildTarget,
        string? serializedApis,
        bool? automatic) {
        if (string.IsNullOrWhiteSpace(buildTarget)) {
            return;
        }

        result[buildTarget] = new GraphicsApiSettings { automatic = automatic ?? true, apis = DecodeGraphicsApis(serializedApis) };
    }

    static string[] DecodeGraphicsApis(string? serialized) {
        if (string.IsNullOrEmpty(serialized)) {
            return [];
        }

        try {
            byte[] bytes = Convert.FromHexString(serialized);
            if (bytes.Length % sizeof(int) != 0) {
                throw new FormatException();
            }

            string[] result = new string[bytes.Length / sizeof(int)];
            for (int index = 0; index < result.Length; index++) {
                result[index] = GraphicsApi(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * sizeof(int), sizeof(int))));
            }

            return result;
        } catch (FormatException) {
            return [$"Unknown ({serialized})"];
        }
    }

    static string RenderPipeline(string projectRoot, JsonNode manifest) {
        string path = Path.Combine(projectRoot, "ProjectSettings", "GraphicsSettings.asset");
        if (!File.Exists(path)) {
            return "Unknown";
        }

        string[] lines = File.ReadAllLines(path);
        string? configured = ReadSetting(lines, "m_CustomRenderPipeline");
        if (configured is null) {
            return "Unknown";
        }

        if (configured.Contains("fileID: 0", StringComparison.Ordinal)) {
            return "BuiltIn";
        }

        if (lines.Any(line => line.Contains("UnityEngine.Rendering.Universal.", StringComparison.Ordinal))) {
            return "Universal";
        }

        if (lines.Any(line => line.Contains("UnityEngine.Rendering.HighDefinition.", StringComparison.Ordinal))) {
            return "HighDefinition";
        }

        var dependencies = manifest["dependencies"]?.AsObject();
        bool universal = dependencies?.ContainsKey("com.unity.render-pipelines.universal") == true;
        bool highDefinition = dependencies?.ContainsKey("com.unity.render-pipelines.high-definition") == true;
        return (universal, highDefinition) switch {
            (true, false) => "Universal",
            (false, true) => "HighDefinition",
            _ => "Custom"
        };
    }

    static string ApiCompatibility(int value, string editorVersion) {
        string majorText = editorVersion.Split('.', 2)[0];
        _ = int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out int major);
        return value switch {
            1 => ".NET 2.0",
            2 => ".NET 2.0 Subset",
            3 => ".NET Framework",
            4 => ".NET Web",
            5 => ".NET Micro",
            6 when major >= 2021 => ".NET Standard 2.1",
            6 => ".NET Standard 2.0",
            7 => ".NET",
            _ => $"Unknown ({value})"
        };
    }

    static string ScriptingBackend(int value) => value switch {
        0 => "Mono",
        1 => "IL2CPP",
        2 => ".NET",
        3 => "CoreCLR",
        _ => $"Unknown ({value})"
    };

    static string ColorSpace(int value) => value switch {
        0 => "Gamma",
        1 => "Linear",
        _ => $"Unknown ({value})"
    };

    static string InputHandling(int value) => value switch {
        0 => "Legacy",
        1 => "InputSystem",
        2 => "Both",
        _ => $"Unknown ({value})"
    };

    static string GraphicsApi(int value) => value switch {
        0 => "OpenGL2",
        1 => "Direct3D9",
        2 => "Direct3D11",
        3 => "PlayStation3",
        4 => "Null",
        6 => "Xbox360",
        8 => "OpenGLES2",
        11 => "OpenGLES3",
        12 => "PlayStationVita",
        13 => "PlayStation4",
        14 => "XboxOne",
        15 => "PlayStationMobile",
        16 => "Metal",
        17 => "OpenGLCore",
        18 => "Direct3D12",
        19 => "N3DS",
        21 => "Vulkan",
        22 => "Switch",
        23 => "XboxOneD3D12",
        24 => "GameCoreXboxOne",
        25 => "GameCoreXboxSeries",
        26 => "PlayStation5",
        27 => "PlayStation5NGGC",
        28 => "WebGPU",
        29 => "Switch2",
        _ => $"Unknown ({value})"
    };

    static int FindSetting(IReadOnlyList<string> lines, string name) {
        string prefix = $"  {name}:";
        for (int index = 0; index < lines.Count; index++) {
            if (lines[index].StartsWith(prefix, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }

    static int Indentation(string value) {
        int result = 0;
        while (result < value.Length && value[result] == ' ') {
            result++;
        }

        return result;
    }

    static string DecodeYamlScalar(string value) {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') {
            return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }

    static string? ReadRevision(string? editorVersionWithRevision) {
        if (string.IsNullOrWhiteSpace(editorVersionWithRevision)) {
            return null;
        }

        int opening = editorVersionWithRevision.LastIndexOf('(');
        int closing = editorVersionWithRevision.LastIndexOf(')');
        return opening >= 0 && closing > opening
            ? editorVersionWithRevision[(opening + 1)..closing].Trim()
            : null;
    }

    static string? ReadValue(IEnumerable<string> lines, string prefix) {
        string? line = lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? null : line[prefix.Length..].Trim();
    }
}

sealed class ProjectProbeResult {
    public string companyName { get; set; } = string.Empty;
    public string projectName { get; set; } = string.Empty;
    public string projectVersion { get; set; } = string.Empty;
    public string editorVersion { get; set; } = string.Empty;
    public string? editorRevision { get; set; }
    public string apiCompatibility { get; set; } = string.Empty;
    public bool allowUnsafeCode { get; set; }
    public Dictionary<string, string> scriptingBackendOverrides { get; set; } = new(StringComparer.Ordinal);
    public string renderPipeline { get; set; } = string.Empty;
    public string colorSpace { get; set; } = string.Empty;
    public Dictionary<string, GraphicsApiSettings> graphicsApis { get; set; } = new(StringComparer.Ordinal);
    public string inputHandling { get; set; } = string.Empty;
    public JsonNode packages { get; set; } = new JsonObject();
}

sealed class GraphicsApiSettings {
    public bool automatic { get; set; }
    public string[] apis { get; set; } = [];
}