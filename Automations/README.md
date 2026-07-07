# Automations

A factory-automation mod for Valheim, built on the same ZDO-first architecture and
autonomous end-to-end test harness as ReviveAllies (its sibling in this repo).

You place **machines**, connect them with directional **pipes** using a **connector**,
and set **blueprints** on the machines that transform items. Chain them together and a
self-running factory emerges — every bit of it authoritative on ZDOs and synchronized
across the network.

## The shared language

- **Machine** — a placed piece with a small item **buffer** and a role. Every machine
  can receive items (input) from a pipe and/or send items (output) down a pipe.
- **Pipe** — a *directional* connection from one machine's output to another's input.
  Pipes are stored on the source machine's ZDO (`MachineView.Outputs`), so they
  replicate for free and survive save/load.
- **Connector** — hold the wiring key (default **Left Alt**) and press **Use** on a
  machine to pick it as a pipe's start, then on a second machine to lay the pipe.
  Alt-Use clears a machine's pipes. While the wiring key is held, all pipes are drawn
  with the game's own dotted station-connection line, oriented in the flow direction.
- **Blueprint** — what a machine is set to make. Press **Use** (without the wiring key)
  to cycle it: a Stockpile's raw item, a Smelter's ore, the Assembler's recipe.

## The machines

| Machine | Role | Blueprint options |
|---|---|---|
| **Stockpile** | Mints a raw material every tick (an "infinite chest") | Wood / CopperOre / TinOre |
| **Automated Kiln** | Wood → Coal | (fixed) |
| **Automated Smelter** | ore + Coal → bar | CopperOre→Copper / TinOre→Tin |
| **Blueprint Assembler** | combine bars into alloys/parts | Copper+Tin→Bronze / Bronze+Wood→BronzeNails |
| **Collector Chest** | stores anything; the end of a chain | (passive) |

Item ids are real Valheim item prefab names, so the buffers move genuine materials.

### The demo factory

Build one and watch it run (or spawn it via the harness):

```
Stockpile(Wood)   -> Kiln ----------\
Stockpile(Copper) -> Smelter(Copper) -> Assembler(Bronze) -> Collector Chest
Stockpile(Tin)    -> Smelter(Tin) --/
                         ^
     the Kiln's Coal feeds BOTH smelters
```

## How it works (design)

- **ZDO-first, single-writer.** A machine's entire state — kind, blueprint, buffered
  items, and outgoing pipes — lives on its ZDO, described by one typed view
  (`Domain/MachineView.cs`). The `Machine` MonoBehaviour only *ticks* it, and only on
  the peer that owns the ZDO.
- **Owner-authoritative tick.** Each tick a machine runs, in order: **produce** (a
  Stockpile mints its raw item), **process** (a processor consumes its blueprint's
  inputs into outputs), **transfer** (ship buffered items down each pipe).
- **Cross-network transfer.** A machine can only write its own ZDO, so to move an item
  it decrements its own buffer and sends a routed `Automations_Accept` RPC to the
  *downstream owner*, who adds to theirs. This routes correctly even when the two
  machines are owned by different clients — which is what makes a factory work across
  the network.
- **Derived prefabs.** Each machine's model/collider/Piece is cloned from a vanilla
  piece (chest, kiln, smelter, forge) and its vanilla station scripts stripped — the
  marker-from-tombstone trick from ReviveAllies, so we ship no art. Machines are added
  to the Hammer's build table so you can place them in normal play.

## End-to-end tests

The harness (`E2E/`, compiled only into Debug builds, opt-in via `AUTO_E2E=1`) drives a
real game process — building the factory, running ticks, and asserting outcomes — then
writes a pass/fail file and quits itself.

```sh
dotnet build                       # Debug build + deploy to BepInEx/plugins

bin/run-e2e.sh                     # single process: full factory, assert Bronze flows
AUTO_E2E_SCENARIO=flow   bin/run-e2e-mp.sh   # two processes over raw-TCP CustomSocket
AUTO_E2E_SCENARIO=wiring bin/run-e2e-mp.sh
```

Scenarios:

- **factory** (single process) — builds the 8-machine factory, asserts each stage
  produces (Coal, Copper, Tin, Bronze) and the collector chest fills with Bronze, then
  cuts the assembler's pipe and asserts flow stops.
- **flow** (cross-client) — the client *claims ownership* of the host-built collector
  chest and asserts Bronze lands in it: proof that the host's assembler ships items to
  a different client over the network via routed RPC.
- **wiring** (cross-client) — the client asserts the host's pipe graph (`Outputs`) and
  a machine's buffer both replicate over the wire and keep updating.

Play it yourself with `AUTO_E2E=1 AUTO_E2E_MANUAL=1` (single process) or
`AUTO_E2E_MANUAL=1` per role — the harness builds a factory in front of you and hands
over control.
