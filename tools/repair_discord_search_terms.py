from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8")


def replace_once(path, old, new):
    text = read(path)
    if old not in text:
        raise SystemExit(f"Expected text not found in {path}: {old[:160]!r}")
    write(path, text.replace(old, new, 1))


# Discord bridge constants + delegates.
replace_once(
    "DiscordBotBridge.cs",
    '    private const string SearchOffButtonId = "partyping_search:off";\n    private const int GuildsIntent = 1;\n',
    '    private const string SearchOffButtonId = "partyping_search:off";\n'
    '    private const string SearchTermsButtonId = "partyping_search_terms";\n'
    '    private const string SearchTermsModalId = "partyping_search_terms_modal";\n'
    '    private const string IncludeTermsInputId = "partyping_include_terms";\n'
    '    private const string ExcludeTermsInputId = "partyping_exclude_terms";\n'
    '    private const int GuildsIntent = 1;\n'
)

replace_once(
    "DiscordBotBridge.cs",
    '    private readonly Func<bool, CancellationToken, Task> setSearchEnabled;\n    private readonly HttpClient http = new();\n',
    '    private readonly Func<bool, CancellationToken, Task> setSearchEnabled;\n'
    '    private readonly Func<(string IncludeKeywords, string ExcludeKeywords)> getSearchTerms;\n'
    '    private readonly Func<string, string, CancellationToken, Task> setSearchTerms;\n'
    '    private readonly HttpClient http = new();\n'
)

replace_once(
    "DiscordBotBridge.cs",
    '''    internal DiscordBotBridge(\n        Func<ulong, CancellationToken, Task<bool>> openListing,\n        Func<ulong, CancellationToken, Task<bool>> ignoreListing,\n        Func<bool, CancellationToken, Task> setSearchEnabled)\n    {\n        this.openListing = openListing;\n        this.ignoreListing = ignoreListing;\n        this.setSearchEnabled = setSearchEnabled;\n    }\n''',
    '''    internal DiscordBotBridge(\n        Func<ulong, CancellationToken, Task<bool>> openListing,\n        Func<ulong, CancellationToken, Task<bool>> ignoreListing,\n        Func<bool, CancellationToken, Task> setSearchEnabled,\n        Func<(string IncludeKeywords, string ExcludeKeywords)> getSearchTerms,\n        Func<string, string, CancellationToken, Task> setSearchTerms)\n    {\n        this.openListing = openListing;\n        this.ignoreListing = ignoreListing;\n        this.setSearchEnabled = setSearchEnabled;\n        this.getSearchTerms = getSearchTerms;\n        this.setSearchTerms = setSearchTerms;\n    }\n'''
)

# Replace interaction handler wholesale so it supports button -> modal -> modal submit.
text = read("DiscordBotBridge.cs")
start = text.index("    private async Task HandleInteractionAsync(\n")
end = text.index("    private async Task SetSearchEnabledAfterAcknowledgementAsync", start)
handler = r'''    private async Task HandleInteractionAsync(
        JsonElement interaction,
        string allowedChannelId,
        string allowedUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!interaction.TryGetProperty("type", out var typeElement))
                return;

            var interactionType = typeElement.GetInt32();
            if (interactionType != 3 && interactionType != 5)
                return;

            if (!interaction.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("custom_id", out var customIdElement))
            {
                return;
            }

            var customId = customIdElement.GetString();
            if (string.IsNullOrWhiteSpace(customId))
                return;

            if (!interaction.TryGetProperty("id", out var interactionIdElement) ||
                !interaction.TryGetProperty("token", out var interactionTokenElement))
            {
                Status = "Discord bot: interaction payload was missing its interaction ID/token";
                return;
            }

            var interactionId = interactionIdElement.GetString();
            var interactionToken = interactionTokenElement.GetString();
            if (string.IsNullOrWhiteSpace(interactionId) || string.IsNullOrWhiteSpace(interactionToken))
            {
                Status = "Discord bot: interaction payload contained an empty interaction ID/token";
                return;
            }

            var channelId = interaction.TryGetProperty("channel_id", out var channelElement)
                ? channelElement.GetString()
                : null;
            var userId = GetInteractionUserId(interaction);

            if (!string.Equals(channelId, allowedChannelId, StringComparison.Ordinal) ||
                !string.Equals(userId, allowedUserId, StringComparison.Ordinal))
            {
                await RespondEphemeralAsync(
                    interactionId,
                    interactionToken,
                    "This PartyPing control is restricted to its configured owner and channel.",
                    cancellationToken).ConfigureAwait(false);
                Status = "Discord bot: rejected an interaction from a different user/channel";
                return;
            }

            if (interactionType == 5)
            {
                if (!string.Equals(customId, SearchTermsModalId, StringComparison.Ordinal))
                    return;

                var includeKeywords = ReadModalTextValue(data, IncludeTermsInputId).Trim();
                var excludeKeywords = ReadModalTextValue(data, ExcludeTermsInputId).Trim();

                await setSearchTerms(includeKeywords, excludeKeywords, cancellationToken).ConfigureAwait(false);
                await RespondEphemeralAsync(
                    interactionId,
                    interactionToken,
                    "PartyPing search terms updated.\nInclude: " + DisplayFilter(includeKeywords) +
                    "\nExclude: " + DisplayFilter(excludeKeywords),
                    cancellationToken).ConfigureAwait(false);

                Status = "Discord bot: connected - search terms updated";
                return;
            }

            var isJoin = customId.StartsWith(JoinButtonPrefix, StringComparison.Ordinal);
            var isOpen = customId.StartsWith(OpenButtonPrefix, StringComparison.Ordinal);
            var isIgnore = customId.StartsWith(IgnoreButtonPrefix, StringComparison.Ordinal);
            var isSearchOn = string.Equals(customId, SearchOnButtonId, StringComparison.Ordinal);
            var isSearchOff = string.Equals(customId, SearchOffButtonId, StringComparison.Ordinal);
            var isSearchTerms = string.Equals(customId, SearchTermsButtonId, StringComparison.Ordinal);
            if (!isJoin && !isOpen && !isIgnore && !isSearchOn && !isSearchOff && !isSearchTerms)
                return;

            if (isSearchTerms)
            {
                var (includeKeywords, excludeKeywords) = getSearchTerms();
                await RespondAsync(
                    interactionId,
                    interactionToken,
                    CreateSearchTermsModal(includeKeywords, excludeKeywords),
                    cancellationToken).ConfigureAwait(false);
                Status = "Discord bot: search terms form opened";
                return;
            }

            Status = isSearchOn
                ? "Discord bot: Search ON received..."
                : isSearchOff
                    ? "Discord bot: Search OFF received..."
                    : isJoin
                        ? "Discord bot: Join Party button received..."
                        : isIgnore
                            ? "Discord bot: Ignore button received..."
                            : "Discord bot: Open in FFXIV button received...";

            if (isSearchOn || isSearchOff)
            {
                var enabled = isSearchOn;
                await RespondAsync(
                    interactionId,
                    interactionToken,
                    new { type = 6 },
                    cancellationToken).ConfigureAwait(false);

                Status = enabled
                    ? "Discord bot: Search ON acknowledged..."
                    : "Discord bot: Search OFF acknowledged...";
                _ = SetSearchEnabledAfterAcknowledgementAsync(enabled, cancellationToken);
                return;
            }

            var prefix = isJoin ? JoinButtonPrefix : isIgnore ? IgnoreButtonPrefix : OpenButtonPrefix;
            if (!ulong.TryParse(customId[prefix.Length..], out var listingId) || listingId == 0)
            {
                await RespondEphemeralAsync(
                    interactionId,
                    interactionToken,
                    "This Party Finder listing ID is invalid.",
                    cancellationToken).ConfigureAwait(false);
                Status = "Discord bot: button contained an invalid PF listing ID";
                return;
            }

            await RespondAsync(
                interactionId,
                interactionToken,
                new { type = 6 },
                cancellationToken).ConfigureAwait(false);

            if (isIgnore)
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
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Status = "Discord bot: interaction failed - " + ex.Message;
            Plugin.Log.Warning(ex, "PartyPing could not process a Discord interaction");
        }
    }

    private static object CreateSearchTermsModal(string includeKeywords, string excludeKeywords) =>
        new
        {
            type = 9,
            data = new
            {
                custom_id = SearchTermsModalId,
                title = "PartyPing Search Terms",
                components = new object[]
                {
                    new
                    {
                        type = 1,
                        components = new object[]
                        {
                            new
                            {
                                type = 4,
                                custom_id = IncludeTermsInputId,
                                label = "Include terms (comma-separated)",
                                style = 1,
                                required = false,
                                max_length = 512,
                                value = includeKeywords ?? string.Empty,
                            },
                        },
                    },
                    new
                    {
                        type = 1,
                        components = new object[]
                        {
                            new
                            {
                                type = 4,
                                custom_id = ExcludeTermsInputId,
                                label = "Exclude terms (comma-separated)",
                                style = 1,
                                required = false,
                                max_length = 512,
                                value = excludeKeywords ?? string.Empty,
                            },
                        },
                    },
                },
            },
        };

    private static string ReadModalTextValue(JsonElement data, string wantedCustomId)
    {
        if (!data.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var component in components.EnumerateArray())
            {
                if (!component.TryGetProperty("custom_id", out var idElement) ||
                    !string.Equals(idElement.GetString(), wantedCustomId, StringComparison.Ordinal))
                {
                    continue;
                }

                return component.TryGetProperty("value", out var valueElement)
                    ? valueElement.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string DisplayFilter(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : value;

'''
write("DiscordBotBridge.cs", text[:start] + handler + text[end:])

# Add Search Terms button to startup control panel and update copy.
replace_once(
    "DiscordBotBridge.cs",
    '            content = "## PartyPing is online\\nUse these controls to start or pause Party Finder searching from Discord.",\n',
    '            content = "## PartyPing is online\\nStart/pause PF searching or change the include/exclude search terms from Discord.",\n'
)
replace_once(
    "DiscordBotBridge.cs",
    '''                        new\n                        {\n                            type = 2,\n                            style = 4,\n                            label = "Search OFF",\n                            custom_id = SearchOffButtonId,\n                            disabled = false,\n                        },\n''',
    '''                        new\n                        {\n                            type = 2,\n                            style = 4,\n                            label = "Search OFF",\n                            custom_id = SearchOffButtonId,\n                            disabled = false,\n                        },\n                        new\n                        {\n                            type = 2,\n                            style = 1,\n                            label = "Search Terms",\n                            custom_id = SearchTermsButtonId,\n                            disabled = false,\n                        },\n'''
)
replace_once(
    "DiscordBotBridge.cs",
    '                    Status = "Discord bot: connected - PF buttons + startup search controls ready";\n',
    '                    Status = "Discord bot: connected - PF buttons + mobile search controls ready";\n'
)

# Wire the filter getter/setter through Plugin.
replace_once(
    "Plugin.cs",
    '''        discordBotBridge = new DiscordBotBridge(\n            OpenLocalPfListingFromDiscordAsync,\n            IgnoreLocalPfListingFromDiscordAsync,\n            SetSearchEnabledFromDiscordAsync);\n''',
    '''        discordBotBridge = new DiscordBotBridge(\n            OpenLocalPfListingFromDiscordAsync,\n            IgnoreLocalPfListingFromDiscordAsync,\n            SetSearchEnabledFromDiscordAsync,\n            () => (Configuration.IncludeKeywords, Configuration.ExcludeKeywords),\n            SetSearchTermsFromDiscordAsync);\n'''
)
replace_once(
    "Plugin.cs",
    '''    internal async Task SendTestAsync()\n''',
    '''    private Task SetSearchTermsFromDiscordAsync(\n        string includeKeywords,\n        string excludeKeywords,\n        CancellationToken cancellationToken)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n\n        Configuration.IncludeKeywords = includeKeywords.Trim();\n        Configuration.ExcludeKeywords = excludeKeywords.Trim();\n        Configuration.Save();\n\n        if (Configuration.Enabled)\n        {\n            LocalPfStatus = "Local PF: search terms updated from Discord - starting a scan now";\n            _ = CheckLocalPfNowAsync();\n        }\n        else\n        {\n            LocalPfStatus = "Local PF: search terms updated from Discord - searching remains paused";\n        }\n\n        return Task.CompletedTask;\n    }\n\n    internal async Task SendTestAsync()\n'''
)

# UI copy.
replace_once(
    "ConfigWindow.cs",
    '        ImGui.TextDisabled("PF cards use Open, Join, and Ignore. Search ON/OFF are sent once in the startup control message.");\n',
    '        ImGui.TextDisabled("PF cards use Open, Join, and Ignore. Startup controls provide Search ON/OFF plus a Search Terms form for include/exclude filters.");\n'
)

# Version bump.
write("version.txt", "0.7.20.0\n")
write("release-version.txt", "0.7.20.0\n")
