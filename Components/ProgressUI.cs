using UnityEngine;
using UnityEngine.UI;

namespace ReviveAllies.Components;

/// <summary>
/// A radial progress circle below the crosshair, created on demand and injected
/// with the timer that drives it (<see cref="IDecayingProgress"/>) plus a style.
/// As a MonoBehaviour it owns the read-and-render loop -- each frame it reads the
/// timer's <see cref="IDecayingProgress.Fraction"/> and updates the circle -- and
/// it despawns itself when the timer raises <see cref="IDecayingProgress.Finished"/>
/// (or the timer's backing object is destroyed). A short idle fallback covers a
/// timer that emptied without the Finished edge ever arriving. No global singleton.
/// </summary>
public class ProgressUI : MonoBehaviour {
    /// <summary>Give-up accent (red), distinct from the revive green.</summary>
    public static readonly Color GiveUpRed = new(0.9f, 0.15f, 0.15f);

    /// <summary>Visible this frame (test hook).</summary>
    public static bool Visible { get; private set; }
    /// <summary>Current fill 0-1 (test hook).</summary>
    public static float Fill { get; private set; }
    /// <summary>True when the shown circle is the red give-up one (test hook).</summary>
    public static bool GivingUp { get; private set; }

    /// <summary>Fallback despawn if the fill idles at zero without a Finished signal.</summary>
    private const float IdleTimeout = 0.5f;

    private IDecayingProgress? m_source;
    private Color m_color;
    private bool m_isGiveUp;
    private float m_idle;

    private GameObject? m_root;
    private Image? m_fillImg;

    /// <summary>Create a circle driven by <paramref name="source"/>, in the given style.</summary>
    public static ProgressUI Create(IDecayingProgress source, Color color, bool isGiveUp) {
        var go = new GameObject("ReviveAllies_ProgressUI");
        DontDestroyOnLoad(go);
        var ui = go.AddComponent<ProgressUI>();
        ui.m_source = source;
        ui.m_color = color;
        ui.m_isGiveUp = isGiveUp;
        source.Finished += ui.OnFinished;
        return ui;
    }

    private void OnFinished() => Close();

    /// <summary>Tear the circle down now. Safe to call more than once.</summary>
    public void Close() {
        if (this != null) Destroy(gameObject);
    }

    private void Awake() {
        var canvasGo = new GameObject("Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;

        var sprite = MakeDiscSprite();

        MakeImage(canvasGo.transform, "bg", sprite, new Color(0f, 0f, 0f, 0.55f), 84f);
        m_fillImg = MakeImage(canvasGo.transform, "fill", sprite, Color.white, 72f);
        m_fillImg.type = Image.Type.Filled;
        m_fillImg.fillMethod = Image.FillMethod.Radial360;
        m_fillImg.fillOrigin = (int)Image.Origin360.Top;
        m_fillImg.fillClockwise = true;
        m_fillImg.fillAmount = 0f;

        m_root = canvasGo;
        m_root.SetActive(false);
    }

    private void Update() {
        // The timer may be a destroyed MonoBehaviour (the marker crumbled) -> despawn.
        if (m_source is Object o && o == null) { Close(); return; }

        float f = Mathf.Clamp01(m_source?.Fraction ?? 0f);
        bool show = f > 0.01f;

        if (m_root != null && m_root.activeSelf != show) m_root.SetActive(show);
        if (show && m_fillImg != null) {
            m_fillImg.fillAmount = f;
            m_fillImg.color = m_color;
        }

        Visible = show;
        Fill = show ? f : 0f;
        GivingUp = show && m_isGiveUp;

        if (show) {
            m_idle = 0f;
        } else {
            m_idle += Time.unscaledDeltaTime;
            if (m_idle >= IdleTimeout) Close();
        }
    }

    private void OnDestroy() {
        if (m_source != null) m_source.Finished -= OnFinished;
        Visible = false;
        Fill = 0f;
        GivingUp = false;
    }

    private static Image MakeImage(Transform parent, string name, Sprite sprite, Color color, float size) {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        var rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, -110f); // just below the crosshair
        rt.sizeDelta = new Vector2(size, size);
        return img;
    }

    /// <summary>Procedural soft-edged disc sprite (no bundled assets needed).</summary>
    private static Sprite MakeDiscSprite() {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.ARGB32, mipChain: false);
        var half = n / 2f;
        for (int y = 0; y < n; y++) {
            for (int x = 0; x < n; x++) {
                var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half));
                float a = Mathf.Clamp01((half - 1f - d) / 2f); // 2px soft edge
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
    }
}
