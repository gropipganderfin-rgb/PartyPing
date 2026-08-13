namespace PartyPing;

internal static class DiscordMessageStore
{
    private static readonly object Sync = new();

    public static void Add(Configuration config, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return;

        lock (Sync)
        {
            config.TrackedDiscordMessageIds ??= [];
            if (!config.TrackedDiscordMessageIds.Contains(messageId, StringComparer.Ordinal))
            {
                config.TrackedDiscordMessageIds.Add(messageId);
                config.Save();
            }
        }
    }

    public static void Remove(Configuration config, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return;

        lock (Sync)
        {
            config.TrackedDiscordMessageIds ??= [];
            if (config.TrackedDiscordMessageIds.RemoveAll(id => string.Equals(id, messageId, StringComparison.Ordinal)) > 0)
                config.Save();
        }
    }

    public static string[] Snapshot(Configuration config)
    {
        lock (Sync)
        {
            config.TrackedDiscordMessageIds ??= [];
            return config.TrackedDiscordMessageIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }
}
