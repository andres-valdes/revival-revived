using System.Collections.Generic;
using Automations.Domain;
using UnityEngine;

namespace Automations.Components;

/// <summary>
/// Port over an <see cref="Assembler"/>. Storage is the assembler's own ZDO buffer.
/// It accepts any item that some blueprint consumes (so pipes can stock it up for
/// either recipe) and emits ONLY items that some blueprint produces -- never its
/// unprocessed inputs, so a half-fed assembler holds its Copper waiting for Tin
/// instead of leaking it into the collector.
/// </summary>
public class AssemblerPort : IMachinePort {
    private readonly ZNetView m_nview;

    public AssemblerPort(ZNetView nview) { m_nview = nview; }

    public bool Accepts(string prefabName) => Blueprints.IsInput(prefabName);

    public int InputFree(string prefabName) =>
        Blueprints.IsInput(prefabName) ? new AssemblerView(m_nview).FreeSpace(prefabName) : 0;

    public int Deliver(string prefabName, int qty) {
        if (!Blueprints.IsInput(prefabName) && !Blueprints.IsOutput(prefabName)) return 0;
        return new AssemblerView(m_nview).Add(prefabName, qty);
    }

    public IReadOnlyList<KeyValuePair<string, int>> Outputs() {
        var result = new List<KeyValuePair<string, int>>();
        foreach (var kv in new AssemblerView(m_nview).ReadBuffer()) {
            if (Blueprints.IsOutput(kv.Key)) result.Add(kv);
        }
        return result;
    }

    public int Withdraw(string prefabName, int qty) => new AssemblerView(m_nview).Remove(prefabName, qty);
}
