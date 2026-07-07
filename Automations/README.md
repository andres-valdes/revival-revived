# Automations

A factory-automation mod for Valheim, built on the same ZDO-first architecture and
autonomous end-to-end test harness as ReviveAllies (its sibling in this repo).

You connect **machines** with directional **pipes** using a **connector**, and set
**blueprints** on the one machine that needs them. Chain them together and a
self-running factory emerges — every bit of it authoritative on ZDOs and
synchronized across the network.

## The key idea: real vanilla pieces *are* the machines

The machines are the game's own pieces. A **chest**, a **smelter**, a **charcoal
kiln** — each keeps all of its normal behaviour and visuals (a piped smelter still
lights its fire, smokes, and produces bars) and simply *gains a pipe layer* on top.
There is nothing to craft or learn: build a chest, build a smelter, and pipe them
together.

The only machine that is a bespoke prefab is the **Blueprint Assembler**, because no
vanilla piece auto-crafts. Everything else is vanilla.

## The shared language

- **Machine** — any piece that can be part of a factory. A `Machine` component is
  attached to every `Container` (chests) and every `Smelter` (smelter, kiln, blast
  furnace...) as it awakes, plus the custom assembler. It exposes the piece's real
  storage to the pipe network through an `IMachinePort`.
- **Pipe** — a *directional* connection from one machine's output to another's
  input, stored on the source machine's ZDO (`MachineView.Outputs`), so it
  replicates and persists for free.
- **Connector** — hold the wiring key (default **Left Alt**) and press **Use** on a
  machine to pick a pipe's start, then on a second machine to lay the pipe. Alt-Use
  clears a machine's pipes. While the key is held every pipe is drawn with the
  game's own dotted station-connection line, oriented in the flow direction.
- **Blueprint** — what the assembler makes. Press **Use** on it (without the wiring
  key) to cycle: `Copper + Tin → Bronze`, `Bronze + Wood → Bronze nails`.

## The machines

| Machine | Backed by | Role |
|---|---|---|
| **Chest** | vanilla `Container` | storage / source / collector — accepts & emits anything |
| **Smelter / Kiln / Blast furnace** | vanilla `Smelter` | ore + fuel in → bars out; lights up and smokes as it works |
| **Blueprint Assembler** | custom prefab (from the forge) | auto-crafts a settable recipe from piped-in materials |

Items are identified by their real Valheim prefab names, so pipes move genuine
materials (Wood, Coal, CopperOre, Copper, Tin, Bronze, BronzeNails...).

### The demo factory

```
chest(CopperOre + Coal) -> Smelter(Cu) --\
                                           Assembler(Copper+Tin->Bronze) -> Collector chest
chest(TinOre + Coal)    -> Smelter(Sn) --/
chest(Wood)             -> Kiln -> chest(Coal)      (bonus: the kiln smokes)
```

## How it works (design)

- **ZDO-first, single-writer.** A machine's pipe graph lives on its ZDO
  (`Domain/MachineView.cs`). The assembler additionally keeps its blueprint and
  buffer on the ZDO (`Domain/AssemblerView.cs`). Only the owning peer writes.
- **Ports bridge to real storage.** `IMachinePort` gives the pipe layer a uniform
  input/output surface over wildly different backends: `ContainerPort` over a
  chest's inventory, `SmelterPort` over a smelter's ore/fuel queue (feeding it
  through the smelter's own entry points so it animates), and `AssemblerPort` over
  the assembler's buffer. A port emits only a machine's *products*, never its
  unprocessed inputs — so a half-fed assembler holds its Copper waiting for Tin.
- **Output capture.** A smelter normally drops its bars on the ground; a Harmony
  patch on `Smelter.Spawn` redirects them into a capture buffer when the smelter has
  an outgoing pipe, so a pipe can ship them (and it drops as vanilla when un-piped).
- **Owner-authoritative transfer.** Each tick, on the owner, a machine moves items
  from its port down each pipe into the downstream machine's port, respecting the
  downstream's free space. When source and target are owned by different clients the
  handoff routes correctly — which is what makes a factory work across the network.
- **No shipped art.** The assembler's look is derived from the forge at runtime (the
  marker-from-tombstone trick), with the crafting station stripped off.

## End-to-end tests

The harness (`E2E/`, compiled only into Debug builds, opt-in via `AUTO_E2E=1`) drives
a real game process — building the factory from real pieces, running it, and
asserting outcomes — then writes a pass/fail file and quits itself.

```sh
dotnet build                       # Debug build + deploy to BepInEx/plugins

bin/run-e2e.sh                     # single process: full factory, assert Bronze flows + machines light up
AUTO_E2E_SCENARIO=flow   bin/run-e2e-mp.sh   # two processes over raw-TCP CustomSocket
AUTO_E2E_SCENARIO=wiring bin/run-e2e-mp.sh
```

Scenarios:

- **factory** (single process) — builds the factory, asserts the smelters light up,
  the assembler produces Bronze into the collector, and the kiln makes coal, then
  cuts the assembler's pipe and asserts flow stops.
- **flow** (cross-client) — the client *claims ownership* of the host-built collector
  chest and asserts Bronze lands in it: proof the host ships items to a different
  client over the network.
- **wiring** (cross-client) — the client asserts the host's pipe graph and a chest's
  contents both replicate over the wire and keep updating.

Play it yourself with `AUTO_E2E=1 AUTO_E2E_MANUAL=1` — the harness builds a factory
in front of you and hands over control; hold the wiring key to see the pipes.
