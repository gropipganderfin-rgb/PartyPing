namespace PartyPing;

internal sealed class SmsSender : IDisposable
{
    private readonly DiscordNotifier discord = new();

    public Task<string> SendAsync(Configuration config, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl))
            config.DiscordWebhookUrl = config.TwilioAccountSid;

        var message = body.Replace("SMS alerts", "Discord notifications", StringComparison.OrdinalIgnoreCase);
        return discord.SendAsync(config, message, cancellationToken);
    }

    public void Dispose() => discord.Dispose();
}
