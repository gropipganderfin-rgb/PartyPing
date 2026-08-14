using System.Net;
using System.Text;
using System.Text.Json;

namespace PartyPing;

internal sealed record DiscordSendResult(string Status, string MessageId, string? SeparatorMessageId = null);

internal sealed class DiscordNotifier : IDisposable
{
    private const int PartyFinderEmbedColor = 0x5865F2;

    private readonly HttpClient http = new();

    private sealed record PartyFinderEmbedData(
        string Duty,
        string Party,
        string OpenSlots,
        string Role,
        string World,
        string Recruiter,
        string OpenInFfxiv,
        string Description);

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
                    new { name = "Open in FFXIV", value = pf.OpenInFfxiv, inline = false },
                },
                footer = new
                {
                    text = "Local FFXIV Party Finder",
                },
            };

            if (includeUsername)
            {
                return new
                {
                    username = "PartyPing",
                    content = string.Empty,
                    embeds = new[] { embed },
                    allowed_mentions = allowedMentions,
                };
            }

            return new
            {
                content = string.Empty,
                embeds = new[] { embed },
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
                allowed_mentions = allowedMentions,
            };
        }

        return new
        {
            content = body,
            embeds = Array.Empty<object>(),
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
        var openInFfxiv = FindField(lines, "**Open in FFXIV:**");

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
            Trim(openInFfxiv, 1024),
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
