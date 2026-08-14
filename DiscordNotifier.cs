using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PartyPing;

internal sealed record DiscordSendResult(
    string Status,
    string MessageId,
    string? SeparatorMessageId = null,
    string Transport = "webhook");

internal sealed class DiscordNotifier : IDisposable
{
    private const string DiscordApiBase = "https://discord.com/api/v10";
    private const int PartyFinderEmbedColor = 0x5865F2;
    private const string OpenButtonPrefix = "partyping_open:";

    private readonly HttpClient http = new();

    private sealed record PartyFinderEmbedData(
        string Duty,
        string Party,
        string OpenSlots,
        string Role,
        string World,
        string Recruiter,
        string ListingId,
        string Description);

    internal static bool HasBotTransport(Configuration config) =>
        !string.IsNullOrWhiteSpace(config.DiscordBotToken) &&
        ulong.TryParse(config.DiscordChannelId?.Trim(), out _);

    public async Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        var result = await SendTrackedAsync(config, body, cancellationToken).ConfigureAwait(false);
        return result.Status;
    }

    public Task<DiscordSendResult> SendTrackedAsync(
        Configuration config,
        string body,
        CancellationToken cancellationToken) =>
        HasBotTransport(config)
            ? SendBotAsync(config, body, cancellationToken)
            : SendWebhookAsync(config, body, cancellationToken);

    public async Task<string> EditAsync(
        Configuration config,
        string messageId,
        string body,
        CancellationToken cancellationToken,
        string? transportHint = null)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException("Discord message ID is missing.");

        if (string.Equals(transportHint, "bot", StringComparison.OrdinalIgnoreCase))
        {
            var bot = await EditBotAsync(config, messageId, body, cancellationToken).ConfigureAwait(false);
            return bot.Found ? bot.Status : "Discord notification already removed";
        }

        if (string.Equals(transportHint, "webhook", StringComparison.OrdinalIgnoreCase))
            return await EditWebhookAsync(config, messageId, body, cancellationToken).ConfigureAwait(false);

        // Older PartyPing versions did not persist which Discord transport owned a
        // message. When bot mode is configured, try the bot channel first and fall
        // back to the old webhook on 404 so those messages remain manageable.
        if (HasBotTransport(config))
        {
            var bot = await EditBotAsync(config, messageId, body, cancellationToken).ConfigureAwait(false);
            if (bot.Found)
                return bot.Status;
        }

        if (HasWebhookTransport(config))
            return await EditWebhookAsync(config, messageId, body, cancellationToken).ConfigureAwait(false);

        return "Discord notification already removed";
    }

    public async Task<string> DeleteAsync(
        Configuration config,
        string messageId,
        CancellationToken cancellationToken,
        string? transportHint = null)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return "Discord notification already removed";

        if (string.Equals(transportHint, "bot", StringComparison.OrdinalIgnoreCase))
        {
            var bot = await DeleteBotAsync(config, messageId, cancellationToken).ConfigureAwait(false);
            return bot.Found ? bot.Status : "Discord notification already removed";
        }

        if (string.Equals(transportHint, "webhook", StringComparison.OrdinalIgnoreCase))
            return await DeleteWebhookAsync(config, messageId, cancellationToken).ConfigureAwait(false);

        if (HasBotTransport(config))
        {
            var bot = await DeleteBotAsync(config, messageId, cancellationToken).ConfigureAwait(false);
            if (bot.Found)
                return bot.Status;
        }

        if (HasWebhookTransport(config))
            return await DeleteWebhookAsync(config, messageId, cancellationToken).ConfigureAwait(false);

        return "Discord notification already removed";
    }

    private async Task<DiscordSendResult> SendBotAsync(
        Configuration config,
        string body,
        CancellationToken cancellationToken)
    {
        var url = BuildBotMessageUrl(config, null);
        var payload = CreatePayload(body, includeUsername: false, includeButton: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent(payload),
        };
        AddBotAuthorization(request, config);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord bot send returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");

        var messageId = ReadMessageId(responseBody);
        return new DiscordSendResult("Discord bot notification sent", messageId, null, "bot");
    }

    private async Task<(bool Found, string Status)> EditBotAsync(
        Configuration config,
        string messageId,
        string body,
        CancellationToken cancellationToken)
    {
        if (!HasBotTransport(config))
            return (false, "Discord bot is not configured");

        var url = BuildBotMessageUrl(config, messageId);
        var payload = CreatePayload(body, includeUsername: false, includeButton: true);
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent(payload),
        };
        AddBotAuthorization(request, config);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (false, "Discord notification already removed");

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord bot edit returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");
        }

        return (true, "Discord bot notification updated");
    }

    private async Task<(bool Found, string Status)> DeleteBotAsync(
        Configuration config,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (!HasBotTransport(config))
            return (false, "Discord bot is not configured");

        var url = BuildBotMessageUrl(config, messageId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        AddBotAuthorization(request, config);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (false, "Discord notification already removed");

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord bot delete returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");
        }

        return (true, "Discord bot notification removed");
    }

    private async Task<DiscordSendResult> SendWebhookAsync(
        Configuration config,
        string body,
        CancellationToken cancellationToken)
    {
        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);
        var executeUrl = WithWait(webhookUrl);
        var payload = CreatePayload(body, includeUsername: true, includeButton: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, executeUrl)
        {
            Content = JsonContent(payload),
        };

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");

        return new DiscordSendResult("Discord notification sent", ReadMessageId(responseBody), null, "webhook");
    }

    private async Task<string> EditWebhookAsync(
        Configuration config,
        string messageId,
        string body,
        CancellationToken cancellationToken)
    {
        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);
        var editUrl = BuildWebhookMessageUrl(webhookUrl, messageId);
        var payload = CreatePayload(body, includeUsername: false, includeButton: false);

        using var request = new HttpRequestMessage(HttpMethod.Patch, editUrl)
        {
            Content = JsonContent(payload),
        };

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return "Discord notification already removed";

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord edit returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");
        }

        return "Discord notification updated";
    }

    private async Task<string> DeleteWebhookAsync(
        Configuration config,
        string messageId,
        CancellationToken cancellationToken)
    {
        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);
        var deleteUrl = BuildWebhookMessageUrl(webhookUrl, messageId);

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

    private static object CreatePayload(string body, bool includeUsername, bool includeButton)
    {
        var allowedMentions = new
        {
            parse = Array.Empty<string>(),
        };

        if (TryParsePartyFinderMessage(body, out var pf))
        {
            var embed = new
            {
                title = pf.Duty,
                description = pf.Description,
                color = PartyFinderEmbedColor,
                fields = new[]
                {
                    new { name = "Party", value = pf.Party, inline = true },
                    new { name = "Open slots", value = pf.OpenSlots, inline = true },
                    new { name = "Role", value = pf.Role, inline = true },
                    new { name = "World", value = pf.World, inline = true },
                    new { name = "Recruiter", value = pf.Recruiter, inline = true },
                },
                footer = new
                {
                    text = "Local FFXIV Party Finder",
                },
            };

            var components = includeButton && ulong.TryParse(pf.ListingId, out var listingId) && listingId != 0
                ? new object[]
                {
                    new
                    {
                        type = 1,
                        components = new[]
                        {
                            new
                            {
                                type = 2,
                                style = 1,
                                label = "Open in FFXIV",
                                custom_id = OpenButtonPrefix + pf.ListingId,
                            },
                        },
                    },
                }
                : Array.Empty<object>();

            if (includeUsername)
            {
                return new
                {
                    username = "PartyPing",
                    content = string.Empty,
                    embeds = new[] { embed },
                    components,
                    allowed_mentions = allowedMentions,
                };
            }

            return new
            {
                content = string.Empty,
                embeds = new[] { embed },
                components,
                allowed_mentions = allowedMentions,
            };
        }

        if (includeUsername)
        {
            return new
            {
                username = "PartyPing",
                content = body,
                embeds = Array.Empty<object>(),
                components = Array.Empty<object>(),
                allowed_mentions = allowedMentions,
            };
        }

        return new
        {
            content = body,
            embeds = Array.Empty<object>(),
            components = Array.Empty<object>(),
            allowed_mentions = allowedMentions,
        };
    }

    private static bool TryParsePartyFinderMessage(string body, out PartyFinderEmbedData data)
    {
        data = null!;

        if (!body.Contains("**Source:** Local FFXIV Party Finder", StringComparison.Ordinal))
            return false;

        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var dutyLine = lines.FirstOrDefault(x => x.StartsWith("## ", StringComparison.Ordinal));
        if (dutyLine is null)
            return false;

        var duty = dutyLine[3..].Trim();
        var party = FindField(lines, "**Party:**");
        var openSlots = FindField(lines, "**Open slots:**");
        var role = FindField(lines, "**Role filter:**");
        var world = FindField(lines, "**World:**");
        var recruiter = FindField(lines, "**Recruiter:**");
        var listingId = FindField(lines, "**Listing ID:**");

        var descriptionIndex = Array.FindIndex(lines, x => x.Equals("### Party Finder Description", StringComparison.Ordinal));
        var description = descriptionIndex >= 0
            ? string.Join('\n', lines[(descriptionIndex + 1)..]
                .Select(x => x.StartsWith("> ", StringComparison.Ordinal) ? x[2..] : x)
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            : "No description";

        if (string.IsNullOrWhiteSpace(description))
            description = "No description";

        data = new PartyFinderEmbedData(
            Trim(duty, 256),
            Trim(party, 1024),
            Trim(openSlots, 1024),
            Trim(role, 1024),
            Trim(world, 1024),
            Trim(recruiter, 1024),
            listingId,
            Trim(description, 4096));
        return true;
    }

    private static string FindField(string[] lines, string prefix)
    {
        var line = lines.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
            return "Unknown";

        var value = line[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string ReadMessageId(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        if (!json.RootElement.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("Discord sent the notification but did not return a message ID.");

        var messageId = idElement.GetString();
        return string.IsNullOrWhiteSpace(messageId)
            ? throw new InvalidOperationException("Discord returned an empty message ID.")
            : messageId;
    }

    private static string BuildBotMessageUrl(Configuration config, string? messageId)
    {
        if (!ulong.TryParse(config.DiscordChannelId?.Trim(), out var channelId) || channelId == 0)
            throw new InvalidOperationException("Discord bot channel ID is invalid.");

        var baseUrl = $"{DiscordApiBase}/channels/{channelId}/messages";
        return string.IsNullOrWhiteSpace(messageId)
            ? baseUrl
            : baseUrl + "/" + Uri.EscapeDataString(messageId.Trim());
    }

    private static void AddBotAuthorization(HttpRequestMessage request, Configuration config)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordBotToken))
            throw new InvalidOperationException("Discord bot token is missing.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", config.DiscordBotToken.Trim());
    }

    private static bool HasWebhookTransport(Configuration config) =>
        !string.IsNullOrWhiteSpace(config.DiscordWebhookUrl);

    private static Uri WithWait(Uri webhookUrl)
    {
        var builder = new UriBuilder(webhookUrl);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(query) ? "wait=true" : query + "&wait=true";
        return builder.Uri;
    }

    private static Uri BuildWebhookMessageUrl(Uri webhookUrl, string messageId)
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
