using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PartyPing;

public sealed partial class Plugin
{
    private const byte LocalPfHighEndDutyCategory = 6;
    private const int LocalPfMaxListingsPerPage = 50;
    private static readonly TimeSpan LocalPfReceiveWindow = TimeSpan.FromMilliseconds(2500);

    private static readonly HashSet<string> LocalPfNorthAmericanWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aether
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",

        // Crystal
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",

        // Dynamis
        "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph",

        // Primal
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
    };

    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;

    internal string LocalPfStatus { get; private set; } = "Local PF: ready for automatic polling";
    internal bool LocalPfCheckInProgress { get; private set; }

    private readonly object localPfSync = new();
    private readonly Dictionary<ulong, LocalPfListingSnapshot> localPfReceived = new();

    private sealed record LocalPfListingSnapshot(
        ulong ListingId,
        string Duty,
        string Description,
        int FilledSlots,
        int TotalSlots,
        string Recruiter,
        string World,
        string Fingerprint,
        JobFlags[] AcceptedJobs);

    internal async Task CheckLocalPfNowAsync()
    {
        if (LocalPfCheckInProgress)
            return;

        if (!Configuration.Enabled)
        {
            LocalPfStatus = "Local PF: enable Discord alerts first";
            return;
        }

        if (string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
        {
            LocalPfStatus = "Local PF: enter a Duty name contains value first";
            return;
        }

        if (!CanRequestLocalPf())
        {
            LocalPfStatus = "Local PF: unavailable while inside a duty, zoning, or in a cutscene";
            return;
        }

        LocalPfCheckInProgress = true;
        lock (localPfSync)
            localPfReceived.Clear();

        PartyFinderGui.ReceiveListing += OnLocalPfListingReceived;

        try
        {
            LocalPfStatus = "Local PF: requesting High-End Duty listings from FFXIV...";

            if (!RequestLocalPfHighEndListings())
            {
                LocalPfStatus = "Local PF: FFXIV rejected the Party Finder refresh request";
                return;
            }

            await Task.Delay(LocalPfReceiveWindow, cancellation.Token).ConfigureAwait(false);

            LocalPfListingSnapshot[] listings;
            lock (localPfSync)
                listings = localPfReceived.Values.ToArray();

            await ProcessLocalPfResultsAsync(listings, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LocalPfStatus = "Local PF check failed: " + ex.Message;
            Log.Warning(ex, "PartyPing local Party Finder check failed");
        }
        finally
        {
            PartyFinderGui.ReceiveListing -= OnLocalPfListingReceived;
            LocalPfCheckInProgress = false;
        }
    }

    private static bool CanRequestLocalPf() =>
        !Condition[ConditionFlag.BoundByDuty] &&
        !Condition[ConditionFlag.BoundByDuty56] &&
        !Condition[ConditionFlag.BoundByDuty95] &&
        !Condition[ConditionFlag.BetweenAreas] &&
        !Condition[ConditionFlag.BetweenAreas51] &&
        !Condition[ConditionFlag.WatchingCutscene] &&
        !Condition[ConditionFlag.WatchingCutscene78] &&
        !Condition[ConditionFlag.OccupiedInCutSceneEvent];

    private static unsafe bool RequestLocalPfHighEndListings()
    {
        var agent = AgentLookingForGroup.Instance();
        return agent != null && agent->RequestCategoryListings(LocalPfHighEndDutyCategory);
    }

    private void OnLocalPfListingReceived(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (!LocalPfCheckInProgress)
            return;

        try
        {
            var duty = listing.Duty.Value.Name.ToString();
            var homeWorld = listing.HomeWorld.Value.Name.ToString();
            var currentWorld = listing.CurrentWorld.Value.Name.ToString();
            var recruiterName = listing.Name.TextValue.Trim();
            var recruiter = $"{recruiterName} @ {homeWorld}";
            var description = listing.Description.TextValue;
            var fingerprint = $"{duty}\u001f{recruiter}\u001f{currentWorld}";
            var acceptedJobs = listing.Slots
                .SelectMany(slot => slot.Accepting)
                .Distinct()
                .ToArray();

            var snapshot = new LocalPfListingSnapshot(
                listing.Id,
                duty,
                description,
                listing.SlotsFilled,
                listing.SlotsAvailable,
                recruiter,
                currentWorld,
                fingerprint,
                acceptedJobs);

            lock (localPfSync)
                localPfReceived[listing.Id] = snapshot;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PartyPing could not parse a local PF listing");
        }
    }

    private async Task ProcessLocalPfResultsAsync(
        IReadOnlyCollection<LocalPfListingSnapshot> listings,
        CancellationToken cancellationToken)
    {
        var config = Configuration;
        var seenFingerprints = listings.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var matchingCount = 0;
        var newCount = 0;
        var updatedCount = 0;
        var removedCount = 0;

        foreach (var listing in listings)
        {
            if (!listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var matches = MatchesLocalPfListing(listing, config);

            if (activePfAlerts.TryGetValue(listing.Fingerprint, out var activeAlert))
            {
                if (!matches)
                {
                    if (await DeleteLocalPfAlertAsync(listing.Fingerprint, activeAlert, config, cancellationToken).ConfigureAwait(false))
                        removedCount++;
                    continue;
                }

                matchingCount++;
                var message = BuildLocalPfMessage(listing, config.RequiredRole);
                if (!string.Equals(message, activeAlert.LastContent, StringComparison.Ordinal))
                {
                    await smsSender.EditAsync(config, activeAlert.MessageId, message, cancellationToken).ConfigureAwait(false);
                    activePfAlerts[listing.Fingerprint] = activeAlert with { LastContent = message };
                    updatedCount++;
                }

                continue;
            }

            if (!matches)
                continue;

            matchingCount++;
            var newMessage = BuildLocalPfMessage(listing, config.RequiredRole);
            var result = await smsSender.SendTrackedAsync(config, newMessage, cancellationToken).ConfigureAwait(false);
            activePfAlerts[listing.Fingerprint] = new ActivePfAlert(result.MessageId, newMessage);
            newCount++;
        }

        // A native PF category response is capped at 50 listings. If we received
        // fewer than 50, the High-End Duty page is complete, so a tracked listing
        // for this configured duty that is missing can safely be treated as gone.
        // With exactly 50 results the response may be truncated, so never prune
        // unseen listings in that case. Also avoid pruning on a zero-result scan,
        // because an unavailable game state can produce no ReceiveListing events.
        var completePage = listings.Count is > 0 and < LocalPfMaxListingsPerPage;
        if (completePage)
        {
            foreach (var pair in activePfAlerts.ToArray())
            {
                if (seenFingerprints.Contains(pair.Key) || !FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))
                    continue;

                if (await DeleteLocalPfAlertAsync(pair.Key, pair.Value, config, cancellationToken).ConfigureAwait(false))
                    removedCount++;
            }
        }

        var completeness = listings.Count == 0
            ? "no response listings; existing posts were not pruned"
            : completePage
                ? "complete page"
                : "50-listing page; missing posts were not pruned";

        LocalPfStatus =
            $"Local PF: checked {DateTime.Now:t} - {listings.Count} High-End listings, " +
            $"{matchingCount} matched, {newCount} new, {updatedCount} updated, {removedCount} removed ({completeness})";
    }

    private static bool MatchesLocalPfListing(LocalPfListingSnapshot listing, Configuration config)
    {
        if (!LocalPfNorthAmericanWorlds.Contains(listing.World))
            return false;

        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return false;

        if (config.RequiredRole != RoleFilter.AnyRole &&
            !listing.AcceptedJobs.Any(job => config.RequiredRole.Matches(job)))
            return false;

        return MatchesTextRules(listing.Duty, listing.Description, config);
    }

    private async Task<bool> DeleteLocalPfAlertAsync(
        string fingerprint,
        ActivePfAlert activeAlert,
        Configuration config,
        CancellationToken cancellationToken)
    {
        try
        {
            await smsSender.DeleteAsync(config, activeAlert.MessageId, cancellationToken).ConfigureAwait(false);
            activePfAlerts.Remove(fingerprint);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not remove Discord alert after local PF check for {Fingerprint}", fingerprint);
            return false;
        }
    }

    private static bool FingerprintMatchesConfiguredDuty(string fingerprint, string dutyNameContains)
    {
        if (string.IsNullOrWhiteSpace(dutyNameContains))
            return false;

        var separator = fingerprint.IndexOf('\u001f');
        var duty = separator >= 0 ? fingerprint[..separator] : fingerprint;
        return duty.Contains(dutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLocalPfMessage(LocalPfListingSnapshot listing, RoleFilter role)
    {
        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        var cleaned = CleanDescription(listing.Description);
        var roleText = role == RoleFilter.AnyRole
            ? "Any role"
            : $"{role.DisplayName()} - open (verified locally)";

        return
            $"## {listing.Duty}\n" +
            "**Source:** Local FFXIV Party Finder\n" +
            $"**Party:** {listing.FilledSlots}/{listing.TotalSlots}\n" +
            $"**Open slots:** {openSlots}\n" +
            $"**Role filter:** {roleText}\n" +
            $"**World:** {listing.World}\n" +
            $"**Recruiter:** {listing.Recruiter}\n\n" +
            "### Party Finder Description\n" +
            $"> {cleaned}";
    }
}
