from pathlib import Path

p = Path("DiscordBotBridge.cs")
text = p.read_text(encoding="utf-8")
start = text.index("    private static object CreateSearchTermsModal(")
end = text.index("    private static string DisplayFilter", start)
replacement = r'''    private static object CreateSearchTermsModal(string includeKeywords, string excludeKeywords) =>
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
                        type = 18,
                        label = "Include terms (comma-separated)",
                        component = new
                        {
                            type = 4,
                            custom_id = IncludeTermsInputId,
                            style = 1,
                            required = false,
                            max_length = 512,
                            value = includeKeywords ?? string.Empty,
                        },
                    },
                    new
                    {
                        type = 18,
                        label = "Exclude terms (comma-separated)",
                        component = new
                        {
                            type = 4,
                            custom_id = ExcludeTermsInputId,
                            style = 1,
                            required = false,
                            max_length = 512,
                            value = excludeKeywords ?? string.Empty,
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
            // Current Discord modal format: a Label component wraps one Text Input.
            if (row.TryGetProperty("component", out var child) &&
                child.ValueKind == JsonValueKind.Object &&
                child.TryGetProperty("custom_id", out var childId) &&
                string.Equals(childId.GetString(), wantedCustomId, StringComparison.Ordinal))
            {
                return child.TryGetProperty("value", out var childValue)
                    ? childValue.GetString() ?? string.Empty
                    : string.Empty;
            }

            // Legacy Action Row format, retained as a compatibility fallback.
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

'''
p.write_text(text[:start] + replacement + text[end:], encoding="utf-8")
