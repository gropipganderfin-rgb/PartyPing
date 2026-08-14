using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PartyPing;

internal sealed class DiscordBotBridge : IDisposable
{
    private const string DiscordApiBase = "https://discord.com/api/v10";
    private const string OpenButtonPrefix = "partyping_open:";
    private const string JoinButtonPrefix = "partyping_join:";
    private const int GuildsIntent = 1;

    private readonly Func<ulong, CancellationToken, Task<bool>> openListing;
    private readonly HttpClient http = new();
    private readonly object stateLock = new();

    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private string configurationKey = string.Empty;
    private bool disposed;

    internal DiscordBotBridge(Func<ulong, CancellationToken, Task<bool>> openListing)
    {
        this.openListing = openListing;
    }

    internal string Status { get; private set; } =
        "Discord bot: configure bot token, channel ID, and your Discord user ID";

    internal static bool IsConfigured(Configuration config) =>
        !string.IsNullOrWhiteSpace(config.DiscordBotToken) &&
        ulong.TryParse(config.DiscordChannelId?.Trim(), out _) &&
        ulong.TryParse(config.DiscordUserId?.Trim(), out _);

    internal void EnsureRunning(Configuration config)
    {
        if (disposed)
            return;

        if (!IsConfigured(config))
        {
            StopCurrent("Discord bot: configure bot token, channel ID, and your Discord user ID");
            return;
        }

        var token = config.DiscordBotToken.Trim();
        var channelId = config.DiscordChannelId.Trim();
        var userId = config.DiscordUserId.Trim();
        var key = BuildConfigurationKey(token, channelId, userId);

        lock (stateLock)
        {
            if (configurationKey == key && runTask is { IsCompleted: false })
                return;

            runCancellation?.Cancel();
            configurationKey = key;
            runCancellation = new CancellationTokenSource();
            var cancellationToken = runCancellation.Token;
            runTask = Task.Run(() => RunReconnectLoopAsync(token, channelId, userId, cancellationToken));
            Status = "Discord bot: connecting...";
        }
    }

    private void StopCurrent(string status)
    {
        lock (stateLock)
        {
            configurationKey = string.Empty;
            runCancellation?.Cancel();
            runCancellation = null;
            runTask = null;
            Status = status;
        }
    }

    private async Task RunReconnectLoopAsync(
        string token,
        string channelId,
        string userId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var interactionEndpoint = await GetInteractionsEndpointUrlAsync(token, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(interactionEndpoint))
                {
                    Status = "Discord bot: remove the Interactions Endpoint URL in Developer Portal > General Information";
                }
                else
                {
                    await RunConnectionAsync(token, channelId, userId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Status = "Discord bot: disconnected; retrying...";
                Plugin.Log.Warning(ex, "PartyPing Discord bot Gateway connection failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RunConnectionAsync(
        string token,
        string channelId,
        string userId,
        CancellationToken cancellationToken)
    {
        var gatewayUrl = await GetGatewayUrlAsync(token, cancellationToken).ConfigureAwait(false);
        var socketUrl = gatewayUrl.TrimEnd('/') + "/?v=10&encoding=json";

        using var socket = new ClientWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = connectionCancellation.Token;

        await socket.ConnectAsync(new Uri(socketUrl), connectionToken).ConfigureAwait(false);

        long? sequence = null;
        var heartbeatInterval = await WaitForHelloAsync(socket, connectionToken).ConfigureAwait(false);
        var heartbeatTask = HeartbeatLoopAsync(
            socket,
            sendLock,
            heartbeatInterval,
            () => sequence,
            connectionToken);

        await SendJsonAsync(socket, sendLock, new
        {
            op = 2,
            d = new
            {
                token,
                intents = GuildsIntent,
                properties = new
                {
                    os = "windows",
                    browser = "PartyPing",
                    device = "PartyPing",
                },
            },
        }, connectionToken).ConfigureAwait(false);

        try
        {
            while (!connectionToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var document = await ReceiveJsonAsync(socket, connectionToken).ConfigureAwait(false);
                var root = document.RootElement;

                if (root.TryGetProperty("s", out var sequenceElement) &&
                    sequenceElement.ValueKind == JsonValueKind.Number &&
                    sequenceElement.TryGetInt64(out var nextSequence))
                {
                    sequence = nextSequence;
                }

                var op = root.TryGetProperty("op", out var opElement) ? opElement.GetInt32() : -1;
                if (op == 7)
                    throw new InvalidOperationException("Discord requested a Gateway reconnect.");

                if (op == 9)
                    throw new InvalidOperationException("Discord invalidated the Gateway session.");

                if (op == 1)
                {
                    await SendHeartbeatAsync(socket, sendLock, sequence, connectionToken).ConfigureAwait(false);
                    continue;
                }

                if (op != 0)
                    continue;

                var eventName = root.TryGetProperty("t", out var eventElement)
                    ? eventElement.GetString()
                    : null;

                if (string.Equals(eventName, "READY", StringComparison.Ordinal))
                {
                    Status = "Discord bot: connected - Open / Join Party buttons ready";
                    continue;
                }

                if (!string.Equals(eventName, "INTERACTION_CREATE", StringComparison.Ordinal) ||
                    !root.TryGetProperty("d", out var interaction))
                {
                    continue;
                }

                await HandleInteractionAsync(
                    interaction.Clone(),
                    channelId,
                    userId,
                    connectionToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connectionCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task HandleInteractionAsync(
        JsonElement interaction,
        string allowedChannelId,
        string allowedUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!interaction.TryGetProperty("type", out var typeElement) || typeElement.GetInt32() != 3)
                return;

            if (!interaction.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("custom_id", out var customIdElement))
            {
                return;
            }

            var customId = customIdElement.GetString();
            if (string.IsNullOrWhiteSpace(customId))
                return;

            var isJoin = customId.StartsWith(JoinButtonPrefix, StringComparison.Ordinal);
            var isOpen = customId.StartsWith(OpenButtonPrefix, StringComparison.Ordinal);
            if (!isJoin && !isOpen)
                return;

            Status = isJoin
                ? "Discord bot: Join Party button received..."
                : "Discord bot: Open in FFXIV button received...";

            if (!interaction.TryGetProperty("id", out var interactionIdElement) ||
                !interaction.TryGetProperty("token", out var interactionTokenElement))
            {
                Status = "Discord bot: button payload was missing its interaction ID/token";
                return;
            }

            var interactionId = interactionIdElement.GetString();
            var interactionToken = interactionTokenElement.GetString();
            if (string.IsNullOrWhiteSpace(interactionId) || string.IsNullOrWhiteSpace(interactionToken))
            {
                Status = "Discord bot: button payload contained an empty interaction ID/token";
                return;
            }

            var channelId = interaction.TryGetProperty("channel_id", out var channelElement)
                ? channelElement.GetString()
                : null;
            var userId = GetInteractionUserId(interaction);

            if (!string.Equals(channelId, allowedChannelId, StringComparison.Ordinal) ||
                !string.Equals(userId, allowedUserId, StringComparison.Ordinal))
            {
                await RespondEphemeralAsync(
                    interactionId,
                    interactionToken,
                    "This PartyPing button is restricted to its configured owner and channel.",
                    cancellationToken).ConfigureAwait(false);
                Status = "Discord bot: rejected a button click from a different user/channel";
                return;
            }

            var prefix = isJoin ? JoinButtonPrefix : OpenButtonPrefix;
            if (!ulong.TryParse(customId[prefix.Length..], out var listingId) || listingId == 0)
            {
                await RespondEphemeralAsync(
                    interactionId,
                    interactionToken,
                    "This Party Finder listing ID is invalid.",
                    cancellationToken).ConfigureAwait(false);
                Status = "Discord bot: button contained an invalid PF listing ID";
                return;
            }

            await RespondAsync(
                interactionId,
                interactionToken,
                new { type = 6 },
                cancellationToken).ConfigureAwait(false);

            if (isJoin)
            {
                Status = "Discord bot: button acknowledged - joining PF listing " + listingId;
                _ = JoinListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }
            else
            {
                Status = "Discord bot: button acknowledged - opening PF listing " + listingId;
                _ = OpenListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "Discord bot: button failed - " + ex.Message;
            Plugin.Log.Warning(ex, "PartyPing could not process a Discord PF button interaction");
        }
    }

    private async Task OpenListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)
    {
        try
        {
            var opened = await openListing(listingId, cancellationToken).ConfigureAwait(false);
            Status = opened
                ? "Discord bot: connected - last button opened PF listing " + listingId
                : "Discord bot: connected - FFXIV could not open PF listing " + listingId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "Discord bot: FFXIV open failed - " + ex.Message;
            Plugin.Log.Warning(ex, "PartyPing could not open PF listing {ListingId} after Discord acknowledgement", listingId);
        }
    }

    private async Task JoinListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await openListing(listingId, cancellationToken).ConfigureAwait(false))
            {
                Status = "Discord bot: connected - FFXIV could not open PF listing " + listingId + " for joining";
                return;
            }

            var joinActionSent = await PartyJoiner.JoinOpenedListingAsync(listingId, cancellationToken).ConfigureAwait(false);
            Status = joinActionSent
                ? "Discord bot: connected - Join Party action sent for PF listing " + listingId
                : "Discord bot: connected - FFXIV could not join PF listing " + listingId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "Discord bot: FFXIV join failed - " + ex.Message;
            Plugin.Log.Warning(ex, "PartyPing could not join PF listing {ListingId} after Discord acknowledgement", listingId);
        }
    }

    private async Task<string?> GetInteractionsEndpointUrlAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DiscordApiBase + "/applications/@me");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord application check returned {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("interactions_endpoint_url", out var endpointElement) ||
            endpointElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return endpointElement.GetString();
    }

    private async Task<string> GetGatewayUrlAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DiscordApiBase + "/gateway/bot");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord Gateway returned {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("url", out var urlElement))
            throw new InvalidOperationException("Discord Gateway response did not include a URL.");

        var url = urlElement.GetString();
        return string.IsNullOrWhiteSpace(url)
            ? throw new InvalidOperationException("Discord Gateway returned an empty URL.")
            : url;
    }

    private static async Task<int> WaitForHelloAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var document = await ReceiveJsonAsync(socket, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var op = root.TryGetProperty("op", out var opElement) ? opElement.GetInt32() : -1;
            if (op != 10 || !root.TryGetProperty("d", out var data) ||
                !data.TryGetProperty("heartbeat_interval", out var intervalElement))
            {
                continue;
            }

            return Math.Max(1000, intervalElement.GetInt32());
        }
    }

    private static async Task HeartbeatLoopAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        int intervalMilliseconds,
        Func<long?> getSequence,
        CancellationToken cancellationToken)
    {
        var initialDelay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * intervalMilliseconds);
        await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await SendHeartbeatAsync(socket, sendLock, getSequence(), cancellationToken).ConfigureAwait(false);
            await Task.Delay(intervalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task SendHeartbeatAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        long? sequence,
        CancellationToken cancellationToken) =>
        SendJsonAsync(socket, sendLock, new { op = 1, d = sequence }, cancellationToken);

    private static async Task SendJsonAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Discord closed the Gateway connection.");

            if (result.Count > 0)
                await buffer.WriteAsync(chunk.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);

            if (result.EndOfMessage)
                break;
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task RespondEphemeralAsync(
        string interactionId,
        string interactionToken,
        string message,
        CancellationToken cancellationToken) =>
        await RespondAsync(
            interactionId,
            interactionToken,
            new
            {
                type = 4,
                data = new
                {
                    content = message,
                    flags = 64,
                    allowed_mentions = new { parse = Array.Empty<string>() },
                },
            },
            cancellationToken).ConfigureAwait(false);

    private async Task RespondAsync(
        string interactionId,
        string interactionToken,
        object payload,
        CancellationToken cancellationToken)
    {
        var url = $"{DiscordApiBase}/interactions/{Uri.EscapeDataString(interactionId)}/{Uri.EscapeDataString(interactionToken)}/callback";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord interaction response returned {(int)response.StatusCode}: {Trim(body, 180)}");
        }
    }

    private static string? GetInteractionUserId(JsonElement interaction)
    {
        if (interaction.TryGetProperty("member", out var member) &&
            member.TryGetProperty("user", out var memberUser) &&
            memberUser.TryGetProperty("id", out var memberUserId))
        {
            return memberUserId.GetString();
        }

        if (interaction.TryGetProperty("user", out var user) &&
            user.TryGetProperty("id", out var userId))
        {
            return userId.GetString();
        }

        return null;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static string BuildConfigurationKey(string token, string channelId, string userId)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return tokenHash + "|" + channelId + "|" + userId;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lock (stateLock)
        {
            runCancellation?.Cancel();
            runCancellation = null;
            runTask = null;
        }

        http.Dispose();
    }
}
