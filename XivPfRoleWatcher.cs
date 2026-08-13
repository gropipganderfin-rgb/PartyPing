using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace PartyPing;

internal sealed class XivPfRoleWatcher : IDisposable
{
    private static readonly Regex ListingRegex = new(
        @"<div\b(?<attrs>[^>]*\bclass\s*=\s*[""'][^""']*\blisting\b[^""']*[""'][^>]*)>(?<body>.*?)(?=<div\b[^>]*\bclass\s*=\s*[""'][^""']*\blisting\b|\z)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DutyRegex = new(
        @"<div\b[^>]*\bclass\s*=\s*[""'][^""']*\bduty\b[^""']*[""'][^>]*>(?<v>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CreatorRegex = new(
        @"\bcreator\b.*?<span\b[^>]*\bclass\s*=\s*[""'][^""']*\btext\b[^""']*[""'][^>]*>(?<v>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SlotRegex = new(
        @"<div\b[^>]*\bclass\s*=\s*[""'](?<c>[^""']*\bslot\b[^""']*)[""'][^>]*\btitle\s*=\s*[""'](?<t>[^""']*)[""'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WsRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase)
        { "GLA", "MRD", "PLD", "WAR", "DRK", "GNB" };
    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
        { "CNJ", "WHM", "SCH", "AST", "SGE" };
    private static readonly HashSet<string> Melee = new(StringComparer.OrdinalIgnoreCase)
        { "PGL", "LNC", "ROG", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR" };
    private static readonly HashSet<string> Phys = new(StringComparer.OrdinalIgnoreCase)
        { "ARC", "BRD", "MCH", "DNC" };
    private static readonly HashSet<string> Casters = new(StringComparer.OrdinalIgnoreCase)
        { "THM", "ACN", "BLM", "SMN", "RDM", "BLU", "PCT" };

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public XivPfRoleWatcher()
    {
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PartyPing", "0.5"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
    }

    public async Task<IReadOnlyDictionary<string, RoleAvailability>> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("https://xivpf.com/listings", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(html);
    }

    internal static IReadOnlyDictionary<string, RoleAvailability> Parse(string html)
    {
        var result = new Dictionary<string, RoleAvailability>(StringComparer.OrdinalIgnoreCase);
        foreach (Match listing in ListingRegex.Matches(html))
        {
            var block = listing.Value;
            var duty = Text(DutyRegex.Match(block).Groups["v"].Value);
            var creator = Text(CreatorRegex.Match(block).Groups["v"].Value);
            if (duty.Length == 0 || creator.Length == 0)
                continue;

            var roles = new RoleAvailability();
            foreach (Match slot in SlotRegex.Matches(block))
            {
                var classes = slot.Groups["c"].Value;
                if (HasClass(classes, "filled"))
                    continue;

                if (HasClass(classes, "empty"))
                {
                    roles.Tank = roles.Healer = roles.Melee = roles.PhysicalRanged = roles.Caster = true;
                    continue;
                }

                var specific = false;
                foreach (var code in WebUtility.HtmlDecode(slot.Groups["t"].Value)
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Tanks.Contains(code)) { roles.Tank = true; specific = true; }
                    else if (Healers.Contains(code)) { roles.Healer = true; specific = true; }
                    else if (Melee.Contains(code)) { roles.Melee = true; specific = true; }
                    else if (Phys.Contains(code)) { roles.PhysicalRanged = true; specific = true; }
                    else if (Casters.Contains(code)) { roles.Caster = true; specific = true; }
                }

                if (!specific)
                {
                    if (HasClass(classes, "tank")) roles.Tank = true;
                    if (HasClass(classes, "healer")) roles.Healer = true;
                    if (HasClass(classes, "dps")) roles.Melee = roles.PhysicalRanged = roles.Caster = true;
                }
            }

            result[Key(duty, creator)] = roles;
        }
        return result;
    }

    public static string Key(XivPfListing listing) => Key(listing.Duty, listing.Recruiter);
    private static string Key(string duty, string creator) => duty.Trim() + "\u001f" + creator.Trim();

    private static string Text(string raw)
    {
        raw = TagRegex.Replace(raw, " ");
        raw = WebUtility.HtmlDecode(raw).Replace('\u00A0', ' ');
        return WsRegex.Replace(raw, " ").Trim();
    }

    private static bool HasClass(string classes, string value) =>
        classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => http.Dispose();
}

internal sealed class RoleAvailability
{
    public bool Tank { get; set; }
    public bool Healer { get; set; }
    public bool Melee { get; set; }
    public bool PhysicalRanged { get; set; }
    public bool Caster { get; set; }

    public bool Matches(RoleFilter role) => role switch
    {
        RoleFilter.AnyRole => Tank || Healer || Melee || PhysicalRanged || Caster,
        RoleFilter.Tank => Tank,
        RoleFilter.Healer => Healer,
        RoleFilter.Melee => Melee,
        RoleFilter.PhysicalRanged => PhysicalRanged,
        RoleFilter.Caster => Caster,
        RoleFilter.AnyDps => Melee || PhysicalRanged || Caster,
        _ => true,
    };
}
