using Dalamud.Configuration;

namespace PartyPing;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;

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
    public string DiscordWebhookUrl { get; set; } = string.Empty;
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
}
