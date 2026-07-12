# Changelog

## 0.3.3

- Fixed client-to-client revives failing to build progress when both players
  were connected through a host or dedicated server.
- Made held revive input more tolerant of frame hitches.

## 0.3.2

- Fixed a bug where enemies would continue to aggro downed players.
- Revive progress builds up smoothly.

## 0.3.1

Internal cleanup of the downed/revive logic, with one gameplay tweak: while you
hold your "Use" key to give up, an ally can no longer finish reviving you until
you let go. Holding "give up" now reliably means you want to die.

## 0.3.0

The third minor release of ReviveAllies, and a full rewrite of the mod, updated
for current Valheim versions.

- Give up: hold your own "Use" key (E) while downed to end it early — a red
  circle fills and you die immediately instead of waiting out the window.
- Revive settings are now controlled by the server: the host's revive window,
  hold time and mode apply to everyone in the session, and a client's own
  values for these are ignored.

## 0.2.0

Reliability pass on the revive flow, especially in multiplayer with real
network latency.

- Reviving is now owner-authoritative: the progress circle no longer overflows
  past full and refills near completion, and a completed revive never leaves a
  lingering marker behind on high-latency servers.
- The revive marker crumbles away (grave despawn effect) when you are revived,
  instead of blinking out.
- Dead bodies stay inert until respawn: no invisible collider to bump into and
  no floating name/health bar over the corpse.
- Hardened the marker-to-tombstone handoff so the swap is gap-free, and a death
  with an empty inventory crumbles the marker instead of leaving it to vanish.

## 0.1.1

- Rewrote the mod page with full usage details.
- New icon.
- The dev/test harness is no longer compiled into release builds (smaller DLL,
  no test hooks).

## 0.1.0

Initial release.

- Downed state on lethal damage with a green revive marker (player-named,
  drop-in spawn, color gradient tracking the bleed-out window).
- Hold-to-revive with radial progress UI (configurable hold time, or
  single-press mode); channeling pauses the bleed-out timer.
- Seamless marker-to-tombstone handoff on true death; marker crumbles when
  there is no loot to drop; corpse stays inert until respawn.
- Downed + disconnect = death, enforced on reconnect; reviving a player who
  vanished mid-channel fizzles cleanly.
- Configurable revive window, hold time and revive mode.
