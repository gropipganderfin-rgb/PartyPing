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
        EnsureDiscordUrl(config);
        var message = body.Replace("SMS alerts", "Discord notifications", StringComparison.OrdinalIgnoreCase);
        var result = await discord.SendTrackedAsync(config, message, cancellationToken).ConfigureAwait(false);
        DiscordMessageStore.Add(config, result.MessageId);
        return result;
    }

    public async Task<string> EditAsync(
        Configuration config,
        string messageId,
        string body,
        CancellationToken cancellationToken)
    {
        EnsureDiscordUrl(config);
        var message = body.Replace("SMS alerts", "Discord notifications", StringComparison.OrdinalIgnoreCase);
        return await discord.EditAsync(config, messageId, message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DeleteAsync(Configuration config, string messageId, CancellationToken cancellationToken)
    {
        EnsureDiscordUrl(config);
        var result = await discord.DeleteAsync(config, messageId, cancellationToken).ConfigureAwait(false);
        DiscordMessageStore.Remove(config, messageId);
        return result;
    }

    private static void EnsureDiscordUrl(Configuration config)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
            config.DiscordWebhookUrl = config.TwilioAccountSid;
    }

    public void Dispose() => discord.Dispose();
}
