using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

if (args.Contains("--version", StringComparer.Ordinal)) {
    Console.WriteLine("Unity daemon-test backend 1.0");
    return 0;
}

int workingDirectoryOption = Array.IndexOf(args, "-d");
if (workingDirectoryOption >= 0 && workingDirectoryOption + 1 < args.Length) {
    Directory.CreateDirectory(args[workingDirectoryOption + 1]);
    Environment.CurrentDirectory = args[workingDirectoryOption + 1];
}

if (args.Length == 2 && args[0] == "probe-project") {
    return runControllerProbe(args[1]);
}

int separator = Array.IndexOf(args, "--");
if (separator < 0 || separator + 1 >= args.Length) {
    Console.Error.WriteLine($"Unexpected daemon-test command: {JsonSerializer.Serialize(args)}");
    return 2;
}

string[] command = args[(separator + 1)..];
return command[0] switch {
    "module-install" => installModules(command),
    "method" => executeMethod(command),
    "tests" => runTests(command),
    _ => unexpected(command)
};

static int installModules(string[] command) {
    if (command.Length != 3 || command[2] != "webgl") {
        Console.Error.WriteLine($"Invalid module-install command: {JsonSerializer.Serialize(command)}");
        return 2;
    }

    return 0;
}

static int runControllerProbe(string root) {
    string executable = Path.Combine(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "unity-sidecar.exe" : "unity-sidecar");
    var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
    startInfo.ArgumentList.Add("probe-project");
    startInfo.ArgumentList.Add(root);
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the real project probe.");
    process.WaitForExit();
    return process.ExitCode;
}

static int executeMethod(string[] command) {
    if (command.Length < 4) {
        Console.Error.WriteLine("Invalid method command.");
        return 2;
    }

    int arguments = Array.IndexOf(command, "--");
    string[] values = arguments < 0 ? [] : command[(arguments + 1)..];
    if (command[2] == "DaemonTests.Progress") {
        Thread.Sleep(TimeSpan.FromMilliseconds(500));
    }

    if (command[2] == "Slothsoft.UnityExtensions.Editor.Build.WebGL") {
        if (values.Length != 3 || values[0] != "-buildTarget" || values[1] != "WebGL") {
            Console.Error.WriteLine("The WebGL build requires its target and exactly one output path.");
            return 2;
        }

        string output = values[2];
        Directory.CreateDirectory(Path.Combine(output, "Build"));
        File.WriteAllText(Path.Combine(output, "index.html"), "<!doctype html><title>Daemon WebGL Build</title><h1>Ready</h1>");
        File.WriteAllBytes(Path.Combine(output, "Build", "game.data"), Encoding.ASCII.GetBytes("0123456789"));
        File.WriteAllBytes(Path.Combine(output, "Build", "game.wasm.br"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(output, "Build", "game.js.gz"), "compressed javascript fixture");
        return 0;
    }

    string credentialPrefix = command[2] switch {
        "DaemonTests.ClientA" => "client-a",
        "DaemonTests.ClientB" => "client-b",
        _ => "daemon"
    };
    Console.WriteLine(JsonSerializer.Serialize(new {
        method = command[2],
        arguments = values,
        workingDirectory = Environment.CurrentDirectory,
        composerProject = Environment.GetEnvironmentVariable("COMPOSER"),
        composerVendorDirectory = Environment.GetEnvironmentVariable("COMPOSER_VENDOR_DIR"),
        credentials = new {
            unity = Environment.GetEnvironmentVariable("UNITY_CREDENTIALS_USR") == $"{credentialPrefix}-unity-user"
                    && Environment.GetEnvironmentVariable("UNITY_CREDENTIALS_PSW") == $"{credentialPrefix}-unity-password",
            email = Environment.GetEnvironmentVariable("EMAIL_CREDENTIALS_USR") == $"{credentialPrefix}-email-user"
                    && Environment.GetEnvironmentVariable("EMAIL_CREDENTIALS_PSW") == $"{credentialPrefix}-email-password"
        }
    }));
    Console.Error.WriteLine("daemon-test stderr");
    return 7;
}

static int runTests(string[] command) {
    int project = Array.IndexOf(command, "-") + 1;
    string[] modes = project > 0 && project + 1 < command.Length ? command[(project + 1)..] : [];
    string cases = string.Concat(modes.Select(mode => $"<testcase name=\"{SecurityElement.Escape(mode)}\" classname=\"DaemonTests\" />"));
    Console.WriteLine($"<testsuites><testsuite tests=\"{modes.Length}\" failures=\"0\" errors=\"0\" skipped=\"0\" time=\"0.01\">{cases}</testsuite></testsuites>");
    return 0;
}

static int unexpected(string[] command) {
    Console.Error.WriteLine($"Unexpected daemon-test operation: {JsonSerializer.Serialize(command)}");
    return 2;
}
