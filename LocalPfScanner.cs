using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PartyPing;

public sealed partial class Plugin
{
    private const byte LocalPfHighEndDutyCategory = 6;
    private const int LocalPfMaxListingsPerPage = 50;
    private const int SaturatedPageMissesBeforeRemoval = 3;
    private static readonly TimeSpan LocalPfMaximumReceiveWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LocalPfMinimumReceiveWindow = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan LocalPfQuietPeriod = TimeSpan.FromMilliseconds(1200);

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
    private DateTime localPfLastReceiveUtc = DateTime.MinValue;

    private sealed record LocalPfListingSnapshot(
        ulong ListingId,
        string Duty,
        string Description,
        int FilledSlots,
        int TotalSlots,
        string Recruiter,
        string World,
        string Fingerprint,
        JobFlags[] AcceptedJobs,
        ushort MinimumItemLevel,
        long ExpiresAtUnixSeconds);

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
            if (activePfAlerts.Count > 0)
                await RemoveAllLocalPfAlertsAsync(cancellation.Token).ConfigureAwait(false);

            LocalPfStatus = "Local PF: duty filter is blank; tracked PF posts removed";
            return;
        }

        if (!CanRequestLocalPf())
        {
            LocalPfStatus = "Local PF: unavailable while inside a duty, zoning, or in a cutscene";
            return;
        }

        LocalPfCheckInProgress = true;
        lock (localPfSync)
        {
            localPfReceived.Clear();
            localPfLastReceiveUtc = DateTime.MinValue;
        }

        PartyFinderGui.ReceiveListing += OnLocalPfListingReceived;

        try
        {
            LocalPfStatus = "Local PF: requesting High-End Duty listings from FFXIV...";

            // Native FFXIV agent calls must run on the framework/game thread.
            var requestAccepted = await Framework.Run(
                RequestLocalPfHighEndListings,
                cancellation.Token).ConfigureAwait(false);

            if (!requestAccepted)
            {
                LocalPfStatus = "Local PF: FFXIV rejected the Party Finder refresh request";
                return;
            }

            await WaitForLocalPfResponseAsync(cancellation.Token).ConfigureAwait(false);

            LocalPfListingSnapshot[] listings;
            lock (localPfSync)
                listings = localPfReceived.Values.ToArray();

            await ProcessLocalPfResultsAsync(listings, cancellation.Token).ConfigureAwait(false);

            if (IsInTrackedNormalParty())
                await SyncCurrentPartyHighlightAsync(cancellation.Token).ConfigureAwait(false);
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
        if (agent is null)
            return false;

        // Force a data-center, normal-party query so a World/Private PF tab
        // cannot silently hide valid prog listings from PartyPing.
        agent->SearchAreaTab = 0;
        agent->GroupTypeTab = 0;

        return agent->RequestCategoryListings(LocalPfHighEndDutyCategory);
    }

    private async Task<bool> OpenLocalPfListingFromDiscordAsync(ulong listingId, CancellationToken cancellationToken)
    {
        if (listingId == 0 || !CanRequestLocalPf())
            return false;

        try
        {
            var openedPfWindow = await Framework.Run(() =>
            {
                unsafe
                {
                    var agent = AgentLookingForGroup.Instance();
                    if (agent == null)
                        return false;

                    var addon = GameGui.GetAddonByName<AtkUnitBase>("LookingForGroup", 1);
                    if (addon != null && addon->IsVisible)
                        return false;

                    agent->Show();
                    return true;
                }
            }, cancellationToken).ConfigureAwait(false);

            if (openedPfWindow)
                await Framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);

            return await Framework.Run(() =>
            {
                unsafe
                {
                    var agent = AgentLookingForGroup.Instance();
                    return agent != null && agent->OpenListing(listingId);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PartyPing could not open PF listing {ListingId} from a Discord button", listingId);
            return false;
        }
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
            // Dalamud exposes JobsPresent as the jobs already in the party, not a
            // guaranteed seat-by-seat occupancy map. Party Finder slot restrictions are
            // ordered with the filled seats first, followed by the currently open seats.
            // Inspect exactly SlotsAvailable-SlotsFilled open seats and ignore padding.
            var openSlotCount = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
            var acceptedJobs = listing.Slots
                .Skip(listing.SlotsFilled)
                .Take(openSlotCount)
                .SelectMany(slot => slot.Accepting)
                .Distinct()
                .ToArray();
            var expiresAt = DateTimeOffset.UtcNow
                .AddSeconds(listing.SecondsRemaining)
                .ToUnixTimeSeconds();

            var snapshot = new LocalPfListingSnapshot(
                listing.Id,
                duty,
                description,
                listing.SlotsFilled,
                listing.SlotsAvailable,
                recruiter,
                currentWorld,
                fingerprint,
                acceptedJobs,
                listing.MinimumItemLevel,
                expiresAt);

            lock (localPfSync)
            {
                localPfReceived[listing.Id] = snapshot;
                localPfLastReceiveUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "PartyPing could not parse a local PF listing");
        }
    }

    private async Task WaitForLocalPfResponseAsync(CancellationToken cancellationToken)
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

            var elapsed = DateTime.UtcNow - startedUtc;
            if (count > 0 &&
                elapsed >= LocalPfMinimumReceiveWindow &&
                DateTime.UtcNow - lastReceiveUtc >= LocalPfQuietPeriod)
            {
                return;
            }
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
        var rejectionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stateChanged = false;

        void CountRejection(string reason)
        {
            rejectionCounts.TryGetValue(reason, out var count);
            rejectionCounts[reason] = count + 1;
        }

        // If the configured duty changes, old-duty posts are no longer valid and
        // should disappear immediately instead of waiting to show up in a scan.
        foreach (var pair in activePfAlerts.ToArray())
        {
            if (FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))
                continue;

            if (await DeleteLocalPfAlertAsync(pair.Key, pair.Value, config, cancellationToken).ConfigureAwait(false))
                removedCount++;
        }

        foreach (var listing in listings)
        {
            if (!listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                CountRejection("duty");
                continue;
            }

            var rejectionReason = GetLocalPfRejectionReason(listing, config);
            var matches = rejectionReason is null;
            if (rejectionReason is not null)
                CountRejection(rejectionReason);

            var isCurrentParty = TryGetCurrentPartySize(listing.Fingerprint, out _);
            var shouldKeep = matches || isCurrentParty;

            if (activePfAlerts.TryGetValue(listing.Fingerprint, out var activeAlert))
            {
                if (!shouldKeep)
                {
                    if (await DeleteLocalPfAlertAsync(listing.Fingerprint, activeAlert, config, cancellationToken).ConfigureAwait(false))
                        removedCount++;
                    continue;
                }

                matchingCount++;
                activeAlert.MissedPolls = 0;
                activeAlert.ExpiresAtUnixSeconds = listing.ExpiresAtUnixSeconds;
                stateChanged = true;

                var message = BuildLocalPfMessage(listing, config.RequiredRole);

                // Existing cards created by the old incoming webhook cannot gain a
                // real interactive button. Once bot mode is configured, replace them
                // with bot-owned cards automatically and keep tracking the new ID.
                if (DiscordNotifier.HasBotTransport(config) &&
                    !string.Equals(activeAlert.Transport, "bot", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await smsSender.DeleteAsync(
                            config,
                            activeAlert.MessageId,
                            cancellationToken,
                            activeAlert.Transport).ConfigureAwait(false);

                        if (!string.IsNullOrWhiteSpace(activeAlert.SeparatorMessageId))
                        {
                            await smsSender.DeleteAsync(
                                config,
                                activeAlert.SeparatorMessageId,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var replacement = await smsSender.SendTrackedAsync(config, message, cancellationToken).ConfigureAwait(false);
                        activeAlert.MessageId = replacement.MessageId;
                        activeAlert.SeparatorMessageId = replacement.SeparatorMessageId;
                        activeAlert.LastContent = message;
                        activeAlert.Transport = replacement.Transport;
                        updatedCount++;
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warning(ex, "PartyPing could not migrate a PF Discord card to bot transport");
                    }
                }

                if (!string.Equals(message, activeAlert.LastContent, StringComparison.Ordinal))
                {
                    await smsSender.EditAsync(
                        config,
                        activeAlert.MessageId,
                        message,
                        cancellationToken,
                        activeAlert.Transport).ConfigureAwait(false);
                    activeAlert.LastContent = message;
                    updatedCount++;
                }

                continue;
            }

            if (!shouldKeep)
                continue;

            matchingCount++;
            var newMessage = BuildLocalPfMessage(listing, config.RequiredRole);
            var result = await smsSender.SendTrackedAsync(config, newMessage, cancellationToken).ConfigureAwait(false);
            activePfAlerts[listing.Fingerprint] = new PersistedPfAlert
            {
                MessageId = result.MessageId,
                SeparatorMessageId = result.SeparatorMessageId,
                LastContent = newMessage,
                MissedPolls = 0,
                ExpiresAtUnixSeconds = listing.ExpiresAtUnixSeconds,
                Transport = result.Transport,
            };
            stateChanged = true;
            newCount++;
        }

        // The native PF response contains at most 50 entries. Below 50, absence is
        // authoritative and we delete immediately. At 50, absence may only mean the
        // listing was pushed beyond the visible result window, so require three
        // consecutive misses before deleting. This prevents stale Discord posts from
        // surviving forever while still avoiding deletion after one saturated scan.
        var completePage = listings.Count is > 0 and < LocalPfMaxListingsPerPage;
        var saturatedPage = listings.Count >= LocalPfMaxListingsPerPage;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (listings.Count > 0)
        {
            foreach (var pair in activePfAlerts.ToArray())
            {
                if (seenFingerprints.Contains(pair.Key))
                    continue;

                if (IsCurrentPartyFingerprint(pair.Key))
                    continue;

                if (!FingerprintMatchesConfiguredDuty(pair.Key, config.DutyNameContains))
                    continue;

                var shouldRemove = completePage;

                if (saturatedPage)
                {
                    pair.Value.MissedPolls++;
                    stateChanged = true;

                    if (pair.Value.MissedPolls >= SaturatedPageMissesBeforeRemoval ||
                        (pair.Value.ExpiresAtUnixSeconds > 0 && pair.Value.ExpiresAtUnixSeconds <= nowUnix))
                    {
                        shouldRemove = true;
                    }
                }

                if (!shouldRemove)
                    continue;

                if (await DeleteLocalPfAlertAsync(pair.Key, pair.Value, config, cancellationToken).ConfigureAwait(false))
                    removedCount++;
            }
        }

        if (stateChanged)
            config.Save();

        var completeness = listings.Count == 0
            ? "no response listings; existing posts kept"
            : completePage
                ? "complete page; missing posts removed immediately"
                : $"50-listing page; unseen posts removed after {SaturatedPageMissesBeforeRemoval} consecutive misses";

        var rejectionSummary = rejectionCounts.Count == 0
            ? "none"
            : string.Join(", ", rejectionCounts
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} {pair.Value}"));

        LocalPfStatus =
            $"Local PF: checked {DateTime.Now:t} - {listings.Count} High-End listings, " +
            $"{matchingCount} matched, {newCount} new, {updatedCount} updated, {removedCount} removed; " +
            $"rejected: {rejectionSummary} ({completeness})";
    }

    private static bool MatchesLocalPfListing(LocalPfListingSnapshot listing, Configuration config) =>
        GetLocalPfRejectionReason(listing, config) is null;

    private static string? GetLocalPfRejectionReason(LocalPfListingSnapshot listing, Configuration config)
    {
        if (listing.MinimumItemLevel == 999)
            return "iLvl 999";

        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return "open slots";

        if (config.RequiredRole != RoleFilter.AnyRole)
        {
            var roleOpenFromSlots = listing.AcceptedJobs.Any(job => config.RequiredRole.Matches(job));
            var roleOpenFromDescription = DescriptionExplicitlyRequestsRole(listing.Description, config.RequiredRole);
            if (!roleOpenFromSlots && !roleOpenFromDescription)
                return "role";
        }

        if (!MatchesTextRules(listing.Duty, listing.Description, config))
            return "keywords";

        return null;
    }

    private static bool DescriptionExplicitlyRequestsRole(string description, RoleFilter role)
    {
        if (string.IsNullOrWhiteSpace(description) || role == RoleFilter.AnyRole)
            return false;

        var text = NormalizeMatchText(description).ToLowerInvariant();

        static bool HasAny(string value, params string[] phrases) =>
            phrases.Any(phrase => value.Contains(phrase, StringComparison.Ordinal));

        return role switch
        {
            RoleFilter.Tank => HasAny(text,
                "need mt", "need ot", "need tank", "need a tank", "lf mt", "lf ot", "lf tank",
                "mt needed", "ot needed", "tank needed", "tank open"),
            RoleFilter.Healer => HasAny(text,
                "need h1", "need h2", "need healer", "need a healer", "lf h1", "lf h2", "lf healer",
                "healer needed", "healer open"),
            RoleFilter.Melee => HasAny(text,
                "need melee", "lf melee", "melee needed", "melee open"),
            RoleFilter.PhysicalRanged => HasAny(text,
                "need phys ranged", "need physical ranged", "need prange", "lf phys ranged", "lf prange",
                "physical ranged needed", "prange needed"),
            RoleFilter.Caster => HasAny(text,
                "need caster", "need magic ranged", "need magical ranged", "lf caster", "caster needed", "caster open"),
            RoleFilter.AnyDps => HasAny(text,
                "need dps", "need a dps", "lf dps", "dps needed", "dps open", "need melee", "need caster",
                "need phys ranged", "need physical ranged", "need prange"),
            _ => false,
        };
    }

    private async Task<bool> DeleteLocalPfAlertAsync(
        string fingerprint,
        PersistedPfAlert activeAlert,
        Configuration config,
        CancellationToken cancellationToken)
    {
        try
        {
            await smsSender.DeleteAsync(
                config,
                activeAlert.MessageId,
                cancellationToken,
                activeAlert.Transport).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(activeAlert.SeparatorMessageId))
                await smsSender.DeleteAsync(config, activeAlert.SeparatorMessageId, cancellationToken).ConfigureAwait(false);

            activePfAlerts.Remove(fingerprint);
            config.Save();
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

    private async Task RemoveAllLocalPfAlertsAsync(CancellationToken cancellationToken)
    {
        foreach (var pair in activePfAlerts.ToArray())
            await DeleteLocalPfAlertAsync(pair.Key, pair.Value, Configuration, cancellationToken).ConfigureAwait(false);
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
            $"**Recruiter:** {listing.Recruiter}\n" +
            $"**Listing ID:** {listing.ListingId}\n\n" +
            "### Party Finder Description\n" +
            $"> {cleaned}";
    }
}
