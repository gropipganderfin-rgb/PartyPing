namespace PartyPing;

public sealed partial class Plugin
{
    private const int LocalPfAutoPollMinimumSeconds = 30;
    private const int LocalPfAutoPollMaximumSeconds = 60;

    private bool localPfAutoPollingStarted;

    internal void StartLocalPfAutoPolling()
    {
        if (localPfAutoPollingStarted)
            return;

        localPfAutoPollingStarted = true;
        _ = RunLocalPfLoopAsync(cancellation.Token);
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
                    LocalPfStatus = $"Local PF: enter a Duty name contains value - next check attempt in {delaySeconds}s";
                }
                else
                {
                    LocalPfStatus = $"Local PF: next automatic check in {delaySeconds}s";
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);

                if (!Configuration.Enabled || string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
                    continue;

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
