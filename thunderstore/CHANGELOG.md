# Changelog

## 0.3.0

TODO: write the 0.3.0 release notes.

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
