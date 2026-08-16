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
        Size = new Vector2(640, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawTopBar();
        ImGui.Separator();
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("PartyPingTabs"))
            return;

        if (ImGui.BeginTabItem("Match"))
        {
            DrawMatchTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Status"))
        {
            DrawStatusTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Discord"))
        {
            DrawDiscordTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Maintenance"))
        {
            DrawMaintenanceTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawTopBar()
    {
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            Config.Enabled = enabled;
            Config.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(enabled ? "PF searching + Discord alerts active" : "PF searching paused");

        var buttonWidth = 125f;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + 12f, ImGui.GetContentRegionAvail().X - buttonWidth));

        if (plugin.LocalPfCheckInProgress)
            ImGui.BeginDisabled();

        if (ImGui.Button(plugin.LocalPfCheckInProgress ? "Scanning..." : "Scan PF now", new Vector2(buttonWidth, 0)))
            _ = plugin.CheckLocalPfNowAsync();

        if (plugin.LocalPfCheckInProgress)
            ImGui.EndDisabled();
    }

    private void DrawMatchTab()
    {
        ImGui.TextUnformatted("Party Finder filters");
        ImGui.TextDisabled("Only matching High-End Duty listings become Discord cards.");
        ImGui.Spacing();

        EditStringField("##DutyName", "Duty", Config.DutyNameContains, v => Config.DutyNameContains = v, 128);
        ImGui.TextDisabled("Example: Dancing Mad");
        ImGui.Spacing();

        ImGui.TextUnformatted("Required open role");
        var role = Config.RequiredRole;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##RequiredRole", role.DisplayName()))
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

        ImGui.Spacing();
        ImGui.TextUnformatted("Minimum open slots");
        var slots = Config.MinimumOpenSlots;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("##MinimumOpenSlots", ref slots))
        {
            Config.MinimumOpenSlots = Math.Clamp(slots, 0, 24);
            Config.Save();
        }

        ImGui.Spacing();
        EditStringField("##IncludeKeywords", "Include keywords", Config.IncludeKeywords, v => Config.IncludeKeywords = v, 512);
        ImGui.TextDisabled("Comma-separated. Example: p3, bh, enrage");

        var all = Config.RequireAllIncludeKeywords;
        if (ImGui.Checkbox("Require every include keyword", ref all))
        {
            Config.RequireAllIncludeKeywords = all;
            Config.Save();
        }

        ImGui.Spacing();
        EditStringField("##ExcludeKeywords", "Exclude keywords", Config.ExcludeKeywords, v => Config.ExcludeKeywords = v, 512);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted($"Ignored listings: {plugin.IgnoredPfListingCount}");
        ImGui.TextDisabled("Ignore removes one PF card until that listing expires or is reposted.");
        if (plugin.IgnoredPfListingCount > 0 && ImGui.Button("Clear ignored listings"))
            plugin.ClearIgnoredPfListings();
    }

    private void DrawStatusTab()
    {
        ImGui.TextUnformatted("Party Finder scanner");
        ImGui.TextDisabled("Automatic scan interval: random 60-90 seconds. Up to 10 PF pages per scan.");
        ImGui.Spacing();
        ImGui.TextWrapped(plugin.LocalPfStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Current party");
        ImGui.TextWrapped(plugin.PartyFillStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Discord connection");
        ImGui.TextWrapped(plugin.DiscordBotStatus);
    }

    private void DrawDiscordTab()
    {
        ImGui.TextUnformatted("Interactive Discord cards");
        ImGui.TextDisabled("PF cards use Open, Join, and Ignore. Search ON/OFF are sent once in the startup control message.");
        ImGui.Spacing();

        EditSecretField("##BotToken", "Bot token", Config.DiscordBotToken, v => Config.DiscordBotToken = v, 256);
        EditStringField("##ChannelId", "Channel ID", Config.DiscordChannelId, v => Config.DiscordChannelId = v, 32);
        EditStringField("##UserId", "Your Discord user ID", Config.DiscordUserId, v => Config.DiscordUserId = v, 32);
        ImGui.TextDisabled("Only this user can control your local FFXIV client from the buttons.");

        ImGui.Spacing();
        ImGui.TextWrapped(plugin.DiscordBotStatus);
        ImGui.Spacing();

        if (ImGui.Button("Send test notification"))
            _ = plugin.SendTestAsync();
        ImGui.SameLine();
        ImGui.TextDisabled(plugin.LastStatus);

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Legacy webhook fallback"))
        {
            var discordUrl = string.IsNullOrWhiteSpace(Config.DiscordWebhookUrl)
                ? Config.TwilioAccountSid
                : Config.DiscordWebhookUrl;
            EditStringField("##LegacyWebhook", "Webhook URL", discordUrl, v =>
            {
                Config.DiscordWebhookUrl = v;
                Config.TwilioAccountSid = v;
            }, 512);
            ImGui.TextDisabled("Optional after bot setup; retained for migration/removal of old webhook cards.");
        }
    }

    private void DrawMaintenanceTab()
    {
        ImGui.TextUnformatted("Discord message cleanup");
        ImGui.TextDisabled("Deletes PartyPing's tracked Discord cards. Matching listings return on the next scan.");
        ImGui.Spacing();

        if (clearInProgress)
            ImGui.BeginDisabled();

        if (ImGui.Button(clearInProgress ? "Clearing..." : "Clear PartyPing Discord messages"))
            _ = ClearDiscordMessagesAsync();

        if (clearInProgress)
            ImGui.EndDisabled();

        if (!string.IsNullOrWhiteSpace(clearStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(clearStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Scanner behavior");
        ImGui.TextWrapped("PartyPing reads FFXIV's High-End Duty Party Finder directly. When a result page is full, it walks later pages with FFXIV's own PF pager. Scans pause while inside a duty, zoning, or watching a cutscene.");
        ImGui.Spacing();
        ImGui.TextWrapped("Ignored cards stay suppressed only for that exact Party Finder listing ID. When the listing disappears, the ignore is automatically cleaned up so a future repost can alert normally.");
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
                plugin.ResetPartyTrackerMessageAfterManualClear();
                clearStatus = $"Cleared {result.Removed} PartyPing Discord message(s).";
            }
            else
            {
                clearStatus = $"Cleared {result.Removed}; {result.Failed} could not be removed. Press Clear again to retry.";
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

    private void EditStringField(string id, string label, string current, Action<string> setter, int maxLength)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        var value = current;
        if (ImGui.InputText(id, ref value, maxLength))
        {
            setter(value);
            Config.Save();
        }
    }

    private void EditSecretField(string id, string label, string current, Action<string> setter, int maxLength)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        var value = current;
        if (ImGui.InputText(id, ref value, maxLength, ImGuiInputTextFlags.Password))
        {
            setter(value);
            Config.Save();
        }
    }
}
