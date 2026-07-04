---
name: zdo
description: Deep reference for Valheim's ZDO networking infrastructure. Use when working with networked state, ZDO fields, ownership, sync, RPCs, prefab registration, or understanding how GameObjects map to ZDOs.
allowed-tools: Read, Grep, Glob
---

# Valheim ZDO Infrastructure

The user is asking about or working with Valheim's ZDO networking layer. Use this context to inform your answers, and read the decompiled source files at `.decompiled/assembly_valheim/` for specifics.

## Architecture

Valheim uses a **ZDO-first** model. ZDOs are the authoritative networked state -- GameObjects are ephemeral visual representations created/destroyed based on player proximity.

```
ZDO (networked data) ←→ ZDOMan (sync engine) ←→ ZNetScene (instantiation) ←→ ZNetView (GameObject bridge)
```

## Key Decompiled Source Files

All under `.decompiled/assembly_valheim/`:

- `ZDO.cs` (~1535 lines) -- Core data container: position, rotation, prefab, flags, revisions
- `ZDOExtraData.cs` -- Static dictionaries storing per-ZDO data (floats, ints, strings, vectors, quats, byte arrays) keyed by ZDOID + field hash
- `ZDOVars.cs` -- Predefined hash constants: `s_health`, `s_stamina`, `s_tamed`, `s_dead`, `s_level`, etc.
- `ZDOMan.cs` (~1400 lines) -- Central manager: spatial partitioning, sync (20 FPS), ownership, lifecycle
- `ZNetScene.cs` (~465 lines) -- Maps ZDOs ↔ GameObjects. Instantiates/destroys prefabs based on proximity (~30 FPS)
- `ZNetView.cs` (~365 lines) -- MonoBehaviour linking a GameObject to its ZDO. RPC registration/dispatch
- `ZRoutedRpc.cs` (~244 lines) -- Routes RPC calls to specific peers or ZDOs
- `ZDOID.cs` -- Identifier struct: `UserID (long) + ID (uint)`

When answering questions, read the relevant file to give precise field names, method signatures, and line numbers.

## ZDO Data Model

**Built-in:** `m_position`, `m_rotation`, `m_prefab` (hash), `m_sector`

**DataFlags (byte):** `Persistent` (0x04), `Distant` (0x08), `Created` (0x10), `Owner` (0x20), `Owned` (0x40), `Valid` (0x80)

**ObjectType:** `Default`, `Prioritized` (players), `Solid` (geometry), `Terrain`

**Extra data** in `ZDOExtraData` static dicts indexed by ZDOID, keyed by `int hash` (from `"fieldName".GetStableHashCode()`).

**Revisions:** `DataRevision` (any data change), `OwnerRevision` (ownership change). Server only sends to a peer if peer's stored revision < current.

## Ownership

- Owner = the peer (long UserID) with authority
- Only owner should modify data and can destroy
- `ZNetView.IsOwner()` / `ClaimOwnership()` for gameplay code
- Server reassigns orphaned ZDOs every 2s via `ReleaseNearbyZDOS()`

## What Lives Where

| ZDO (survives save/load, synced over network) | GameObject (ephemeral, local only) |
|---|---|
| Health, stamina, tamed state, inventory | Visual mesh, animations, particle effects |
| Position, rotation | Colliders, rigidbody, physics |
| Owner, portal connections, prefab hash | MonoBehaviour logic, UI, audio |

---

# Runbook: Creating Networked Objects with ZDOs

## Core Concept

Every networked object in Valheim is a **prefab with a `ZNetView` component**. The prefab name's hash IS the ZDO type -- there's no separate type registry. When ZNetScene sees a ZDO with `m_prefab = "MyThing".GetStableHashCode()`, it instantiates the "MyThing" prefab.

## Step 1: Create and Register a Prefab

At mod init (in `Plugin.Awake()` or a postfix on `ZNetScene.Awake()`), create a prefab and register it:

```csharp
// Create the prefab GameObject (inactive so Awake doesn't fire yet)
var prefab = new GameObject("MyCustomObject");
prefab.SetActive(false);

// Add the ZNetView -- this is what makes it networked
var nview = prefab.AddComponent<ZNetView>();
nview.m_persistent = true;   // survives save/load
nview.m_distant = false;     // only render when nearby
nview.m_type = ZDO.ObjectType.Default;

// Add your gameplay components
prefab.AddComponent<MyCustomBehaviour>();

// Add any visual/physics components
// prefab.AddComponent<MeshFilter>();
// prefab.AddComponent<MeshRenderer>();

// Prevent Unity from destroying it
UnityEngine.Object.DontDestroyOnLoad(prefab);

// Register with ZNetScene so the ZDO system knows about it
var hash = "MyCustomObject".GetStableHashCode();
ZNetScene.instance.m_namedPrefabs[hash] = prefab;
ZNetScene.instance.m_prefabs.Add(prefab);
```

**Important:** The prefab name must be unique and must match exactly -- `GetStableHashCode()` on the name is used everywhere.

## Step 2: Spawn at Runtime

```csharp
// This broadcasts to all peers -- each one instantiates locally
ZNetScene.instance.SpawnObject(position, rotation, prefab);
```

Or instantiate directly (creates ZDO only on this peer, ZDOMan syncs it):

```csharp
var obj = UnityEngine.Object.Instantiate(prefab, position, rotation);
obj.SetActive(true);
// ZNetView.Awake() fires, creates ZDO, registers with ZNetScene
```

## Step 3: Store Custom Data on the ZDO

Networked fields are declared as a typed schema. The **ZdoTyped** generator
(sibling repo `../zdo-typed`) turns each `[ZdoField]` into the matching ZDO
`Get`/`Set` with its stable-hash key baked in as a compile-time constant
(hashed via `GetStableHashCode`, parity-tested against `assembly_utils.dll`).

```csharp
[ZdoSchema("MyMod")]                       // keys become "MyMod_<field>"
public partial struct State {
    [ZdoField] public partial int Level { get; set; }
    [ZdoField(Default = "inactive")] public partial string Mode { get; set; }
    [ZdoField] public partial ZDOID Target { get; set; }   // vanilla _u/_i pair
}
```

Nest the schema in its component and read/write through the generated
`NetView` accessor — the type is inferred from the class, so there is no type
parameter and nothing else to declare:

```csharp
public partial class MyThing : MonoBehaviour {
    [ZdoSchema("MyMod")]
    public partial struct State {
        [ZdoField] public partial int Level { get; set; }
    }

    void Bump() {
        if (!NetView.IsOwner) return;         // only the owner writes
        var s = NetView.Zdo; s.Level += 1;    // writes go through a local
    }
    int Read() => NetView.Zdo.Level;          // anyone reads
    bool Ready(out State s) => NetView.TryZdo(out s);
}
```

`NetView` (a `ZView<State>`) exposes `.Zdo`, `.TryZdo(out v)`, `.IsValid`,
`.IsOwner`, `.ClaimOwnership()`, `.Raw`. Where you hold a `ZNetView` but are
not the nesting component, bind the same schema with the extensions:
`m_nview.GetZdo<State>()` / `m_nview.TryGetZdo<State>(out var s)`.

The schema name (`"MyMod"`) prefixes every field hash, keeping keys clear of
vanilla and other mods. Generated `StateFieldHash` constants are available for
interop with vanilla keys and `ZDOVars`. Misuse is a compile error
(`ZDO001`–`ZDO006`). RevivalRevived's `DownedPlayerZdo` (top-level, on Player)
and `DownedMarker.View` (nested → gets `NetView`) are the in-repo examples.

## Step 4: RPCs for Multiplayer Communication

RPCs are declared as a typed set. The **RpcTyped** generator (sibling repo
`../rpc-typed`) emits the wire-name constants, the routed invokers, and the
register/unregister companions.

```csharp
[RpcSet("MyMod")]                          // names become "MyMod_<method>"
public partial struct Rpcs {
    [Rpc] public partial void DoThing(int amount, string reason);
}
```

Bind the set to a `ZNetView` and use it:

```csharp
var rpcs = m_nview.GetRpcs<Rpcs>();
rpcs.RegisterDoThing((long sender, int amount, string reason) => {
    // runs on the receiving peer; sender is the caller's peer id
});
rpcs.DoThing(42, "reason");                 // routed to the ZDO owner
rpcs.DoThingToAll(42, "broadcast");         // every peer + locally
rpcs.DoThingTo(peerId, 42, "targeted");     // a specific peer
```

Nest the set in a component for a typed `Rpc` accessor (type inferred, no
`GetRpcs<>`): `Rpc.DoThingToAll(...)`, `Rpc.RegisterDoThing(...)`.

Parameter types are checked at compile time against what `ZRpc` serializes:
`int`, `uint`, `long`, `float`, `double`, `bool`, `string`, `ZPackage`,
`Vector3`, `Quaternion`, `ZDOID`, `HitData` — up to three per RPC. Misuse is a
compile error (`RPC001`–`RPC005`). RevivalRevived's `DownedRpcs` (registered
on Player) is the in-repo example.

## Step 5: Handle ZNetView Lifecycle in Your Components

```csharp
public partial class MyCustomBehaviour : MonoBehaviour {
    [ZdoSchema("MyMod")]
    public partial struct State { [ZdoField] public partial int Level { get; set; } }

    [RpcSet("MyMod")]
    public partial struct Rpcs { [Rpc] public partial void SetLevel(int level); }

    void Awake() {
        // Register handlers once the object exists. Guard: the ZDO may not be
        // valid yet during loading.
        if (!NetView.IsValid) return;
        Rpc.RegisterSetLevel((long sender, int level) => {
            if (!NetView.IsOwner) return;
            var s = NetView.Zdo; s.Level = level;
        });
    }

    void Update() {
        // Always check validity -- the ZDO can be released when the object
        // leaves the active area.
        if (!NetView.IsValid) return;

        // Only the owner runs authoritative logic.
        if (!NetView.IsOwner) return;

        // ... owner-side update ...
    }
}
```

## Step 6: Patching with Harmony to Register Prefabs

The safest hook point for registering custom prefabs:

```csharp
[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
static class ZNetScene_Awake_Patch {
    static void Postfix(ZNetScene __instance) {
        // Register your custom prefabs here
        var prefab = CreateMyPrefab();
        var hash = prefab.name.GetStableHashCode();
        __instance.m_namedPrefabs[hash] = prefab;
        __instance.m_prefabs.Add(prefab);
    }
}
```

## Generators

Typed ZDO schemas and RPC sets come from two sibling repos, referenced as
analyzers: `../zdo-typed` (https://github.com/andres-valdes/zdo-typed) and
`../rpc-typed`. Their `README.md` files are the full reference for the
attributes (`[ZdoSchema]`/`[ZdoField]`, `[RpcSet]`/`[Rpc]`), the `NetView` and
`Rpc` component accessors, and the diagnostics.

## Common Pitfalls

- **Writing to ZDO you don't own:** Data won't sync properly. Always check `IsOwner()` or `ClaimOwnership()` first.
- **Forgetting null/validity checks:** ZDOs can be released when objects leave active area. Always guard with `nview.IsValid()`.
- **Prefab name collisions:** Use a unique prefix for your mod's prefab names.
- **Field hash collisions:** Prefix custom ZDO field names with your mod name.
- **Spawning before ZNetScene is ready:** Only spawn after `ZNetScene.instance` is available (postfix on `ZNetScene.Awake` is safe).
- **Active prefab triggering Awake:** Set `prefab.SetActive(false)` before adding components, or use `DontDestroyOnLoad`.
