namespace PartyPing;

internal static class DiscordClearer
{
    public static async Task<DiscordClearResult> ClearAsync(
        Configuration config,
        CancellationToken cancellationToken)
    {
        var messageIds = DiscordMessageStore.Snapshot(config);
        if (messageIds.Length == 0)
            return new DiscordClearResult(0, 0);

        var removed = 0;
        var failed = 0;
        using var sender = new SmsSender();

        foreach (var messageId in messageIds)
        {
            try
            {
                await sender.DeleteAsync(config, messageId, cancellationToken).ConfigureAwait(false);
                removed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failed++;
            }
        }

        return new DiscordClearResult(removed, failed);
    }
}
