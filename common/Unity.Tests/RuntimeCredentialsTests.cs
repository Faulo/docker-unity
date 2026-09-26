using System.Diagnostics;

namespace Unity.Tests;

public sealed class RuntimeCredentialsTests {
    [Test]
    public void ResolvesDirectFileBackedAndMixedPairs() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = "unity-user",
            ["UNITY_CREDENTIALS_PSW"] = "unity-password",
            ["EMAIL_CREDENTIALS_USR"] = "email-user",
            ["EMAIL_CREDENTIALS_PSW_FILE"] = "/secrets/email-password"
        };
        var files = new Dictionary<string, string> {
            ["/secrets/email-password"] = "email-password\n"
        };

        var credentials = Resolve(environment, path => files[path]);
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        foreach (var variable in environment) {
            startInfo.Environment[variable.Key] = variable.Value;
        }
        credentials.ApplyTo(startInfo);

        Assert.Multiple(() => {
            Assert.That(startInfo.Environment["UNITY_CREDENTIALS_USR"], Is.EqualTo("unity-user"));
            Assert.That(startInfo.Environment["UNITY_CREDENTIALS_PSW"], Is.EqualTo("unity-password"));
            Assert.That(startInfo.Environment["EMAIL_CREDENTIALS_USR"], Is.EqualTo("email-user"));
            Assert.That(startInfo.Environment["EMAIL_CREDENTIALS_PSW"], Is.EqualTo("email-password"));
            Assert.That(startInfo.Environment.Keys, Has.None.EndsWith("_FILE"));
        });
    }

    [TestCase("UNITY_CREDENTIALS_USR")]
    [TestCase("UNITY_CREDENTIALS_PSW")]
    [TestCase("EMAIL_CREDENTIALS_USR")]
    [TestCase("EMAIL_CREDENTIALS_PSW")]
    public void RejectsDirectAndFileBackedFormsTogether(string name) {
        var environment = new Dictionary<string, string?> {
            [name] = "direct-secret",
            [name + "_FILE"] = "/secrets/value"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => "file-secret"));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(name));
            Assert.That(exception.Message, Does.Contain(name + "_FILE"));
            Assert.That(exception.Message, Does.Not.Contain("direct-secret"));
            Assert.That(exception.Message, Does.Not.Contain("file-secret"));
        });
    }

    [Test]
    public void RejectsMissingCredentialFileWithoutLeakingPairedValue() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR_FILE"] = "/secrets/missing",
            ["UNITY_CREDENTIALS_PSW"] = "paired-secret"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(
            environment,
            path => throw new FileNotFoundException("missing", path)));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain("UNITY_CREDENTIALS_USR_FILE"));
            Assert.That(exception.Message, Does.Contain("/secrets/missing"));
            Assert.That(exception.Message, Does.Not.Contain("paired-secret"));
        });
    }

    [Test]
    public void RejectsUnreadableCredentialFile() {
        var environment = new Dictionary<string, string?> {
            ["EMAIL_CREDENTIALS_USR_FILE"] = "/secrets/email-user",
            ["EMAIL_CREDENTIALS_PSW_FILE"] = "/secrets/email-password"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(
            environment,
            _ => throw new UnauthorizedAccessException("denied")));

        Assert.That(exception!.Message, Does.Contain("EMAIL_CREDENTIALS_USR_FILE"));
    }

    [TestCase("")]
    [TestCase("\n")]
    [TestCase("\r\n")]
    public void RejectsEmptyCredentialFile(string contents) {
        var environment = new Dictionary<string, string?> {
            ["EMAIL_CREDENTIALS_USR_FILE"] = "/secrets/email-user",
            ["EMAIL_CREDENTIALS_PSW_FILE"] = "/secrets/email-password"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => contents));

        Assert.That(exception!.Message, Does.Contain("EMAIL_CREDENTIALS_USR_FILE").And.Contain("empty"));
    }

    [TestCase("UNITY_CREDENTIALS_USR")]
    [TestCase("UNITY_CREDENTIALS_PSW")]
    [TestCase("EMAIL_CREDENTIALS_USR")]
    [TestCase("EMAIL_CREDENTIALS_PSW")]
    public void PreservesIncompleteCredentialInput(string present) {
        var environment = new Dictionary<string, string?> { [present] = "configured-secret" };

        var credentials = Resolve(environment, _ => throw new InvalidOperationException());

        Assert.That(credentials.WorkerEnvironment(), Is.EqualTo(new[] { $"{present}=configured-secret" }));
    }

    [Test]
    public void TreatsCompleteEmptyDirectPairAsUnconfigured() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = string.Empty,
            ["UNITY_CREDENTIALS_PSW"] = string.Empty
        };

        var credentials = Resolve(environment, _ => throw new InvalidOperationException());
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        startInfo.Environment["UNITY_CREDENTIALS_USR"] = string.Empty;
        startInfo.Environment["UNITY_CREDENTIALS_PSW"] = string.Empty;
        credentials.ApplyTo(startInfo);

        Assert.Multiple(() => {
            Assert.That(startInfo.Environment.ContainsKey("UNITY_CREDENTIALS_USR"), Is.False);
            Assert.That(startInfo.Environment.ContainsKey("UNITY_CREDENTIALS_PSW"), Is.False);
        });
    }

    [Test]
    public void RejectsEmptyCredentialFilePath() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR_FILE"] = " ",
            ["UNITY_CREDENTIALS_PSW_FILE"] = "/secrets/unity-password"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Resolve(environment, _ => "secret"));

        Assert.That(exception!.Message, Does.Contain("UNITY_CREDENTIALS_USR_FILE"));
    }

    [Test]
    public void ExposesUnityAndEmailCredentialsToWorkers() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = "unity-user",
            ["UNITY_CREDENTIALS_PSW"] = "unity-password",
            ["EMAIL_CREDENTIALS_USR"] = "email-user",
            ["EMAIL_CREDENTIALS_PSW"] = "email-password"
        };

        var credentials = Resolve(environment, _ => throw new InvalidOperationException());

        Assert.That(credentials.WorkerEnvironment(), Is.EqualTo(new[] {
            "UNITY_CREDENTIALS_USR=unity-user",
            "UNITY_CREDENTIALS_PSW=unity-password",
            "EMAIL_CREDENTIALS_USR=email-user",
            "EMAIL_CREDENTIALS_PSW=email-password"
        }));
    }

    [Test]
    public void RequestHeadersOverrideIndividualFallbackValuesWithoutRequiringPairs() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = "fallback-unity-user",
            ["UNITY_CREDENTIALS_PSW"] = "fallback-unity-password",
            ["EMAIL_CREDENTIALS_USR"] = "fallback-email-user"
        };
        var headers = new Dictionary<string, string?> {
            [RuntimeCredentials.UNITY_USERNAME_HEADER] = "client-unity-user",
            [RuntimeCredentials.EMAIL_PASSWORD_HEADER] = "client-email-password"
        };

        var request = Resolve(environment, _ => throw new InvalidOperationException())
            .WithRequestHeaders(name => headers.TryGetValue(name, out string? value) ? value : null);

        Assert.That(request.WorkerEnvironment(), Is.EqualTo(new[] {
            "UNITY_CREDENTIALS_USR=client-unity-user",
            "UNITY_CREDENTIALS_PSW=fallback-unity-password",
            "EMAIL_CREDENTIALS_USR=fallback-email-user",
            "EMAIL_CREDENTIALS_PSW=client-email-password"
        }));
    }

    [Test]
    public void EmptyRequestHeaderClearsItsIndividualFallback() {
        var environment = new Dictionary<string, string?> {
            ["UNITY_CREDENTIALS_USR"] = "fallback-unity-user",
            ["UNITY_CREDENTIALS_PSW"] = "fallback-unity-password"
        };

        var request = Resolve(environment, _ => throw new InvalidOperationException())
            .WithRequestHeaders(name => name == RuntimeCredentials.UNITY_USERNAME_HEADER ? string.Empty : null);

        Assert.That(request.WorkerEnvironment(), Is.EqualTo(new[] {
            "UNITY_CREDENTIALS_PSW=fallback-unity-password"
        }));
    }

    static RuntimeCredentials Resolve(
        IReadOnlyDictionary<string, string?> environment,
        Func<string, string> readFile) =>
        RuntimeCredentials.Resolve(
            name => environment.TryGetValue(name, out string? value) ? value : null,
            readFile);
}
