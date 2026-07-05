# Revive Allies

TODO: write the mod description.

## USAGE

- On lethal damage you are downed instead of killed, leaving a revive marker at your point of death.
- Your friends hold their "Use" key (E) on the marker to revive you (4 seconds by default; there is a single-press mode in the config).
- You stay revivable for the window (30 seconds by default); after that you die for real and your tombstone appears as usual.
- Revived players come back at 25% health.

## CONFIGURATION

`BepInEx/config/com.andres.reviveallies.cfg` (created on first launch):

- `Revive.Mode` — `Hold` (default) or `Press`.
- `Revive.HoldTimeSeconds` — how long the channel takes (default 4).
- `Revive.WindowSeconds` — how long you stay revivable (default 30).

These settings are **server-authoritative**: the host's values govern everyone
in the session and are replicated to connected players, so a client's own
settings for these are ignored. All players should still run the mod.

## CONTACT ME

If you have any issues with the mod, open an issue on GitHub: https://github.com/andres-valdes/revival-revived/issues
