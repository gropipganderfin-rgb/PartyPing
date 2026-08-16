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

        // The old one-shot 8/8 notification and standalone tracker card are
        // superseded by highlighting the matching PF card in place.
        Configuration.NotifyWhenPartyFull = false;
        _ = RemoveLegacyStandalonePartyTrackerCardsAsync(cancellation.Token);
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
                    TrackJoinedPartyCardRobust();
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Current party highlight stopped: " + ex.Message;
            Log.Error(ex, "PartyPing current-party highlight stopped unexpectedly");
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
                else if (string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
                {
                    LocalPfStatus = $"Local PF: duty filter blank - tracked PF posts will be cleaned on the next cycle in {delaySeconds}s";
                }
                else if (IsInTrackedNormalParty())
                {
                    LocalPfStatus = $"Local PF: next automatic check in {delaySeconds}s - current party remains highlighted";
                }
                else
                {
                    LocalPfStatus = $"Local PF: next automatic check in {delaySeconds}s";
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);

                if (!Configuration.Enabled)
                    continue;

                // Continue polling while in a party. The current party's card is kept
                // highlighted, while every other matching PF card continues to update.
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
