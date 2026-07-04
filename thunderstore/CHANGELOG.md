# Changelog

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
