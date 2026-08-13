using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PartyPing.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Configuration;

    public ConfigWindow(Plugin plugin) : base("PartyPing###PartyPingConfig")
    {
        this.plugin = plugin;
        Size = new Vector2(680, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("Enable Discord alerts", ref enabled))
        {
            Config.Enabled = enabled;
            Config.Save();
        }

        DrawSectionHeader("Match rules");

        var dutyId = (int)Config.DutyId;
        if (ImGui.InputInt("Duty ID (0 = any)", ref dutyId))
        {
            Config.DutyId = (ushort)Math.Clamp(dutyId, 0, ushort.MaxValue);
            Config.Save();
        }

        EditString("Duty name contains", Config.DutyNameContains, v => Config.DutyNameContains = v, 128);
        ImGui.TextDisabled("Duty name is also used by XIVPF background monitoring.");

        EditString("Include keywords", Config.IncludeKeywords, v => Config.IncludeKeywords = v, 512);
        ImGui.TextDisabled("Separate keywords with commas. Example: p3, bh, enrage");
        EditString("Exclude keywords", Config.ExcludeKeywords, v => Config.ExcludeKeywords = v, 512);

        var all = Config.RequireAllIncludeKeywords;
        if (ImGui.Checkbox("Require ALL include keywords", ref all))
        {
            Config.RequireAllIncludeKeywords = all;
            Config.Save();
        }

        var role = Config.RequiredRole;
        if (ImGui.BeginCombo("Required open role", role.DisplayName()))
        {
            foreach (var candidate in Enum.GetValues<RoleFilter>())
            {
                var selected = candidate == role;
                if (ImGui.Selectable(candidate.DisplayName(), selected))
                {
                    Config.RequiredRole = candidate;
                    Config.Save();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        ImGui.TextDisabled("Structured role filtering is exact for listings received in-game.");

        var slots = Config.MinimumOpenSlots;
        if (ImGui.InputInt("Minimum open slots", ref slots))
        {
            Config.MinimumOpenSlots = Math.Clamp(slots, 0, 24);
            Config.Save();
        }

        var cooldown = Config.PerListingCooldownMinutes;
        if (ImGui.InputInt("Same-listing cooldown (minutes)", ref cooldown))
        {
            Config.PerListingCooldownMinutes = Math.Clamp(cooldown, 1, 1440);
            Config.Save();
        }

        DrawSectionHeader("XIVPF background monitoring");

        var xivPfEnabled = Config.XivPfPollingEnabled;
        if (ImGui.Checkbox("Monitor xivpf.com automatically", ref xivPfEnabled))
        {
            Config.XivPfPollingEnabled = xivPfEnabled;
            Config.Save();
        }

        var pollSeconds = Config.XivPfPollSeconds;
        if (ImGui.InputInt("XIVPF poll interval (seconds)", ref pollSeconds))
        {
            Config.XivPfPollSeconds = Math.Clamp(pollSeconds, 60, 600);
            Config.Save();
        }

        ImGui.TextWrapped("This checks xivpf.com in the background, so you do not need to open or refresh Party Finder. FFXIV/Dalamud must still be running because PartyPing runs inside the game client.");
        ImGui.TextWrapped("XIVPF mode uses Duty name contains, keyword filters, and open-slot count. The public HTML feed does not expose Dalamud's structured recruiting-slot data reliably, so the selected role is shown as unverified for XIVPF-only matches.");
        ImGui.TextDisabled(plugin.XivPfStatus);

        DrawSectionHeader("Discord notifications");
        var discordUrl = string.IsNullOrWhiteSpace(Config.DiscordWebhookUrl)
            ? Config.TwilioAccountSid
            : Config.DiscordWebhookUrl;
        EditString("Discord URL", discordUrl, v =>
        {
            Config.DiscordWebhookUrl = v;
            Config.TwilioAccountSid = v;
        }, 512);
        ImGui.TextDisabled("Paste the Discord incoming webhook URL for the channel that should receive PartyPing alerts.");

        if (ImGui.Button("Send test Discord notification"))
            _ = plugin.SendTestAsync();

        ImGui.SameLine();
        ImGui.TextWrapped(plugin.LastStatus);

        ImGui.Spacing();
        DrawSectionHeader("Important");
        ImGui.TextWrapped("PartyPing can monitor both listings received by your game client and xivpf.com. XIVPF is crowdsourced, so listings can appear or disappear with some delay compared with the in-game Party Finder.");
    }

    private static void DrawSectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(text);
    }

    private void EditString(string label, string current, Action<string> setter, int maxLength)
    {
        var value = current;
        if (ImGui.InputText(label, ref value, maxLength))
        {
            setter(value);
            Config.Save();
        }
    }
}
