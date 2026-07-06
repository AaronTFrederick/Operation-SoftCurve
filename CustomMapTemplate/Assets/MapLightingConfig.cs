// ============================================================
//  MapLightingConfig — add to any child of MapGeometry to
//  customise the sun, ambient light, and fog for your map.
//
//  The exporter reads these fields, serialises them as JSON,
//  and packs them into the bundle. The mod applies them when
//  the map loads. The component itself is stripped from the
//  bundle — only the JSON travels.
//
//  Changes you make in the Inspector are previewed live in
//  the Unity editor (via OnValidate). Export to see them in-game.
// ============================================================
using UnityEngine;

[ExecuteAlways]
public class MapLightingConfig : MonoBehaviour
{
    [Header("Sun (Directional Light)")]
    [Tooltip("Colour of the directional light.")]
    public Color  sunColor     = new Color(1.0f, 0.96f, 0.84f);

    [Tooltip("Brightness of the directional light (0 = dark, 1 = normal, 2 = very bright).")]
    [Range(0f, 3f)]
    public float  sunIntensity = 1.0f;

    [Tooltip("Euler angles for the sun direction. X=elevation (50 = mid-afternoon), Y=azimuth.")]
    public Vector3 sunRotation = new Vector3(50f, -30f, 0f);

    [Header("Ambient Light")]
    [Tooltip("Flat ambient colour that fills shadows. Keep dark for realistic look.")]
    public Color  ambientColor = new Color(0.21f, 0.23f, 0.26f);

    [Header("Fog")]
    [Tooltip("Enable distance fog.")]
    public bool   fogEnabled = false;

    [Tooltip("Fog colour — usually matches the sky or horizon colour.")]
    public Color  fogColor   = new Color(0.5f, 0.5f, 0.5f);

    [Tooltip("Exponential-squared fog density. 0.005 = very light, 0.05 = heavy.")]
    [Range(0f, 0.1f)]
    public float  fogDensity = 0.01f;

#if UNITY_EDITOR
    // ── Live preview in the editor ───────────────────────────────────────────
    void OnValidate()
    {
        // Delay one frame so Unity finishes its own validation pass first.
        UnityEditor.EditorApplication.delayCall += ApplyToScene;
    }

    void ApplyToScene()
    {
        if (this == null) return; // component was destroyed during the delay

        RenderSettings.ambientLight = ambientColor;
        RenderSettings.fog          = fogEnabled;
        RenderSettings.fogColor     = fogColor;
        RenderSettings.fogMode      = FogMode.ExponentialSquared;
        RenderSettings.fogDensity   = fogDensity;

        // Find the first directional light in the scene.
        foreach (var l in FindObjectsOfType<Light>())
        {
            if (l.type != LightType.Directional) continue;
            l.color     = sunColor;
            l.intensity = sunIntensity;
            l.transform.rotation = Quaternion.Euler(sunRotation);
            break;
        }
    }
#endif
}
