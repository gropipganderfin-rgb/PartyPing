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
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    internal string LastStatus { get; private set; } = "Idle";
    internal string PartyFillStatus { get; private set; } = "Party fill: not in a party";

    private readonly WindowSystem windowSystem = new("PartyPing");
    private readonly ConfigWindow configWindow;
    private readonly SmsSender smsSender = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly LocalPfLinkServer localPfLinkServer;

    private Dictionary<string, PersistedPfAlert> activePfAlerts => Configuration.ActivePfAlerts;

    private long trackedPartyId;
    private int lastPartySize;
    private bool partyFullNotificationSent;
    private DateTime lastPartyCheckUtc = DateTime.MinValue;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.ActivePfAlerts ??= [];
        Configuration.TrackedDiscordMessageIds ??= [];

        localPfLinkServer = new LocalPfLinkServer(OpenLocalPfListingFromDiscordAsync);
        try
        {
            localPfLinkServer.Start();
            Log.Information("PartyPing localhost PF link server listening on 127.0.0.1:{Port}", localPfLinkServer.Port);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PartyPing could not start its localhost PF link server; Discord open links will be unavailable");
        }

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

        _ = RemoveLegacyPfSeparatorsAsync();
        StartLocalPfAutoPolling();
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        CommandManager.RemoveHandler(CommandName);

        cancellation.Cancel();
        localPfLinkServer.Dispose();
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
            "Your matching local Party Finder listings will appear here."
        ).ConfigureAwait(false);
    }

    internal void ResetPfAlertStateAfterManualClear()
    {
        activePfAlerts.Clear();
        Configuration.Save();
        LocalPfStatus = "Local PF: cleared - matching listings will repopulate on the next poll";
        Log.Information("PartyPing manual clear reset local PF alert state");
    }

    private async Task RemoveLegacyPfSeparatorsAsync()
    {
        var changed = false;

        foreach (var alert in activePfAlerts.Values)
        {
            if (string.IsNullOrWhiteSpace(alert.SeparatorMessageId))
                continue;

            try
            {
                await smsSender.DeleteAsync(Configuration, alert.SeparatorMessageId, cancellation.Token).ConfigureAwait(false);
                alert.SeparatorMessageId = null;
                changed = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PartyPing could not remove a legacy Discord PF separator");
            }
        }

        if (changed)
            Configuration.Save();
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

    private static string CleanDescription(string description)
    {
        var cleaned = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 1000)
            cleaned = cleaned[..997] + "...";
        return string.IsNullOrWhiteSpace(cleaned) ? "No description" : cleaned;
    }
}
