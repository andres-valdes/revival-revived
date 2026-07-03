# TODO

## Author the DownedMarker prefab in Unity (replace runtime derivation)

Today `DownedMarker.RegisterPrefab` clones `Player_tombstone` at `ZNetScene.Awake`
and curates the template programmatically (strip `TombStone`/`Container`, delete
ember/flare children, attach `DownedMarker` + `ReviveInteractable`). It works and
instances are pure, but the prefab should be *authored*, not constructed.

Agreed approach (no external mod dependencies — no Jötunn):

1. Extract the tombstone assets from Valheim with AssetRipper
   (`valheim_Data/` → model, materials, textures of `Player_tombstone`).
2. Unity project pinned to Valheim's engine version (**6000.0.61f1** — check
   `BepInEx/LogOutput.log` "Detected Unity version" after game updates).
3. Author `RevivalRevived_DownedMarker.prefab`: tombstone visual + collider +
   Rigidbody + Floating + world-text, WITHOUT ember/flare/TombStone/Container;
   with `DownedMarker` + `ReviveInteractable` attached (reference the mod DLL).
4. `BuildPipeline.BuildAssetBundles` → ship the bundle in the plugin folder;
   `AssetBundle.LoadFromFile` in `Plugin.Awake`, register the prefab in the
   existing `ZNetScene.Awake` postfix (replaces the clone-and-curate body).
5. Publishing caveat: Thunderstore prohibits shipping vanilla art in bundles.
   If/when publishing, either swap to original art or resolve vanilla meshes at
   runtime (which is why the current runtime derivation is the community norm).

Blocked on: Unity editor install (~8 GB) + free license activation (interactive).
