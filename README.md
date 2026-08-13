# PartyPing

PartyPing is a Dalamud API 15 plugin that sends an SMS through Twilio when a Party Finder listing received by your FFXIV client matches your configured duty/prog filters.

## What it matches

- Optional exact Duty ID
- Optional duty-name substring
- Include keywords (ANY or ALL)
- Exclude keywords
- Required open role: Any, Tank, Healer, Melee, Physical Ranged, Caster, or Any DPS
- Minimum open slots
- Per-listing deduplication/cooldown

Example TEA Wormhole setup:

- Duty name contains: `The Epic of Alexander`
- Include keywords: `wormhole, p4`
- Require ALL: off
- Exclude keywords: `fresh`
- Required open role: `Healer`

## Role filtering

Role filtering uses Dalamud's structured Party Finder slot data. Each recruiting slot reports which jobs it accepts, and PartyPing only alerts when at least one recruiting slot accepts a job in your selected role. It does not guess the role from the PF description.

## Install/build

This project targets current Dalamud API 15 using `Dalamud.NET.Sdk/15.0.0` and .NET 10.

1. Install the .NET 10 SDK.
2. Clone/extract this project.
3. Run `dotnet build PartyPing/PartyPing.csproj -c Release`.
4. In Dalamud, enable developer plugins and point it at the generated PartyPing DLL, or publish it through your custom plugin repository workflow.
5. Run `/partyping` in game.

## Twilio setup

Create a Twilio account/number, then enter:

- Account SID
- Auth Token
- Twilio From number (E.164, such as `+15551234567`)
- Your destination number (E.164)

Use **Send test SMS** before enabling alerts.

Twilio trial accounts may require the destination number to be verified.

## Important limitation

The native Dalamud `IPartyFinderGui.ReceiveListing` event fires when the game receives PF listings. PartyPing deliberately does **not** automate PF refreshes and does **not** scrape xivpf.com. That means FFXIV must be running and the client must actually be receiving PF listing results for the SMS trigger to see them.

For true away-from-PC monitoring, the better architecture is an external xivpf.com/PartyFinderEx alert source -> webhook -> SMS gateway. That can be added as a second mode later without automating the game client.

## Security

The Twilio auth token is currently saved in the Dalamud plugin configuration. Treat that file as a secret. A production version should move the credential to Windows Credential Manager or a small server-side webhook.
