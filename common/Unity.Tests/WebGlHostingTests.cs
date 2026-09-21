namespace Unity.Tests;

public sealed class WebGlHostingTests {
    [TestCase(@"C:\Windows\Temp\unity-webgl\build-id", "build-id")]
    [TestCase("/tmp/unity-webgl/build-id", "build-id")]
    public void GetsContainerPathNameAcrossPlatforms(string path, string expected) => Assert.That(UnityMcpController.ContainerPathName(path), Is.EqualTo(expected));

    [Test]
    public void MovesWindowsArchiveDirectoryContentsToBuildRoot() {
        string destination = Path.Combine(Path.GetTempPath(), "unity-webgl-archive-tests-" + Guid.NewGuid().ToString("N"));
        string archiveRoot = Path.Combine(destination, "worker-build-id");
        Directory.CreateDirectory(Path.Combine(archiveRoot, "Build"));
        File.WriteAllText(Path.Combine(archiveRoot, "index.html"), "fixture");
        File.WriteAllText(Path.Combine(archiveRoot, "Build", "game.data"), "data");
        try {
            UnityMcpController.MoveArchiveContentsToRoot(destination, "worker-build-id");

            Assert.Multiple(() => {
                Assert.That(File.ReadAllText(Path.Combine(destination, "index.html")), Is.EqualTo("fixture"));
                Assert.That(File.ReadAllText(Path.Combine(destination, "Build", "game.data")), Is.EqualTo("data"));
                Assert.That(Directory.Exists(archiveRoot), Is.False);
            });
        } finally {
            Directory.Delete(destination, true);
        }
    }

    [Test]
    public void UsesToolInstallationDirectoryAsDocumentRoot() {
        Assert.That(WebGlHosting.documentRoot, Is.EqualTo(OperatingSystem.IsWindows()
            ? @"C:\unity\webgl"
            : "/unity/webgl"));
    }

    [TestCase("Example Game", "example-game")]
    [TestCase("  A/B:C  ", "a-b-c")]
    [TestCase("___", "project")]
    [TestCase(null, "project")]
    public void CreatesReadableSafeProjectSlugs(string? value, string expected) => Assert.That(WebGlHosting.ProjectSlug(value), Is.EqualTo(expected));

    [TestCase("game.wasm", "application/wasm")]
    [TestCase("game.wasm.br", "application/wasm")]
    [TestCase("game.js.gz", "application/javascript")]
    [TestCase("game.data", "application/octet-stream")]
    [TestCase("game.data.gz", "application/gzip")]
    [TestCase("game.symbols.json.br", "application/octet-stream")]
    [TestCase("game.unityweb", "application/octet-stream")]
    public void ProvidesUnityWebContentTypes(string path, string expected) {
        var provider = new UnityWebContentTypeProvider();

        bool found = provider.TryGetContentType(path, out string contentType);

        using (Assert.EnterMultipleScope()) {
            Assert.That(found, Is.True);
            Assert.That(contentType, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task ClaimsHumanReadableTimestampDirectory() {
        string root = Path.Combine(Path.GetTempPath(), "unity-webgl-tests-" + Guid.NewGuid().ToString("N"));
        try {
            var build = await WebGlHosting.ClaimBuildDirectoryAsync(root, "example-game", CancellationToken.None);

            using (Assert.EnterMultipleScope()) {
                Assert.That(build.projectSlug, Is.EqualTo("example-game"));
                Assert.That(build.buildId, Does.Match(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}Z$"));
                Assert.That(Directory.Exists(build.directory), Is.True);
                Assert.That(WebGlHosting.PublicPath(build), Is.EqualTo($"/webgl/example-game/{build.buildId}/"));
            }
        } finally {
            if (Directory.Exists(root)) {
                Directory.Delete(root, true);
            }
        }
    }
}