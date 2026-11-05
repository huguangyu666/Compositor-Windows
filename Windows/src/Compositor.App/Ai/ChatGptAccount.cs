using System.Text.Json;

namespace Compositor.App.Ai;

// This store belongs to Compositor only. It never reads or rotates another application's tokens.
internal sealed class ChatGptAccount
{
    private readonly ChatGptOAuth oauth;
    private readonly IAiCredentialStore store;
    private readonly object gate = new();
    private ChatGptTokens? tokens;
    private Task<ChatGptTokens>? renewal;
    private long generation;

    public ChatGptAccount(ChatGptOAuth oauth, IAiCredentialStore store)
    {
        this.oauth = oauth;
        this.store = store;
        if (store.Read("chatgpt-account") is { } saved)
        {
            try { tokens = JsonSerializer.Deserialize<ChatGptTokens>(saved); }
            catch (JsonException) { throw new AiServiceException("The saved ChatGPT account is invalid. Sign in again."); }
            if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken) ||
                string.IsNullOrWhiteSpace(tokens.RefreshToken) || string.IsNullOrWhiteSpace(tokens.AccountId))
                throw new AiServiceException("The saved ChatGPT account is incomplete. Sign in again.");
        }
    }

    public void SetTokens(ChatGptTokens value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.RefreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.AccountId);
        lock (gate)
        {
            store.Write("chatgpt-account", JsonSerializer.Serialize(value));
            generation++;
            tokens = value;
            renewal = null;
        }
    }

    public void SignOut()
    {
        lock (gate)
        {
            // Retire memory even if the disk deletion fails. An older renewal cannot save over this generation.
            generation++;
            tokens = null;
            renewal = null;
            store.Delete("chatgpt-account");
        }
    }

    public Task<ChatGptTokens> GetTokensAsync(CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (tokens is null) return Task.FromException<ChatGptTokens>(new AiServiceException("Sign in to the selected ChatGPT account first."));
            if (tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return Task.FromResult(tokens);
            var before = tokens;
            long expected = generation;
            // Renewal has its own bounded lifetime. Cancelling one image request must not abandon a rotating token.
            renewal ??= Task.Run(() => RenewAsync(before, expected));
            return renewal.WaitAsync(cancellation);
        }
    }

    private async Task<ChatGptTokens> RenewAsync(ChatGptTokens before, long expected)
    {
        try
        {
            var updated = await oauth.RefreshAsync(before).ConfigureAwait(false);
            lock (gate)
            {
                if (expected != generation) throw new OperationCanceledException("The account changed during renewal.");
                // Upstream may already have consumed the old refresh token. Keep the rotated bundle
                // in memory even when persistence fails; the caller still sees the disk failure.
                tokens = updated;
                store.Write("chatgpt-account", JsonSerializer.Serialize(updated));
                return updated;
            }
        }
        finally { lock (gate) { if (expected == generation) renewal = null; } }
    }
}
