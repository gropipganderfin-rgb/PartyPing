# PartyPing

PartyPing is a Dalamud plugin that sends Discord notifications when local FFXIV Party Finder listings match your configured duty/prog filters.

PartyPing now uses only Party Finder data received directly by your FFXIV client. It does not use XIVPF.com or any external Party Finder website.

FFXIV/Dalamud must be running because PartyPing is an in-game plugin.

## What it matches

- Duty-name substring
- Include keywords (ANY or ALL)
- Exclude keywords
- Required open role: Any, Tank, Healer, Melee, Physical Ranged, Caster, or Any DPS
- Minimum open slots
- North American worlds only

Role availability comes from FFXIV's actual accepted-job data for each open Party Finder slot.

## Local Party Finder polling

PartyPing automatically requests the High-End Duty Party Finder category directly from FFXIV.

After each polling cycle, it chooses a new random whole-number interval from 30 through 60 seconds before the next check. You do not need to keep the Party Finder window open.

You can also use `Check local PF now` in `/partyping` for an immediate refresh.

For every matching listing, PartyPing tracks its Discord message and keeps it synchronized:

- New matching listing: creates a Discord post.
- Party count, description, or other displayed information changes: edits the existing post.
- Description no longer matches your include/exclude rules: deletes the post.
- Selected role is no longer open: deletes the post.
- Minimum open slots is no longer met: deletes the post.
- Listing fills or closes: deletes the post when the local scan can determine it safely.

Automatic local checks pause while you are inside a duty, zoning, or in a cutscene.

## Example Dancing Mad setup

- Duty name contains: `Dancing Mad`
- Include keywords: `p3, bh, enrage`
- Require ALL: off
- Exclude keywords: `fresh`
- Required open role: `Tank`
- Minimum open slots: `1`

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
