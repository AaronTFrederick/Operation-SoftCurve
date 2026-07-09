using BepInEx;
using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CrosshairMod
{
    [BepInPlugin("com.peakzelo.crosshair", "Crosshair", "3.0.2")]
    [BepInProcess("Project Hardline.exe")]
    public class CrosshairMod : BaseUnityPlugin
    {
        // Keybinds - made public static so FFAMod can access them
        public static KeyCode crosshairMenuKey = KeyCode.F9;

        // Crosshair Menu
        private bool showCrosshairMenu = false;
        private Rect crosshairWindowRect = new(350, 20, 350, 500);

        // Crosshair Settings (will be loaded from config)
        private bool customCrosshairEnabled;
        private float crosshairSize;
        private float crosshairThickness;
        private float crosshairGap;
        private Color crosshairColor;
        private bool showDot;
        private float dotSize;
        private bool showOutline;
        private Color outlineColor;
        private int crosshairStyle;
        private bool hideGameCrosshair;

        // Configuration entries
        private ConfigEntry<bool> configEnabled;
        private ConfigEntry<float> configSize;
        private ConfigEntry<float> configThickness;
        private ConfigEntry<float> configGap;
        private ConfigEntry<float> configColorR;
        private ConfigEntry<float> configColorG;
        private ConfigEntry<float> configColorB;
        private ConfigEntry<bool> configShowDot;
        private ConfigEntry<float> configDotSize;
        private ConfigEntry<bool> configShowOutline;
        private ConfigEntry<int> configStyle;
        private ConfigEntry<bool> configHideGameCrosshair;

        // UI Styles
        private GUIStyle buttonStyle;
        private GUIStyle windowStyle;
        private GUIStyle labelStyle;
        private GUIStyle creditStyle;
        private bool stylesInitialized = false;

        // Cached UI strings (avoids per-OnGUI string allocations)
        private string clientLabel;

        // Crosshair-hiding state
        private const float CrosshairScanInterval = 1f;
        private float nextCrosshairScan;
        private bool crosshairHiddenLastFrame;
        private readonly List<Behaviour> hiddenComponents = [];
        private readonly List<GameObject> hiddenObjects = [];
        private bool scanErrorLogged;

        private static readonly string[] CrosshairKeywords = ["crosshair", "reticle", "aim", "sight", "cursor"];

        // Precomputed unit circle for the circle crosshair (avoids per-frame trig)
        private const int CircleSegments = 24;
        private static readonly Vector2[] UnitCircle = BuildUnitCircle(CircleSegments);

        private static Vector2[] BuildUnitCircle(int segments)
        {
            var points = new Vector2[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float angle = (i / (float)segments) * 2f * Mathf.PI;
                points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            return points;
        }

        private void Awake()
        {
            Logger.LogInfo("Crosshair Mod initialized!");

            // Bind configuration entries (crosshair OFF by default)
            configEnabled = Config.Bind("Crosshair", "Enabled", false, "Custom Crosshair Enabled");
            configSize = Config.Bind("Crosshair", "Size", 20f, "Crosshair Size");
            configThickness = Config.Bind("Crosshair", "Thickness", 2f, "Crosshair Thickness");
            configGap = Config.Bind("Crosshair", "Gap", 5f, "Crosshair Gap");
            configColorR = Config.Bind("Crosshair", "ColorR", 0f, "Color Red Component");
            configColorG = Config.Bind("Crosshair", "ColorG", 1f, "Color Green Component");
            configColorB = Config.Bind("Crosshair", "ColorB", 0f, "Color Blue Component");
            configShowDot = Config.Bind("Crosshair", "ShowDot", true, "Show Center Dot");
            configDotSize = Config.Bind("Crosshair", "DotSize", 3f, "Dot Size");
            configShowOutline = Config.Bind("Crosshair", "ShowOutline", true, "Show Outline");
            configStyle = Config.Bind("Crosshair", "Style", 0, "Crosshair Style (0=Cross, 1=T, 2=Circle, 3=Square)");
            configHideGameCrosshair = Config.Bind("Crosshair", "HideGameCrosshair", false, "Hide Game Crosshair");

            // Load configuration
            LoadConfiguration();

            // Build static UI labels once (keybinds don't change at runtime)
            clientLabel = $"Press {crosshairMenuKey} for Crosshair Menu";

            Logger.LogInfo($"Crosshair loaded: Enabled={customCrosshairEnabled}, Size={crosshairSize}, Style={crosshairStyle}");
        }

        private void LoadConfiguration()
        {
            customCrosshairEnabled = configEnabled.Value;
            crosshairSize = configSize.Value;
            crosshairThickness = configThickness.Value;
            crosshairGap = configGap.Value;
            crosshairColor = new Color(configColorR.Value, configColorG.Value, configColorB.Value);
            showDot = configShowDot.Value;
            dotSize = configDotSize.Value;
            showOutline = configShowOutline.Value;
            crosshairStyle = configStyle.Value;
            hideGameCrosshair = configHideGameCrosshair.Value;
            outlineColor = Color.black; // Outline is always black
        }

        private void SaveConfiguration()
        {
            configEnabled.Value = customCrosshairEnabled;
            configSize.Value = crosshairSize;
            configThickness.Value = crosshairThickness;
            configGap.Value = crosshairGap;
            configColorR.Value = crosshairColor.r;
            configColorG.Value = crosshairColor.g;
            configColorB.Value = crosshairColor.b;
            configShowDot.Value = showDot;
            configDotSize.Value = dotSize;
            configShowOutline.Value = showOutline;
            configStyle.Value = crosshairStyle;
            configHideGameCrosshair.Value = hideGameCrosshair;

            Config.Save();
            Logger.LogInfo("Crosshair configuration saved!");
        }

        private void Update()
        {
            // Toggle crosshair menu with configurable key (Everyone)
            if (Input.GetKeyDown(crosshairMenuKey))
            {
                showCrosshairMenu = !showCrosshairMenu;
                Logger.LogInfo($"Crosshair menu toggled: {showCrosshairMenu}");
            }
        }

        private void OnGUI()
        {
            if (!stylesInitialized)
            {
                InitializeStyles();
            }
            // Draw crosshair menu (Everyone)
            if (showCrosshairMenu)
            {
                crosshairWindowRect = GUI.Window(12346, crosshairWindowRect, DrawCrosshairWindow, "Crosshair Settings", windowStyle);
            }

            // Pure drawing below this point - only needed during the repaint event.
            // OnGUI runs multiple times per frame (layout, input, repaint); skipping
            // the non-repaint passes avoids doing the crosshair math for nothing.
            if (Event.current.type != EventType.Repaint) return;

            // Draw custom crosshair
            if (customCrosshairEnabled)
            {
                DrawCustomCrosshair();
            }

            // Mod credit in top right corner
            GUI.Label(new Rect(Screen.width - 120, 5, 110, 20), "Mod by PeakZelo & VALIDUSER", creditStyle);
        }

        private void InitializeStyles()
        {
            // Button style
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                padding = new RectOffset(10, 10, 5, 5)
            };
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.red;

            // Window style
            windowStyle = new GUIStyle(GUI.skin.window)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold
            };

            // Label style
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
            labelStyle.normal.textColor = Color.yellow;

            // Credit style
            creditStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.UpperRight
            };
            creditStyle.normal.textColor = new Color(1f, 1f, 1f, 0.5f); // Semi-transparent white

            stylesInitialized = true;
        }

        // ===== CROSSHAIR FUNCTIONS =====

        private void DrawCrosshairWindow(int windowID)
        {
            GUILayout.BeginVertical();

            // Enable/Disable Custom Crosshair
            GUILayout.BeginHorizontal();
            GUILayout.Label("Custom Crosshair:", GUILayout.Width(150));
            customCrosshairEnabled = GUILayout.Toggle(customCrosshairEnabled, customCrosshairEnabled ? "Enabled" : "Disabled");
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Crosshair Style
            GUILayout.Label("Crosshair Style:", labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cross", crosshairStyle == 0 ? buttonStyle : GUI.skin.button)) crosshairStyle = 0;
            if (GUILayout.Button("T-Shape", crosshairStyle == 1 ? buttonStyle : GUI.skin.button)) crosshairStyle = 1;
            if (GUILayout.Button("Circle", crosshairStyle == 2 ? buttonStyle : GUI.skin.button)) crosshairStyle = 2;
            if (GUILayout.Button("Square", crosshairStyle == 3 ? buttonStyle : GUI.skin.button)) crosshairStyle = 3;
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Size Slider
            GUILayout.Label($"Size: {crosshairSize:F0}");
            crosshairSize = GUILayout.HorizontalSlider(crosshairSize, 5f, 50f);

            // Thickness Slider
            GUILayout.Label($"Thickness: {crosshairThickness:F0}");
            crosshairThickness = GUILayout.HorizontalSlider(crosshairThickness, 1f, 10f);

            // Gap Slider
            GUILayout.Label($"Gap: {crosshairGap:F0}");
            crosshairGap = GUILayout.HorizontalSlider(crosshairGap, 0f, 20f);

            GUILayout.Space(10);

            // Color Selection
            GUILayout.Label("Crosshair Color:", labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Green")) crosshairColor = Color.green;
            if (GUILayout.Button("Red")) crosshairColor = Color.red;
            if (GUILayout.Button("White")) crosshairColor = Color.white;
            if (GUILayout.Button("Cyan")) crosshairColor = Color.cyan;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Yellow")) crosshairColor = Color.yellow;
            if (GUILayout.Button("Magenta")) crosshairColor = Color.magenta;
            if (GUILayout.Button("Blue")) crosshairColor = new Color(0.3f, 0.5f, 1f);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Center Dot
            GUILayout.BeginHorizontal();
            GUILayout.Label("Center Dot:", GUILayout.Width(150));
            showDot = GUILayout.Toggle(showDot, showDot ? "On" : "Off");
            GUILayout.EndHorizontal();
            if (showDot)
            {
                GUILayout.Label($"Dot Size: {dotSize:F0}");
                dotSize = GUILayout.HorizontalSlider(dotSize, 1f, 10f);
            }

            GUILayout.Space(10);

            // Outline
            GUILayout.BeginHorizontal();
            GUILayout.Label("Outline:", GUILayout.Width(150));
            showOutline = GUILayout.Toggle(showOutline, showOutline ? "On" : "Off");
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Hide Game Crosshair Toggle
            GUILayout.BeginHorizontal();
            GUILayout.Label("Hide Game Crosshair:", GUILayout.Width(150));
            hideGameCrosshair = GUILayout.Toggle(hideGameCrosshair, hideGameCrosshair ? "On" : "Off");
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Save Button
            if (GUILayout.Button("Save Settings", buttonStyle))
            {
                SaveConfiguration();
            }

            // Close Button
            if (GUILayout.Button("Close Menu", buttonStyle))
            {
                showCrosshairMenu = false;
                SaveConfiguration(); // Auto-save on close
            }

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DrawCustomCrosshair()
        {
            Vector2 center = new(Screen.width / 2f, Screen.height / 2f);

            switch (crosshairStyle)
            {
                case 0: // Cross
                    DrawCross(center);
                    break;
                case 1: // T-Shape
                    DrawTShape(center);
                    break;
                case 2: // Circle
                    DrawCircle(center);
                    break;
                case 3: // Square
                    DrawSquare(center);
                    break;
            }

            // Draw center dot
            if (showDot)
            {
                DrawDot(center);
            }
        }

        private void DrawCross(Vector2 center)
        {
            // Top line
            DrawLine(
                new Vector2(center.x - crosshairThickness / 2, center.y - crosshairGap - crosshairSize),
                new Vector2(crosshairThickness, crosshairSize),
                crosshairColor, outlineColor, showOutline
            );

            // Bottom line
            DrawLine(
                new Vector2(center.x - crosshairThickness / 2, center.y + crosshairGap),
                new Vector2(crosshairThickness, crosshairSize),
                crosshairColor, outlineColor, showOutline
            );

            // Left line
            DrawLine(
                new Vector2(center.x - crosshairGap - crosshairSize, center.y - crosshairThickness / 2),
                new Vector2(crosshairSize, crosshairThickness),
                crosshairColor, outlineColor, showOutline
            );

            // Right line
            DrawLine(
                new Vector2(center.x + crosshairGap, center.y - crosshairThickness / 2),
                new Vector2(crosshairSize, crosshairThickness),
                crosshairColor, outlineColor, showOutline
            );
        }

        private void DrawTShape(Vector2 center)
        {
            // Top line
            DrawLine(
                new Vector2(center.x - crosshairThickness / 2, center.y - crosshairGap - crosshairSize),
                new Vector2(crosshairThickness, crosshairSize),
                crosshairColor, outlineColor, showOutline
            );

            // Left line
            DrawLine(
                new Vector2(center.x - crosshairGap - crosshairSize, center.y - crosshairThickness / 2),
                new Vector2(crosshairSize, crosshairThickness),
                crosshairColor, outlineColor, showOutline
            );

            // Right line
            DrawLine(
                new Vector2(center.x + crosshairGap, center.y - crosshairThickness / 2),
                new Vector2(crosshairSize, crosshairThickness),
                crosshairColor, outlineColor, showOutline
            );
        }

        private void DrawCircle(Vector2 center)
        {
            float radius = crosshairSize + crosshairGap;

            // Uses precomputed unit-circle points instead of Cos/Sin every frame
            for (int i = 0; i < CircleSegments; i++)
            {
                Vector2 p1 = center + UnitCircle[i] * radius;
                Vector2 p2 = center + UnitCircle[i + 1] * radius;

                DrawLineSegment(p1, p2, crosshairThickness, crosshairColor, outlineColor, showOutline);
            }
        }

        private void DrawSquare(Vector2 center)
        {
            float halfSize = crosshairSize + crosshairGap;

            // Top
            DrawLine(
                new Vector2(center.x - halfSize, center.y - halfSize),
                new Vector2(halfSize * 2, crosshairThickness),
                crosshairColor, outlineColor, showOutline
            );

            // Bottom
            DrawLine(
                new Vector2(center.x - halfSize, center.y + halfSize - crosshairThickness),
                new Vector2(halfSize * 2, crosshairThickness),
                crosshairColor, outlineColor, showOutline
            );

            // Left
            DrawLine(
                new Vector2(center.x - halfSize, center.y - halfSize),
                new Vector2(crosshairThickness, halfSize * 2),
                crosshairColor, outlineColor, showOutline
            );

            // Right
            DrawLine(
                new Vector2(center.x + halfSize - crosshairThickness, center.y - halfSize),
                new Vector2(crosshairThickness, halfSize * 2),
                crosshairColor, outlineColor, showOutline
            );
        }

        private void DrawDot(Vector2 center)
        {
            float halfDot = dotSize / 2f;
            DrawLine(
                new Vector2(center.x - halfDot, center.y - halfDot),
                new Vector2(dotSize, dotSize),
                crosshairColor, outlineColor, showOutline
            );
        }

        private void DrawLine(Vector2 position, Vector2 size, Color color, Color outline, bool withOutline)
        {
            if (withOutline)
            {
                // Draw outline
                GUI.color = outline;
                GUI.DrawTexture(new Rect(position.x - 1, position.y - 1, size.x + 2, size.y + 2), Texture2D.whiteTexture);
            }

            // Draw main line
            GUI.color = color;
            GUI.DrawTexture(new Rect(position.x, position.y, size.x, size.y), Texture2D.whiteTexture);
            GUI.color = Color.white; // Reset
        }

        private void DrawLineSegment(Vector2 start, Vector2 end, float thickness, Color color, Color outline, bool withOutline)
        {
            Vector2 diff = end - start;
            float length = diff.magnitude;
            float angle = Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;

            Matrix4x4 matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, start);

            DrawLine(start, new Vector2(length, thickness), color, outline, withOutline);

            GUI.matrix = matrix;
        }

        // ===== GAME CROSSHAIR HIDING =====

        private void LateUpdate()
        {
            // Restore anything we hid if the user turned the option off
            if (!hideGameCrosshair)
            {
                if (crosshairHiddenLastFrame)
                {
                    RestoreGameCrosshair();
                    crosshairHiddenLastFrame = false;
                }
                return;
            }

            crosshairHiddenLastFrame = true;

            // Throttle the scan - FindObjectsOfType is expensive and UI crosshairs
            // don't spawn every frame. Once a second is plenty.
            if (Time.unscaledTime < nextCrosshairScan) return;
            nextCrosshairScan = Time.unscaledTime + CrosshairScanInterval;

            try
            {
                HideGameCrosshairScan();
            }
            catch (System.Exception ex)
            {
                // Log once instead of silently swallowing a failure that
                // would otherwise repeat every scan
                if (!scanErrorLogged)
                {
                    scanErrorLogged = true;
                    Logger.LogWarning($"Crosshair-hide scan failed: {ex}");
                }
            }
        }

        // Allocation-free case-insensitive keyword match (no ToLower per object)
        private static bool IsCrosshairName(string name)
        {
            foreach (var kw in CrosshairKeywords)
            {
                if (name.IndexOf(kw, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private void HideGameCrosshairScan()
        {
            // Method 1: Disable Image components
            Image[] images = Object.FindObjectsOfType<Image>();
            foreach (Image img in images)
            {
                if (img.enabled && img.gameObject.activeSelf && IsCrosshairName(img.gameObject.name))
                {
                    img.enabled = false;
                    hiddenComponents.Add(img);
                }
            }

            // Method 2: Disable UI GameObjects with crosshair-related names.
            // Restricted to RectTransform (UI) objects so a broad keyword like
            // "aim" can't deactivate gameplay objects (AimController, etc.)
            RectTransform[] uiTransforms = Object.FindObjectsOfType<RectTransform>();
            foreach (RectTransform rt in uiTransforms)
            {
                GameObject obj = rt.gameObject;
                if (obj.activeSelf && IsCrosshairName(obj.name))
                {
                    string name = obj.name;
                    // Only disable if it's not our mod window
                    if (name.IndexOf("window", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("menu", System.StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        obj.SetActive(false);
                        hiddenObjects.Add(obj);
                    }
                }
            }

            // Method 3: Disable Canvas components (common for UI crosshairs)
            Canvas[] canvases = Object.FindObjectsOfType<Canvas>();
            foreach (Canvas canvas in canvases)
            {
                if (canvas.enabled && canvas.gameObject.activeSelf && IsCrosshairName(canvas.gameObject.name))
                {
                    canvas.enabled = false;
                    hiddenComponents.Add(canvas);
                }
            }
        }

        private void RestoreGameCrosshair()
        {
            foreach (Behaviour component in hiddenComponents)
            {
                if (component != null) component.enabled = true;
            }
            hiddenComponents.Clear();

            foreach (GameObject obj in hiddenObjects)
            {
                obj?.SetActive(true);
            }
            hiddenObjects.Clear();

            scanErrorLogged = false;
            Logger.LogInfo("Game crosshair restored.");
        }
    }
}