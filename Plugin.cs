using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PartyPing.Windows;

namespace PartyPing;

public sealed partial class Plugin : IDalamudPlugin
{
    private const string CommandName = "/partyping";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    internal string LastStatus { get; private set; } = "Idle";
    internal string XivPfStatus { get; private set; } = "XIVPF: waiting for first check";
    internal string PartyFillStatus { get; private set; } = "Party fill: not in a party";

    private readonly WindowSystem windowSystem = new("PartyPing");
    private readonly ConfigWindow configWindow;
    private readonly SmsSender smsSender = new();
    private readonly XivPfWatcher xivPfWatcher = new();
    private readonly XivPfRoleWatcher xivPfRoleWatcher = new();
    private readonly CancellationTokenSource cancellation = new();

    private readonly Dictionary<string, DateTimeOffset> notifiedXivPfListings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveXivPfAlert> activeXivPfAlerts = new(StringComparer.Ordinal);

    private long trackedPartyId;
    private int lastPartySize;
    private bool partyFullNotificationSent;
    private DateTime lastPartyCheckUtc = DateTime.MinValue;

    private sealed record ActiveXivPfAlert(string MessageId, DateTimeOffset CreatedAt, string LastContent);

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open PartyPing settings.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
        Framework.Update += OnFrameworkUpdate;

        _ = RunXivPfLoopAsync(cancellation.Token);
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        CommandManager.RemoveHandler(CommandName);

        cancellation.Cancel();
        xivPfRoleWatcher.Dispose();
        xivPfWatcher.Dispose();
        smsSender.Dispose();
        cancellation.Dispose();
        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleConfig();
    public void ToggleConfig() => configWindow.Toggle();

    internal async Task SendTestAsync()
    {
        await SendDiscordAsync(
            "# PartyPing Test\n" +
            "**Discord notifications are working.**\n\n" +
            "Your matching XIVPF listings will appear here."
        ).ConfigureAwait(false);
    }

    internal void ResetXivPfAlertStateAfterManualClear()
    {
        activeXivPfAlerts.Clear();
        notifiedXivPfListings.Clear();
        XivPfStatus = "XIVPF: cleared - matching listings will repopulate on the next poll";
        Log.Information("PartyPing manual clear reset XIVPF alert and cooldown state");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now - lastPartyCheckUtc < TimeSpan.FromSeconds(1))
            return;

        lastPartyCheckUtc = now;

        try
        {
            TrackPartyFill();
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Party fill tracker error: " + ex.Message;
            Log.Warning(ex, "PartyPing party fill tracker error");
        }
    }

    private void TrackPartyFill()
    {
        var partySize = PartyList.Length;
        var partyId = PartyList.PartyId;

        if (PartyList.IsAlliance)
        {
            PartyFillStatus = "Party fill: alliance ignored";
            ResetPartyTracking();
            return;
        }

        if (partySize <= 1 || partyId == 0)
        {
            PartyFillStatus = "Party fill: not in a party";
            ResetPartyTracking();
            return;
        }

        if (IsBoundByDuty())
        {
            PartyFillStatus = $"Party fill: {partySize}/8 - inside duty, not tracking";
            lastPartySize = partySize;
            return;
        }

        if (trackedPartyId != partyId)
        {
            trackedPartyId = partyId;
            partyFullNotificationSent = false;
            lastPartySize = partySize;
            PartyFillStatus = $"Party fill: tracking {partySize}/8";
        }
        else if (partySize != lastPartySize)
        {
            lastPartySize = partySize;
            PartyFillStatus = $"Party fill: tracking {partySize}/8";
        }

        if (!Configuration.Enabled || !Configuration.NotifyWhenPartyFull)
            return;

        if (partySize < 8 || partyFullNotificationSent)
            return;

        partyFullNotificationSent = true;
        PartyFillStatus = "Party fill: 8/8 - notification sent";

        _ = SendDiscordAsync(
            "## Party Filled\n" +
            "**Party:** 8/8\n" +
            "Your party is full and ready to go."
        );
    }

    private static bool IsBoundByDuty() =>
        Condition[ConditionFlag.BoundByDuty] ||
        Condition[ConditionFlag.BoundByDuty56] ||
        Condition[ConditionFlag.BoundByDuty95];

    private void ResetPartyTracking()
    {
        trackedPartyId = 0;
        lastPartySize = 0;
        partyFullNotificationSent = false;
    }

    private async Task RunXivPfLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var config = Configuration;
                var intervalSeconds = Math.Clamp(config.XivPfPollSeconds, 60, 600);

                if (!config.Enabled)
                {
                    XivPfStatus = "XIVPF: alerts disabled";
                }
                else if (!config.XivPfPollingEnabled)
                {
                    XivPfStatus = "XIVPF: background monitoring disabled";
                }
                else if (string.IsNullOrWhiteSpace(config.DutyNameContains))
                {
                    XivPfStatus = "XIVPF: enter a Duty name contains value to monitor the website";
                }
                else
                {
                    await PollXivPfAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            XivPfStatus = "XIVPF watcher stopped: " + ex.Message;
            Log.Error(ex, "PartyPing XIVPF watcher stopped unexpectedly");
        }
    }

    private async Task PollXivPfAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = Configuration;
            var listings = await xivPfWatcher.FetchAsync(config.DutyNameContains, cancellationToken).ConfigureAwait(false);
            var roleAvailability = await xivPfRoleWatcher.FetchAsync(cancellationToken).ConfigureAwait(false);
            var currentListings = listings.ToDictionary(x => x.Fingerprint, StringComparer.Ordinal);

            CleanupOldXivPfListings();
            var removedCount = await RemoveClosedXivPfAlertsAsync(
                currentListings,
                roleAvailability,
                config,
                cancellationToken).ConfigureAwait(false);

            var matchingCount = 0;
            var newAlertCount = 0;
            var updatedAlertCount = 0;

            foreach (var listing in listings)
            {
                if (!MatchesXivPfListing(listing, roleAvailability, config))
                    continue;

                matchingCount++;

                if (activeXivPfAlerts.TryGetValue(listing.Fingerprint, out var activeAlert))
                {
                    if (await UpdateXivPfListingAsync(listing, activeAlert, config, cancellationToken).ConfigureAwait(false))
                        updatedAlertCount++;
                    continue;
                }

                if (await NotifyXivPfListingAsync(listing, config, cancellationToken).ConfigureAwait(false))
                    newAlertCount++;
            }

            XivPfStatus =
                $"XIVPF: checked {DateTime.Now:t} - {listings.Count} listings, {matchingCount} matched, " +
                $"{newAlertCount} new, {updatedAlertCount} updated, {removedCount} removed";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            XivPfStatus = "XIVPF check failed: " + ex.Message;
            Log.Warning(ex, "PartyPing XIVPF check failed");
        }
    }

    private async Task<int> RemoveClosedXivPfAlertsAsync(
        IReadOnlyDictionary<string, XivPfListing> currentListings,
        IReadOnlyDictionary<string, RoleAvailability> roleAvailability,
        Configuration config,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;

        foreach (var pair in activeXivPfAlerts.ToArray())
        {
            var stillPresent = currentListings.TryGetValue(pair.Key, out var listing);
            var removalReason = GetRemovalReason(stillPresent, listing, roleAvailability, config);
            if (removalReason is null)
                continue;

            try
            {
                await smsSender.DeleteAsync(config, pair.Value.MessageId, cancellationToken).ConfigureAwait(false);
                activeXivPfAlerts.Remove(pair.Key);
                notifiedXivPfListings.Remove(pair.Key);
                removedCount++;

                Log.Information(
                    "Removed Discord alert for XIVPF listing {Fingerprint}; reason: {Reason}",
                    pair.Key,
                    removalReason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not remove Discord alert for XIVPF listing {Fingerprint}", pair.Key);
            }
        }

        return removedCount;
    }

    private static string? GetRemovalReason(
        bool stillPresent,
        XivPfListing? listing,
        IReadOnlyDictionary<string, RoleAvailability> roleAvailability,
        Configuration config)
    {
        if (!stillPresent || listing is null)
            return "listing disappeared";

        if (listing.TotalSlots > 0 && listing.FilledSlots >= listing.TotalSlots)
            return "party filled";

        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return "minimum open slots no longer met";

        if (!MatchesTextRules(listing.Duty, listing.Description, config))
            return "description/keyword requirements no longer met";

        if (config.RequiredRole != RoleFilter.AnyRole)
        {
            var roleKey = XivPfRoleWatcher.Key(listing);
            if (roleAvailability.TryGetValue(roleKey, out var roles) && !roles.Matches(config.RequiredRole))
                return "selected role filled";
        }

        return null;
    }

    private static bool MatchesXivPfListing(
        XivPfListing listing,
        IReadOnlyDictionary<string, RoleAvailability> roleAvailability,
        Configuration config)
    {
        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return false;

        if (!string.IsNullOrWhiteSpace(config.DutyNameContains) &&
            !listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (config.RequiredRole != RoleFilter.AnyRole)
        {
            var roleKey = XivPfRoleWatcher.Key(listing);
            if (!roleAvailability.TryGetValue(roleKey, out var roles) || !roles.Matches(config.RequiredRole))
                return false;
        }

        return MatchesTextRules(listing.Duty, listing.Description, config);
    }

    private async Task<bool> NotifyXivPfListingAsync(
        XivPfListing listing,
        Configuration config,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = GetListingCooldown(config);
        if (notifiedXivPfListings.TryGetValue(listing.Fingerprint, out var last) && now - last < cooldown)
            return false;

        var message = BuildXivPfMessage(listing, config.RequiredRole);

        try
        {
            LastStatus = "Sending Discord notification...";
            var result = await smsSender.SendTrackedAsync(config, message, cancellationToken).ConfigureAwait(false);

            activeXivPfAlerts[listing.Fingerprint] = new ActiveXivPfAlert(result.MessageId, now, message);
            notifiedXivPfListings[listing.Fingerprint] = now;

            LastStatus = result.Status + " at " + DateTime.Now.ToString("t");
            Log.Information("{Status}", LastStatus);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastStatus = "Discord notification failed: " + ex.Message;
            Log.Error(ex, "PartyPing Discord notification failed");
            return false;
        }
    }

    private async Task<bool> UpdateXivPfListingAsync(
        XivPfListing listing,
        ActiveXivPfAlert activeAlert,
        Configuration config,
        CancellationToken cancellationToken)
    {
        var message = BuildXivPfMessage(listing, config.RequiredRole);
        if (string.Equals(message, activeAlert.LastContent, StringComparison.Ordinal))
            return false;

        try
        {
            LastStatus = "Updating Discord notification...";
            var result = await smsSender.EditAsync(
                config,
                activeAlert.MessageId,
                message,
                cancellationToken).ConfigureAwait(false);

            activeXivPfAlerts[listing.Fingerprint] = activeAlert with { LastContent = message };
            LastStatus = result + " at " + DateTime.Now.ToString("t");
            Log.Information("Updated Discord alert for XIVPF listing {Fingerprint}", listing.Fingerprint);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastStatus = "Discord update failed: " + ex.Message;
            Log.Warning(ex, "Could not update Discord alert for XIVPF listing {Fingerprint}", listing.Fingerprint);
            return false;
        }
    }

    private async Task SendDiscordAsync(string message)
    {
        try
        {
            LastStatus = "Sending Discord notification...";
            var result = await smsSender.SendTrackedAsync(Configuration, message, cancellation.Token).ConfigureAwait(false);
            LastStatus = result.Status + " at " + DateTime.Now.ToString("t");
            Log.Information("{Status}", LastStatus);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LastStatus = "Discord notification failed: " + ex.Message;
            Log.Error(ex, "PartyPing Discord notification failed");
        }
    }

    private static bool MatchesTextRules(string dutyName, string description, Configuration config)
    {
        var haystack = dutyName + "\n" + description;
        var includes = SplitKeywords(config.IncludeKeywords);
        var excludes = SplitKeywords(config.ExcludeKeywords);

        if (excludes.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (includes.Length == 0)
            return true;

        return config.RequireAllIncludeKeywords
            ? includes.All(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase))
            : includes.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SplitKeywords(string raw) => raw
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void CleanupOldXivPfListings()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var pair in notifiedXivPfListings.ToArray())
            if (pair.Value < cutoff && !activeXivPfAlerts.ContainsKey(pair.Key))
                notifiedXivPfListings.Remove(pair.Key);
    }

    private static TimeSpan GetListingCooldown(Configuration config) =>
        TimeSpan.FromMinutes(Math.Clamp(config.PerListingCooldownMinutes, 1, 1440));

    private static string BuildXivPfMessage(XivPfListing listing, RoleFilter role)
    {
        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        return BuildXivPfMessage(
            listing.Duty,
            listing.FilledSlots,
            listing.TotalSlots,
            openSlots,
            listing.World,
            listing.Recruiter,
            role,
            listing.Description);
    }

    private static string BuildXivPfMessage(
        string duty,
        int filledSlots,
        int totalSlots,
        int openSlots,
        string world,
        string recruiter,
        RoleFilter role,
        string description)
    {
        var cleaned = CleanDescription(description);
        var roleText = role == RoleFilter.AnyRole
            ? "Any role"
            : $"{role.DisplayName()} - open";

        return
            $"## {duty}\n" +
            "**Source:** XIVPF.com\n" +
            $"**Party:** {filledSlots}/{totalSlots}\n" +
            $"**Open slots:** {openSlots}\n" +
            $"**Role filter:** {roleText}\n" +
            $"**World:** {world}\n" +
            $"**Recruiter:** {recruiter}\n\n" +
            "### Party Finder Description\n" +
            $"> {cleaned}";
    }

    private static string CleanDescription(string description)
    {
        var cleaned = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 1000)
            cleaned = cleaned[..997] + "...";
        return string.IsNullOrWhiteSpace(cleaned) ? "No description" : cleaned;
    }
}
