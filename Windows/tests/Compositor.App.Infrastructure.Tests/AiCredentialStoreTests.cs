using System.Text;
using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class AiCredentialStoreTests
{
    [Fact]
    public void IndependentCredentialSlotsStaySeparateAfterDeletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "compositor-credential-slots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AiCredentialStore(directory);
            store.Write("api-key", "existing-api-test-key");
            store.Write("chatgpt-account", "account-test-credential");
            Assert.Equal("account-test-credential", new AiCredentialStore(directory).Read("chatgpt-account"));
            store.Delete("chatgpt-account");
            Assert.Null(store.Read("chatgpt-account"));
            Assert.Equal("existing-api-test-key", store.Read("api-key"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CredentialsSurviveReopenWithoutAppearingAsPlaintextOnDisk()
    {
        string directory = Path.Combine(Path.GetTempPath(), "compositor-ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AiCredentialStore(directory);
            Assert.Null(store.Read("api-key"));
            // JWT bundles can exceed Credential Manager's per-entry limit; DPAPI protects the whole bundle.
            string secret = "local-test-secret:" + new string('a', 12000);
            store.Write("chatgpt-account", secret);
            store.Write("api-key", "local-test-key");
            var again = new AiCredentialStore(directory);
            Assert.Equal(secret, again.Read("chatgpt-account"));
            Assert.Equal("local-test-key", again.Read("api-key"));
            foreach (var path in Directory.GetFiles(directory))
                Assert.DoesNotContain("local-test-", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            again.Write("api-key", "replacement-test-key");
            Assert.Equal("replacement-test-key", store.Read("api-key"));
            again.Delete("chatgpt-account");
            Assert.Null(store.Read("chatgpt-account"));
            Assert.Equal("replacement-test-key", store.Read("api-key"));
            Assert.Single(Directory.GetFiles(directory));
            Assert.Throws<ArgumentException>(() => store.Write("../escape", "no"));
        }
        finally
        {
            // Only the random directory created by this test is removed.
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
