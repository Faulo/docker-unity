using System.Text;

namespace Unity.Tests;

public class UnixProcessTreeTests {
    [TestCase("UNITY_INVOCATION_ID=abc\0", true)]
    [TestCase("OTHER=UNITY_INVOCATION_ID=abc\0", false)]
    [TestCase("UNITY_INVOCATION_ID=abcd\0", false)]
    [TestCase("OTHER=UNITY_INVOCATION_ID=abc\0UNITY_INVOCATION_ID=abc\0", true)]
    [TestCase("UNITY_INVOCATION_ID=abc", false)]
    public void InvocationIdentityRequiresAnExactEnvironmentEntry(string environment, bool expected) {
        Assert.That(UnixProcessTree.ContainsMarker(Encoding.UTF8.GetBytes(environment),
            Encoding.UTF8.GetBytes("UNITY_INVOCATION_ID=abc\0")), Is.EqualTo(expected));
    }
}
