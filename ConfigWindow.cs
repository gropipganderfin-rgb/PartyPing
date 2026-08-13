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
        Size = new Vector2(620, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("Enable SMS alerts", ref enabled))
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
        EditString("Include keywords", Config.IncludeKeywords, v => Config.IncludeKeywords = v, 512);
        ImGui.TextDisabled("Separate keywords with commas. Example: p5, sigma, omega");
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
        ImGui.TextDisabled("Alert only if a recruiting PF slot accepts at least one job in this role.");

        var slots = Config.MinimumOpenSlots;
        if (ImGui.InputInt("Minimum open slots", ref slots))
        {
            Config.MinimumOpenSlots = Math.Clamp(slots, 0, 8);
            Config.Save();
        }

        var cooldown = Config.PerListingCooldownMinutes;
        if (ImGui.InputInt("Same-listing cooldown (minutes)", ref cooldown))
        {
            Config.PerListingCooldownMinutes = Math.Clamp(cooldown, 1, 1440);
            Config.Save();
        }

        DrawSectionHeader("Twilio SMS");
        EditString("Account SID", Config.TwilioAccountSid, v => Config.TwilioAccountSid = v, 128);
        EditSecret("Auth Token", Config.TwilioAuthToken, v => Config.TwilioAuthToken = v, 256);
        EditString("Twilio From (+1...)", Config.TwilioFromNumber, v => Config.TwilioFromNumber = v, 32);
        EditString("Text me at (+1...)", Config.ToNumber, v => Config.ToNumber = v, 32);

        if (ImGui.Button("Send test SMS"))
            _ = plugin.SendTestAsync();

        ImGui.SameLine();
        ImGui.TextWrapped(plugin.LastStatus);

        ImGui.Spacing();
        DrawSectionHeader("Important");
        ImGui.TextWrapped("PartyPing reacts to Party Finder listings that your game client receives. It does not automatically refresh Party Finder or scrape xivpf.com. Keep FFXIV running and ensure PF listings are being fetched/refreshed for alerts to fire.");
        ImGui.TextWrapped("Your Twilio token is stored in Dalamud's plugin configuration. Do not share that configuration file.");
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

    private void EditSecret(string label, string current, Action<string> setter, int maxLength)
    {
        var value = current;
        if (ImGui.InputText(label, ref value, maxLength, ImGuiInputTextFlags.Password))
        {
            setter(value);
            Config.Save();
        }
    }
}
