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
        Size = new Vector2(700, 900);
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
        ImGui.TextDisabled("Uses FFXIV's accepted job data for open slots. Alerts are removed when this role is no longer open unless it is the party you are currently in.");

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

        ImGui.TextWrapped("PartyPing automatically polls FFXIV's High-End Duty Party Finder at a new random whole-second interval from 30 through 60 seconds after each cycle. If the first 50 results are saturated, it walks additional PF pages (up to 10) so listings beyond page 1 are included.");
        ImGui.TextWrapped("Matching posts stay visible and continue updating while you are in a party. Posts are removed when they stop qualifying, except the PF belonging to your current party is retained until you leave.");
        ImGui.TextWrapped("After each scan, the status below shows why received listings were rejected: duty, world, iLvl 999, open slots, role, excluded keyword, or missing include keyword.");
        ImGui.TextWrapped(plugin.LocalPfStatus);

        DrawSectionHeader("Current party highlight");
        ImGui.TextWrapped("When you join a normal party, PartyPing keeps all matching PF cards visible and highlights the card belonging to your party. The highlighted card uses your live in-game party count, so it updates when someone joins or leaves. If your join fills the role you were filtering for or the listing disappears from public PF, your highlighted card is kept until you leave the party.");
        ImGui.TextWrapped(plugin.PartyFillStatus);

        DrawSectionHeader("Discord bot - interactive PF buttons");
        ImGui.TextWrapped("Configure a Discord application bot here to get native Open in FFXIV and Join Party buttons on PF cards. PartyPing connects directly to Discord's Gateway; clicking the buttons does not open a browser.");

        EditSecret("Bot token", Config.DiscordBotToken, v => Config.DiscordBotToken = v, 256);
        EditString("Channel ID", Config.DiscordChannelId, v => Config.DiscordChannelId = v, 32);
        EditString("Your Discord user ID", Config.DiscordUserId, v => Config.DiscordUserId = v, 32);
        ImGui.TextDisabled("The user ID restriction prevents other people in the channel from controlling your local FFXIV client.");
        ImGui.TextWrapped(plugin.DiscordBotStatus);

        DrawSectionHeader("Discord fallback / migration");
        var discordUrl = string.IsNullOrWhiteSpace(Config.DiscordWebhookUrl)
            ? Config.TwilioAccountSid
            : Config.DiscordWebhookUrl;
        EditString("Legacy webhook URL", discordUrl, v =>
        {
            Config.DiscordWebhookUrl = v;
            Config.TwilioAccountSid = v;
        }, 512);
        ImGui.TextDisabled("Optional after bot setup. Keep it during migration so PartyPing can remove older webhook-owned cards.");

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

        ImGui.TextDisabled("Deletes tracked PartyPing posts. Matching PF listings will repopulate on the next poll; your current party will be highlighted again if its PF card is available.");
        if (!string.IsNullOrWhiteSpace(clearStatus))
            ImGui.TextWrapped(clearStatus);

        ImGui.Spacing();
        DrawSectionHeader("Important");
        ImGui.TextWrapped("PartyPing uses only data received directly from the FFXIV Party Finder. Automatic polling currently requests the High-End Duty category only and pauses while you are inside a duty, zoning, or in a cutscene.");
        ImGui.TextWrapped("Interactive Discord buttons require the bot token, channel ID, and your user ID. Current-party highlighting uses the recruiter/party-leader identity from FFXIV to match your party back to its PF card.");
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
                clearStatus = $"Cleared {result.Removed} PartyPing Discord message(s). Matching local PF listings will repopulate on the next poll.";
            }
            else
            {
                clearStatus = $"Cleared {result.Removed} message(s); {result.Failed} could not be removed. Press Clear again to retry.";
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
