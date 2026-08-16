using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PartyPing;

public sealed partial class Plugin
{
    private readonly object currentPartySync = new();
    private readonly SemaphoreSlim currentPartyHighlightGate = new(1, 1);

    private long currentPartyId;
    private string currentPartyRecruiter = string.Empty;
    private int currentPartySize;
    private string currentHighlightedFingerprint = string.Empty;

    private void TrackJoinedPartyCard()
    {
        if (PartyList.IsAlliance)
        {
            UpdateCurrentPartyState(0, string.Empty, 0, "Current party: alliance ignored");
            return;
        }

        var partySize = PartyList.Length;
        var partyId = PartyList.PartyId;

        if (partySize <= 1)
        {
            UpdateCurrentPartyState(0, string.Empty, 0, "Current party: not in a party");
            return;
        }

        var leaderIndex = PartyList.PartyLeaderIndex;
        var identities = new List<(string Identity, ulong ContentId, bool IsLeader)>();

        for (var i = 0; i < partySize; i++)
        {
            var member = PartyList[i];
            if (member is null)
                continue;

            var name = member.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var world = member.World.Value.Name.ToString();
            var identity = string.IsNullOrWhiteSpace(world)
                ? name
                : $"{name} @ {world}";
            identities.Add((identity, member.ContentId, (uint)i == leaderIndex));
        }

        var leaderIdentity = identities.FirstOrDefault(x => x.IsLeader);
        var recruiter = leaderIdentity.Identity ?? string.Empty;

        // Cross-world PF transitions can temporarily leave PartyLeaderIndex invalid.
        // In that case, identify the recruiter by matching every current party member
        // against the PF cards PartyPing is already tracking.
        if (string.IsNullOrWhiteSpace(recruiter) ||
            !activePfAlerts.Keys.Any(key => FingerprintRecruiterEquals(key, recruiter)))
        {
            var matchedIdentity = identities
                .Select(x => x.Identity)
                .FirstOrDefault(identity => activePfAlerts.Keys.Any(key =>
                    FingerprintRecruiterEquals(key, identity) &&
                    FingerprintMatchesConfiguredDuty(key, Configuration.DutyNameContains)));

            if (!string.IsNullOrWhiteSpace(matchedIdentity))
                recruiter = matchedIdentity;
        }

        var fallbackContentId = identities.FirstOrDefault(x => x.IsLeader).ContentId;
        if (fallbackContentId == 0 && identities.Count > 0)
            fallbackContentId = identities[0].ContentId;

        var effectivePartyId = partyId != 0
            ? partyId
            : fallbackContentId != 0
                ? unchecked((long)fallbackContentId)
                : 1;

        UpdateCurrentPartyState(
            effectivePartyId,
            recruiter,
            partySize,
            string.IsNullOrWhiteSpace(recruiter)
                ? $"Current party: {partySize}/8 - detected; waiting to identify PF recruiter"
                : $"Current party: {partySize}/8 - highlighting matching PF card");
    }

    private void UpdateCurrentPartyState(long partyId, string recruiter, int partySize, string status)
    {
        string oldRecruiter;
        int oldSize;
        long oldPartyId;

        lock (currentPartySync)
        {
            oldPartyId = currentPartyId;
            oldRecruiter = currentPartyRecruiter;
            oldSize = currentPartySize;

            currentPartyId = partyId;
            currentPartyRecruiter = recruiter;
            currentPartySize = partySize;
        }

        PartyFillStatus = status;

        if (oldPartyId == partyId &&
            oldSize == partySize &&
            string.Equals(oldRecruiter, recruiter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = SyncCurrentPartyHighlightAsync(cancellation.Token);
    }

    internal bool TryGetCurrentPartySize(string fingerprint, out int partySize)
    {
        lock (currentPartySync)
        {
            partySize = currentPartySize;
            return currentPartySize > 1 &&
                   !string.IsNullOrWhiteSpace(currentPartyRecruiter) &&
                   FingerprintRecruiterEquals(fingerprint, currentPartyRecruiter);
        }
    }

    internal bool IsCurrentPartyFingerprint(string fingerprint) =>
        TryGetCurrentPartySize(fingerprint, out _);

    internal bool IsInTrackedNormalParty()
    {
        lock (currentPartySync)
            return currentPartySize > 1;
    }

    private async Task SyncCurrentPartyHighlightAsync(CancellationToken cancellationToken)
    {
        await currentPartyHighlightGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string recruiter;
            int partySize;
            long partyId;

            lock (currentPartySync)
            {
                recruiter = currentPartyRecruiter;
                partySize = currentPartySize;
                partyId = currentPartyId;
            }

            var oldFingerprint = currentHighlightedFingerprint;
            var newFingerprint = string.IsNullOrWhiteSpace(recruiter)
                ? string.Empty
                : activePfAlerts.Keys.FirstOrDefault(
                    key => FingerprintRecruiterEquals(key, recruiter) &&
                           FingerprintMatchesConfiguredDuty(key, Configuration.DutyNameContains)) ?? string.Empty;

            var changed = false;

            if (!string.IsNullOrWhiteSpace(oldFingerprint) &&
                !string.Equals(oldFingerprint, newFingerprint, StringComparison.Ordinal) &&
                activePfAlerts.TryGetValue(oldFingerprint, out var oldAlert))
            {
                var normalMessage = SetCurrentPartyPresentation(oldAlert.LastContent, false, null);
                if (!string.Equals(normalMessage, oldAlert.LastContent, StringComparison.Ordinal))
                {
                    await smsSender.EditAsync(
                        Configuration,
                        oldAlert.MessageId,
                        normalMessage,
                        cancellationToken,
                        oldAlert.Transport).ConfigureAwait(false);
                    oldAlert.LastContent = normalMessage;
                    changed = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(newFingerprint) &&
                activePfAlerts.TryGetValue(newFingerprint, out var currentAlert))
            {
                var highlightedMessage = SetCurrentPartyPresentation(
                    currentAlert.LastContent,
                    true,
                    partySize);

                if (!string.Equals(highlightedMessage, currentAlert.LastContent, StringComparison.Ordinal))
                {
                    await smsSender.EditAsync(
                        Configuration,
                        currentAlert.MessageId,
                        highlightedMessage,
                        cancellationToken,
                        currentAlert.Transport).ConfigureAwait(false);
                    currentAlert.LastContent = highlightedMessage;
                    changed = true;
                }

                PartyFillStatus = $"Current party: {partySize}/8 - PF card highlighted";
            }
            else if (partySize > 1)
            {
                PartyFillStatus = $"Current party: {partySize}/8 - waiting for matching PF card";
            }
            else
            {
                PartyFillStatus = "Current party: not in a party";
            }

            currentHighlightedFingerprint = newFingerprint;

            if (changed)
                Configuration.Save();

            // Once the party ends, immediately refresh PF so a highlighted card that
            // closed or stopped matching while we were inside it can be cleaned up.
            if (partySize <= 1 &&
                Configuration.Enabled &&
                !string.IsNullOrWhiteSpace(Configuration.DutyNameContains) &&
                !LocalPfCheckInProgress)
            {
                await CheckLocalPfNowAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Current party highlight error: " + ex.Message;
            Log.Warning(ex, "PartyPing could not update the current-party PF highlight");
        }
        finally
        {
            currentPartyHighlightGate.Release();
        }
    }

    internal void ResetPartyTrackerMessageAfterManualClear()
    {
        currentHighlightedFingerprint = string.Empty;
    }

    internal static string SetCurrentPartyPresentation(
        string body,
        bool highlighted,
        int? livePartySize)
    {
        var lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !line.StartsWith("**Current party:**", StringComparison.Ordinal))
            .ToList();

        if (highlighted)
        {
            var sourceIndex = lines.FindIndex(
                line => line.StartsWith("**Source:**", StringComparison.Ordinal));
            lines.Insert(sourceIndex >= 0 ? sourceIndex + 1 : 1, "**Current party:** Yes");
        }

        if (livePartySize is > 0)
        {
            var partyIndex = lines.FindIndex(
                line => line.StartsWith("**Party:**", StringComparison.Ordinal));

            if (partyIndex >= 0)
            {
                var existing = lines[partyIndex]["**Party:**".Length..].Trim();
                var slash = existing.IndexOf('/');
                var total = slash >= 0 &&
                            int.TryParse(existing[(slash + 1)..].Trim(), out var parsedTotal) &&
                            parsedTotal > 0
                    ? parsedTotal
                    : 8;

                lines[partyIndex] = $"**Party:** {livePartySize.Value}/{total}";
            }
        }

        return string.Join('\n', lines).TrimEnd();
    }

    private static bool FingerprintRecruiterEquals(string fingerprint, string recruiter)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(recruiter))
            return false;

        var first = fingerprint.IndexOf('\u001f');
        if (first < 0 || first + 1 >= fingerprint.Length)
            return false;

        var second = fingerprint.IndexOf('\u001f', first + 1);
        var fingerprintRecruiter = second >= 0
            ? fingerprint[(first + 1)..second]
            : fingerprint[(first + 1)..];

        return string.Equals(
            fingerprintRecruiter.Trim(),
            recruiter.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal async Task RemoveLegacyStandalonePartyTrackerCardsAsync(CancellationToken cancellationToken)
    {
        if (!DiscordNotifier.HasBotTransport(Configuration) ||
            !ulong.TryParse(Configuration.DiscordChannelId?.Trim(), out var channelId) ||
            channelId == 0 ||
            string.IsNullOrWhiteSpace(Configuration.DiscordBotToken))
        {
            return;
        }

        var activeIds = activePfAlerts.Values
            .Select(alert => alert.MessageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = DiscordMessageStore.Snapshot(Configuration)
            .Where(id => !activeIds.Contains(id))
            .ToArray();

        if (candidates.Length == 0)
            return;

        using var http = new HttpClient();

        foreach (var messageId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = new Uri(
                $"https://discord.com/api/v10/channels/{channelId}/messages/{Uri.EscapeDataString(messageId)}");

            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.Authorization = new AuthenticationHeaderValue(
                "Bot",
                Configuration.DiscordBotToken.Trim());

            using var response = await http.SendAsync(get, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                DiscordMessageStore.Remove(Configuration, messageId);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                continue;

            var jsonText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(jsonText);

            var isLegacyTracker =
                json.RootElement.TryGetProperty("embeds", out var embeds) &&
                embeds.ValueKind == JsonValueKind.Array &&
                embeds.EnumerateArray().Any(embed =>
                    embed.TryGetProperty("title", out var title) &&
                    string.Equals(title.GetString(), "Party Tracker", StringComparison.Ordinal));

            if (!isLegacyTracker)
                continue;

            using var delete = new HttpRequestMessage(HttpMethod.Delete, url);
            delete.Headers.Authorization = new AuthenticationHeaderValue(
                "Bot",
                Configuration.DiscordBotToken.Trim());
            using var deleteResponse = await http.SendAsync(delete, cancellationToken).ConfigureAwait(false);

            if (deleteResponse.IsSuccessStatusCode || deleteResponse.StatusCode == HttpStatusCode.NotFound)
                DiscordMessageStore.Remove(Configuration, messageId);
        }
    }
}
