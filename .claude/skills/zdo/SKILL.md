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

Custom fields live in the ZDO's extra-data dictionaries, keyed by the stable
hash of a name. Precompute the hashes once and read/write through the vanilla
ZDO primitives. Prefix the names with your mod to avoid collisions.

```csharp
static class Keys {
    public static readonly int Level = "MyMod_Level".GetStableHashCode();
    public static readonly int Mode  = "MyMod_Mode".GetStableHashCode();
    public static readonly KeyValuePair<int, int> Target =
        ZDO.GetHashZDOID("MyMod_Target");   // ZDOID stores as a _u/_i long pair
}

var zdo = m_nview.GetZDO();

if (m_nview.IsOwner()) {                     // only the owner writes
    zdo.Set(Keys.Level, 5);
    zdo.Set(Keys.Mode, "active");
    zdo.Set(Keys.Target, someZdoId);
}

int level  = zdo.GetInt(Keys.Level);         // anyone reads
string mode = zdo.GetString(Keys.Mode, "inactive");
ZDOID target = zdo.GetZDOID(Keys.Target);
```

Getters take a default: `GetInt`/`GetFloat`/`GetLong`/`GetBool`/`GetString`/
`GetVec3`/`GetQuaternion(hash, default)` and `GetZDOID(pair)`. Setters are
`Set(hash, value)` (owner only). RevivalRevived keeps its field-hash constants
in `DownedKeys`.

## Step 4: RPCs for Multiplayer Communication

Register handlers on the `ZNetView` and route calls to the owner, a peer, or
everyone. Name the RPCs with a mod prefix (kept in one place as constants) to
avoid collisions.

```csharp
static class Rpcs {
    public const string DoThing = "MyMod_DoThing";
}

void Awake() {
    var nview = GetComponent<ZNetView>();
    nview.Register<int, string>(Rpcs.DoThing, RPC_DoThing);
}

void RPC_DoThing(long sender, int amount, string reason) {
    // runs on the receiving peer; sender is the caller's peer id
}

void Send(ZNetView nview) {
    nview.InvokeRPC(Rpcs.DoThing, 42, "reason");                    // to the ZDO owner
    nview.InvokeRPC(ZNetView.Everybody, Rpcs.DoThing, 42, "all");   // every peer + locally
    nview.InvokeRPC(peerId, Rpcs.DoThing, 42, "targeted");          // a specific peer
}
```

`ZNetView.Register`/the RPC arguments handle up to three parameters of the
types `ZRpc` serializes: `int`, `uint`, `long`, `float`, `double`, `bool`,
`string`, `ZPackage`, `Vector3`, `Quaternion`, `ZDOID`, `HitData`.
RevivalRevived's `DownedKeys` holds its RPC name constants.

## Step 5: Handle ZNetView Lifecycle in Your Components

```csharp
public class MyCustomBehaviour : MonoBehaviour {
    static readonly int s_level = "MyMod_Level".GetStableHashCode();
    private ZNetView m_nview = null!;

    void Awake() {
        m_nview = GetComponent<ZNetView>();
        // Register handlers once the object exists. Guard: the ZDO may not be
        // valid yet during loading.
        if (!m_nview.IsValid()) return;
        m_nview.Register<int>("MyMod_SetLevel", (long sender, int level) => {
            if (!m_nview.IsOwner()) return;
            m_nview.GetZDO().Set(s_level, level);
        });
    }

    void Update() {
        // Always check validity -- the ZDO can be released when the object
        // leaves the active area.
        if (!m_nview.IsValid()) return;

        // Only the owner runs authoritative logic.
        if (!m_nview.IsOwner()) return;

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

## Common Pitfalls

- **Writing to ZDO you don't own:** Data won't sync properly. Always check `IsOwner()` or `ClaimOwnership()` first.
- **Forgetting null/validity checks:** ZDOs can be released when objects leave active area. Always guard with `nview.IsValid()`.
- **Prefab name collisions:** Use a unique prefix for your mod's prefab names.
- **Field hash collisions:** Prefix custom ZDO field names with your mod name.
- **Spawning before ZNetScene is ready:** Only spawn after `ZNetScene.instance` is available (postfix on `ZNetScene.Awake` is safe).
- **Active prefab triggering Awake:** Set `prefab.SetActive(false)` before adding components, or use `DontDestroyOnLoad`.
