using UnityEngine;
using UnityEngine.UI;

namespace RevivalRevived.Components;

/// <summary>
/// Radial progress circle shown while a revive is being channeled (Hold mode).
///
/// ZDO-driven like the rest of the mod: every frame it scans the loaded players
/// for one that is downed with a non-zero replicated
/// <see cref="DownedMarker.View.ReviveProgress"/> and shows that progress. Because
/// the progress field replicates, the circle appears for the reviver, the
/// downed player, and any bystander -- no RPC plumbing.
/// </summary>
public class ReviveProgressUI : MonoBehaviour {
    private static ReviveProgressUI? s_instance;

    /// <summary>Visible this frame (test hook).</summary>
    public static bool Visible { get; private set; }
    /// <summary>Current fill 0-1 (test hook).</summary>
    public static float Fill { get; private set; }

    private GameObject? m_root;
    private Image? m_bg;
    private Image? m_fill;

    public static void Ensure() {
        if (s_instance != null) return;
        var go = new GameObject("RevivalRevived_ReviveProgressUI");
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<ReviveProgressUI>();
    }

    private void Awake() {
        var canvasGo = new GameObject("Canvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;

        var sprite = MakeDiscSprite();

        m_bg = MakeImage(canvasGo.transform, "bg", sprite, new Color(0f, 0f, 0f, 0.55f), 84f);
        m_fill = MakeImage(canvasGo.transform, "fill", sprite, DownedMarker.ReviveGreen, 72f);
        m_fill.type = Image.Type.Filled;
        m_fill.fillMethod = Image.FillMethod.Radial360;
        m_fill.fillOrigin = (int)Image.Origin360.Top;
        m_fill.fillClockwise = true;
        m_fill.fillAmount = 0f;

        m_root = canvasGo;
        m_root.SetActive(false);
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

    private void Update() {
        float progress = 0f;
        // Any downed player with channel progress (replicated ZDO) drives the UI.
        foreach (var p in Player.GetAllPlayers()) {
            if (p == null || !p.IsDowned()) continue;
            progress = Mathf.Max(progress, p.GetReviveProgress());
        }

        // Never over-full: whatever replicates in, the circle shows at most a
        // complete ring.
        progress = Mathf.Clamp01(progress);

        bool show = progress > 0.01f;
        if (m_root != null && m_root.activeSelf != show) m_root.SetActive(show);
        if (show && m_fill != null) m_fill.fillAmount = progress;

        Visible = show;
        Fill = show ? progress : 0f;
    }
}
