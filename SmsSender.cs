namespace PartyPing;

internal sealed class SmsSender : IDisposable
{
    private readonly DiscordNotifier discord = new();

    public async Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        var result = await SendTrackedAsync(config, body, cancellationToken).ConfigureAwait(false);
        return result.Status;
    }

    public async Task<DiscordSendResult> SendTrackedAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        EnsureLegacyDiscordUrl(config);
        var message = PrepareMessage(body);
        var result = await discord.SendTrackedAsync(config, message, cancellationToken).ConfigureAwait(false);
        DiscordMessageStore.Add(config, result.MessageId);
        return result;
    }

    public async Task<string> EditAsync(
        Configuration config,
        string messageId,
        string body,
        CancellationToken cancellationToken,
        string? transportHint = null)
    {
        EnsureLegacyDiscordUrl(config);

        // Once bot transport is enabled, a missing legacy webhook must not break PF
        // polling just because an older persisted alert was webhook-owned. The bot
        // migration path will replace that orphaned alert with a bot-owned card.
        if (IsUnavailableLegacyWebhook(config, transportHint))
            return "Legacy Discord webhook unavailable";

        var message = PrepareMessage(body);
        return await discord.EditAsync(config, messageId, message, cancellationToken, transportHint).ConfigureAwait(false);
    }

    public async Task<string> DeleteAsync(
        Configuration config,
        string messageId,
        CancellationToken cancellationToken,
        string? transportHint = null)
    {
        EnsureLegacyDiscordUrl(config);

        if (IsUnavailableLegacyWebhook(config, transportHint))
        {
            DiscordMessageStore.Remove(config, messageId);
            return "Legacy Discord webhook unavailable; local tracking removed";
        }

        var result = await discord.DeleteAsync(config, messageId, cancellationToken, transportHint).ConfigureAwait(false);
        DiscordMessageStore.Remove(config, messageId);
        return result;
    }

    private static string PrepareMessage(string body) =>
        body.Replace("SMS alerts", "Discord notifications", StringComparison.OrdinalIgnoreCase).TrimEnd();

    private static bool IsUnavailableLegacyWebhook(Configuration config, string? transportHint) =>
        string.Equals(transportHint, "webhook", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(config.DiscordWebhookUrl) &&
        string.IsNullOrWhiteSpace(config.TwilioAccountSid);

    private static void EnsureLegacyDiscordUrl(Configuration config)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl) &&
            !string.IsNullOrWhiteSpace(config.TwilioAccountSid))
        {
            config.DiscordWebhookUrl = config.TwilioAccountSid;
        }
    }

    public void Dispose() => discord.Dispose();
}
