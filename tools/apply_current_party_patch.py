from pathlib import Path


def replace_exact(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8").replace("\r\n", "\n")
    if old not in text:
        raise RuntimeError(f"Expected block not found in {path}: {old[:100]!r}")
    p.write_text(text.replace(old, new), encoding="utf-8", newline="\n")

# Local PF matching: keep and render the party the user is currently inside even
# after their join consumes the configured role/open slot.
replace_exact(
    "LocalPfScanner.cs",
    '''            var matches = MatchesLocalPfListing(listing, config);\n\n            if (activePfAlerts.TryGetValue(listing.Fingerprint, out var activeAlert))\n            {\n                if (!matches)\n                {''',
    '''            var matches = MatchesLocalPfListing(listing, config);\n            var isCurrentParty = TryGetCurrentPartySize(listing.Fingerprint, out var livePartySize);\n            var shouldKeep = matches || isCurrentParty;\n\n            if (activePfAlerts.TryGetValue(listing.Fingerprint, out var activeAlert))\n            {\n                if (!shouldKeep)\n                {''')

replace_exact(
    "LocalPfScanner.cs",
    '''                var message = BuildLocalPfMessage(listing, config.RequiredRole);''',
    '''                var message = BuildLocalPfMessage(\n                    listing,\n                    config.RequiredRole,\n                    isCurrentParty,\n                    livePartySize);''')

replace_exact(
    "LocalPfScanner.cs",
    '''            if (!matches)\n                continue;\n\n            matchingCount++;\n            var newMessage = BuildLocalPfMessage(listing, config.RequiredRole);''',
    '''            if (!shouldKeep)\n                continue;\n\n            matchingCount++;\n            var newMessage = BuildLocalPfMessage(\n                listing,\n                config.RequiredRole,\n                isCurrentParty,\n                livePartySize);''')

replace_exact(
    "LocalPfScanner.cs",
    '''                if (seenFingerprints.Contains(pair.Key))\n                    continue;\n\n                if (!FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))''',
    '''                if (seenFingerprints.Contains(pair.Key))\n                    continue;\n\n                // Keep the card for the party we are currently inside even if the\n                // public PF listing closes, fills, or falls outside the 50-result page.\n                if (IsCurrentPartyFingerprint(pair.Key))\n                    continue;\n\n                if (!FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))''')

old_builder = '''    private static string BuildLocalPfMessage(LocalPfListingSnapshot listing, RoleFilter role)\n    {\n        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);\n        var cleaned = CleanDescription(listing.Description);\n        var roleText = role == RoleFilter.AnyRole\n            ? "Any role"\n            : $"{role.DisplayName()} - open (verified locally)";\n\n        return\n            $"## {listing.Duty}\\n" +\n            "**Source:** Local FFXIV Party Finder\\n" +\n            $"**Party:** {listing.FilledSlots}/{listing.TotalSlots}\\n" +\n            $"**Open slots:** {openSlots}\\n" +\n            $"**Role filter:** {roleText}\\n" +\n            $"**World:** {listing.World}\\n" +\n            $"**Recruiter:** {listing.Recruiter}\\n" +\n            $"**Listing ID:** {listing.ListingId}\\n\\n" +\n            "### Party Finder Description\\n" +\n            $"> {cleaned}";\n    }'''

new_builder = '''    private static string BuildLocalPfMessage(\n        LocalPfListingSnapshot listing,\n        RoleFilter role,\n        bool isCurrentParty,\n        int livePartySize)\n    {\n        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);\n        var cleaned = CleanDescription(listing.Description);\n        var roleText = role == RoleFilter.AnyRole\n            ? "Any role"\n            : $"{role.DisplayName()} - open (verified locally)";\n        var shownPartySize = isCurrentParty && livePartySize > 0\n            ? livePartySize\n            : listing.FilledSlots;\n        var currentPartyMarker = isCurrentParty\n            ? "**Current party:** Yes\\n"\n            : string.Empty;\n\n        return\n            $"## {listing.Duty}\\n" +\n            "**Source:** Local FFXIV Party Finder\\n" +\n            currentPartyMarker +\n            $"**Party:** {shownPartySize}/{listing.TotalSlots}\\n" +\n            $"**Open slots:** {openSlots}\\n" +\n            $"**Role filter:** {roleText}\\n" +\n            $"**World:** {listing.World}\\n" +\n            $"**Recruiter:** {listing.Recruiter}\\n" +\n            $"**Listing ID:** {listing.ListingId}\\n\\n" +\n            "### Party Finder Description\\n" +\n            $"> {cleaned}";\n    }'''
replace_exact("LocalPfScanner.cs", old_builder, new_builder)

# Discord card presentation: green highlight/banner and disable the redundant Join
# button for the party the player is already in.
replace_exact(
    "DiscordNotifier.cs",
    '''        string Recruiter,\n        string ListingId,\n        string Description);''',
    '''        string Recruiter,\n        string ListingId,\n        string Description,\n        bool IsCurrentParty);''')

replace_exact(
    "DiscordNotifier.cs",
    '''            var embed = new\n            {\n                title = pf.Duty,\n                description = pf.Description,\n                color = PartyFinderEmbedColor,''',
    '''            var embed = new\n            {\n                title = pf.Duty,\n                description = pf.IsCurrentParty\n                    ? "⭐ **YOUR PARTY**\\n\\n" + pf.Description\n                    : pf.Description,\n                color = pf.IsCurrentParty ? 0x57F287 : PartyFinderEmbedColor,''')

replace_exact(
    "DiscordNotifier.cs",
    '''                                style = 3,\n                                label = "Join Party",\n                                custom_id = JoinButtonPrefix + pf.ListingId,''',
    '''                                style = 3,\n                                label = pf.IsCurrentParty ? "Joined" : "Join Party",\n                                custom_id = JoinButtonPrefix + pf.ListingId,\n                                disabled = pf.IsCurrentParty,''')

replace_exact(
    "DiscordNotifier.cs",
    '''        var recruiter = FindField(lines, "**Recruiter:**");\n        var listingId = FindField(lines, "**Listing ID:**");''',
    '''        var recruiter = FindField(lines, "**Recruiter:**");\n        var listingId = FindField(lines, "**Listing ID:**");\n        var isCurrentParty = string.Equals(\n            FindField(lines, "**Current party:**"),\n            "Yes",\n            StringComparison.OrdinalIgnoreCase);''')

replace_exact(
    "DiscordNotifier.cs",
    '''            Trim(recruiter, 1024),\n            listingId,\n            Trim(description, 4096));''',
    '''            Trim(recruiter, 1024),\n            listingId,\n            Trim(description, 4096),\n            isCurrentParty);''')

Path("version.txt").write_text("0.7.7.0\n", encoding="utf-8", newline="\n")
