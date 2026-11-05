using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class AiApiConnectionTests
{
    private sealed class Store : IAiCredentialStore
    {
        public string? Value;
        public string? Read(string name) => Value;
        public void Write(string name, string value) => Value = value;
        public void Delete(string name) => Value = null;
    }

    [Fact]
    public void SavedKeyReopensWithItsEndpointAndCannotBeUsedAtAnotherEndpoint()
    {
        var store = new Store();
        new AiApiConnection("test-key", new("https://images.example.test/v1")).Save(store);
        var reopened = AiApiConnection.Load(store)!;
        Assert.Equal("https://images.example.test/v1/", reopened.ApiBase.AbsoluteUri);
        Assert.Equal("test-key", reopened.KeyFor(new("https://images.example.test/v1/")));
        var error = Assert.Throws<AiServiceException>(() => reopened.KeyFor(new("https://api.openai.com/v1/")));
        Assert.DoesNotContain("test-key", error.ToString());
        Assert.DoesNotContain("test-key", reopened.ToString());
    }

    [Fact]
    public void LegacyUnscopedKeysBelongOnlyToTheOriginalOfficialEndpoint()
    {
        var reopened = AiApiConnection.Load(new Store { Value = "legacy-test-key" })!;
        Assert.Equal("legacy-test-key", reopened.KeyFor(new("https://api.openai.com/v1/")));
        Assert.Throws<AiServiceException>(() => reopened.KeyFor(new("https://other.example.test/v1/")));
        Assert.Null(AiApiConnection.Load(new Store()));
    }

    [Theory]
    [InlineData("http://images.example.test/v1/")]
    [InlineData("https://user:pass@images.example.test/v1/")]
    [InlineData("https://images.example.test/v1/?token=private")]
    public void InvalidEndpointsCannotBeSaved(string address)
    {
        Assert.Throws<ArgumentException>(() => new AiApiConnection("test-key", new(address)));
    }

    [Fact]
    public void InvalidSavedConnectionDoesNotBecomeARawKeyOrExposeItsContents()
    {
        var error = Assert.Throws<AiServiceException>(() => AiApiConnection.Load(new Store { Value = "{private-test-key" }));
        Assert.DoesNotContain("private-test-key", error.ToString());
    }
}
