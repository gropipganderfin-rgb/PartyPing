namespace PartyPing;

public sealed partial class Plugin
{
    private const int LocalPfAutoPollMinimumSeconds = 30;
    private const int LocalPfAutoPollMaximumSeconds = 60;

    private bool localPfAutoPollingStarted;
    private bool joinedPartyTrackerStarted;

    internal void StartLocalPfAutoPolling()
    {
        if (localPfAutoPollingStarted)
            return;

        localPfAutoPollingStarted = true;
        StartJoinedPartyTrackerLoop();
        _ = RunLocalPfLoopAsync(cancellation.Token);
    }

    private void StartJoinedPartyTrackerLoop()
    {
        if (joinedPartyTrackerStarted)
            return;

        joinedPartyTrackerStarted = true;

        // The old one-shot 8/8 notification is superseded by the live tracker card.
        Configuration.NotifyWhenPartyFull = false;
        _ = RunJoinedPartyTrackerLoopAsync(cancellation.Token);
    }

    private async Task RunJoinedPartyTrackerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Framework.Run(() =>
                {
                    Configuration.NotifyWhenPartyFull = false;
                    TrackJoinedPartyCard();
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Party tracker stopped: " + ex.Message;
            Log.Error(ex, "PartyPing joined-party tracker stopped unexpectedly");
        }
    }

    private async Task RunLocalPfLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delaySeconds = Random.Shared.Next(
                    LocalPfAutoPollMinimumSeconds,
                    LocalPfAutoPollMaximumSeconds + 1);

                if (!Configuration.Enabled)
                {
                    LocalPfStatus = $"Local PF: alerts disabled - next automatic check attempt in {delaySeconds}s";
                }
                else if (IsInTrackedNormalParty())
                {
                    LocalPfStatus = $"Local PF: paused while joined party tracker is active ({PartyList.Length}/8)";
                }
                else if (string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
                {
                    LocalPfStatus = $"Local PF: duty filter blank - tracked PF posts will be cleaned on the next cycle in {delaySeconds}s";
                }
                else
                {
                    LocalPfStatus = $"Local PF: next automatic check in {delaySeconds}s";
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);

                if (!Configuration.Enabled || IsInTrackedNormalParty())
                    continue;

                // CheckLocalPfNowAsync also handles a blank duty filter by deleting
                // tracked PF posts, so do not skip the call solely because the filter
                // has been cleared.
                await CheckLocalPfNowAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LocalPfStatus = "Local PF automatic polling stopped: " + ex.Message;
            Log.Error(ex, "PartyPing automatic local Party Finder polling stopped unexpectedly");
        }
    }
}
