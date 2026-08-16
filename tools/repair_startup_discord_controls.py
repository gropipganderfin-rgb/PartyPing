from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"Expected text not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# PF cards: remove Search ON/OFF from every listing card.
replace_once(
    "DiscordNotifier.cs",
    '''                            new\n                            {\n                                type = 2,\n                                style = 2,\n                                label = "Ignore",\n                                custom_id = IgnoreButtonPrefix + pf.ListingId,\n                                disabled = false,\n                            },\n                            new\n                            {\n                                type = 2,\n                                style = 3,\n                                label = "Search ON",\n                                custom_id = SearchOnButtonId,\n                                disabled = false,\n                            },\n                            new\n                            {\n                                type = 2,\n                                style = 4,\n                                label = "Search OFF",\n                                custom_id = SearchOffButtonId,\n                                disabled = false,\n                            },\n''',
    '''                            new\n                            {\n                                type = 2,\n                                style = 2,\n                                label = "Ignore",\n                                custom_id = IgnoreButtonPrefix + pf.ListingId,\n                                disabled = false,\n                            },\n'''
)

# The PF notifier no longer owns the global search buttons.
replace_once(
    "DiscordNotifier.cs",
    '    private const string IgnoreButtonPrefix = "partyping_ignore:";\n    private const string SearchOnButtonId = "partyping_search:on";\n    private const string SearchOffButtonId = "partyping_search:off";\n',
    '    private const string IgnoreButtonPrefix = "partyping_ignore:";\n'
)

# Discord bridge: send one dedicated control panel per plugin startup, not per reconnect.
replace_once(
    "DiscordBotBridge.cs",
    '    private string configurationKey = string.Empty;\n    private bool disposed;\n',
    '    private string configurationKey = string.Empty;\n    private bool startupControlSent;\n    private bool disposed;\n'
)

replace_once(
    "DiscordBotBridge.cs",
    '''                if (string.Equals(eventName, "READY", StringComparison.Ordinal))\n                {\n                    Status = "Discord bot: connected - Open / Join / Ignore / Search controls ready";\n                    continue;\n                }\n''',
    '''                if (string.Equals(eventName, "READY", StringComparison.Ordinal))\n                {\n                    Status = "Discord bot: connected - PF buttons + startup search controls ready";\n\n                    if (!startupControlSent)\n                    {\n                        await SendStartupControlMessageAsync(token, channelId, connectionToken).ConfigureAwait(false);\n                        startupControlSent = true;\n                    }\n\n                    continue;\n                }\n'''
)

insert_before = '''    private async Task<string?> GetInteractionsEndpointUrlAsync(string token, CancellationToken cancellationToken)\n'''
startup_method = '''    private async Task SendStartupControlMessageAsync(\n        string token,\n        string channelId,\n        CancellationToken cancellationToken)\n    {\n        var payload = new\n        {\n            content = "## PartyPing is online\\nUse these controls to start or pause Party Finder searching from Discord.",\n            components = new object[]\n            {\n                new\n                {\n                    type = 1,\n                    components = new object[]\n                    {\n                        new\n                        {\n                            type = 2,\n                            style = 3,\n                            label = "Search ON",\n                            custom_id = SearchOnButtonId,\n                            disabled = false,\n                        },\n                        new\n                        {\n                            type = 2,\n                            style = 4,\n                            label = "Search OFF",\n                            custom_id = SearchOffButtonId,\n                            disabled = false,\n                        },\n                    },\n                },\n            },\n            allowed_mentions = new\n            {\n                parse = Array.Empty<string>(),\n            },\n        };\n\n        using var request = new HttpRequestMessage(\n            HttpMethod.Post,\n            $"{DiscordApiBase}/channels/{channelId}/messages")\n        {\n            Content = new StringContent(\n                JsonSerializer.Serialize(payload),\n                Encoding.UTF8,\n                "application/json"),\n        };\n        request.Headers.Authorization =\n            new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);\n\n        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);\n        if (!response.IsSuccessStatusCode)\n        {\n            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);\n            throw new InvalidOperationException(\n                $"Discord startup control message returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 220)]}");\n        }\n    }\n\n'''
replace_once(
    "DiscordBotBridge.cs",
    insert_before,
    startup_method + insert_before
)

# Existing PF cards need one refresh to remove the global control buttons.
replace_once(
    "LocalPfScanner.cs",
    '    private const int CurrentDiscordCardUiVersion = 2;\n',
    '    private const int CurrentDiscordCardUiVersion = 3;\n'
)

# Clarify the new UI behavior.
replace_once(
    "ConfigWindow.cs",
    '        ImGui.TextDisabled("Bot mode enables Open, Join, Ignore, Search ON, and Search OFF from Discord mobile.");\n',
    '        ImGui.TextDisabled("PF cards use Open, Join, and Ignore. Search ON/OFF are sent once in the startup control message.");\n'
)

# Version bump.
Path("version.txt").write_text("0.7.18.0\n", encoding="utf-8")
Path("release-version.txt").write_text("0.7.18.0\n", encoding="utf-8")
