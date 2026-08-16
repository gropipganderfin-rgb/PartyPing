from pathlib import Path

path = Path('LocalPfScanner.cs')
text = path.read_text(encoding='utf-8')

old_slots = '''            // SlotFlags and RawJobsPresent are both 8-entry PF arrays. A zero job entry
            // marks an unfilled seat at that same slot index. Do not assume filled seats
            // are packed at the front: an open tank/healer seat can be anywhere in the row.
            var openSlotCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
            var slots = listing.Slots.ToArray();
            var jobsPresent = listing.RawJobsPresent.ToArray();
            var usableSlotCount = Math.Min((int)listing.SlotsAvailable, Math.Min(slots.Length, jobsPresent.Length));

            var openSlotIndexes = Enumerable.Range(0, usableSlotCount)
                .Where(index => jobsPresent[index] == 0)
                .ToArray();

            // Defensive fallback for malformed/transitional packets. If the positional
            // zero count does not agree with SlotsAvailable-SlotsFilled, retain the old
            // packed-seat interpretation instead of dropping the listing entirely.
            if (openSlotIndexes.Length != openSlotCount)
            {
                var start = Math.Min((int)listing.SlotsFilled, usableSlotCount);
                var count = Math.Min(openSlotCount, Math.Max(0, usableSlotCount - start));
                openSlotIndexes = Enumerable.Range(start, count).ToArray();
            }

            var acceptedJobs = openSlotIndexes
                .SelectMany(index => slots[index].Accepting)
                .Distinct()
                .ToArray();
'''
new_slots = '''            // Dalamud exposes JobsPresent as the jobs already in the party, not a
            // guaranteed seat-by-seat occupancy map. Party Finder slot restrictions are
            // ordered with the filled seats first, followed by the currently open seats.
            // Inspect exactly SlotsAvailable-SlotsFilled open seats and ignore padding.
            var openSlotCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
            var acceptedJobs = listing.Slots
                .Skip(listing.SlotsFilled)
                .Take(openSlotCount)
                .SelectMany(slot => slot.Accepting)
                .Distinct()
                .ToArray();
'''
if old_slots not in text:
    raise SystemExit('slot parsing block not found')
text = text.replace(old_slots, new_slots, 1)

old_cap = '''            if (count >= LocalPfMaxListingsPerPage)
                return;

'''
if old_cap not in text:
    raise SystemExit('50-listing early-return block not found')
text = text.replace(old_cap, '', 1)

old_counts = '''        var matchingCount = 0;
        var newCount = 0;
        var updatedCount = 0;
        var removedCount = 0;
        var stateChanged = false;
'''
new_counts = '''        var matchingCount = 0;
        var newCount = 0;
        var updatedCount = 0;
        var removedCount = 0;
        var rejectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stateChanged = false;

        void CountRejection(string reason)
        {
            rejectionCounts.TryGetValue(reason, out var count);
            rejectionCounts[reason] = count + 1;
        }
'''
if old_counts not in text:
    raise SystemExit('process counters block not found')
text = text.replace(old_counts, new_counts, 1)

old_loop = '''        foreach (var listing in listings)
        {
            if (!listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var matches = MatchesLocalPfListing(listing, config);
            var isCurrentParty = TryGetCurrentPartySize(listing.Fingerprint, out _);
            var shouldKeep = matches || isCurrentParty;
'''
new_loop = '''        foreach (var listing in listings)
        {
            if (!listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                CountRejection("duty");
                continue;
            }

            var rejectionReason = GetLocalPfRejectionReason(listing, config);
            var matches = rejectionReason is null;
            if (rejectionReason is not null)
                CountRejection(rejectionReason);

            var isCurrentParty = TryGetCurrentPartySize(listing.Fingerprint, out _);
            var shouldKeep = matches || isCurrentParty;
'''
if old_loop not in text:
    raise SystemExit('listing match loop block not found')
text = text.replace(old_loop, new_loop, 1)

old_status = '''        LocalPfStatus =
            $"Local PF: checked {DateTime.Now:t} - {listings.Count} High-End listings, " +
            $"{matchingCount} matched, {newCount} new, {updatedCount} updated, {removedCount} removed ({completeness})";
    }

    private static bool MatchesLocalPfListing(LocalPfListingSnapshot listing, Configuration config)
    {
        if (listing.MinimumItemLevel == 999)
            return false;

        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return false;

        if (config.RequiredRole != RoleFilter.AnyRole &&
            !listing.AcceptedJobs.Any(job => config.RequiredRole.Matches(job)))
            return false;

        return MatchesTextRules(listing.Duty, listing.Description, config);
    }
'''
new_status = '''        var rejectionSummary = rejectionCounts.Count == 0
            ? "none"
            : string.Join(", ", rejectionCounts
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} {pair.Value}"));

        LocalPfStatus =
            $"Local PF: checked {DateTime.Now:t} - {listings.Count} High-End listings, " +
            $"{matchingCount} matched, {newCount} new, {updatedCount} updated, {removedCount} removed; " +
            $"rejected: {rejectionSummary} ({completeness})";
    }

    private static bool MatchesLocalPfListing(LocalPfListingSnapshot listing, Configuration config) =>
        GetLocalPfRejectionReason(listing, config) is null;

    private static string? GetLocalPfRejectionReason(LocalPfListingSnapshot listing, Configuration config)
    {
        if (listing.MinimumItemLevel == 999)
            return "iLvl 999";

        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return "open slots";

        if (config.RequiredRole != RoleFilter.AnyRole)
        {
            var roleOpenFromSlots = listing.AcceptedJobs.Any(job => config.RequiredRole.Matches(job));
            var roleOpenFromDescription = DescriptionExplicitlyRequestsRole(listing.Description, config.RequiredRole);
            if (!roleOpenFromSlots && !roleOpenFromDescription)
                return "role";
        }

        if (!MatchesTextRules(listing.Duty, listing.Description, config))
            return "keywords";

        return null;
    }

    private static bool DescriptionExplicitlyRequestsRole(string description, RoleFilter role)
    {
        if (string.IsNullOrWhiteSpace(description) || role == RoleFilter.AnyRole)
            return false;

        var text = NormalizeMatchText(description).ToLowerInvariant();

        static bool HasAny(string value, params string[] phrases) =>
            phrases.Any(phrase => value.Contains(phrase, StringComparison.Ordinal));

        return role switch
        {
            RoleFilter.Tank => HasAny(text,
                "need mt", "need ot", "need tank", "need a tank", "lf mt", "lf ot", "lf tank",
                "mt needed", "ot needed", "tank needed", "tank open"),
            RoleFilter.Healer => HasAny(text,
                "need h1", "need h2", "need healer", "need a healer", "lf h1", "lf h2", "lf healer",
                "healer needed", "healer open"),
            RoleFilter.Melee => HasAny(text,
                "need melee", "lf melee", "melee needed", "melee open"),
            RoleFilter.PhysicalRanged => HasAny(text,
                "need phys ranged", "need physical ranged", "need prange", "lf phys ranged", "lf prange",
                "physical ranged needed", "prange needed"),
            RoleFilter.Caster => HasAny(text,
                "need caster", "need magic ranged", "need magical ranged", "lf caster", "caster needed", "caster open"),
            RoleFilter.AnyDps => HasAny(text,
                "need dps", "need a dps", "lf dps", "dps needed", "dps open", "need melee", "need caster",
                "need phys ranged", "need physical ranged", "need prange"),
            _ => false,
        };
    }
'''
if old_status not in text:
    raise SystemExit('status/matcher block not found')
text = text.replace(old_status, new_status, 1)

path.write_text(text, encoding='utf-8')

Path('version.txt').write_text('0.7.14.0\n', encoding='utf-8')
