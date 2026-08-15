using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PartyPing;

public sealed partial class Plugin
{
    private const int PartyTrackerEmbedColor = 0x57F287;

    private readonly SemaphoreSlim partyTrackerGate = new(1, 1);
    private long partyTrackerPartyId;
    private string partyTrackerRosterSignature = string.Empty;
    private string? partyTrackerMessageId;
    private bool partyTrackerEndQueued;

    private sealed record PartyTrackerSnapshot(
        long PartyId,
        int PartySize,
        string RosterSignature,
        string Members);

    private void TrackJoinedPartyCard()
    {
        if (PartyList.IsAlliance)
        {
            PartyFillStatus = "Party tracker: alliance ignored";
            QueuePartyTrackerEnd();
            return;
        }

        var partySize = PartyList.Length;
        var partyId = PartyList.PartyId;

        if (partySize <= 1 || partyId == 0)
        {
            PartyFillStatus = "Party tracker: not in a party";
            QueuePartyTrackerEnd();
            return;
        }

        PartyFillStatus = $"Party tracker: live {partySize}/8";

        if (!Configuration.Enabled)
            return;

        if (!DiscordNotifier.HasBotTransport(Configuration))
        {
            PartyFillStatus = "Party tracker: configure the Discord bot first";
            return;
        }

        var snapshot = CapturePartyTrackerSnapshot(partyId, partySize);
        var newParty = partyTrackerPartyId != partyId;
        var rosterChanged = !string.Equals(
            partyTrackerRosterSignature,
            snapshot.RosterSignature,
            StringComparison.Ordinal);
        var strayPfCards = activePfAlerts.Count > 0;

        if (!newParty && !rosterChanged && !strayPfCards && !string.IsNullOrWhiteSpace(partyTrackerMessageId))
            return;

        partyTrackerPartyId = partyId;
        partyTrackerRosterSignature = snapshot.RosterSignature;
        _ = SyncPartyTrackerAsync(snapshot, newParty, cancellation.Token);
    }

    private PartyTrackerSnapshot CapturePartyTrackerSnapshot(long partyId, int partySize)
    {
        var leaderIndex = PartyList.PartyLeaderIndex;
        var memberLines = new List<string>();
        var signatureParts = new List<string>();

        for (var i = 0; i < PartyList.Length; i++)
        {
            var member = PartyList[i];
            if (member is null)
                continue;

            var name = member.Name.TextValue.Trim();
            var world = member.World.Value.Name.ToString();
            var display = string.IsNullOrWhiteSpace(world)
                ? name
                : $"{name} @ {world}";
            var leader = (uint)i == leaderIndex;

            memberLines.Add(leader ? $"👑 {display}" : $"• {display}");
            signatureParts.Add($"{member.ContentId}:{name}:{world}:{leader}");
        }

        var members = memberLines.Count == 0
            ? "Party roster unavailable"
            : string.Join("\n", memberLines);
        var signature = $"{partyId}|{partySize}|{string.Join("|", signatureParts)}";

        return new PartyTrackerSnapshot(partyId, partySize, signature, members);
    }

    private async Task SyncPartyTrackerAsync(
        PartyTrackerSnapshot snapshot,
        bool newParty,
        CancellationToken cancellationToken)
    {
        await partyTrackerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (newParty)
            {
                PartyFillStatus = $"Party tracker: joined {snapshot.PartySize}/8 - clearing other PartyPing posts...";

                for (var i = 0; i < 40 && LocalPfCheckInProgress; i++)
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);

                await DiscordClearer.ClearAsync(Configuration, cancellationToken).ConfigureAwait(false);
                activePfAlerts.Clear();
                Configuration.Save();
                partyTrackerMessageId = null;
            }
            else if (activePfAlerts.Count > 0)
            {
                foreach (var pair in activePfAlerts.ToArray())
                    await DeleteLocalPfAlertAsync(pair.Key, pair.Value, Configuration, cancellationToken).ConfigureAwait(false);
            }

            if (partyTrackerPartyId != snapshot.PartyId)
                return;

            if (string.IsNullOrWhiteSpace(partyTrackerMessageId))
            {
                partyTrackerMessageId = await SendPartyTrackerCardAsync(snapshot, cancellationToken).ConfigureAwait(false);
                DiscordMessageStore.Add(Configuration, partyTrackerMessageId);
                PartyFillStatus = $"Party tracker: live {snapshot.PartySize}/8";
                return;
            }

            await EditPartyTrackerCardAsync(
                partyTrackerMessageId,
                snapshot,
                cancellationToken).ConfigureAwait(false);

            PartyFillStatus = $"Party tracker: live {snapshot.PartySize}/8 - updated";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Party tracker error: " + ex.Message;
            Log.Warning(ex, "PartyPing joined-party tracker update failed");
        }
        finally
        {
            partyTrackerGate.Release();
        }
    }

    private void QueuePartyTrackerEnd()
    {
        if (partyTrackerEndQueued)
            return;

        if (partyTrackerPartyId == 0 && string.IsNullOrWhiteSpace(partyTrackerMessageId))
            return;

        partyTrackerEndQueued = true;
        partyTrackerPartyId = 0;
        partyTrackerRosterSignature = string.Empty;
        _ = EndPartyTrackerAsync(cancellation.Token);
    }

    private async Task EndPartyTrackerAsync(CancellationToken cancellationToken)
    {
        await partyTrackerGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrWhiteSpace(partyTrackerMessageId))
            {
                await DeletePartyTrackerCardAsync(partyTrackerMessageId, cancellationToken).ConfigureAwait(false);
                DiscordMessageStore.Remove(Configuration, partyTrackerMessageId);
            }

            partyTrackerMessageId = null;
            PartyFillStatus = "Party tracker: not in a party";

            if (Configuration.Enabled && !string.IsNullOrWhiteSpace(Configuration.DutyNameContains))
                await CheckLocalPfNowAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PartyFillStatus = "Party tracker cleanup failed: " + ex.Message;
            Log.Warning(ex, "PartyPing could not remove joined-party tracker card");
        }
        finally
        {
            partyTrackerEndQueued = false;
            partyTrackerGate.Release();
        }
    }

    internal void ResetPartyTrackerMessageAfterManualClear()
    {
        partyTrackerMessageId = null;
        partyTrackerRosterSignature = string.Empty;
    }

    internal bool IsInTrackedNormalParty() =>
        !PartyList.IsAlliance && PartyList.Length > 1 && PartyList.PartyId != 0;

    private async Task<string> SendPartyTrackerCardAsync(
        PartyTrackerSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildPartyTrackerMessageUrl(null))
        {
            Content = PartyTrackerJsonContent(snapshot),
        };
        AddPartyTrackerBotAuthorization(request);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord party tracker send returned {(int)response.StatusCode}: {responseBody}");

        using var json = JsonDocument.Parse(responseBody);
        if (!json.RootElement.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("Discord did not return a party tracker message ID.");

        var messageId = idElement.GetString();
        return string.IsNullOrWhiteSpace(messageId)
            ? throw new InvalidOperationException("Discord returned an empty party tracker message ID.")
            : messageId;
    }

    private async Task EditPartyTrackerCardAsync(
        string messageId,
        PartyTrackerSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            BuildPartyTrackerMessageUrl(messageId))
        {
            Content = PartyTrackerJsonContent(snapshot),
        };
        AddPartyTrackerBotAuthorization(request);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            partyTrackerMessageId = null;
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord party tracker edit returned {(int)response.StatusCode}: {responseBody}");
        }
    }

    private async Task DeletePartyTrackerCardAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            BuildPartyTrackerMessageUrl(messageId));
        AddPartyTrackerBotAuthorization(request);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Discord party tracker delete returned {(int)response.StatusCode}: {responseBody}");
        }
    }

    private Uri BuildPartyTrackerMessageUrl(string? messageId)
    {
        if (!ulong.TryParse(Configuration.DiscordChannelId?.Trim(), out var channelId) || channelId == 0)
            throw new InvalidOperationException("Discord bot channel ID is invalid.");

        var url = $"https://discord.com/api/v10/channels/{channelId}/messages";
        if (!string.IsNullOrWhiteSpace(messageId))
            url += "/" + Uri.EscapeDataString(messageId.Trim());

        return new Uri(url);
    }

    private void AddPartyTrackerBotAuthorization(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(Configuration.DiscordBotToken))
            throw new InvalidOperationException("Discord bot token is missing.");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bot",
            Configuration.DiscordBotToken.Trim());
    }

    private static StringContent PartyTrackerJsonContent(PartyTrackerSnapshot snapshot)
    {
        var payload = new
        {
            content = string.Empty,
            embeds = new[]
            {
                new
                {
                    title = "Party Tracker",
                    color = PartyTrackerEmbedColor,
                    fields = new object[]
                    {
                        new { name = "Party", value = $"{snapshot.PartySize}/8", inline = true },
                        new { name = "Members", value = snapshot.Members, inline = false },
                    },
                },
            },
            components = Array.Empty<object>(),
            allowed_mentions = new
            {
                parse = Array.Empty<string>(),
            },
        };

        return new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
    }
}
