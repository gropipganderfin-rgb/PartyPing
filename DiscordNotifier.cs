using System.Text;
using System.Text.Json;

namespace PartyPing;

internal sealed class DiscordNotifier : IDisposable
{
    private readonly HttpClient http = new();

    public async Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        var webhookUrl = ValidateWebhookUrl(config.DiscordWebhookUrl);

        var payload = new
        {
            username = "PartyPing",
            content = body,
            allowed_mentions = new
            {
                parse = Array.Empty<string>(),
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord returned {(int)response.StatusCode}: {Trim(responseBody, 220)}");

        return "Discord notification sent";
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
