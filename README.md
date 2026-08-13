# PartyPing

PartyPing is a Dalamud API 15 plugin that sends Discord notifications when Party Finder listings match your configured duty/prog filters.

PartyPing can monitor two sources:

- Party Finder listings received directly by your FFXIV client.
- `xivpf.com/listings` in the background, so you do not have to keep opening or refreshing Party Finder yourself.

FFXIV/Dalamud must still be running because PartyPing is an in-game plugin.

## What it matches

- Optional exact Duty ID for in-game listings
- Duty-name substring
- Include keywords (ANY or ALL)
- Exclude keywords
- Required open role: Any, Tank, Healer, Melee, Physical Ranged, Caster, or Any DPS
- Minimum open slots
- Per-listing deduplication/cooldown

## XIVPF background monitoring

Enable `Monitor xivpf.com automatically` in `/partyping`.

The default polling interval is 90 seconds and the plugin enforces a minimum interval of 60 seconds.

For XIVPF monitoring, set `Duty name contains` because the public listings page exposes the duty name rather than PartyPing's in-game Duty ID. Keyword filters and minimum-open-slot filtering are applied to XIVPF matches too.

Structured job/role restrictions are available directly from Dalamud for listings received in-game. The XIVPF HTML page does not expose the same structured Dalamud slot objects, so a selected role is marked as **not verified** on XIVPF-only alerts rather than pretending the role match is exact.

XIVPF is crowdsourced, so its listing data may lag behind the in-game Party Finder or briefly contain a listing that has already changed/closed.

## Example Dancing Mad setup

- Duty ID: `1094`
- Duty name contains: `Dancing Mad`
- Include keywords: `p3, bh, enrage`
- Require ALL: off
- Exclude keywords: `fresh`
- Required open role: `Tank`
- Monitor xivpf.com automatically: on
- XIVPF poll interval: `90`

## Install

Add this custom plugin repository URL in Dalamud Settings -> Experimental -> Custom Plugin Repositories:

`https://github.com/gropipganderfin-rgb/PartyPing/releases/latest/download/repo.json`

Then open `/xlplugins`, search for PartyPing, and install/update it normally.

## Discord setup

1. In Discord, create an incoming webhook for the channel where you want PartyPing alerts.
2. Copy the webhook URL.
3. In FFXIV, run `/partyping`.
4. Paste it into `Discord URL`.
5. Click `Send test Discord notification`.
6. Enable Discord alerts.

PartyPing sends messages directly to the configured Discord channel. It does not require a Discord bot.
