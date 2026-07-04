# Revival Revived

Allows you to revive your friends where they fell, within a bleed-out window, before death becomes permanent.

## USAGE

- On lethal damage you are downed instead of killed: your body vanishes in a puff of smoke and a green revive marker with your name on it drops in at your point of death.
- Your friends hold their "Use" key (E) on the marker to revive you. A progress circle shows the channel (4 seconds by default; there is a single-press mode in the config).
- You stay revivable for the window (30 seconds by default). The marker's green glow fades back to tombstone red as time runs out, and actively channeling a revive pauses the timer.
- Revived players come back at 25% health.
- If nobody reaches you in time, you die for real and your regular loot tombstone takes the marker's place. Nothing to drop? The marker crumbles away.
- Logging out (or crashing) while downed won't save you: the death completes when you reconnect.

## CONFIGURATION

`BepInEx/config/com.andres.revivalrevived.cfg` (created on first launch):

- `Revive.Mode` — `Hold` (default) or `Press`.
- `Revive.HoldTimeSeconds` — how long the channel takes (default 4).
- `Revive.WindowSeconds` — how long you stay revivable (default 30).

All players in a session should run the mod. Timing is authoritative on the downed player's side.

## CONTACT ME

If you have any issues with the mod, open an issue on GitHub: https://github.com/andres-valdes/revival-revived/issues
