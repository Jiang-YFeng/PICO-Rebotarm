using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;

// UI hit marker only: no collider and no Graphic, so it cannot intercept clicks.
[DefaultExecutionOrder(100)]
public sealed class SettingsRayPointer : MonoBehaviour
{
    XRRayInteractor ray;
    GameObject marker;
    LineRenderer border, ring;
    Material material;
    bool wasPressed;

    void Awake()
    {
        ray = GetComponent<XRRayInteractor>();
        marker = new GameObject("UI Ray Hit Ring");
        marker.transform.SetParent(transform, false);
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) { enabled = false; return; }
        material = new Material(shader);
        border = MakeRing("Outline", 0.16f, Color.black);
        ring = MakeRing("Pointer", 0.075f, Color.cyan);
        ring.transform.localPosition = new Vector3(0, 0, -0.01f);
        marker.SetActive(false);
    }

    LineRenderer MakeRing(string name, float width, Color color)
    {
        var child = new GameObject(name);
        child.transform.SetParent(marker.transform, false);
        var line = child.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = false; line.loop = true;
        line.positionCount = 48; line.widthMultiplier = width;
        line.startColor = line.endColor = color;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        for (int i = 0; i < 48; i++)
        {
            float angle = i * Mathf.PI * 2 / 48;
            line.SetPosition(i, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * 0.5f);
        }
        return line;
    }

    void LateUpdate()
    {
        bool pressed = ray && ray.TryGetUIModel(out var model) && model.select;
        if (!ray || !ray.isActiveAndEnabled || !ray.TryGetCurrentUIRaycastResult(out var hit) || !hit.gameObject)
        {
            marker.SetActive(false); wasPressed = pressed; return;
        }
        var camera = Camera.main;
        Vector3 viewer = camera ? camera.transform.position : ray.transform.position;
        Vector3 towardViewer = (viewer - hit.worldPosition).normalized;
        marker.SetActive(true);
        marker.transform.position = hit.worldPosition + towardViewer * 0.008f;
        marker.transform.rotation = Quaternion.LookRotation(-towardViewer);
        float size = Mathf.Clamp(Vector3.Distance(viewer, hit.worldPosition) * 0.012f, 0.009f, 0.04f);
        border.widthMultiplier = size * 0.16f;
        ring.widthMultiplier = size * 0.075f;
        var parentScale = transform.lossyScale;
        marker.transform.localScale = new Vector3(size / Mathf.Max(Mathf.Abs(parentScale.x), 0.001f),
            size / Mathf.Max(Mathf.Abs(parentScale.y), 0.001f), size / Mathf.Max(Mathf.Abs(parentScale.z), 0.001f));
        var selectable = hit.gameObject.GetComponentInParent<Selectable>();
        bool actionable = selectable && selectable.IsActive() && selectable.IsInteractable();
        Color color = pressed && actionable ? new Color(1f, 0.65f, 0.1f) : actionable ? Color.green : Color.cyan;
        ring.startColor = ring.endColor = color;
        if (pressed && !wasPressed && actionable && ray.xrController)
            ray.xrController.SendHapticImpulse(0.2f, 0.035f);
        wasPressed = pressed;
    }

    void OnDisable() { if (marker) marker.SetActive(false); }
    void OnDestroy()
    {
        if (marker) Destroy(marker);
        if (material) Destroy(material);
    }
}
