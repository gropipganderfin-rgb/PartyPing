using FFXIVClientStructs.FFXIV.Client.UI.Info;

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
        // Avoid calling InfoProxyCrossRealm member functions from the tracker.
        // Reading the already-populated proxy fields removes a native-call crash
        // surface from a loop that runs for the whole session.
        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy is null || !proxy->IsInCrossRealmParty || proxy->IsInAllianceRaid)
            return false;

        var availableGroups = proxy->CrossRealmGroups.Length;
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
        {
            var member = group.GroupMembers[i];
            if (member.ContentId == 0)
                continue;

            var name = member.NameString.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            identities.Add((name, member.ContentId, member.IsPartyLeader));
        }

        var memberCount = reportedMemberCount > 0 ? reportedMemberCount : identities.Count;
        if (memberCount <= 1)
            return false;

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
