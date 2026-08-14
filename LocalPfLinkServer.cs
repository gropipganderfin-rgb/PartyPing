using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PartyPing;

internal sealed class LocalPfLinkServer : IDisposable
{
    private const int PreferredPort = 17854;

    private readonly Func<ulong, CancellationToken, Task<bool>> openListing;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private TcpListener? listener;
    private Task? acceptLoop;

    internal LocalPfLinkServer(Func<ulong, CancellationToken, Task<bool>> openListing)
    {
        this.openListing = openListing;
    }

    internal int Port { get; private set; }
    internal bool IsRunning => listener is not null;

    internal void Start()
    {
        if (listener is not null)
            return;

        try
        {
            StartOnPort(PreferredPort);
        }
        catch (SocketException)
        {
            listener?.Stop();
            listener = null;
            StartOnPort(0);
        }
    }

    internal string? BuildOpenUrl(ulong listingId)
    {
        if (!IsRunning || listingId == 0)
            return null;

        return $"http://127.0.0.1:{Port}/open?id={listingId}&token={Uri.EscapeDataString(token)}";
    }

    private void StartOnPort(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(8);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(() => AcceptLoopAsync(cancellation.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "PartyPing localhost PF link listener failed while accepting a request");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                string? headerLine;
                do
                {
                    headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(headerLine));

                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", "Only GET requests are supported.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var requestUri) ||
                    !string.Equals(requestUri.AbsolutePath, "/open", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", "This PartyPing link is not valid.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var requestToken = GetQueryParameter(requestUri, "token");
                if (!TokenMatches(requestToken))
                {
                    await WriteResponseAsync(stream, 403, "Forbidden", "This PartyPing link is expired or invalid.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var idText = GetQueryParameter(requestUri, "id");
                if (!ulong.TryParse(idText, out var listingId) || listingId == 0)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", "The Party Finder listing ID is invalid.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var opened = await openListing(listingId, cancellationToken).ConfigureAwait(false);
                if (opened)
                {
                    await WriteResponseAsync(
                        stream,
                        200,
                        "OK",
                        "PartyPing sent this listing to FFXIV. Return to the game to view the Party Finder listing.",
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteResponseAsync(
                        stream,
                        409,
                        "Conflict",
                        "FFXIV could not open this listing. It may have ended, filled, or the game may currently be unable to open Party Finder.",
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "PartyPing localhost PF link request failed");
            }
        }
    }

    private bool TokenMatches(string? requestToken)
    {
        if (string.IsNullOrWhiteSpace(requestToken))
            return false;

        var expected = Encoding.UTF8.GetBytes(token);
        var actual = Encoding.UTF8.GetBytes(requestToken);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (!string.Equals(WebUtility.UrlDecode(pair[0]), name, StringComparison.Ordinal))
                continue;

            return pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : string.Empty;
        }

        return null;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string message,
        CancellationToken cancellationToken)
    {
        var body =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            "<title>PartyPing</title></head>" +
            "<body style=\"font-family:system-ui,sans-serif;max-width:620px;margin:64px auto;padding:0 20px\">" +
            "<h1>PartyPing</h1><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        listener?.Stop();
        listener = null;
        acceptLoop = null;
        cancellation.Dispose();
    }
}
