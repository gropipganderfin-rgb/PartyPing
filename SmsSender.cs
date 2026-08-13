namespace PartyPing;

internal sealed class SmsSender : IDisposable
{
    private readonly DiscordNotifier discord = new();

    public Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        EnsureDiscordUrl(config);
        var message = body.Replace("SMS alerts", "Discord notifications", StringComparison.OrdinalIgnoreCase);
        return discord.SendAsync(config, message, cancellationToken);
    }

    public Task<DiscordSendResult> SendTrackedAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        EnsureDiscordUrl(config);
        var message = body.Replace("SMS alerts", "Discord notifications", StringComparison.OrdinalIgnoreCase);
        return discord.SendTrackedAsync(config, message, cancellationToken);
    }

    public Task<string> DeleteAsync(Configuration config, string messageId, CancellationToken cancellationToken)
    {
        EnsureDiscordUrl(config);
        return discord.DeleteAsync(config, messageId, cancellationToken);
    }

    private static void EnsureDiscordUrl(Configuration config)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
            config.DiscordWebhookUrl = config.TwilioAccountSid;
    }

    public void Dispose() => discord.Dispose();
}
