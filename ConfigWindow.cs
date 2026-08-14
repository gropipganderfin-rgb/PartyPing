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

        DrawSectionHeader("Party Finder match rules");

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
        ImGui.TextDisabled("Uses FFXIV's accepted job data for open slots. Alerts are removed when this role is no longer open.");

        var slots = Config.MinimumOpenSlots;
        if (ImGui.InputInt("Minimum open slots", ref slots))
        {
            Config.MinimumOpenSlots = Math.Clamp(slots, 0, 24);
            Config.Save();
        }

        DrawSectionHeader("Local Party Finder polling");

        if (plugin.LocalPfCheckInProgress)
            ImGui.BeginDisabled();

        if (ImGui.Button("Check local PF now"))
            _ = plugin.CheckLocalPfNowAsync();

        if (plugin.LocalPfCheckInProgress)
            ImGui.EndDisabled();

        ImGui.TextWrapped("PartyPing automatically polls FFXIV's High-End Duty Party Finder at a new random whole-second interval from 30 through 60 seconds after each cycle. The Party Finder window does not need to be open.");
        ImGui.TextWrapped("The button above performs an immediate check. Matching posts are created or edited with current local data; posts are removed when the listing closes, fills, stops matching your description filters, falls below the minimum open slots, or no longer has your selected role open.");
        ImGui.TextDisabled(plugin.LocalPfStatus);

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

        ImGui.TextDisabled("Deletes tracked PartyPing posts, then matching local PF listings repopulate on the next poll.");
        if (!string.IsNullOrWhiteSpace(clearStatus))
            ImGui.TextWrapped(clearStatus);

        ImGui.Spacing();
        DrawSectionHeader("Important");
        ImGui.TextWrapped("PartyPing now uses only data received directly from the FFXIV Party Finder. Automatic polling currently requests the High-End Duty category only and pauses while you are inside a duty, zoning, or in a cutscene.");
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

            if (result.Failed == 0)
            {
                plugin.ResetPfAlertStateAfterManualClear();
                clearStatus = $"Cleared {result.Removed} PartyPing Discord message(s). Matching local PF listings will repopulate on the next poll.";
            }
            else
            {
                clearStatus = $"Cleared {result.Removed} message(s); {result.Failed} could not be removed. Repopulation is paused to avoid duplicate posts; press Clear again to retry.";
            }
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
