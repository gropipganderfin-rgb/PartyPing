using Dalamud.Configuration;

namespace PartyPing;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 9;

    public bool Enabled { get; set; } = false;
    public ushort DutyId { get; set; } = 0;
    public string DutyNameContains { get; set; } = string.Empty;
    public string IncludeKeywords { get; set; } = string.Empty;
    public string ExcludeKeywords { get; set; } = string.Empty;
    public bool RequireAllIncludeKeywords { get; set; } = false;
    public int MinimumOpenSlots { get; set; } = 1;
    public RoleFilter RequiredRole { get; set; } = RoleFilter.AnyRole;

    public bool NotifyWhenPartyFull { get; set; } = true;

    public string TwilioAccountSid { get; set; } = string.Empty;
    public string TwilioAuthToken { get; set; } = string.Empty;
    public string TwilioFromNumber { get; set; } = string.Empty;
    public string ToNumber { get; set; } = string.Empty;

    // Legacy/fallback incoming-webhook transport. Interactive PF buttons require
    // the bot transport below, but this is retained so existing installations can
    // migrate without losing their old tracked webhook messages.
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    // Discord bot transport. The bot connects to Discord's Gateway from inside
    // PartyPing, so button clicks do not need a browser or a public HTTP endpoint.
    public string DiscordBotToken { get; set; } = string.Empty;
    public string DiscordChannelId { get; set; } = string.Empty;
    public string DiscordUserId { get; set; } = string.Empty;

    public List<string> TrackedDiscordMessageIds { get; set; } = [];

    // Persist the PF listing -> Discord message relationship so live edits/deletes
    // continue to work after a plugin reload, game restart, or PartyPing update.
    public Dictionary<string, PersistedPfAlert> ActivePfAlerts { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public sealed class PersistedPfAlert
{
    public string MessageId { get; set; } = string.Empty;
    public string? SeparatorMessageId { get; set; }
    public string LastContent { get; set; } = string.Empty;
    public int MissedPolls { get; set; }
    public long ExpiresAtUnixSeconds { get; set; }

    // Empty means the alert predates transport tracking. Those messages are
    // migrated to the bot transport automatically on the next matching PF poll.
    public string Transport { get; set; } = string.Empty;
}
