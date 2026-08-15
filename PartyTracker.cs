namespace PartyPing;

public sealed partial class Plugin
{
    private readonly SemaphoreSlim partyTrackerGate = new(1, 1);
    private long partyTrackerPartyId;
    private string partyTrackerRosterSignature = string.Empty;
    private string? partyTrackerMessageId;
    private string partyTrackerTransport = string.Empty;

    private sealed record PartyTrackerSnapshot(
        long PartyId,
        int PartySize,
        string RosterSignature,
        string Message);

    private void TrackJoinedPartyCard()
    {
        if (PartyList.IsAlliance)
        {
            PartyFillStatus = "Party tracker: alliance ignored";
            QueuePartyTrackerEnd();
            return;
        }

        var partySize = PartyList.Length;
        var partyId = PartyList.PartyId;

        if (partySize <= 1 || partyId == 0)
        {
            PartyFillStatus = "Party tracker: not in a party";
            QueuePartyTrackerEnd();
            return;
        }

        PartyFillStatus = $"Party tracker: live {partySize}/8";

        if (!Configuration.Enabled)
            return;

        var snapshot = CapturePartyTrackerSnapshot(partyId, partySize);
        var newParty = partyTrackerPartyId != partyId;
        var rosterChanged = !string.Equals(
            partyTrackerRosterSignature,
            snapshot.RosterSignature,
            StringComparison.Ordinal);

        if (!newParty && !rosterChanged && !string.IsNullOrWhiteSpace(partyTrackerMessageId))
            return;

        partyTrackerPartyId = partyId;
        partyTrackerRosterSignature = snapshot.RosterSignature;
        _ = SyncPartyTrackerAsync(snapshot, newParty, cancellation.Token);
    }

    private PartyTrackerSnapshot CapturePartyTrackerSnapshot(long partyId, int partySize)
    {
        var leaderIndex = PartyList.PartyLeaderIndex;
        var memberLines = new List<string>();
        var signatureParts = new List<string>();

        for (var i = 0; i < PartyList.Length; i++)
        {
            var member = PartyList[i];
            if (member is null)
                continue;

            var name = member.Name.TextValue.Trim();
            var world = member.World.Value.Name.ToString();
            var display = string.IsNullOrWhiteSpace(world)
                ? name
                : $"{name} @ {world}";
            var leader = (uint)i == leaderIndex;

            memberLines.Add(leader ? $"👑 {display}" : $"• {display}");
            signatureParts.Add($"{member.ContentId}:{name}:{world}:{leader}");
        }

        var members = memberLines.Count == 0
            ? "Party roster unavailable"
            : string.Join('\n', memberLines);
        var signature = $"{partyId}|{partySize}|{string.Join('|', signatureParts)}";
        var message =
            "## Party Tracker\n" +
            $"**Party:** {partySize}/8\n" +
            "### Members\n" +
            members;

        return new PartyTrackerSnapshot(partyId, partySize, signature, message);
    }

    private async Task SyncPartyTrackerAsync(
        PartyTrackerSnapshot snapshot,
        bool newParty,
        CancellationToken cancellationToken)
    {
        await partyTrackerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (newParty)
            {
                PartyFillStatus = $"Party tracker: joined {snapshot.PartySize}/8 - clearing PF cards...";

                await DiscordClearer.ClearAsync(Configuration, cancellationToken).ConfigureAwait(false);
                activePfAlerts.Clear();
                Configuration.Save();

                partyTrackerMessageId = null;
                partyTrackerTransport = string.Empty;
            }

            if (partyTrackerPartyId != snapshot.PartyId)
                return;

            if (string.IsNullOrWhiteSpace(partyTrackerMessageId))
            {
                var result = await smsSender.SendTrackedAsync(
                    Configuration,
                    snapshot.Message,
                    cancellationToken).ConfigureAwait(false);

                partyTrackerMessageId = result.MessageId;
                partyTrackerTransport = result.Transport;
                PartyFillStatus = $"Party tracker: live {snapshot.PartySize}/8";
                return;
            }

            await smsSender.EditAsync(
                Configuration,
                partyTrackerMessageId,
                snapshot.Message,
                cancellationToken,
                partyTrackerTransport).ConfigureAwait(false);

            PartyFillStatus = $"Party tracker: live {snapshot.PartySize}/8 - updated";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Party tracker error: " + ex.Message;
            Log.Warning(ex, "PartyPing joined-party tracker update failed");
        }
        finally
        {
            partyTrackerGate.Release();
        }
    }

    private void QueuePartyTrackerEnd()
    {
        if (partyTrackerPartyId == 0 && string.IsNullOrWhiteSpace(partyTrackerMessageId))
            return;

        partyTrackerPartyId = 0;
        partyTrackerRosterSignature = string.Empty;
        _ = EndPartyTrackerAsync(cancellation.Token);
    }

    private async Task EndPartyTrackerAsync(CancellationToken cancellationToken)
    {
        await partyTrackerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrWhiteSpace(partyTrackerMessageId))
            {
                await smsSender.DeleteAsync(
                    Configuration,
                    partyTrackerMessageId,
                    cancellationToken,
                    partyTrackerTransport).ConfigureAwait(false);
            }

            partyTrackerMessageId = null;
            partyTrackerTransport = string.Empty;
            PartyFillStatus = "Party tracker: not in a party";

            if (Configuration.Enabled && !string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
                await CheckLocalPfNowAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Party tracker cleanup failed: " + ex.Message;
            Log.Warning(ex, "PartyPing could not remove joined-party tracker card");
        }
        finally
        {
            partyTrackerGate.Release();
        }
    }

    private bool IsInTrackedNormalParty() =>
        !PartyList.IsAlliance && PartyList.Length > 1 && PartyList.PartyId != 0;
}
