using System.Collections.Generic;

namespace Automations.Components;

/// <summary>
/// The bridge between the pipe network and whatever a machine actually IS. A port
/// exposes a uniform input/output surface over a vanilla component's real storage
/// (a chest's inventory, a smelter's ore/fuel queue and its output), so the pipe
/// layer (<see cref="Machine"/>) can move items between wildly different machines
/// without knowing their internals. Items are identified by prefab name
/// (e.g. "CopperOre", "Coal", "Copper").
///
/// All methods are called only on the owning peer.
/// </summary>
public interface IMachinePort {
    /// <summary>Whether an upstream pipe may deliver this item at all.</summary>
    bool Accepts(string prefabName);

    /// <summary>How many of this item can currently be delivered in (0 = full/blocked).</summary>
    int InputFree(string prefabName);

    /// <summary>Deliver up to <paramref name="qty"/> in; returns how many were accepted.</summary>
    int Deliver(string prefabName, int qty);

    /// <summary>Items currently available to ship downstream (prefab name -> count).</summary>
    IReadOnlyList<KeyValuePair<string, int>> Outputs();

    /// <summary>Withdraw up to <paramref name="qty"/> for shipment; returns how many were removed.</summary>
    int Withdraw(string prefabName, int qty);
}
