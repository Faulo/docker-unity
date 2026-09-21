using System.Text.Json.Nodes;

namespace Unity.Tests;

public sealed class ProjectProbeTests {
    static string validProject {
        get => Path.Combine(AppContext.BaseDirectory, "test-files", "ValidProject");
    }

    [Test]
    public void ReadsCompleteUnityProject() {
        var result = ProjectProbe.Read(validProject);

        Assert.That(result.companyName, Is.EqualTo("Example Company"));
        Assert.That(result.projectName, Is.EqualTo("Example Game"));
        Assert.That(result.projectVersion, Is.EqualTo("1.2.3"));
        Assert.That(result.editorVersion, Is.EqualTo("6000.3.13f1"));
        Assert.That(result.editorRevision, Is.EqualTo("8c4f11e4fb20"));
        Assert.That(result.apiCompatibility, Is.EqualTo(".NET Standard 2.1"));
        Assert.That(result.allowUnsafeCode, Is.False);
        Assert.That(result.scriptingBackendOverrides["WindowsStandaloneSupport"], Is.EqualTo("IL2CPP"));
        Assert.That(result.scriptingBackendOverrides["UnknownTarget"], Is.EqualTo("Unknown (99)"));
        Assert.That(result.renderPipeline, Is.EqualTo("Universal"));
        Assert.That(result.colorSpace, Is.EqualTo("Linear"));
        Assert.That(result.inputHandling, Is.EqualTo("InputSystem"));
        Assert.That(result.graphicsApis["WindowsStandaloneSupport"].automatic, Is.False);
        Assert.That(result.graphicsApis["WindowsStandaloneSupport"].apis, Is.EqualTo(new[] { "Direct3D11", "Direct3D12" }));
        Assert.That(result.packages["custom"]?.GetValue<string>(), Is.EqualTo("1.2.3"));
    }

    [Test]
    public void ReportsUnknownWhenGraphicsSettingsIsOptionalAndMissing() {
        using var project = TemporaryProject.CopyOf(validProject);
        File.Delete(Path.Combine(project.path, "ProjectSettings", "GraphicsSettings.asset"));

        Assert.That(ProjectProbe.Read(project.path).renderPipeline, Is.EqualTo("Unknown"));
    }

    [Test]
    public void RejectsMissingUnityProjectDirectory() {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try {
            var exception = Assert.Throws<InvalidOperationException>(() => ProjectProbe.Read(path))!;
            Assert.That(exception.Message, Does.Contain("Assets"));
        } finally {
            Directory.Delete(path, true);
        }
    }

    [Test]
    public void PreservesCompleteManifest() {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(validProject, "Packages", "manifest.json")))!.AsObject();

        Assert.That(JsonNode.DeepEquals(expected, ProjectProbe.Read(validProject).packages), Is.True);
    }

    sealed class TemporaryProject : IDisposable {
        TemporaryProject(string path) => this.path = path;

        internal string path { get; }

        public void Dispose() => Directory.Delete(path, true);

        internal static TemporaryProject CopyOf(string source) {
            string destination = Path.Combine(Path.GetTempPath(), "unity-tests-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, destination);
            return new TemporaryProject(destination);
        }

        static void CopyDirectory(string source, string destination) {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source)) {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (string directory in Directory.EnumerateDirectories(source)) {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}