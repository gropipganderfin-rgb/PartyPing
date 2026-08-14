using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PartyPing;

internal static class PartyJoiner
{
    internal static async Task<bool> JoinOpenedListingAsync(
        ulong listingId,
        CancellationToken cancellationToken)
    {
        if (listingId == 0)
            return false;

        // OpenListing is asynchronous from the UI's perspective. Give the detail
        // addon a short window to appear, then click the game's own Join Party button.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await Plugin.Framework.Run(
                () => TryJoinOpenedListing(listingId),
                cancellationToken).ConfigureAwait(false);

            if (result.HasValue)
                return result.Value;

            await Plugin.Framework.DelayTicks(1, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static unsafe bool? TryJoinOpenedListing(ulong listingId)
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return false;

        var detail = Plugin.GameGui.GetAddonByName<AddonLookingForGroupDetail>(
            "LookingForGroupDetail",
            1);

        // null means "not ready yet" so the caller keeps waiting for a few ticks.
        if (detail == null ||
            !detail->AtkUnitBase.IsReady ||
            !detail->AtkUnitBase.IsVisible)
        {
            return null;
        }

        // Never click a Join Party button unless the detail window is showing the
        // exact listing requested by the Discord interaction.
        if (agent->LastViewedListing.ListingId != listingId)
            return false;

        var button = detail->JoinPartyButton;
        if (button == null || !button->IsEnabled)
            return false;

        var node = button->AtkComponentBase.GetAtkResNode();
        if (node == null || !node->IsEventRegistered(AtkEventType.ButtonClick))
            return false;

        AtkEventDispatcher.Event click = default;
        click.State.EventType = AtkEventType.ButtonClick;

        // This follows the same registered UI event path as clicking Join Party in
        // the native Party Finder detail window. Any game-side validation/dialogs
        // remain in force.
        return node->DispatchEvent(&click);
    }
}
