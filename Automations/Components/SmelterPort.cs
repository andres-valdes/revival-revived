using System.Collections.Generic;
using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// Port over a vanilla <see cref="Smelter"/> -- which backs the smelter, the
/// charcoal kiln, the blast furnace, the windmill and the spinning wheel. Input is
/// fed through the smelter's own ore/fuel entry points, so the piece lights its
/// fire, smokes and animates exactly as if a player loaded it by hand. Output is
/// the bars it would normally drop on the ground: a Harmony patch on
/// <see cref="Smelter.Spawn"/> redirects those into a capture buffer on the ZDO
/// (<see cref="MachineView.ReadCapture"/>) when the machine has a pipe to send them
/// down, which this port then ships.
/// </summary>
public class SmelterPort : IMachinePort {
    /// <summary>Cap on captured-but-unshipped output, so a backed-up smelter stops hoarding.</summary>
    public const int CaptureCap = 30;

    private readonly Smelter m_smelter;
    private readonly ZNetView m_nview;

    public SmelterPort(Smelter smelter, ZNetView nview) {
        m_smelter = smelter;
        m_nview = nview;
    }

    private string? FuelName =>
        m_smelter.m_maxFuel > 0 && m_smelter.m_fuelItem != null ? m_smelter.m_fuelItem.gameObject.name : null;

    private bool IsOre(string prefabName) {
        foreach (var c in m_smelter.m_conversion) {
            if (c.m_from != null && c.m_from.gameObject.name == prefabName) return true;
        }
        return false;
    }

    public bool Accepts(string prefabName) => prefabName == FuelName || IsOre(prefabName);

    public int InputFree(string prefabName) {
        if (prefabName == FuelName) {
            int fuel = Mathf.RoundToInt(m_smelter.GetFuel());
            return Mathf.Max(0, m_smelter.m_maxFuel - fuel);
        }
        if (IsOre(prefabName)) {
            return Mathf.Max(0, m_smelter.m_maxOre - m_smelter.GetQueueSize());
        }
        return 0;
    }

    public int Deliver(string prefabName, int qty) {
        if (qty <= 0) return 0;
        // Not an ore or fuel? Then this is a give-back of an item we captured as
        // output (the transfer's rare short-accept path). Return it to the capture
        // buffer rather than losing it.
        if (prefabName != FuelName && !IsOre(prefabName)) {
            var view = new MachineView(m_nview);
            var cap = view.ReadCapture();
            cap[prefabName] = (cap.TryGetValue(prefabName, out var c) ? c : 0) + qty;
            view.WriteCapture(cap);
            return qty;
        }
        int free = InputFree(prefabName);
        int n = Mathf.Min(qty, free);
        // Drive the vanilla entry points so ore-added / fuel-added effects fire and
        // the piece switches to its "working" visual state. We are the owner, so
        // these routed RPCs dispatch locally and synchronously.
        for (int i = 0; i < n; i++) {
            if (prefabName == FuelName) m_nview.InvokeRPC("RPC_AddFuel");
            else m_nview.InvokeRPC("RPC_AddOre", prefabName);
        }
        return n;
    }

    public IReadOnlyList<KeyValuePair<string, int>> Outputs() {
        var result = new List<KeyValuePair<string, int>>();
        foreach (var kv in new MachineView(m_nview).ReadCapture()) result.Add(kv);
        return result;
    }

    public int Withdraw(string prefabName, int qty) {
        if (qty <= 0) return 0;
        var view = new MachineView(m_nview);
        var cap = view.ReadCapture();
        if (!cap.TryGetValue(prefabName, out var have) || have <= 0) return 0;
        int removed = Mathf.Min(have, qty);
        int left = have - removed;
        if (left > 0) cap[prefabName] = left; else cap.Remove(prefabName);
        view.WriteCapture(cap);
        return removed;
    }

    /// <summary>
    /// Called from the Smelter.Spawn patch: try to capture a produced item for the
    /// pipes. Returns true if captured (skip the vanilla ground drop), false to let
    /// it drop normally (no pipe, or capture full).
    /// </summary>
    public bool CaptureOutput(string producedPrefabName, int stack) {
        var view = new MachineView(m_nview);
        var cap = view.ReadCapture();
        int have = cap.TryGetValue(producedPrefabName, out var n) ? n : 0;
        if (have + stack > CaptureCap) return false; // back-pressure: let it drop
        cap[producedPrefabName] = have + stack;
        view.WriteCapture(cap);
        return true;
    }
}
