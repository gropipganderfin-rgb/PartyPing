using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace PartyPing;

internal sealed record XivPfListing(
    string Duty,
    string Description,
    int FilledSlots,
    int TotalSlots,
    string Recruiter,
    string World,
    string Fingerprint);

internal sealed class XivPfWatcher : IDisposable
{
    private static readonly Regex ScriptStyleRegex = new(
        @"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BreakRegex = new(
        @"<(?:br\s*/?|/(?:p|div|li|article|section|h[1-6]|a|span|td|tr))\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    private static readonly Regex SlotRegex = new(
        @"^(\d{1,2})\s*/\s*(\d{1,2})$",
        RegexOptions.Compiled);

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public XivPfWatcher()
    {
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PartyPing", "0.4"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    }

    public async Task<IReadOnlyList<XivPfListing>> FetchAsync(string dutyNameContains, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dutyNameContains))
            return Array.Empty<XivPfListing>();

        using var response = await http.GetAsync("https://xivpf.com/listings", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(html, dutyNameContains.Trim());
    }

    internal static IReadOnlyList<XivPfListing> Parse(string html, string dutyNameContains)
    {
        var text = ScriptStyleRegex.Replace(html, "\n");
        text = BreakRegex.Replace(text, "\n");
        text = TagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');

        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => WhitespaceRegex.Replace(line, " ").Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        var results = new List<XivPfListing>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(dutyNameContains, StringComparison.OrdinalIgnoreCase))
                continue;

            var slotIndex = FindSlotLine(lines, i + 1, Math.Min(lines.Length, i + 30));
            if (slotIndex < 0)
                continue;

            var slotMatch = SlotRegex.Match(lines[slotIndex]);
            if (!slotMatch.Success ||
                !int.TryParse(slotMatch.Groups[1].Value, out var filled) ||
                !int.TryParse(slotMatch.Groups[2].Value, out var total) ||
                total <= 0)
                continue;

            var description = JoinDescription(lines, i + 1, slotIndex);
            var recruiterIndex = FindRecruiterLine(lines, slotIndex + 1, Math.Min(lines.Length, slotIndex + 20));
            var recruiter = recruiterIndex >= 0 ? lines[recruiterIndex] : "Unknown recruiter";
            var world = recruiterIndex >= 0 ? FindWorld(lines, recruiterIndex + 1, Math.Min(lines.Length, recruiterIndex + 8)) : "Unknown";

            if (world == "Unknown")
            {
                var at = recruiter.LastIndexOf(" @ ", StringComparison.Ordinal);
                if (at >= 0 && at + 3 < recruiter.Length)
                    world = recruiter[(at + 3)..].Trim();
            }

            var duty = lines[i];
            var fingerprint = $"{duty}\u001f{description}\u001f{recruiter}\u001f{world}";
            if (!seen.Add(fingerprint))
                continue;

            results.Add(new XivPfListing(duty, description, filled, total, recruiter, world, fingerprint));
        }

        return results;
    }

    private static int FindSlotLine(string[] lines, int start, int endExclusive)
    {
        for (var i = start; i < endExclusive; i++)
            if (SlotRegex.IsMatch(lines[i]))
                return i;

        return -1;
    }

    private static string JoinDescription(string[] lines, int start, int endExclusive)
    {
        if (start >= endExclusive)
            return "No description";

        var description = string.Join(' ', lines[start..endExclusive]);
        return string.IsNullOrWhiteSpace(description) ? "No description" : description;
    }

    private static int FindRecruiterLine(string[] lines, int start, int endExclusive)
    {
        for (var i = start; i < endExclusive; i++)
            if (lines[i].Contains(" @ ", StringComparison.Ordinal))
                return i;

        return -1;
    }

    private static string FindWorld(string[] lines, int start, int endExclusive)
    {
        for (var i = start; i < endExclusive; i++)
        {
            var line = lines[i];
            if (line.Equals("Min IL", StringComparison.OrdinalIgnoreCase) || int.TryParse(line, out _))
                continue;

            if (LooksLikeRelativeTime(line))
                continue;

            return line;
        }

        return "Unknown";
    }

    private static bool LooksLikeRelativeTime(string value)
    {
        return value.Equals("now", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("minute", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("second", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("in ", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => http.Dispose();
}
