from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"Expected text not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# DiscordNotifier: add explicit mobile Search ON / Search OFF buttons.
replace_once(
    "DiscordNotifier.cs",
    '    private const string IgnoreButtonPrefix = "partyping_ignore:";\n',
    '    private const string IgnoreButtonPrefix = "partyping_ignore:";\n'
    '    private const string SearchOnButtonId = "partyping_search:on";\n'
    '    private const string SearchOffButtonId = "partyping_search:off";\n'
)

replace_once(
    "DiscordNotifier.cs",
    '''                            new\n                            {\n                                type = 2,\n                                style = 2,\n                                label = "Ignore",\n                                custom_id = IgnoreButtonPrefix + pf.ListingId,\n                                disabled = false,\n                            },\n''',
    '''                            new\n                            {\n                                type = 2,\n                                style = 2,\n                                label = "Ignore",\n                                custom_id = IgnoreButtonPrefix + pf.ListingId,\n                                disabled = false,\n                            },\n                            new\n                            {\n                                type = 2,\n                                style = 3,\n                                label = "Search ON",\n                                custom_id = SearchOnButtonId,\n                                disabled = false,\n                            },\n                            new\n                            {\n                                type = 2,\n                                style = 4,\n                                label = "Search OFF",\n                                custom_id = SearchOffButtonId,\n                                disabled = false,\n                            },\n'''
)

# DiscordBotBridge: recognize global search controls without requiring a listing ID.
replace_once(
    "DiscordBotBridge.cs",
    '    private const string IgnoreButtonPrefix = "partyping_ignore:";\n',
    '    private const string IgnoreButtonPrefix = "partyping_ignore:";\n'
    '    private const string SearchOnButtonId = "partyping_search:on";\n'
    '    private const string SearchOffButtonId = "partyping_search:off";\n'
)

replace_once(
    "DiscordBotBridge.cs",
    '''    private readonly Func<ulong, CancellationToken, Task<bool>> openListing;\n    private readonly Func<ulong, CancellationToken, Task<bool>> ignoreListing;\n''',
    '''    private readonly Func<ulong, CancellationToken, Task<bool>> openListing;\n    private readonly Func<ulong, CancellationToken, Task<bool>> ignoreListing;\n    private readonly Func<bool, CancellationToken, Task> setSearchEnabled;\n'''
)

replace_once(
    "DiscordBotBridge.cs",
    '''    internal DiscordBotBridge(\n        Func<ulong, CancellationToken, Task<bool>> openListing,\n        Func<ulong, CancellationToken, Task<bool>> ignoreListing)\n    {\n        this.openListing = openListing;\n        this.ignoreListing = ignoreListing;\n    }\n''',
    '''    internal DiscordBotBridge(\n        Func<ulong, CancellationToken, Task<bool>> openListing,\n        Func<ulong, CancellationToken, Task<bool>> ignoreListing,\n        Func<bool, CancellationToken, Task> setSearchEnabled)\n    {\n        this.openListing = openListing;\n        this.ignoreListing = ignoreListing;\n        this.setSearchEnabled = setSearchEnabled;\n    }\n'''
)

replace_once(
    "DiscordBotBridge.cs",
    '                    Status = "Discord bot: connected - Open / Join / Ignore buttons ready";\n',
    '                    Status = "Discord bot: connected - Open / Join / Ignore / Search controls ready";\n'
)

replace_once(
    "DiscordBotBridge.cs",
    '''            var isJoin = customId.StartsWith(JoinButtonPrefix, StringComparison.Ordinal);\n            var isOpen = customId.StartsWith(OpenButtonPrefix, StringComparison.Ordinal);\n            var isIgnore = customId.StartsWith(IgnoreButtonPrefix, StringComparison.Ordinal);\n            if (!isJoin && !isOpen && !isIgnore)\n                return;\n\n            Status = isJoin\n                ? "Discord bot: Join Party button received..."\n                : isIgnore\n                    ? "Discord bot: Ignore button received..."\n                    : "Discord bot: Open in FFXIV button received...";\n''',
    '''            var isJoin = customId.StartsWith(JoinButtonPrefix, StringComparison.Ordinal);\n            var isOpen = customId.StartsWith(OpenButtonPrefix, StringComparison.Ordinal);\n            var isIgnore = customId.StartsWith(IgnoreButtonPrefix, StringComparison.Ordinal);\n            var isSearchOn = string.Equals(customId, SearchOnButtonId, StringComparison.Ordinal);\n            var isSearchOff = string.Equals(customId, SearchOffButtonId, StringComparison.Ordinal);\n            if (!isJoin && !isOpen && !isIgnore && !isSearchOn && !isSearchOff)\n                return;\n\n            Status = isSearchOn\n                ? "Discord bot: Search ON received..."\n                : isSearchOff\n                    ? "Discord bot: Search OFF received..."\n                    : isJoin\n                        ? "Discord bot: Join Party button received..."\n                        : isIgnore\n                            ? "Discord bot: Ignore button received..."\n                            : "Discord bot: Open in FFXIV button received...";\n'''
)

replace_once(
    "DiscordBotBridge.cs",
    '''            var prefix = isJoin ? JoinButtonPrefix : isIgnore ? IgnoreButtonPrefix : OpenButtonPrefix;\n            if (!ulong.TryParse(customId[prefix.Length..], out var listingId) || listingId == 0)\n''',
    '''            if (isSearchOn || isSearchOff)\n            {\n                var enabled = isSearchOn;\n                await RespondAsync(\n                    interactionId,\n                    interactionToken,\n                    new { type = 6 },\n                    cancellationToken).ConfigureAwait(false);\n\n                Status = enabled\n                    ? "Discord bot: Search ON acknowledged..."\n                    : "Discord bot: Search OFF acknowledged...";\n                _ = SetSearchEnabledAfterAcknowledgementAsync(enabled, cancellationToken);\n                return;\n            }\n\n            var prefix = isJoin ? JoinButtonPrefix : isIgnore ? IgnoreButtonPrefix : OpenButtonPrefix;\n            if (!ulong.TryParse(customId[prefix.Length..], out var listingId) || listingId == 0)\n'''
)

replace_once(
    "DiscordBotBridge.cs",
    '''    private async Task IgnoreListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)\n''',
    '''    private async Task SetSearchEnabledAfterAcknowledgementAsync(bool enabled, CancellationToken cancellationToken)\n    {\n        try\n        {\n            await setSearchEnabled(enabled, cancellationToken).ConfigureAwait(false);\n            Status = enabled\n                ? "Discord bot: connected - PF searching enabled"\n                : "Discord bot: connected - PF searching paused";\n        }\n        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)\n        {\n        }\n        catch (Exception ex)\n        {\n            Status = "Discord bot: search control failed - " + ex.Message;\n            Plugin.Log.Warning(ex, "PartyPing could not change PF search state from Discord");\n        }\n    }\n\n    private async Task IgnoreListingAfterAcknowledgementAsync(ulong listingId, CancellationToken cancellationToken)\n'''
)

# Plugin: wire the remote control and immediately scan when mobile Search ON is pressed.
replace_once(
    "Plugin.cs",
    '''        discordBotBridge = new DiscordBotBridge(\n            OpenLocalPfListingFromDiscordAsync,\n            IgnoreLocalPfListingFromDiscordAsync);\n''',
    '''        discordBotBridge = new DiscordBotBridge(\n            OpenLocalPfListingFromDiscordAsync,\n            IgnoreLocalPfListingFromDiscordAsync,\n            SetSearchEnabledFromDiscordAsync);\n'''
)

replace_once(
    "Plugin.cs",
    '''    internal async Task SendTestAsync()\n''',
    '''    private Task SetSearchEnabledFromDiscordAsync(bool enabled, CancellationToken cancellationToken)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n\n        Configuration.Enabled = enabled;\n        Configuration.Save();\n\n        if (enabled)\n        {\n            LocalPfStatus = "Local PF: searching enabled from Discord - starting a scan now";\n            _ = CheckLocalPfNowAsync();\n        }\n        else\n        {\n            LocalPfStatus = "Local PF: searching paused from Discord";\n        }\n\n        return Task.CompletedTask;\n    }\n\n    internal async Task SendTestAsync()\n'''
)

# Scanner: if OFF is pressed during a multipage scan, stop walking pages and do not publish partial results.
replace_once(
    "LocalPfScanner.cs",
    '''            var scan = await ScanLocalPfPagesAsync(originalUiState.Value, cancellation.Token).ConfigureAwait(false);\n            openedPagingUi = scan.OpenedPagingUi;\n\n            if (!string.IsNullOrWhiteSpace(scan.FailureStatus))\n''',
    '''            var scan = await ScanLocalPfPagesAsync(originalUiState.Value, cancellation.Token).ConfigureAwait(false);\n            openedPagingUi = scan.OpenedPagingUi;\n\n            if (!Configuration.Enabled)\n            {\n                LocalPfStatus = "Local PF: searching paused from Discord";\n                return;\n            }\n\n            if (!string.IsNullOrWhiteSpace(scan.FailureStatus))\n'''
)

replace_once(
    "LocalPfScanner.cs",
    '''        while (!complete && pagesScanned < LocalPfMaximumPages)\n        {\n            ResetLocalPfPageCapture();\n''',
    '''        while (!complete && pagesScanned < LocalPfMaximumPages)\n        {\n            if (!Configuration.Enabled)\n                break;\n\n            ResetLocalPfPageCapture();\n'''
)

replace_once(
    "LocalPfScanner.cs",
    '            LocalPfStatus = "Local PF: enable Discord alerts first";\n',
    '            LocalPfStatus = "Local PF: PF searching is paused";\n'
)

# Force existing bot-owned PF cards to refresh their component row on the next scan.
replace_once(
    "Configuration.cs",
    '    public int Version { get; set; } = 10;\n',
    '    public int Version { get; set; } = 11;\n'
)

replace_once(
    "Configuration.cs",
    '''    public ulong ListingId { get; set; }\n\n    // Empty means the alert predates transport tracking. Those messages are\n''',
    '''    public ulong ListingId { get; set; }\n    public int CardUiVersion { get; set; }\n\n    // Empty means the alert predates transport tracking. Those messages are\n'''
)

replace_once(
    "LocalPfScanner.cs",
    '    private const int SaturatedPageMissesBeforeRemoval = 3;\n',
    '    private const int SaturatedPageMissesBeforeRemoval = 3;\n    private const int CurrentDiscordCardUiVersion = 2;\n'
)

replace_once(
    "LocalPfScanner.cs",
    '''                        activeAlert.LastContent = message;\n                        activeAlert.Transport = replacement.Transport;\n                        updatedCount++;\n''',
    '''                        activeAlert.LastContent = message;\n                        activeAlert.Transport = replacement.Transport;\n                        activeAlert.CardUiVersion = CurrentDiscordCardUiVersion;\n                        updatedCount++;\n'''
)

replace_once(
    "LocalPfScanner.cs",
    '''                if (!string.Equals(message, activeAlert.LastContent, StringComparison.Ordinal))\n                {\n''',
    '''                if (!string.Equals(message, activeAlert.LastContent, StringComparison.Ordinal) ||\n                    activeAlert.CardUiVersion != CurrentDiscordCardUiVersion)\n                {\n'''
)

replace_once(
    "LocalPfScanner.cs",
    '''                    activeAlert.LastContent = message;\n                    updatedCount++;\n''',
    '''                    activeAlert.LastContent = message;\n                    activeAlert.CardUiVersion = CurrentDiscordCardUiVersion;\n                    stateChanged = true;\n                    updatedCount++;\n'''
)

replace_once(
    "LocalPfScanner.cs",
    '''                ListingId = listing.ListingId,\n                Transport = result.Transport,\n''',
    '''                ListingId = listing.ListingId,\n                CardUiVersion = CurrentDiscordCardUiVersion,\n                Transport = result.Transport,\n'''
)

# UI wording: Enabled now clearly means scanning + alerts, not just Discord delivery.
replace_once(
    "ConfigWindow.cs",
    '        ImGui.TextDisabled(enabled ? "Discord PF alerts active" : "Alerts paused");\n',
    '        ImGui.TextDisabled(enabled ? "PF searching + Discord alerts active" : "PF searching paused");\n'
)

replace_once(
    "ConfigWindow.cs",
    '        ImGui.TextDisabled("Bot mode enables Open in FFXIV, Join Party, and Ignore buttons.");\n',
    '        ImGui.TextDisabled("Bot mode enables Open, Join, Ignore, Search ON, and Search OFF from Discord mobile.");\n'
)

Path("version.txt").write_text("0.7.17.0\n", encoding="utf-8")
Path("release-version.txt").write_text("0.7.17.0\n", encoding="utf-8")
