namespace Unity.Tests;

public sealed class CommandAndConfigurationTests {
    [Test]
    public void RoutesSidecarExecutable() {
        var command = CommandRouter.Route("unity-sidecar", ["health"]);

        Assert.That(command.mode, Is.EqualTo(EApplicationMode.SIDECAR));
        Assert.That(command.arguments, Is.EqualTo(new[] { "health" }));
    }

    [Test]
    public void DoesNotTreatUnityArgumentsAsSidecarCommands() {
        string[] arguments = ["sidecar", "status"];
        var command = CommandRouter.Route("unity", arguments);

        Assert.That(command.mode, Is.EqualTo(EApplicationMode.UNITY));
        Assert.That(command.arguments, Is.SameAs(arguments));
    }

    [Test]
    public void UsesInvokedAliasInsteadOfResolvedProcessPath() {
        string executable = Program.ResolveExecutable(
            "/usr/local/bin/unity",
            ["/usr/local/bin/unity", "health"],
            "/usr/local/bin/unity-sidecar");

        Assert.That(executable, Is.EqualTo("unity-sidecar"));
    }

    [Test]
    public void PreservesUnityArguments() {
        string[] arguments = ["empty-project", "test"];
        var command = CommandRouter.Route("unity", arguments);

        Assert.That(command.mode, Is.EqualTo(EApplicationMode.UNITY));
        Assert.That(command.arguments, Is.SameAs(arguments));
    }

    [Test]
    public void SelectsInstalledComposerProjectWithoutChangingWorkingDirectory() {
        var startInfo = Program.ComposerStartInfo(["--version"]);
        string[] arguments = startInfo.ArgumentList.ToArray();

        using (Assert.EnterMultipleScope()) {
            Assert.That(startInfo.Environment["COMPOSER"], Is.EqualTo(OperatingSystem.IsWindows()
                ? @"C:\unity\composer.json"
                : "/unity/composer.json"));
            Assert.That(startInfo.Environment["COMPOSER_VENDOR_DIR"], Is.EqualTo(OperatingSystem.IsWindows()
                ? @"C:\unity\vendor"
                : "/unity/vendor"));
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo(Environment.CurrentDirectory));
            Assert.That(arguments, Does.Not.Contain("-d"));
            Assert.That(arguments, Does.Not.Contain(OperatingSystem.IsWindows()
                ? @"C:\unity"
                : "/unity"));
        }
    }

    [TestCase(null, false, false)]
    [TestCase("0", false, false)]
    [TestCase("1", true, false)]
    [TestCase("", false, true)]
    [TestCase("true", false, true)]
    [TestCase(" 1", false, true)]
    [TestCase("2", false, true)]
    public void ParsesMcpActivation(string? value, bool expected, bool expectsWarning) {
        var warnings = new List<string>();
        Assert.Multiple(() => {
            Assert.That(McpActivation.Parse(value, warnings.Add), Is.EqualTo(expected));
            Assert.That(warnings, expectsWarning ? Has.Count.EqualTo(1) : Is.Empty);
        });
    }

    [TestCase(null, 86_400)]
    [TestCase("", 86_400)]
    [TestCase("0", 0)]
    [TestCase("42", 42)]
    public void ParsesCallTimeout(string? value, long expected) =>
        Assert.That(Program.ParseTimeout(value), Is.EqualTo(expected));

    [TestCase("-1")]
    [TestCase("one")]
    [TestCase(" 1")]
    public void RejectsInvalidCallTimeout(string value) =>
        Assert.Throws<ArgumentException>(() => Program.ParseTimeout(value));

    [Test]
    public void SanitizesLoggedCommandsWithoutArguments() {
        Assert.That(Program.SanitizeCommand(["exec", "--", "unity-command", "secret"]), Is.EqualTo("unity-command"));
        Assert.That(Program.SanitizeCommand(["exec", "weird command"]), Is.EqualTo("weird_command"));
    }

    [Test]
    public void FormatsDurationsAndDeadlines() {
        var started = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

        Assert.That(Program.FormatDuration(TimeSpan.FromHours(25) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)), Is.EqualTo("25:02:03"));
        Assert.That(Program.ResolveDeadline(started, 5), Is.EqualTo(started.AddSeconds(5)));
        Assert.That(Program.ResolveDeadline(started, 0), Is.Null);
    }

    [TestCase("http://localhost:8080", true)]
    [TestCase("https://127.0.0.1:1234", true)]
    [TestCase("http://[::1]:8080", true)]
    [TestCase("https://example.com", false)]
    [TestCase("null", false)]
    [TestCase(null, false)]
    public void ValidatesLoopbackOrigins(string? value, bool expected) =>
        Assert.That(McpServerRuntime.IsLoopbackOrigin(value), Is.EqualTo(expected));
}