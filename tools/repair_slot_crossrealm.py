from pathlib import Path

scanner_path = Path("LocalPfScanner.cs")
scanner = scanner_path.read_text(encoding="utf-8")
old_slot = '''            // Only inspect seats that are currently open. Filled seats and padding must not count toward role availability.
            var openSlotCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
            var acceptedJobs = listing.Slots
                .Skip(listing.SlotsFilled)
                .Take(openSlotCount)
                .SelectMany(slot => slot.Accepting)
                .Distinct()
                .ToArray();
'''
new_slot = '''            // SlotFlags and RawJobsPresent are both 8-entry PF arrays. A zero job entry
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
if old_slot not in scanner:
    raise SystemExit("expected LocalPfScanner slot parsing block not found")
scanner = scanner.replace(old_slot, new_slot, 1)
scanner_path.write_text(scanner, encoding="utf-8")

cross_path = Path("CrossRealmPartyTracker.cs")
cross = cross_path.read_text(encoding="utf-8")
old_group = '''        var availableGroups = proxy->CrossRealmGroups.Length;
        var groupCount = Math.Clamp((int)proxy->GroupCount, 0, availableGroups);
        if (groupCount <= 0)
            return false;

        var groupIndex = Math.Clamp((int)proxy->LocalPlayerGroupIndex, 0, groupCount - 1);
        var group = proxy->CrossRealmGroups[groupIndex];

        var identities = new List<(string Name, ulong ContentId, bool IsLeader)>();
        var scanCount = Math.Min(8, group.GroupMembers.Length);
        for (var i = 0; i < scanCount; i++)
'''
new_group = '''        var availableGroups = proxy->CrossRealmGroups.Length;
        if (availableGroups <= 0)
            return false;

        // GroupCount can briefly be zero during cross-world PF transitions even while
        // IsInCrossRealmParty is already true. LocalPlayerGroupIndex + the group's own
        // GroupMemberCount are the useful authoritative fields for a normal CW party.
        var groupIndex = (int)proxy->LocalPlayerGroupIndex;
        if (groupIndex < 0 || groupIndex >= availableGroups)
            groupIndex = 0;

        var group = proxy->CrossRealmGroups[groupIndex];
        var reportedMemberCount = Math.Clamp((int)group.GroupMemberCount, 0, Math.Min(8, group.GroupMembers.Length));

        var identities = new List<(string Name, ulong ContentId, bool IsLeader)>();
        var scanCount = reportedMemberCount > 0
            ? reportedMemberCount
            : Math.Min(8, group.GroupMembers.Length);
        for (var i = 0; i < scanCount; i++)
'''
if old_group not in cross:
    raise SystemExit("expected CrossRealmPartyTracker group block not found")
cross = cross.replace(old_group, new_group, 1)
old_count = '''        var memberCount = identities.Count;
        if (memberCount <= 1)
            return false;
'''
new_count = '''        var memberCount = reportedMemberCount > 0 ? reportedMemberCount : identities.Count;
        if (memberCount <= 1)
            return false;
'''
if old_count not in cross:
    raise SystemExit("expected CrossRealmPartyTracker member count block not found")
cross = cross.replace(old_count, new_count, 1)
cross_path.write_text(cross, encoding="utf-8")

Path("version.txt").write_text("0.7.13.0\n", encoding="utf-8")
