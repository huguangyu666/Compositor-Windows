using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class ChatGptOAuthTests
{
    [Fact]
    public async Task AuthorizationUsesPkceAndTheSameVerifierAtExchange()
    {
        string? challenge = null;
        using var http = new HttpClient(new ImageGenerationTests.Handler(async (request, ct) =>
        {
            Assert.Equal("https://auth.openai.com/oauth/token", request.RequestUri!.AbsoluteUri);
            var form = ChatGptOAuth.Query(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("test-code", form["code"]);
            Assert.Equal(ChatGptOAuth.RedirectUri.AbsoluteUri, form["redirect_uri"]);
            Assert.Equal(challenge, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
            return TokenResponse("access-one", "refresh-one", includeIdToken: true);
        }));
        var oauth = new ChatGptOAuth(http);
        var login = oauth.CreateLogin();
        var query = ChatGptOAuth.Query(login.AuthorizationUri.Query);
        challenge = query["code_challenge"];
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.NotEqual(query["state"], ChatGptOAuth.Query(oauth.CreateLogin().AuthorizationUri.Query)["state"]);
        var tokens = await login.CompleteAsync(new(ChatGptOAuth.RedirectUri + "?code=test-code&state=" + query["state"]));
        Assert.Equal("access-one", tokens.AccessToken);
        Assert.Equal("refresh-one", tokens.RefreshToken);
        Assert.Equal("account-one", tokens.AccountId);
        Assert.True(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.DoesNotContain(tokens.AccessToken, tokens.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => login.CompleteAsync(
            new(ChatGptOAuth.RedirectUri + "?code=test-code&state=" + query["state"])));
    }

    [Theory]
    [InlineData("http://localhost:1455/auth/callback?code=a&state=wrong")]
    [InlineData("http://localhost:1455/auth/callback?code=a")]
    [InlineData("http://evil.example/auth/callback?code=a&state=wrong")]
    [InlineData("http://localhost:1455/other?code=a&state=wrong")]
    public async Task UnrelatedCallbacksCannotExchangeACode(string callback)
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) => throw new Xunit.Sdk.XunitException("Exchange called")));
        await Assert.ThrowsAsync<AiServiceException>(() => new ChatGptOAuth(http).CreateLogin().CompleteAsync(new(callback)));
    }

    [Fact]
    public async Task RenewalKeepsAccountAndRefreshTokenWhenUpstreamOmitsThem()
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler(async (request, ct) =>
        {
            var form = ChatGptOAuth.Query(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("refresh-old", form["refresh_token"]);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"access-new\",\"expires_in\":3600}") };
        }));
        var refreshed = await new ChatGptOAuth(http).RefreshAsync(Expired());
        Assert.Equal("refresh-old", refreshed.RefreshToken);
        Assert.Equal("account-old", refreshed.AccountId);
        Assert.Equal("access-new", refreshed.AccessToken);
    }

    [Theory]
    [InlineData("{\"access_token\":\"access-new\",\"expires_in\":\"invalid\"}")]
    [InlineData("{\"access_token\":\"access-new\",\"expires_in\":null}")]
    [InlineData("{\"access_token\":\"access-new\",\"expires_in\":0}")]
    public async Task InvalidTokenResponsesAreReportedAsServiceErrors(string body)
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        await Assert.ThrowsAsync<AiServiceException>(() => new ChatGptOAuth(http).RefreshAsync(Expired()));
    }

    [Fact]
    public async Task LoopbackLoginRejectsBadStateThenCompletesAndReleasesItsPort()
    {
        int exchanges = 0;
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) =>
        {
            exchanges++;
            return Task.FromResult(TokenResponse("access-one", "refresh-one", includeIdToken: true));
        }));
        var browser = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var signingIn = new ChatGptOAuth(http).SignInAsync(uri => browser.SetResult(uri), timeout.Token);
        var authorization = await browser.Task.WaitAsync(timeout.Token);
        // A browser/tab can close a connection halfway through a request. It must not end the sign-in.
        using (var interrupted = new System.Net.Sockets.TcpClient())
        {
            await interrupted.ConnectAsync(IPAddress.Loopback, 1455, timeout.Token);
            await interrupted.GetStream().WriteAsync("GET /auth/"u8.ToArray(), timeout.Token);
            interrupted.Client.LingerState = new System.Net.Sockets.LingerOption(true, 0);
        }
        using var callback = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var bad = await callback.GetAsync(ChatGptOAuth.RedirectUri + "?code=wrong&state=wrong", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(0, exchanges);
        string state = ChatGptOAuth.Query(authorization.Query)["state"];
        using var good = await callback.GetAsync(ChatGptOAuth.RedirectUri + "?code=valid&state=" + state, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("account-one", (await signingIn).AccountId);
        Assert.Equal(1, exchanges);
        AssertCallbackPortIsFree();
    }

    [Fact]
    public async Task CancellingBrowserLoginReleasesItsPort()
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) => throw new Xunit.Sdk.XunitException("Exchange called")));
        using var cancel = new CancellationTokenSource();
        var browser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signingIn = new ChatGptOAuth(http).SignInAsync(_ => browser.SetResult(), cancel.Token);
        await browser.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signingIn);
        AssertCallbackPortIsFree();
    }

    private static void AssertCallbackPortIsFree()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 1455);
        listener.Start();
    }

    [Fact]
    public async Task ConcurrentCallersShareRenewalAndOneCancellationDoesNotLoseRotatedCredentials()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var http = new HttpClient(new ImageGenerationTests.Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse("access-new", "refresh-new");
        }));
        var store = new MemoryStore();
        var account = new ChatGptAccount(new(http), store);
        account.SetTokens(Expired());
        using var cancel = new CancellationTokenSource();
        var first = account.GetTokensAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = account.GetTokensAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal("refresh-new", (await second).RefreshToken);
        Assert.Equal(1, calls);
        var restored = new ChatGptAccount(new(http), store);
        Assert.Equal("access-new", (await restored.GetTokensAsync()).AccessToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SigningOutDuringRenewalCannotRestoreTheOldAccount()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new ImageGenerationTests.Handler(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse("access-new", "refresh-new");
        }));
        var store = new MemoryStore();
        var account = new ChatGptAccount(new(http), store);
        account.SetTokens(Expired());
        var renewing = account.GetTokensAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        account.SignOut();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renewing);
        Assert.Null(store.Read("chatgpt-account"));
        await Assert.ThrowsAsync<AiServiceException>(() => account.GetTokensAsync());
    }

    [Fact]
    public async Task AFailedDiskWriteDoesNotMakeUsReuseARotatedRefreshToken()
    {
        int calls = 0;
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(TokenResponse("access-new", "refresh-new"));
        }));
        var store = new MemoryStore();
        var account = new ChatGptAccount(new(http), store);
        account.SetTokens(Expired());
        store.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => account.GetTokensAsync());
        Assert.Equal("refresh-new", (await account.GetTokensAsync()).RefreshToken);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AccountImagesUseTheAccountBackendAndNeverSwitchToApi()
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((request, _) =>
        {
            Assert.Equal("https://chatgpt.com/backend-api/codex/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer account-access", request.Headers.Authorization!.ToString());
            Assert.Equal("account-id", Assert.Single(request.Headers.GetValues("Chatgpt-Account-Id")));
            Assert.Equal("codex-tui", Assert.Single(request.Headers.GetValues("Originator")));
            return Task.FromResult(ImageGenerationTests.ImageResponse());
        }));
        var result = await new ImageGenerationClient(http, _ => Task.FromResult(
            new ChatGptTokens("account-access", "refresh", "account-id", DateTimeOffset.UtcNow.AddHours(1))))
            .GenerateAsync(new("prompt"));
        Assert.Equal(ImageGenerationTests.Png, result.Png);
    }

    internal static ChatGptTokens Expired() => new("access-old", "refresh-old", "account-old", DateTimeOffset.UtcNow.AddHours(-1));

    internal static HttpResponseMessage TokenResponse(string access, string refresh, bool includeIdToken = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            access_token = access, refresh_token = refresh, expires_in = 3600,
            id_token = includeIdToken ? "e30." + Base64Url(Encoding.UTF8.GetBytes(
                "{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"account-one\"}}")) + ".test-signature" : null
        }))
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed class MemoryStore : IAiCredentialStore
    {
        private readonly Dictionary<string, string> values = new();
        internal bool FailNextWrite { get; set; }
        public string? Read(string name) { lock (values) return values.GetValueOrDefault(name); }
        public void Write(string name, string value)
        {
            lock (values)
            {
                if (FailNextWrite) { FailNextWrite = false; throw new IOException("Simulated credential write failure."); }
                values[name] = value;
            }
        }
        public void Delete(string name) { lock (values) values.Remove(name); }
    }
}
