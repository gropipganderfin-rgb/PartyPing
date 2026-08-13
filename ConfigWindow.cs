using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PartyPing.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Configuration;
    private string clearStatus = string.Empty;
    private bool clearInProgress;

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

        DrawSectionHeader("XIVPF match rules");

        EditString("Duty name contains", Config.DutyNameContains, v => Config.DutyNameContains = v, 128);
        ImGui.TextDisabled("Example: Dancing Mad");

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
        ImGui.TextDisabled("PartyPing reads XIVPF's accepted job codes for each open slot. Alerts are removed when this role is no longer open.");

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

        ImGui.TextWrapped("Party Finder alerts come from XIVPF.com only. PartyPing does not listen for local in-game Party Finder listing refreshes.");
        ImGui.TextWrapped("Each XIVPF alert is tracked by its Discord message ID. PartyPing removes that specific Discord post when the listing fills, disappears, or your selected role is no longer open.");
        ImGui.TextWrapped("FFXIV/Dalamud must still be running because PartyPing runs inside the game client.");
        ImGui.TextDisabled(plugin.XivPfStatus);

        DrawSectionHeader("My party tracker");

        var notifyWhenFull = Config.NotifyWhenPartyFull;
        if (ImGui.Checkbox("Notify me when my joined party reaches 8/8", ref notifyWhenFull))
        {
            Config.NotifyWhenPartyFull = notifyWhenFull;
            Config.Save();
        }
        ImGui.TextDisabled(plugin.PartyFillStatus);

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
        if (clearInProgress)
            ImGui.BeginDisabled();

        if (ImGui.Button("Clear PartyPing Discord messages"))
            _ = ClearDiscordMessagesAsync();

        if (clearInProgress)
            ImGui.EndDisabled();

        ImGui.TextDisabled("Deletes messages created and tracked by PartyPing. It does not touch other users' or bots' messages.");
        if (!string.IsNullOrWhiteSpace(clearStatus))
            ImGui.TextWrapped(clearStatus);

        ImGui.Spacing();
        DrawSectionHeader("Important");
        ImGui.TextWrapped("XIVPF is crowdsourced, so listings can appear, update, fill, or disappear with some delay compared with the in-game Party Finder.");
    }

    private async Task ClearDiscordMessagesAsync()
    {
        if (clearInProgress)
            return;

        clearInProgress = true;
        clearStatus = "Clearing PartyPing Discord messages...";

        try
        {
            var result = await DiscordClearer.ClearAsync(Config, CancellationToken.None).ConfigureAwait(false);
            clearStatus = result.Failed == 0
                ? $"Cleared {result.Removed} PartyPing Discord message(s)."
                : $"Cleared {result.Removed} message(s); {result.Failed} could not be removed.";
        }
        catch (Exception ex)
        {
            clearStatus = "Clear failed: " + ex.Message;
        }
        finally
        {
            clearInProgress = false;
        }
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
