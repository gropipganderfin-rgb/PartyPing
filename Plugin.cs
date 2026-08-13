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

    private readonly WindowSystem windowSystem = new("PartyPing");
    private readonly ConfigWindow configWindow;
    private readonly SmsSender smsSender = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> notifiedListings = new();

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
    }

    public void Dispose()
    {
        PartyFinderGui.ReceiveListing -= OnReceiveListing;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        CommandManager.RemoveHandler(CommandName);

        cancellation.Cancel();
        cancellation.Dispose();
        smsSender.Dispose();
        windowSystem.RemoveAllWindows();
        configWindow.Dispose();
    }

    private void OnCommand(string command, string args) => ToggleConfig();
    public void ToggleConfig() => configWindow.Toggle();

    internal async Task SendTestAsync()
    {
        await SendSmsAsync(
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

            var haystack = dutyName + "\n" + description;
            var includes = SplitKeywords(config.IncludeKeywords);
            var excludes = SplitKeywords(config.ExcludeKeywords);

            if (excludes.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return;

            if (includes.Length > 0)
            {
                var includeMatched = config.RequireAllIncludeKeywords
                    ? includes.All(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase))
                    : includes.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));

                if (!includeMatched)
                    return;
            }

            CleanupOldListings();
            var now = DateTimeOffset.UtcNow;
            var cooldown = TimeSpan.FromMinutes(Math.Clamp(config.PerListingCooldownMinutes, 1, 1440));
            if (notifiedListings.TryGetValue(listing.Id, out var last) && now - last < cooldown)
                return;

            notifiedListings[listing.Id] = now;

            var recruitingSlots = listing.SlotsAvailable;
            var filledSlots = listing.SlotsFilled;
            var world = GetWorldName(listing);
            var message = BuildMessage(
                dutyName,
                filledSlots,
                recruitingSlots,
                openSlots,
                world,
                config.RequiredRole,
                description);

            _ = SendSmsAsync(message);
        }
        catch (Exception ex)
        {
            LastStatus = "Match processing error: " + ex.Message;
            Log.Error(ex, "Error processing PF listing");
        }
    }

    private async Task SendSmsAsync(string message)
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

    private static bool HasMatchingRoleSlot(IPartyFinderListing listing, RoleFilter role)
    {
        if (role == RoleFilter.AnyRole)
            return true;

        return listing.Slots.Any(slot => slot.Accepting.Any(job => role.Matches(job)));
    }

    private static string BuildMessage(
        string duty,
        int filledSlots,
        int recruitingSlots,
        int openSlots,
        string world,
        RoleFilter role,
        string description)
    {
        var cleaned = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 1200)
            cleaned = cleaned[..1197] + "...";

        var roleText = role == RoleFilter.AnyRole ? "Any role" : role.DisplayName();
        var recruitingText = recruitingSlots > 0
            ? $"{filledSlots}/{recruitingSlots} filled"
            : "Not specified";

        return
            "# PartyPing Match\n" +
            $"## {duty}\n" +
            $"**Recruiting slots:** {recruitingText}\n" +
            $"**Open slots:** {openSlots}\n" +
            $"**Role match:** {roleText}\n" +
            $"**World:** {world}\n\n" +
            "### Party Finder Description\n" +
            $"> {cleaned}";
    }
}
