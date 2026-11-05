// Open-source attribution: PKCE, authorize/exchange and refresh protocol adapted from
// router-for-me/CLIProxyAPI, internal/auth/codex/{pkce,openai_auth}.go at
// 1f917e77c4562623aa49e9804d999524151a2f96. MIT; see Windows/third-party/CLIProxyAPI-LICENSE.txt.
// https://github.com/router-for-me/CLIProxyAPI -- C# adaptation with loopback-only callbacks.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Compositor.App.Ai;

internal sealed class ChatGptOAuth(HttpClient http)
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly Uri RedirectUri = new("http://localhost:1455/auth/callback");

    public ChatGptLogin CreateLogin() => new(this);

    public async Task<ChatGptTokens> SignInAsync(Action<Uri>? openBrowser = null, CancellationToken cancellation = default)
    {
        var login = CreateLogin();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var listener = new TcpListener(IPAddress.Loopback, RedirectUri.Port);
        try
        {
            try { listener.Start(); }
            catch (SocketException) { throw new AiServiceException("The OAuth callback port 1455 is unavailable. Close the other sign-in and try again."); }
            if (openBrowser is not null) openBrowser(login.AuthorizationUri);
            else Process.Start(new ProcessStartInfo(login.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                await using var stream = connection.GetStream();
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                Uri? callback;
                try { callback = await ReadCallbackAsync(stream, readTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
                if (callback is null || !login.Matches(callback))
                {
                    try { await ReplyAsync(stream, false, timeout.Token).ConfigureAwait(false); }
                    catch (IOException) { }
                    continue;
                }
                try
                {
                    var tokens = await login.CompleteAsync(callback, timeout.Token).ConfigureAwait(false);
                    // A closed browser tab must not discard an already exchanged credential bundle.
                    try { await ReplyAsync(stream, true, timeout.Token).ConfigureAwait(false); }
                    catch (IOException) { }
                    return tokens;
                }
                catch
                {
                    try { await ReplyAsync(stream, false, timeout.Token).ConfigureAwait(false); }
                    catch (IOException) { }
                    throw;
                }
            }
        }
        finally { listener.Stop(); }
    }

    internal async Task<ChatGptTokens> ExchangeAsync(string code, string verifier, CancellationToken cancellation)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = RedirectUri.AbsoluteUri, ["code_verifier"] = verifier
        };
        return await RequestTokensAsync(fields, null, cancellation).ConfigureAwait(false);
    }

    public Task<ChatGptTokens> RefreshAsync(ChatGptTokens previous, CancellationToken cancellation = default) =>
        RequestTokensAsync(new()
        {
            ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = previous.RefreshToken,
            ["scope"] = "openid profile email"
        }, previous, cancellation);

    private async Task<ChatGptTokens> RequestTokensAsync(Dictionary<string, string> fields, ChatGptTokens? previous,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token")
        { Content = new FormUrlEncodedContent(fields) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var json = await AiHttp.SendAsync(http, request, 256 * 1024, timeout.Token).ConfigureAwait(false);
        var root = json.RootElement;
        string? access = Text(root, "access_token");
        string? refresh = Text(root, "refresh_token") ?? previous?.RefreshToken;
        string? account = AccountId(Text(root, "id_token")) ?? previous?.AccountId;
        if (!AiHttp.IsCredentialValue(access) || string.IsNullOrWhiteSpace(refresh) || !AiHttp.IsCredentialValue(account) ||
            !root.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number ||
            !expiry.TryGetInt32(out int seconds) || seconds is <= 0 or > 31536000)
            throw new AiServiceException("The sign-in service returned incomplete account credentials.");
        return new(access!, refresh, account!, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private static string? AccountId(string? idToken)
    {
        if (idToken is null) return null;
        var parts = idToken.Split('.');
        if (parts.Length != 3) return null;
        string payload = parts[1].Replace('-', '+').Replace('_', '/');
        try
        {
            // Routing metadata from the TLS token response, not verification of an arbitrary user's JWT.
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                ? Text(auth, "chatgpt_account_id") : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
        ? text : null;

    internal static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!values.TryAdd(key, value)) throw new AiServiceException("The OAuth callback contains duplicate fields.");
        }
        return values;
    }

    private static async Task<Uri?> ReadCallbackAsync(NetworkStream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[8192];
        int count = 0;
        while (count < header.Length)
        {
            int read = await stream.ReadAsync(header.AsMemory(count), cancellation).ConfigureAwait(false);
            if (read == 0) return null;
            count += read;
            string text = Encoding.ASCII.GetString(header, 0, count);
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
            string[] line = text.Split("\r\n", 2)[0].Split(' ');
            return line.Length == 3 && line[0] == "GET" && line[1].StartsWith('/') &&
                Uri.TryCreate("http://localhost:1455" + line[1], UriKind.Absolute, out var uri) ? uri : null;
        }
        return null;
    }

    private static Task ReplyAsync(NetworkStream stream, bool success, CancellationToken cancellation)
    {
        string body = success ? "Sign-in complete. Return to Compositor." : "Sign-in could not be completed. Return to Compositor.";
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n{body}");
        return stream.WriteAsync(response, cancellation).AsTask();
    }

    internal sealed class ChatGptLogin
    {
        private readonly ChatGptOAuth owner;
        private readonly string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        private readonly string verifier = Base64Url(RandomNumberGenerator.GetBytes(96));
        private int used;
        public Uri AuthorizationUri { get; }

        internal ChatGptLogin(ChatGptOAuth owner)
        {
            this.owner = owner;
            var fields = new Dictionary<string, string>
            {
                ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri.AbsoluteUri,
                ["scope"] = "openid email profile offline_access", ["state"] = state,
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256", ["prompt"] = "login", ["id_token_add_organizations"] = "true",
                ["codex_cli_simplified_flow"] = "true"
            };
            AuthorizationUri = new("https://auth.openai.com/oauth/authorize?" + string.Join("&",
                fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        }

        internal bool Matches(Uri callback)
        {
            if (!callback.IsAbsoluteUri || callback.Scheme != "http" || callback.Host != "localhost" ||
                callback.Port != 1455 || callback.AbsolutePath != "/auth/callback" || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0)
                return false;
            try
            {
                return Query(callback.Query).TryGetValue("state", out var received) &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(received));
            }
            catch (AiServiceException) { return false; }
        }

        public Task<ChatGptTokens> CompleteAsync(Uri callback, CancellationToken cancellation = default)
        {
            if (!Matches(callback)) return Task.FromException<ChatGptTokens>(new AiServiceException("The OAuth callback does not match this sign-in."));
            var values = Query(callback.Query);
            if (values.ContainsKey("error") || !values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                return Task.FromException<ChatGptTokens>(new AiServiceException("Sign-in was denied or cancelled."));
            if (Interlocked.Exchange(ref used, 1) != 0)
                return Task.FromException<ChatGptTokens>(new InvalidOperationException("This sign-in callback was already used."));
            return owner.ExchangeAsync(code, verifier, cancellation);
        }

        private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
