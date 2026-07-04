# Revival Revived

Dying gets a second chance. When your health hits zero you are **downed**
instead of dead: your body fades in a puff of smoke and a **green revive
marker** drops in where you fell. Teammates can channel on the marker to bring
you back — if nobody does before the window runs out, you die for real and the
marker becomes your regular tombstone, right in place.

## Features

- **Downed, not dead** — lethal damage puts you in a downed state. No ragdoll,
  no instant grave: a green-glowing marker (with your name on it) marks the
  spot.
- **Hold-to-revive** — teammates hold the interact key on the marker; a radial
  progress circle shows the channel. Revive progress is driven by the reviving
  player, so it is lag-free for the person doing the work. A single-press mode
  is available in the config.
- **Bleed-out window** — you stay revivable for a configurable window
  (30s default). The marker's accent gradually shifts from revive-green back
  to tombstone-red as time runs out. Actively channeling a revive **pauses**
  the timer.
- **Clean handoff on true death** — when the window expires, the real loot
  tombstone takes the marker's place seamlessly (no drop-in pop replay, no
  flicker). If you had nothing to drop, the marker crumbles away like an
  emptied grave.
- **No disconnect cheese** — logging out (or crashing) while downed doesn't
  save you: the marker persists, and completing the death is enforced when you
  reconnect.
- **Multiplayer-first** — state lives in replicated ZDOs; late joiners and
  players streaming into the area see downed markers correctly.

## Configuration

`BepInEx/config/com.andres.revivalrevived.cfg` (created on first launch):

| Setting | Default | Description |
| --- | --- | --- |
| `Revive.Mode` | `Hold` | `Hold`: keep the interact key held to revive. `Press`: a single press revives instantly. |
| `Revive.HoldTimeSeconds` | `4` | How long the interact key must be held to complete a revive (Hold mode). |
| `Revive.WindowSeconds` | `30` | How long a downed player stays revivable before dying for real. |

Timing is authoritative on the downed player's side; a reviver's own config
only affects which button gesture their client accepts.

## Notes

- Revived players come back at 25% health.
- All players in a session should run the mod (it patches death handling and
  registers a networked marker prefab).
