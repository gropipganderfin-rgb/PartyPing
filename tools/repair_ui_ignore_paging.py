from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if old not in text:
        raise SystemExit(f'Expected text not found in {path}: {old[:120]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')


# 1) Replace the crowded settings window with a compact tabbed layout.
Path('ConfigWindow.cs').write_text(r'''using System.Numerics;
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
        ImGui.TextDisabled(enabled ? "Discord PF alerts active" : "Alerts paused");

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
        ImGui.TextDisabled("Automatic scan interval: random 30-60 seconds. Up to 10 PF pages per scan.");
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
        ImGui.TextDisabled("Bot mode enables Open in FFXIV, Join Party, and Ignore buttons.");
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
''', encoding='utf-8')

# 2) Persist ignored listing IDs and listing IDs on alert records.
replace_once('Configuration.cs',
'''    public int Version { get; set; } = 9;''',
'''    public int Version { get; set; } = 10;''')
replace_once('Configuration.cs',
'''    public Dictionary<string, PersistedPfAlert> ActivePfAlerts { get; set; } = [];

    public void Save()''',
'''    public Dictionary<string, PersistedPfAlert> ActivePfAlerts { get; set; } = [];

    // Individual PF cards dismissed with the Discord Ignore button. IDs are removed
    // automatically after a complete scan proves the listing no longer exists.
    public List<ulong> IgnoredPfListingIds { get; set; } = [];

    public void Save()''')
replace_once('Configuration.cs',
'''    public long ExpiresAtUnixSeconds { get; set; }

    // Empty means''',
'''    public long ExpiresAtUnixSeconds { get; set; }
    public ulong ListingId { get; set; }

    // Empty means''')

# 3) Wire the ignore callback into the Discord bot bridge.
replace_once('Plugin.cs',
'''        Configuration.DiscordUserId ??= string.Empty;

        discordBotBridge = new DiscordBotBridge(OpenLocalPfListingFromDiscordAsync);''',
'''        Configuration.DiscordUserId ??= string.Empty;
        Configuration.IgnoredPfListingIds ??= [];

        discordBotBridge = new DiscordBotBridge(
            OpenLocalPfListingFromDiscordAsync,
            IgnoreLocalPfListingFromDiscordAsync);''')

# 4) Add Ignore to Discord PF cards.
replace_once('DiscordNotifier.cs',
'''    private const string JoinButtonPrefix = "partyping_join:";''',
'''    private const string JoinButtonPrefix = "partyping_join:";
    private const string IgnoreButtonPrefix = "partyping_ignore:";''')
replace_once('DiscordNotifier.cs',
'''                            new
                            {
                                type = 2,
                                style = 3,
                                label = pf.IsCurrentParty ? "Joined" : "Join Party",
                                custom_id = JoinButtonPrefix + pf.ListingId,
                                disabled = pf.IsCurrentParty,
                            },
                        },''',
'''                            new
                            {
                                type = 2,
                                style = 3,
                                label = pf.IsCurrentParty ? "Joined" : "Join Party",
                                custom_id = JoinButtonPrefix + pf.ListingId,
                                disabled = pf.IsCurrentParty,
                            },
                            new
                            {
                                type = 2,
                                style = 2,
                                label = "Ignore",
                                custom_id = IgnoreButtonPrefix + pf.ListingId,
                                disabled = false,
                            },
                        },''')

# 5) Teach the Gateway bridge to process Ignore interactions.
replace_once('DiscordBotBridge.cs',
'''    private const string JoinButtonPrefix = "partyping_join:";''',
'''    private const string JoinButtonPrefix = "partyping_join:";
    private const string IgnoreButtonPrefix = "partyping_ignore:";''')
replace_once('DiscordBotBridge.cs',
'''    private readonly Func<ulong, CancellationToken, Task<bool>> openListing;
    private readonly HttpClient http = new();''',
'''    private readonly Func<ulong, CancellationToken, Task<bool>> openListing;
    private readonly Func<ulong, CancellationToken, Task<bool>> ignoreListing;
    private readonly HttpClient http = new();''')
replace_once('DiscordBotBridge.cs',
'''    internal DiscordBotBridge(Func<ulong, CancellationToken, Task<bool>> openListing)
    {
        this.openListing = openListing;
    }''',
'''    internal DiscordBotBridge(
        Func<ulong, CancellationToken, Task<bool>> openListing,
        Func<ulong, CancellationToken, Task<bool>> ignoreListing)
    {
        this.openListing = openListing;
        this.ignoreListing = ignoreListing;
    }''')
replace_once('DiscordBotBridge.cs',
'''                    Status = "Discord bot: connected - Open / Join Party buttons ready";''',
'''                    Status = "Discord bot: connected - Open / Join / Ignore buttons ready";''')
replace_once('DiscordBotBridge.cs',
'''            var isJoin = customId.StartsWith(JoinButtonPrefix, StringComparison.Ordinal);
            var isOpen = customId.StartsWith(OpenButtonPrefix, StringComparison.Ordinal);
            if (!isJoin && !isOpen)
                return;

            Status = isJoin
                ? "Discord bot: Join Party button received..."
                : "Discord bot: Open in FFXIV button received...";''',
'''            var isJoin = customId.StartsWith(JoinButtonPrefix, StringComparison.Ordinal);
            var isOpen = customId.StartsWith(OpenButtonPrefix, StringComparison.Ordinal);
            var isIgnore = customId.StartsWith(IgnoreButtonPrefix, StringComparison.Ordinal);
            if (!isJoin && !isOpen && !isIgnore)
                return;

            Status = isJoin
                ? "Discord bot: Join Party button received..."
                : isIgnore
                    ? "Discord bot: Ignore button received..."
                    : "Discord bot: Open in FFXIV button received...";''')
replace_once('DiscordBotBridge.cs',
'''            var prefix = isJoin ? JoinButtonPrefix : OpenButtonPrefix;''',
'''            var prefix = isJoin ? JoinButtonPrefix : isIgnore ? IgnoreButtonPrefix : OpenButtonPrefix;''')
replace_once('DiscordBotBridge.cs',
'''            if (isJoin)
            {
                Status = "Discord bot: button acknowledged - joining PF listing " + listingId;
                _ = JoinListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }
            else
            {
                Status = "Discord bot: button acknowledged - opening PF listing " + listingId;
                _ = OpenListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }''',
'''            if (isIgnore)
            {
                Status = "Discord bot: button acknowledged - ignoring PF listing " + listingId;
                _ = IgnoreListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }
            else if (isJoin)
            {
                Status = "Discord bot: button acknowledged - joining PF listing " + listingId;
                _ = JoinListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }
            else
            {
                Status = "Discord bot: button acknowledged - opening PF listing " + listingId;
                _ = OpenListingAfterAcknowledgementAsync(listingId, cancellationToken);
            }''')
replace_once('DiscordBotBridge.cs',
'''    private async Task OpenListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)
    {''',
'''    private async Task IgnoreListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)
    {
        try
        {
            var ignored = await ignoreListing(listingId, cancellationToken).ConfigureAwait(false);
            Status = ignored
                ? "Discord bot: connected - ignored PF listing " + listingId
                : "Discord bot: connected - could not ignore PF listing " + listingId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "Discord bot: ignore failed - " + ex.Message;
            Plugin.Log.Warning(ex, "PartyPing could not ignore PF listing {ListingId} after Discord acknowledgement", listingId);
        }
    }

    private async Task OpenListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)
    {''')

# 6) Keep the PF addon active while making it visually invisible. Hiding it was
#    deactivating native paging, which is why multipage only worked when PF was open manually.
replace_once('LocalPfScanner.cs',
'''        // FFXIV only sends one PF page at a time. A saturated page therefore needs
        // the actual LookingForGroup pager. If the user did not already have PF open,
        // create the addon, hide it without closing it, and invoke its registered page
        // event directly. No hard-coded callback/event number is used.''',
'''        // FFXIV only sends one PF page at a time. A saturated page therefore needs
        // the actual LookingForGroup pager. If the user did not already have PF open,
        // create the addon and make it transparent while leaving it active. Hiding the
        // addon deactivates native paging. No hard-coded callback/event number is used.''')
replace_once('LocalPfScanner.cs',
'''                        // Keep the addon alive so its registered page-button event is valid,
                        // but hide a PF window that PartyPing opened solely for pagination.
                        addon->AddonLookingForGroupBase.AtkUnitBase.Hide(true, false, 0);
                        return true;''',
'''                        // Keep the addon shown/active so its registered page-button event
                        // remains functional, but make the background pager invisible.
                        addon->AddonLookingForGroupBase.AtkUnitBase.SetAlpha(0);
                        return true;''')

# 7) Ignore/suppress individual listing IDs and clean them once the listing expires.
replace_once('LocalPfScanner.cs',
'''        var config = Configuration;
        var seenFingerprints = listings.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal);''',
'''        var config = Configuration;
        var seenFingerprints = listings.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var ignoredListingIds = config.IgnoredPfListingIds.ToHashSet();''')
replace_once('LocalPfScanner.cs',
'''        foreach (var listing in listings)
        {
            if (!listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))''',
'''        foreach (var listing in listings)
        {
            if (ignoredListingIds.Contains(listing.ListingId))
            {
                CountRejection("ignored");
                if (activePfAlerts.TryGetValue(listing.Fingerprint, out var ignoredAlert))
                {
                    if (await DeleteLocalPfAlertAsync(listing.Fingerprint, ignoredAlert, config, cancellationToken).ConfigureAwait(false))
                        removedCount++;
                }
                continue;
            }

            if (!listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))''')
replace_once('LocalPfScanner.cs',
'''                activeAlert.MissedPolls = 0;
                activeAlert.ExpiresAtUnixSeconds = listing.ExpiresAtUnixSeconds;
                stateChanged = true;''',
'''                activeAlert.MissedPolls = 0;
                activeAlert.ExpiresAtUnixSeconds = listing.ExpiresAtUnixSeconds;
                activeAlert.ListingId = listing.ListingId;
                stateChanged = true;''')
replace_once('LocalPfScanner.cs',
'''                ExpiresAtUnixSeconds = listing.ExpiresAtUnixSeconds,
                Transport = result.Transport,''',
'''                ExpiresAtUnixSeconds = listing.ExpiresAtUnixSeconds,
                ListingId = listing.ListingId,
                Transport = result.Transport,''')
replace_once('LocalPfScanner.cs',
'''        if (stateChanged)
            config.Save();

        var pageText = pagesScanned == 1 ? "1 page" : $"{pagesScanned} pages";''',
'''        if (completeScan && config.IgnoredPfListingIds.Count > 0)
        {
            var liveListingIds = listings.Select(x => x.ListingId).ToHashSet();
            if (config.IgnoredPfListingIds.RemoveAll(id => !liveListingIds.Contains(id)) > 0)
                stateChanged = true;
        }

        if (stateChanged)
            config.Save();

        var pageText = pagesScanned == 1 ? "1 page" : $"{pagesScanned} pages";''')
replace_once('LocalPfScanner.cs',
'''    private async Task<bool> DeleteLocalPfAlertAsync(
        string fingerprint,''',
'''    internal int IgnoredPfListingCount => Configuration.IgnoredPfListingIds.Count;

    internal void ClearIgnoredPfListings()
    {
        if (Configuration.IgnoredPfListingIds.Count == 0)
            return;

        Configuration.IgnoredPfListingIds.Clear();
        Configuration.Save();
        LocalPfStatus = "Local PF: ignored listing list cleared";
    }

    private async Task<bool> IgnoreLocalPfListingFromDiscordAsync(
        ulong listingId,
        CancellationToken cancellationToken)
    {
        if (listingId == 0)
            return false;

        if (!Configuration.IgnoredPfListingIds.Contains(listingId))
            Configuration.IgnoredPfListingIds.Add(listingId);

        foreach (var pair in activePfAlerts.ToArray())
        {
            var matchesListing = pair.Value.ListingId == listingId ||
                pair.Value.LastContent.Contains($"**Listing ID:** {listingId}", StringComparison.Ordinal);
            if (!matchesListing)
                continue;

            await DeleteLocalPfAlertAsync(pair.Key, pair.Value, Configuration, cancellationToken).ConfigureAwait(false);
        }

        Configuration.Save();
        LocalPfStatus = $"Local PF: ignored listing {listingId} until it expires or is reposted";
        return true;
    }

    private async Task<bool> DeleteLocalPfAlertAsync(
        string fingerprint,''')

# 8) Version bump.
Path('version.txt').write_text('0.7.16.0\n', encoding='utf-8')

print('PartyPing UI/ignore/background paging repair applied')
