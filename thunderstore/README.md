# Revive Allies

When your friends die, they drop a green tombstone instead, where you can revive them until the revive window expires.

## USAGE

- Your friends hold their "Use" key (E) on the marker to revive you (4 seconds by default; there is a single-press mode in the config).
- You stay revivable for the window (30 seconds by default); after that you die for real and your tombstone appears as usual.
- Don't want to wait? Hold your own "Use" key (E) to give up — the progress turns red and you die immediately.
- Revived players come back at 25% health.

## CONFIGURATION

`BepInEx/config/com.andres.reviveallies.cfg` (created on first launch):

- `Revive.Mode` — `Hold` (default) or `Press`.
- `Revive.HoldTimeSeconds` — how long the revive takes (default 4 seconds).
- `Revive.WindowSeconds` — how long you stay revivable (default 30 seconds).

These settings are controlled **on the server**.

## CONTACT ME

If you have any issues with the mod, open an issue on GitHub: https://github.com/andres-valdes/revival-revived/issues
