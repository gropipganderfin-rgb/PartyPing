# PartyPing

PartyPing is a Dalamud API 15 plugin that sends a Discord notification when a Party Finder listing received by your FFXIV client matches your configured duty/prog filters.

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

## Install

Add this custom plugin repository URL in Dalamud Settings -> Experimental -> Custom Plugin Repositories:

`https://github.com/gropipganderfin-rgb/PartyPing/releases/latest/download/repo.json`

Then open `/xlplugins`, search for PartyPing, and install it normally.

## Discord setup

1. In Discord, create an incoming notification URL for the channel where you want PartyPing alerts.
2. Copy that URL.
3. In FFXIV, run `/partyping`.
4. Paste it into `Discord URL`.
5. Click `Send test Discord notification`.
6. Enable Discord alerts.

PartyPing sends messages directly to the Discord channel. It does not require a Discord bot.

## Important limitation

The native Dalamud `IPartyFinderGui.ReceiveListing` event fires when the game receives PF listings. PartyPing deliberately does **not** automate PF refreshes and does **not** scrape xivpf.com. FFXIV must be running and the client must actually be receiving PF listing results for alerts to fire.
