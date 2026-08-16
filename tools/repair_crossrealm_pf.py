from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected exactly one match in {path}, found {count}: {old[:80]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# Make PF collection wait for the full burst instead of assuming everything arrives in 2.5 seconds.
replace_once(
    "LocalPfScanner.cs",
    "    private static readonly TimeSpan LocalPfReceiveWindow = TimeSpan.FromMilliseconds(2500);",
    "    private static readonly TimeSpan LocalPfMaximumReceiveWindow = TimeSpan.FromSeconds(5);\n"
    "    private static readonly TimeSpan LocalPfMinimumReceiveWindow = TimeSpan.FromMilliseconds(1500);\n"
    "    private static readonly TimeSpan LocalPfQuietPeriod = TimeSpan.FromMilliseconds(900);",
)

replace_once(
    "LocalPfScanner.cs",
    "    private readonly object localPfSync = new();\n"
    "    private readonly Dictionary<ulong, LocalPfListingSnapshot> localPfReceived = new();",
    "    private readonly object localPfSync = new();\n"
    "    private readonly Dictionary<ulong, LocalPfListingSnapshot> localPfReceived = new();\n"
    "    private DateTime localPfLastReceiveUtc = DateTime.MinValue;",
)

replace_once(
    "LocalPfScanner.cs",
    "        LocalPfCheckInProgress = true;\n"
    "        lock (localPfSync)\n"
    "            localPfReceived.Clear();",
    "        LocalPfCheckInProgress = true;\n"
    "        lock (localPfSync)\n"
    "        {\n"
    "            localPfReceived.Clear();\n"
    "            localPfLastReceiveUtc = DateTime.MinValue;\n"
    "        }",
)

replace_once(
    "LocalPfScanner.cs",
    "            await Task.Delay(LocalPfReceiveWindow, cancellation.Token).ConfigureAwait(false);",
    "            await WaitForLocalPfResponseAsync(cancellation.Token).ConfigureAwait(false);",
)

replace_once(
    "LocalPfScanner.cs",
    "            await ProcessLocalPfResultsAsync(listings, cancellation.Token).ConfigureAwait(false);",
    "            await ProcessLocalPfResultsAsync(listings, cancellation.Token).ConfigureAwait(false);\n\n"
    "            if (IsInTrackedNormalParty())\n"
    "                await SyncCurrentPartyHighlightAsync(cancellation.Token).ConfigureAwait(false);",
)

replace_once(
    "LocalPfScanner.cs",
    "            lock (localPfSync)\n"
    "                localPfReceived[listing.Id] = snapshot;",
    "            lock (localPfSync)\n"
    "            {\n"
    "                localPfReceived[listing.Id] = snapshot;\n"
    "                localPfLastReceiveUtc = DateTime.UtcNow;\n"
    "            }",
)

wait_method = '''    private async Task WaitForLocalPfResponseAsync(CancellationToken cancellationToken)
    {
        var startedUtc = DateTime.UtcNow;

        while (DateTime.UtcNow - startedUtc < LocalPfMaximumReceiveWindow)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);

            int count;
            DateTime lastReceiveUtc;
            lock (localPfSync)
            {
                count = localPfReceived.Count;
                lastReceiveUtc = localPfLastReceiveUtc;
            }

            if (count >= LocalPfMaxListingsPerPage)
                return;

            var elapsed = DateTime.UtcNow - startedUtc;
            if (count > 0 &&
                elapsed >= LocalPfMinimumReceiveWindow &&
                DateTime.UtcNow - lastReceiveUtc >= LocalPfQuietPeriod)
            {
                return;
            }
        }
    }

'''
replace_once(
    "LocalPfScanner.cs",
    "    private async Task ProcessLocalPfResultsAsync(\n",
    wait_method + "    private async Task ProcessLocalPfResultsAsync(\n",
)

# The request is already local to the player's PF data center. A hard-coded world allowlist can only create false negatives.
replace_once(
    "LocalPfScanner.cs",
    "        if (!LocalPfNorthAmericanWorlds.Contains(listing.World))\n"
    "            return false;\n\n",
    "",
)

# Treat the party we are currently inside as a keep/highlight target even if it fills or stops matching filters.
replace_once(
    "LocalPfScanner.cs",
    "            var matches = MatchesLocalPfListing(listing, config);",
    "            var matches = MatchesLocalPfListing(listing, config);\n"
    "            var isCurrentParty = TryGetCurrentPartySize(listing.Fingerprint, out _);\n"
    "            var shouldKeep = matches || isCurrentParty;",
)

replace_once(
    "LocalPfScanner.cs",
    "                if (!matches)\n"
    "                {\n"
    "                    if (await DeleteLocalPfAlertAsync(listing.Fingerprint, activeAlert, config, cancellationToken).ConfigureAwait(false))",
    "                if (!shouldKeep)\n"
    "                {\n"
    "                    if (await DeleteLocalPfAlertAsync(listing.Fingerprint, activeAlert, config, cancellationToken).ConfigureAwait(false))",
)

replace_once(
    "LocalPfScanner.cs",
    "            if (!matches)\n"
    "                continue;",
    "            if (!shouldKeep)\n"
    "                continue;",
)

replace_once(
    "LocalPfScanner.cs",
    "                if (!FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))\n"
    "                    continue;",
    "                if (IsCurrentPartyFingerprint(pair.Key))\n"
    "                    continue;\n\n"
    "                if (!FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))\n"
    "                    continue;",
)

# Make all current-party matching tolerant of cross-world identities that initially arrive as name-only.
replace_once(
    "PartyTracker.cs",
    "                   FingerprintRecruiterEquals(fingerprint, currentPartyRecruiter);",
    "                   (FingerprintRecruiterEquals(fingerprint, currentPartyRecruiter) ||\n"
    "                    FingerprintRecruiterNameEquals(fingerprint, currentPartyRecruiter));",
)

replace_once(
    "PartyTracker.cs",
    "                    key => FingerprintRecruiterEquals(key, recruiter) &&\n"
    "                           FingerprintMatchesConfiguredDuty(key, Configuration.DutyNameContains)) ?? string.Empty;",
    "                    key => (FingerprintRecruiterEquals(key, recruiter) ||\n"
    "                            FingerprintRecruiterNameEquals(key, recruiter)) &&\n"
    "                           FingerprintMatchesConfiguredDuty(key, Configuration.DutyNameContains)) ?? string.Empty;",
)

replace_once(
    "AutoLocalPfPolling.cs",
    "                    TrackJoinedPartyCard();",
    "                    TrackJoinedPartyCardRobust();",
)

# Native cross-realm party detection. IPartyList is still retained as the fallback for normal parties.
Path("CrossRealmPartyTracker.cs").write_text(r'''using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PartyPing;

public sealed partial class Plugin
{
    private unsafe void TrackJoinedPartyCardRobust()
    {
        if (TryTrackCrossRealmParty())
            return;

        TrackJoinedPartyCard();
    }

    private unsafe bool TryTrackCrossRealmParty()
    {
        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy is null ||
            !InfoProxyCrossRealm.IsCrossRealmParty() ||
            InfoProxyCrossRealm.IsAllianceRaid())
        {
            return false;
        }

        var groupIndex = (int)proxy->LocalPlayerGroupIndex;
        var memberCount = Math.Clamp((int)InfoProxyCrossRealm.GetGroupMemberCount(groupIndex), 0, 8);
        if (memberCount <= 1)
            memberCount = Math.Clamp((int)InfoProxyCrossRealm.GetPartyMemberCount(), 0, 8);

        if (memberCount <= 1)
            return false;

        var identities = new List<(string Name, ulong ContentId, bool IsLeader)>();
        for (var i = 0; i < memberCount; i++)
        {
            var member = InfoProxyCrossRealm.GetGroupMember((uint)i, groupIndex);
            if (member is null)
                continue;

            var name = member->NameString.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            identities.Add((name, member->ContentId, member->IsPartyLeader));
        }

        var leader = identities.FirstOrDefault(x => x.IsLeader);
        var recruiter = ResolveTrackedRecruiter(leader.Name);

        if (string.IsNullOrWhiteSpace(recruiter))
        {
            foreach (var identity in identities)
            {
                recruiter = ResolveTrackedRecruiter(identity.Name);
                if (!string.IsNullOrWhiteSpace(recruiter))
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(recruiter))
            recruiter = leader.Name ?? string.Empty;

        var fallbackContentId = leader.ContentId;
        if (fallbackContentId == 0 && identities.Count > 0)
            fallbackContentId = identities[0].ContentId;

        var effectivePartyId = fallbackContentId != 0
            ? unchecked((long)fallbackContentId)
            : 1;

        UpdateCurrentPartyState(
            effectivePartyId,
            recruiter,
            memberCount,
            string.IsNullOrWhiteSpace(recruiter)
                ? $"Current cross-world party: {memberCount}/8 - detected; waiting for recruiter data"
                : $"Current cross-world party: {memberCount}/8 - detected");

        return true;
    }

    private string ResolveTrackedRecruiter(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        var fingerprint = activePfAlerts.Keys.FirstOrDefault(key =>
            FingerprintRecruiterNameEquals(key, playerName) &&
            FingerprintMatchesConfiguredDuty(key, Configuration.DutyNameContains));

        return string.IsNullOrWhiteSpace(fingerprint)
            ? string.Empty
            : ExtractFingerprintRecruiter(fingerprint);
    }

    private static bool FingerprintRecruiterNameEquals(string fingerprint, string recruiterOrName)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(recruiterOrName))
            return false;

        var fingerprintRecruiter = ExtractFingerprintRecruiter(fingerprint);
        if (string.IsNullOrWhiteSpace(fingerprintRecruiter))
            return false;

        return string.Equals(
            ExtractPlayerName(fingerprintRecruiter),
            ExtractPlayerName(recruiterOrName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFingerprintRecruiter(string fingerprint)
    {
        var first = fingerprint.IndexOf('\u001f');
        if (first < 0 || first + 1 >= fingerprint.Length)
            return string.Empty;

        var second = fingerprint.IndexOf('\u001f', first + 1);
        return (second >= 0
            ? fingerprint[(first + 1)..second]
            : fingerprint[(first + 1)..]).Trim();
    }

    private static string ExtractPlayerName(string identity)
    {
        var marker = identity.IndexOf(" @ ", StringComparison.Ordinal);
        return (marker >= 0 ? identity[..marker] : identity).Trim();
    }
}
''', encoding="utf-8")

Path("version.txt").write_text("0.7.11.0\n", encoding="utf-8")
