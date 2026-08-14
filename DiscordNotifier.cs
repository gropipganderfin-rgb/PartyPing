using System.Net;
using System.Text;
using System.Text.Json;

namespace PartyPing;

internal sealed record DiscordSendResult(string Status, string MessageId);

internal sealed class DiscordNotifier : IDisposable
{
    private readonly HttpClient http = new();

    public async Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        var result = await SendTrackedAsync(config, body, cancellationToken).ConfigureAwait(false);
        return result.Status;
    }

    public async Task<DiscordSendResult> SendTrackedAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);
        var executeUrl = WithWait(webhookUrl);
        var payload = CreatePayload(body, includeUsername: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, executeUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");

        using var json = JsonDocument.Parse(responseBody);
        if (!json.RootElement.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("Discord sent the notification but did not return a message ID.");

        var messageId = idElement.GetString();
        if (string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException("Discord returned an empty message ID.");

        return new DiscordSendResult("Discord notification sent", messageId);
    }

    public async Task<string> EditAsync(
        Configuration config,
        string messageId,
        string body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException("Discord message ID is missing.");

        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);
        var editUrl = BuildMessageUrl(webhookUrl, messageId);
        var payload = CreatePayload(body, includeUsername: false);

        using var request = new HttpRequestMessage(HttpMethod.Patch, editUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord edit returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");
        }

        return "Discord notification updated";
    }

    public async Task<string> DeleteAsync(Configuration config, string messageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return "Discord notification already removed";

        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);
        var deleteUrl = BuildMessageUrl(webhookUrl, messageId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return "Discord notification already removed";

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord delete returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");
        }

        return "Discord notification removed";
    }

    private static object CreatePayload(string body, bool includeUsername)
    {
        if (includeUsername)
        {
            return new
            {
                username = "PartyPing",
                content = body,
                allowed_mentions = new
                {
                    parse = Array.Empty<string>(),
                },
            };
        }

        return new
        {
            content = body,
            allowed_mentions = new
            {
                parse = Array.Empty<string>(),
            },
        };
    }

    private static Uri WithWait(Uri webhookUrl)
    {
        var builder = new UriBuilder(webhookUrl);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(query) ? "wait=true" : query + "&wait=true";
        return builder.Uri;
    }

    private static Uri BuildMessageUrl(Uri webhookUrl, string messageId)
    {
        var builder = new UriBuilder(webhookUrl)
        {
            Path = webhookUrl.AbsolutePath.TrimEnd('/') + "/messages/" + Uri.EscapeDataString(messageId.Trim()),
        };

        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("wait=", StringComparison.OrdinalIgnoreCase));
        builder.Query = string.Join('&', queryParts);
        return builder.Uri;
    }

    private static Uri ValidateWebhookUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Discord webhook URL is missing.");

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Discord webhook URL is invalid.");

        var host = uri.Host.ToLowerInvariant();
        var validHost = host == "discord.com" || host.EndsWith(".discord.com", StringComparison.Ordinal) ||
                        host == "discordapp.com" || host.EndsWith(".discordapp.com", StringComparison.Ordinal);

        if (!validHost || !uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a Discord incoming webhook URL.");

        return uri;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "...";

    public void Dispose() => http.Dispose();
}
