using System.Collections.Concurrent;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PartyPing.Windows;

namespace PartyPing;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/partyping";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    internal string LastStatus { get; private set; } = "Idle";
    internal string XivPfStatus { get; private set; } = "XIVPF: waiting for first check";

    private readonly WindowSystem windowSystem = new("PartyPing");
    private readonly ConfigWindow configWindow;
    private readonly SmsSender smsSender = new();
    private readonly XivPfWatcher xivPfWatcher = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> notifiedListings = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> notifiedXivPfListings = new(StringComparer.Ordinal);

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
        PartyFinderGui.ReceiveListing += OnReceiveListing;

        _ = RunXivPfLoopAsync(cancellation.Token);
    }

    public void Dispose()
    {
        PartyFinderGui.ReceiveListing -= OnReceiveListing;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        CommandManager.RemoveHandler(CommandName);

        cancellation.Cancel();
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
            "Your matching Party Finder listings will appear here."
        ).ConfigureAwait(false);
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        try
        {
            var config = Configuration;
            if (!config.Enabled)
                return;

            var description = listing.Description.ToString();
            var dutyName = GetDutyName(listing);

            if (config.DutyId != 0 && listing.RawDuty != config.DutyId)
                return;

            if (!string.IsNullOrWhiteSpace(config.DutyNameContains) &&
                !dutyName.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            var openSlots = Math.Max(0, listing.SlotsAvailable - listing.SlotsFilled);
            if (openSlots < Math.Max(0, config.MinimumOpenSlots))
                return;

            if (!HasMatchingRoleSlot(listing, config.RequiredRole))
                return;

            if (!MatchesTextRules(dutyName, description, config))
                return;

            CleanupOldListings();
            var now = DateTimeOffset.UtcNow;
            var cooldown = GetListingCooldown(config);
            if (notifiedListings.TryGetValue(listing.Id, out var last) && now - last < cooldown)
                return;

            notifiedListings[listing.Id] = now;

            var recruitingSlots = listing.SlotsAvailable;
            var filledSlots = listing.SlotsFilled;
            var world = GetWorldName(listing);
            var message = BuildGameMessage(
                dutyName,
                filledSlots,
                recruitingSlots,
                openSlots,
                world,
                config.RequiredRole,
                description);

            _ = SendDiscordAsync(message);
        }
        catch (Exception ex)
        {
            LastStatus = "Match processing error: " + ex.Message;
            Log.Error(ex, "Error processing PF listing");
        }
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
            var matchingCount = 0;

            CleanupOldXivPfListings();

            foreach (var listing in listings.Take(20))
            {
                if (!MatchesXivPfListing(listing, config))
                    continue;

                matchingCount++;
                await NotifyXivPfListingAsync(listing, config).ConfigureAwait(false);
            }

            XivPfStatus = $"XIVPF: checked {DateTime.Now:t} - {listings.Count} duty listings, {matchingCount} matched filters";
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

    private static bool MatchesXivPfListing(XivPfListing listing, Configuration config)
    {
        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        if (openSlots < Math.Max(0, config.MinimumOpenSlots))
            return false;

        if (!string.IsNullOrWhiteSpace(config.DutyNameContains) &&
            !listing.Duty.Contains(config.DutyNameContains.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        return MatchesTextRules(listing.Duty, listing.Description, config);
    }

    private async Task NotifyXivPfListingAsync(XivPfListing listing, Configuration config)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = GetListingCooldown(config);
        if (notifiedXivPfListings.TryGetValue(listing.Fingerprint, out var last) && now - last < cooldown)
            return;

        notifiedXivPfListings[listing.Fingerprint] = now;

        var openSlots = Math.Max(0, listing.TotalSlots - listing.FilledSlots);
        var message = BuildXivPfMessage(
            listing.Duty,
            listing.FilledSlots,
            listing.TotalSlots,
            openSlots,
            listing.World,
            listing.Recruiter,
            config.RequiredRole,
            listing.Description);

        await SendDiscordAsync(message).ConfigureAwait(false);
    }

    private async Task SendDiscordAsync(string message)
    {
        try
        {
            LastStatus = "Sending Discord notification...";
            var status = await smsSender.SendAsync(Configuration, message, cancellation.Token).ConfigureAwait(false);
            LastStatus = status + " at " + DateTime.Now.ToString("t");
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

    private static string GetDutyName(IPartyFinderListing listing)
    {
        try
        {
            if (listing.RawDuty == 0)
                return "Other Party Finder";
            return listing.Duty.Value.Name.ToString();
        }
        catch
        {
            return $"Duty #{listing.RawDuty}";
        }
    }

    private static string GetWorldName(IPartyFinderListing listing)
    {
        try
        {
            return listing.CurrentWorld.Value.Name.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    private void CleanupOldListings()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var pair in notifiedListings)
            if (pair.Value < cutoff)
                notifiedListings.TryRemove(pair.Key, out _);
    }

    private void CleanupOldXivPfListings()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach (var pair in notifiedXivPfListings)
            if (pair.Value < cutoff)
                notifiedXivPfListings.TryRemove(pair.Key, out _);
    }

    private static TimeSpan GetListingCooldown(Configuration config) =>
        TimeSpan.FromMinutes(Math.Clamp(config.PerListingCooldownMinutes, 1, 1440));

    private static bool HasMatchingRoleSlot(IPartyFinderListing listing, RoleFilter role)
    {
        if (role == RoleFilter.AnyRole)
            return true;

        return listing.Slots.Any(slot => slot.Accepting.Any(job => role.Matches(job)));
    }

    private static string BuildGameMessage(
        string duty,
        int filledSlots,
        int recruitingSlots,
        int openSlots,
        string world,
        RoleFilter role,
        string description)
    {
        var cleaned = CleanDescription(description);
        var roleText = role == RoleFilter.AnyRole ? "Any role" : role.DisplayName();
        var recruitingText = recruitingSlots > 0
            ? $"{filledSlots}/{recruitingSlots} filled"
            : "Not specified";

        return
            "# PartyPing Match\n" +
            $"## {duty}\n" +
            "**Source:** In-game Party Finder\n" +
            $"**Recruiting slots:** {recruitingText}\n" +
            $"**Open slots:** {openSlots}\n" +
            $"**Role match:** {roleText}\n" +
            $"**World:** {world}\n\n" +
            "### Party Finder Description\n" +
            $"> {cleaned}";
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
            : $"{role.DisplayName()} - not verified by XIVPF HTML";

        return
            "# PartyPing Match\n" +
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
