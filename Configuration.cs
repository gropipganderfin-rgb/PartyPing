using Dalamud.Configuration;

namespace PartyPing;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;

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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
