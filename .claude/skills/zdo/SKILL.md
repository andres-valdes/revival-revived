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

Define hash constants for your custom fields:

```csharp
static readonly int s_myLevel = "MyMod_Level".GetStableHashCode();
static readonly int s_myState = "MyMod_State".GetStableHashCode();
```

Read/write through the ZNetView's ZDO:

```csharp
var nview = GetComponent<ZNetView>();
var zdo = nview.GetZDO();

// Write (must be owner)
if (nview.IsOwner()) {
    zdo.Set(s_myLevel, 5);
    zdo.Set(s_myState, "active");
}

// Read (anyone can read)
int level = zdo.GetInt(s_myLevel);
string state = zdo.GetString(s_myState, "inactive");
```

**Prefix custom field names** (e.g., `"MyMod_Level"`) to avoid collisions with vanilla or other mods.

## Step 4: Register RPCs for Multiplayer Communication

```csharp
void Awake() {
    var nview = GetComponent<ZNetView>();

    // Register handler
    nview.Register<int, string>("MyMod_DoThing", RPC_DoThing);
}

void RPC_DoThing(long sender, int amount, string reason) {
    // Runs on the peer that owns this object
    Plugin.Logger.LogInfo($"DoThing called: {amount} because {reason}");
}

void RequestDoThing() {
    var nview = GetComponent<ZNetView>();

    // Send to owner
    nview.InvokeRPC("MyMod_DoThing", 42, "because reasons");

    // Send to everyone
    nview.InvokeRPC(ZRoutedRpc.Everybody, "MyMod_DoThing", 42, "broadcast");

    // Send to specific peer
    nview.InvokeRPC(targetPeerID, "MyMod_DoThing", 42, "targeted");
}
```

## Step 5: Handle ZNetView Lifecycle in Your Components

```csharp
public class MyCustomBehaviour : MonoBehaviour {
    private ZNetView m_nview;

    void Awake() {
        m_nview = GetComponent<ZNetView>();

        // Guard: ZNetView might not have a valid ZDO yet during loading
        if (m_nview.GetZDO() == null) return;

        m_nview.Register<int>("MyMod_SetLevel", RPC_SetLevel);
    }

    void Update() {
        // Always check validity -- ZDO can be released if object leaves active area
        if (m_nview == null || !m_nview.IsValid()) return;

        // Only owner runs authoritative logic
        if (!m_nview.IsOwner()) return;

        // Your update logic here
    }

    void RPC_SetLevel(long sender, int level) {
        if (!m_nview.IsOwner()) return;
        m_nview.GetZDO().Set(s_myLevel, level);
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

## Strongly-Typed ZDO Access: ZdoTyped (PREFER THIS)

This workspace has a source generator for typed ZDO schemas: **`../zdo-typed`**
(sibling repo, published at https://github.com/andres-valdes/zdo-typed). Read
its `README.md` before writing raw `zdo.GetFloat(hash)`-style code -- new ZDO
fields should be declared as schemas, not loose hash constants.

```csharp
[ZdoSchema("MyMod")]
public partial struct MarkerZdo {
    [ZdoField] public partial float ReviveProgress { get; set; }
    [ZdoField(Name = "ownerName", NoPrefix = true)] public partial string OwnerName { get; set; }
    [ZdoField] public partial ZDOID Player { get; set; }   // vanilla _u/_i pair
}

var view = m_nview.GetZdo<MarkerZdo>();          // typed view over the live ZDO
view.ReviveProgress = 0.5f;                      // networked write, hash precomputed
if (m_nview.TryGetZdo<MarkerZdo>(out var v)) ... // validity-guarded
class Marker : ZdoComponent<MarkerZdo> { ... }   // attach-by-generic base
```

Keys hash via GetStableHashCode at COMPILE time (parity-tested against
assembly_utils.dll); misuse is a compile error (ZDO001-ZDO006). RevivalRevived's
`DownedPlayerZdo` and `DownedMarker.View` are the in-repo examples.

## Common Pitfalls

- **Writing to ZDO you don't own:** Data won't sync properly. Always check `IsOwner()` or `ClaimOwnership()` first.
- **Forgetting null/validity checks:** ZDOs can be released when objects leave active area. Always guard with `nview.IsValid()`.
- **Prefab name collisions:** Use a unique prefix for your mod's prefab names.
- **Field hash collisions:** Prefix custom ZDO field names with your mod name.
- **Spawning before ZNetScene is ready:** Only spawn after `ZNetScene.instance` is available (postfix on `ZNetScene.Awake` is safe).
- **Active prefab triggering Awake:** Set `prefab.SetActive(false)` before adding components, or use `DontDestroyOnLoad`.
