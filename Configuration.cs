using Dalamud.Configuration;

namespace PartyPing;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    public bool Enabled { get; set; } = false;
    public ushort DutyId { get; set; } = 0;
    public string DutyNameContains { get; set; } = string.Empty;
    public string IncludeKeywords { get; set; } = string.Empty;
    public string ExcludeKeywords { get; set; } = string.Empty;
    public bool RequireAllIncludeKeywords { get; set; } = false;
    public int MinimumOpenSlots { get; set; } = 1;
    public RoleFilter RequiredRole { get; set; } = RoleFilter.AnyRole;
    public int PerListingCooldownMinutes { get; set; } = 180;

    public bool XivPfPollingEnabled { get; set; } = true;
    public int XivPfPollSeconds { get; set; } = 90;

    public string TwilioAccountSid { get; set; } = string.Empty;
    public string TwilioAuthToken { get; set; } = string.Empty;
    public string TwilioFromNumber { get; set; } = string.Empty;
    public string ToNumber { get; set; } = string.Empty;
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
