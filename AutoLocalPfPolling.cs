namespace PartyPing;

public sealed partial class Plugin
{
    private const int LocalPfAutoPollMinimumSeconds = 30;
    private const int LocalPfAutoPollMaximumSeconds = 60;

    // Local FFXIV PF and XIVPF both update the same tracked Discord-alert state.
    // Serialize those operations so the two background pollers cannot mutate the
    // shared dictionaries at the same time.
    private readonly SemaphoreSlim pfPollGate = new(1, 1);

    internal async Task CheckLocalPfCoordinatedAsync()
    {
        if (LocalPfCheckInProgress)
            return;

        LocalPfStatus = "Local PF: waiting for any current PF check to finish...";

        try
        {
            await pfPollGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await CheckLocalPfNowAsync().ConfigureAwait(false);
        }
        finally
        {
            pfPollGate.Release();
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
                    LocalPfStatus = $"Local PF: enter a Duty name contains value - next check attempt in {delaySeconds}s";
                }
                else
                {
                    LocalPfStatus = $"Local PF: next automatic check in {delaySeconds}s";
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);

                if (!Configuration.Enabled || string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
                    continue;

                await CheckLocalPfCoordinatedAsync().ConfigureAwait(false);
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
