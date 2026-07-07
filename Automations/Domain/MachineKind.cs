namespace Automations.Domain;

/// <summary>
/// The kinds of machine in the automation graph. Stored as an int on each
/// machine's ZDO (<see cref="MachineView.Kind"/>) so it is authoritative and
/// replicated; the numeric values are the wire format -- append, never reorder.
/// </summary>
public enum MachineKind {
    /// <summary>Infinite raw-material source ("a chest of wood/ore"). Produces its
    /// configured raw item every tick; accepts nothing.</summary>
    Stockpile = 0,

    /// <summary>Pure storage / collector. Accepts and emits anything -- a buffer
    /// between stages and the place a finished factory piles its product.</summary>
    Chest = 1,

    /// <summary>Fixed processor: Wood -> Coal.</summary>
    Kiln = 2,

    /// <summary>Blueprint processor: ore + coal -> bar (which bar is selectable).</summary>
    Smelter = 3,

    /// <summary>Blueprint assembler: combine bars into alloys/parts (the "forge you
    /// set a blueprint on"). Copper + Tin -> Bronze, etc.</summary>
    Assembler = 4,
}
