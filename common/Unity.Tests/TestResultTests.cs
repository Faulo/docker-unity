using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unity.Tests;

public sealed class TestResultTests {
    [Test]
    public void BuildsPassedResultFromJUnit() {
        const string xml = """
                           <testsuites>
                             <testsuite tests="2" failures="0" errors="0" skipped="1" time="1.2345">
                               <testcase name="Passes" classname="Example.Tests" />
                               <testcase name="Skipped" classname="Example.Tests"><skipped /></testcase>
                             </testsuite>
                           </testsuites>
                           """;

        var result = Build(xml, 0);

        Assert.That(result["outcome"]?.GetValue<string>(), Is.EqualTo("passed"));
        Assert.That(result["counts"]?["total"]?.GetValue<int>(), Is.EqualTo(2));
        Assert.That(result["counts"]?["passed"]?.GetValue<int>(), Is.EqualTo(1));
        Assert.That(result["counts"]?["skipped"]?.GetValue<int>(), Is.EqualTo(1));
        Assert.That(result["durationSeconds"]?.GetValue<decimal>(), Is.EqualTo(1.234m));
        Assert.That(result["failures"]!.AsArray(), Is.Empty);
    }

    [Test]
    public void BuildsFailedResultWithCompleteDetails() {
        const string xml = """
                           <testsuites>
                             <testsuite tests="1" failures="1" errors="0" skipped="0" time="2">
                               <testcase name="Fails" classname="Example.Tests"><failure message="Expected true" type="AssertionException">line one
                           line two</failure></testcase>
                             </testsuite>
                           </testsuites>
                           """;

        var result = Build(xml, 2);
        var failure = result["failures"]![0]!.AsObject();

        Assert.That(result["outcome"]?.GetValue<string>(), Is.EqualTo("failed"));
        Assert.That(result["exitCode"]?.GetValue<int>(), Is.EqualTo(2));
        Assert.That(failure["name"]?.GetValue<string>(), Is.EqualTo("Fails"));
        Assert.That(failure["stackTrace"]?.GetValue<string>(), Does.Contain("line two"));
        Assert.That(result["failuresTruncated"]?.GetValue<bool>(), Is.False);
    }

    [Test]
    public void TruncatesFailureRecordsAtOneHundred() {
        string cases = string.Concat(Enumerable.Range(0, 101)
            .Select(index => $"<testcase name=\"Failure{index}\"><failure message=\"bad\">stack {index}</failure></testcase>"));
        string xml = $"<testsuites><testsuite tests=\"101\" failures=\"101\" errors=\"0\" skipped=\"0\" time=\"0\">{cases}</testsuite></testsuites>";

        var result = Build(xml, 2);

        Assert.That(result["failures"]!.AsArray(), Has.Count.EqualTo(100));
        Assert.That(result["failuresTruncated"]?.GetValue<bool>(), Is.True);
    }

    [TestCase("not xml", 1)]
    [TestCase("<testsuites />", 1)]
    [TestCase("<testsuites><testsuite tests=\"0\" failures=\"0\" errors=\"0\" skipped=\"0\" /></testsuites>", 1)]
    public void ReturnsCompleteOrderedLogForUntrustworthyResults(string standardOutput, int exitCode) {
        var result = new ExecResult("id", exitCode, standardOutput, "stderr", "[stdout]\nxml\n[stderr]\nstderr");
        var value = JsonNode.Parse(JsonSerializer.Serialize(UnityMcpController.BuildTestResult(result)))!.AsObject();

        Assert.That(value["outcome"]?.GetValue<string>(), Is.EqualTo("error"));
        Assert.That(value["log"]?.GetValue<string>(), Is.EqualTo("[stdout]\nxml\n[stderr]\nstderr"));
        Assert.That(value, Has.Count.EqualTo(3));
    }

    static JsonObject Build(string standardOutput, int exitCode) {
        var result = new ExecResult("id", exitCode, standardOutput, "stderr", "combined");
        return JsonNode.Parse(JsonSerializer.Serialize(UnityMcpController.BuildTestResult(result)))!.AsObject();
    }
}