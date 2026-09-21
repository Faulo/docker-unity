using System.Buffers.Binary;
using System.Text;

namespace Unity.Tests;

public sealed class DockerStreamTests {
    [Test]
    public void NormalizesUppercaseWindowsHostnameForDockerInspection() {
        var candidates = DockerEngineClient.SelfInspectionCandidates(null, "84A2225C9DAC");

        Assert.That(candidates, Is.EqualTo(new[] { "84a2225c9dac", "84A2225C9DAC" }));
    }

    [Test]
    public async Task SeparatesAndOrdersMultiplexedDockerFrames() {
        await using var stream = new MemoryStream();
        WriteFrame(stream, 1, "first");
        WriteFrame(stream, 1, " second");
        WriteFrame(stream, 2, "problem");
        WriteFrame(stream, 1, "last");
        stream.Position = 0;

        var output = await DockerEngineClient.ReadMultiplexedAsync(stream, CancellationToken.None);

        Assert.That(output.standardOutput, Is.EqualTo("first secondlast"));
        Assert.That(output.standardError, Is.EqualTo("problem"));
        Assert.That(output.combinedOutput, Is.EqualTo("[stdout]\nfirst second\n[stderr]\nproblem\n[stdout]\nlast"));
    }

    [Test]
    public async Task AcceptsUnframedOutputAsStandardOutput() {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("plain output"));

        var output = await DockerEngineClient.ReadMultiplexedAsync(stream, CancellationToken.None);

        Assert.That(output.standardOutput, Is.EqualTo("plain output"));
        Assert.That(output.standardError, Is.Empty);
        Assert.That(output.combinedOutput, Is.EqualTo("[stdout]\nplain output"));
    }

    [Test]
    public async Task RejectsTruncatedDockerFrame() {
        await using var stream = new MemoryStream();
        WriteFrame(stream, 1, "short", 10);
        stream.Position = 0;

        var readTask = DockerEngineClient.ReadMultiplexedAsync(stream, CancellationToken.None);
        Assert.ThrowsAsync<EndOfStreamException>(async () => await readTask);
    }

    static void WriteFrame(Stream stream, byte streamType, string value, int? declaredLength = null) {
        byte[] content = Encoding.UTF8.GetBytes(value);
        Span<byte> header = stackalloc byte[8];
        header[0] = streamType;
        BinaryPrimitives.WriteInt32BigEndian(header[4..], declaredLength ?? content.Length);
        stream.Write(header);
        stream.Write(content);
    }
}