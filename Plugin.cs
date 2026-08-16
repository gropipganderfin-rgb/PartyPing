using System.Text;
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
    internal string PartyFillStatus { get; private set; } = "Current party: not in a party";
    internal string DiscordBotStatus => discordBotBridge.Status;

    private readonly WindowSystem windowSystem = new("PartyPing");
    private readonly ConfigWindow configWindow;
    private readonly SmsSender smsSender = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly DiscordBotBridge discordBotBridge;

    private Dictionary<string, PersistedPfAlert> activePfAlerts => Configuration.ActivePfAlerts;

    private DateTime lastPartyCheckUtc = DateTime.MinValue;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.ActivePfAlerts ??= [];
        Configuration.TrackedDiscordMessageIds ??= [];
        Configuration.DiscordBotToken ??= string.Empty;
        Configuration.DiscordChannelId ??= string.Empty;
        Configuration.DiscordUserId ??= string.Empty;

        discordBotBridge = new DiscordBotBridge(OpenLocalPfListingFromDiscordAsync);
        discordBotBridge.EnsureRunning(Configuration);

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
        discordBotBridge.Dispose();
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
        discordBotBridge.EnsureRunning(Configuration);
    }

    private static bool IsBoundByDuty() =>
        Condition[ConditionFlag.BoundByDuty] ||
        Condition[ConditionFlag.BoundByDuty56] ||
        Condition[ConditionFlag.BoundByDuty95];

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
        var haystack = NormalizeMatchText(dutyName + "\n" + description);
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

    private static string[] SplitKeywords(string raw) => (raw ?? string.Empty)
        .Normalize(NormalizationForm.FormKC)
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeMatchText)
        .Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string NormalizeMatchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string CleanDescription(string description)
    {
        var cleaned = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 1000)
            cleaned = cleaned[..997] + "...";
        return string.IsNullOrWhiteSpace(cleaned) ? "No description" : cleaned;
    }
}
