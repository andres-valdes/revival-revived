using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The give-up concern as its own Player component: the client-side decaying
/// timer, its red circle, the Use-hold input (with the test hook), and the local
/// fraction mirror for tests. The downed machine's Waiting/GivingUp states drive
/// it straight off the Player -- <c>player.GetComponent&lt;GiveUp&gt;()</c> -- so
/// the controller holds none of this.
/// </summary>
public class GiveUp : MonoBehaviour {
    /// <summary>How long the downed player must hold Use to give up and die early.</summary>
    public const float Duration = 2f;

    private Player m_player = null!;
    private readonly GiveUpDecayingProgress m_timer = new();
    private ProgressUI? m_ui;
    private bool m_simulate;

    /// <summary>The local give-up hold 0-1 (0 when not giving up). Mirror for the tests.</summary>
    public static float LocalFraction { get; private set; }

    private void Awake() => m_player = GetComponent<Player>();

    private void OnDestroy() {
        m_ui?.Close();
        if (m_player == Player.m_localPlayer) LocalFraction = 0f;
    }

    /// <summary>Is the Use key held for give-up (ignoring menus/chat/console)?</summary>
    public bool Held() {
        if (m_simulate) { m_simulate = false; return true; } // test hook
        if (Chat.instance != null && Chat.instance.HasFocus()) return false;
        if (Console.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible() || TextInput.IsVisible()) return false;
        return ZInput.GetButton("Use") || ZInput.GetButton("JoyUse");
    }

    /// <summary>Test hook: one frame of give-up input.</summary>
    public void SimulateHold() => m_simulate = true;

    /// <summary>Show the red circle once (on entering GivingUp); despawns on the timer's Finished.</summary>
    public void ShowUI() {
        if (m_ui == null) m_ui = ProgressUI.Create(m_timer, ProgressUI.GiveUpRed, isGiveUp: true);
    }

    /// <summary>One held frame: fill. Returns true once the hold is complete.</summary>
    public bool Channel(float dt) {
        m_timer.Channel(dt);
        Mirror();
        return m_timer.Full;
    }

    /// <summary>One idle frame: decay toward zero.</summary>
    public void Decay(float dt) {
        m_timer.Decay(dt);
        Mirror();
    }

    /// <summary>Clear immediately (completion / leaving downed).</summary>
    public void Reset() {
        m_timer.Reset();
        Mirror();
    }

    private void Mirror() {
        if (m_player == Player.m_localPlayer) LocalFraction = m_timer.Fraction;
    }
}
