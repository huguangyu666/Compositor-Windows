using System.Net;
using System.Net.Sockets;

namespace Compositor.App.Ai;

internal static class ImageDownload
{
    // Returned URLs are untrusted. Never reuse the API client's credentials, cookies or proxy.
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync
    }) { Timeout = TimeSpan.FromMinutes(2) };

    internal static async Task<byte[]> ReadAsync(HttpClient http, string address, int byteLimit,
        CancellationToken cancellation)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && !IsPublicAddress(ip)))
            throw new AiServiceException("The AI service returned an unsafe image URL. An HTTPS URL on a public host is required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new("image/png"));
        try
        {
            return await AiHttp.ReadBytesAsync(http, request, byteLimit, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new AiServiceException("The generated image download timed out.");
        }
        catch (AiServiceException error)
        {
            throw new AiServiceException("The generated image could not be downloaded. " + error.Message, error.StatusCode);
        }
        catch (HttpRequestException error)
        {
            // Exception details may contain signed URLs. Keep them out of logs and UI.
            throw new AiServiceException("The generated image could not be downloaded. " + FailureReason(error));
        }
        catch (IOException)
        {
            throw new AiServiceException("The generated image download was interrupted.");
        }
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellation)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation).ConfigureAwait(false); }
        catch (SocketException) { throw new ConnectionFailure("DNS lookup failed."); }
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new ConnectionFailure("DNS returned a non-public or synthetic address. VPN Fake-IP DNS may require an exclusion for the image host.");
        // Connect to the checked address itself, so a second DNS lookup cannot bypass the check.
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new ConnectionFailure("A direct connection to the public image host failed. Check the network or proxy requirements.");
    }

    internal sealed class ConnectionFailure(string reason) : HttpRequestException
    {
        internal string Reason { get; } = reason;
    }

    internal static string FailureReason(HttpRequestException error)
    {
        // Inspect only our fixed messages and framework enums, never upstream exception text.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ConnectionFailure failure) return failure.Reason;
        return error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "DNS lookup failed.",
            HttpRequestError.SecureConnectionError => "TLS negotiation or certificate validation failed.",
            HttpRequestError.ConnectionError => "The direct HTTPS connection failed. Check the network or proxy requirements.",
            HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse => "The image host returned an invalid HTTP response.",
            HttpRequestError.ResponseEnded => "The image host closed the response early.",
            _ => "An HTTPS transport error occurred."
        };
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || bytes[1] == 2)) &&
                !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // Only global unicast IPv6; exclude transition and documentation ranges.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId == 0 &&
            (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }
}
