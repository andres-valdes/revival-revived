using System.Collections.Generic;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// Port over a vanilla <see cref="Container"/> (chest, and anything else with a
/// Container). Storage IS the chest's real inventory, so a piped chest is just a
/// normal chest you can also open and rummage through. It accepts and emits any
/// item -- the universal buffer / source / collector of a factory.
/// </summary>
public class ContainerPort : IMachinePort {
    private readonly Container m_container;

    public ContainerPort(Container container) { m_container = container; }

    private Inventory? Inv => m_container != null ? m_container.GetInventory() : null;

    public bool Accepts(string prefabName) => ResolvePrefab(prefabName) != null;

    public int InputFree(string prefabName) {
        var inv = Inv;
        var prefab = ResolvePrefab(prefabName);
        if (inv == null || prefab == null) return 0;
        // Coarse but safe: if the chest can take at least one, allow a tick's worth.
        return inv.CanAddItem(prefab, 1) ? Plugin.TransferPerTick : 0;
    }

    public int Deliver(string prefabName, int qty) {
        var inv = Inv;
        var prefab = ResolvePrefab(prefabName);
        if (inv == null || prefab == null || qty <= 0) return 0;
        int added = 0;
        // Add one at a time so a full/last-slot chest reports the true accepted count.
        while (added < qty && inv.CanAddItem(prefab, 1) && inv.AddItem(prefab, 1)) added++;
        return added;
    }

    public IReadOnlyList<KeyValuePair<string, int>> Outputs() {
        var result = new List<KeyValuePair<string, int>>();
        var inv = Inv;
        if (inv == null) return result;
        var totals = new Dictionary<string, int>();
        foreach (var item in inv.GetAllItems()) {
            if (item?.m_dropPrefab == null) continue;
            var name = item.m_dropPrefab.name;
            totals[name] = (totals.TryGetValue(name, out var n) ? n : 0) + item.m_stack;
        }
        foreach (var kv in totals) result.Add(kv);
        return result;
    }

    public int Withdraw(string prefabName, int qty) {
        var inv = Inv;
        if (inv == null || qty <= 0) return 0;
        int removed = 0;
        // Copy the list: RemoveItem mutates the inventory.
        foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems())) {
            if (removed >= qty) break;
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != prefabName) continue;
            int take = Mathf.Min(qty - removed, item.m_stack);
            inv.RemoveItem(item, take);
            removed += take;
        }
        return removed;
    }

    private static GameObject? ResolvePrefab(string prefabName) =>
        ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
}
