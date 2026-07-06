// ============================================================
//  Project Hardline — Custom Map Exporter
//  Drop this file into Assets/Editor/ in your Unity project.
// ============================================================
using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Linq;

public class CustomMapExporter : EditorWindow
{
    // ── State ────────────────────────────────────────────────────────────────
    private string mapName      = "MyMap";
    private string outputFolder = "";
    private Vector2 scroll;

    // ── Menu items ───────────────────────────────────────────────────────────

    [MenuItem("Window/Custom Maps/Open Exporter")]
    public static void ShowWindow()
    {
        var win = GetWindow<CustomMapExporter>("Custom Map Exporter");
        win.minSize = new Vector2(440, 560);
    }

    /// <summary>
    /// Creates a starter scene structure so the map maker doesn't have to
    /// set anything up manually.
    /// </summary>
    [MenuItem("Window/Custom Maps/Create Map Template in Scene")]
    public static void CreateTemplate()
    {
        // Root object — the exporter looks for this by name.
        GameObject root = new GameObject("MapGeometry");
        Undo.RegisterCreatedObjectUndo(root, "Create Map Template");

        // Ground floor — a flat Cube with a BoxCollider.
        // Cubes always export correctly in AssetBundles (no mesh reference issues).
        // Top surface sits exactly at Y=0; players spawn at Y=1 above it.
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(ground, "Create Map Template");
        ground.name = "Ground";
        ground.transform.SetParent(root.transform);
        ground.transform.localScale    = new Vector3(100, 1, 100); // 100 × 100 m
        ground.transform.localPosition = new Vector3(0, -0.5f, 0); // top surface at Y=0

        // Boundary walls — four Cubes around the edge so players don't walk off.
        CreateWall(root.transform,  new Vector3(  0, 2,  51), new Vector3(100, 5, 1)); // North
        CreateWall(root.transform,  new Vector3(  0, 2, -51), new Vector3(100, 5, 1)); // South
        CreateWall(root.transform,  new Vector3( 51, 2,   0), new Vector3(1, 5, 100)); // East
        CreateWall(root.transform,  new Vector3(-51, 2,   0), new Vector3(1, 5, 100)); // West

        // Two Team-1 spawn points (blue side) — Y=1 so players land on the floor.
        for (int i = 0; i < 2; i++)
        {
            GameObject sp = new GameObject($"T1Spawn_{i}");
            Undo.RegisterCreatedObjectUndo(sp, "Create Map Template");
            sp.transform.SetParent(root.transform);
            sp.transform.localPosition = new Vector3(-4f + i * 4f, 1f, -35f);
            sp.AddComponent<SpawnMarker>().team = 1;
        }

        // Two Team-2 spawn points (red side).
        for (int i = 0; i < 2; i++)
        {
            GameObject sp = new GameObject($"T2Spawn_{i}");
            Undo.RegisterCreatedObjectUndo(sp, "Create Map Template");
            sp.transform.SetParent(root.transform);
            sp.transform.localPosition = new Vector3(-4f + i * 4f, 1f, 35f);
            sp.AddComponent<SpawnMarker>().team = 2;
        }

        // Uplink spawn marker — move this object in the scene view to set where
        // the uplink station will appear in-game. The gold gizmo shows its position.
        GameObject uplinkSpawn = new GameObject("UplinkSpawn");
        Undo.RegisterCreatedObjectUndo(uplinkSpawn, "Create Map Template");
        uplinkSpawn.transform.SetParent(root.transform);
        uplinkSpawn.transform.localPosition = new Vector3(0f, 0f, 0f); // centre of map, on the floor
        uplinkSpawn.AddComponent<UplinkSpawnMarker>();

        // Resupply spawn marker — move this object to set where the ammo resupply
        // station will appear in-game. The green box gizmo shows its position.
        // Add more ResupplySpawn_N objects for additional resupply stations.
        GameObject resupplySpawn = new GameObject("ResupplySpawn_0");
        Undo.RegisterCreatedObjectUndo(resupplySpawn, "Create Map Template");
        resupplySpawn.transform.SetParent(root.transform);
        resupplySpawn.transform.localPosition = new Vector3(15f, 0f, 0f); // offset from centre
        resupplySpawn.AddComponent<ResupplySpawnMarker>();

        // Lighting config — select this child in the Inspector to tune sun colour,
        // ambient light, and fog. Values preview live and are exported as JSON.
        GameObject lightingSetup = new GameObject("LightingSetup");
        Undo.RegisterCreatedObjectUndo(lightingSetup, "Create Map Template");
        lightingSetup.transform.SetParent(root.transform);
        lightingSetup.AddComponent<MapLightingConfig>();

        // Select the root so the user can see it in the hierarchy.
        Selection.activeGameObject = root;
        if (SceneView.lastActiveSceneView != null)
            SceneView.lastActiveSceneView.FrameSelected();

        Debug.Log("[Custom Maps] Template created! " +
                  "Build your geometry under MapGeometry, then use the Exporter window.");
    }

    // ── GUI ──────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        // Title
        var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
        EditorGUILayout.LabelField("Project Hardline — Custom Map Exporter", titleStyle);
        EditorGUILayout.Space(8);

        // Instructions box
        EditorGUILayout.HelpBox(
            "HOW TO MAKE A MAP:\n\n" +
            "1. Click  Custom Maps → Create Map Template in Scene\n" +
            "   (only needed once per project — skip if already done)\n\n" +
            "2. Build your map geometry INSIDE the 'MapGeometry' object.\n" +
            "   Primitives (Cube, Plane…) work out of the box.\n" +
            "   For .fbx or .obj files: drag them into the Project panel, select the\n" +
            "   asset, then in the Inspector → Model tab enable  Read/Write  and click\n" +
            "   Apply. Then drag the model into the scene as a child of MapGeometry.\n\n" +
            "3. (Optional) Assign colours to your objects in the Inspector — the\n" +
            "   exporter preserves each renderer's colour in-game.\n\n" +
            "4. Move the blue/red spawn markers to the correct positions.\n" +
            "   Add more: right-click MapGeometry → Create Empty, add a SpawnMarker\n" +
            "   component and set the Team number (1 or 2).\n\n" +
            "5. Move the gold 'UplinkSpawn' marker to where you want the uplink\n" +
            "   station to appear in-game.\n\n" +
            "5b. Move the green 'ResupplySpawn_0' marker to where you want the ammo\n" +
            "   resupply station. To add more stations: right-click MapGeometry →\n" +
            "   Create Empty, name it ResupplySpawn_1 (etc.), add a ResupplySpawnMarker\n" +
            "   component. Each marker places one resupply station in-game.\n\n" +
            "6. (Optional) Select the 'LightingSetup' child to tune sun colour,\n" +
            "   ambient light, and fog in the Inspector. Changes preview live.\n\n" +
            "7. Enter a map name below, pick a save folder, and click Export.",
            MessageType.Info);

        EditorGUILayout.Space(8);

        // Map name
        EditorGUILayout.LabelField("Map Name", EditorStyles.boldLabel);
        mapName = EditorGUILayout.TextField(mapName);
        if (string.IsNullOrWhiteSpace(mapName)) mapName = "MyMap";

        EditorGUILayout.Space(6);

        // Output folder
        EditorGUILayout.LabelField("Save .bundle File To", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        outputFolder = EditorGUILayout.TextField(outputFolder);
        if (GUILayout.Button("...", GUILayout.Width(28)))
        {
            string picked = EditorUtility.OpenFolderPanel("Choose output folder", outputFolder, "");
            if (!string.IsNullOrEmpty(picked)) outputFolder = picked;
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox(
            "Tip: browse directly to the game's CustomMaps/ folder so the file is " +
            "placed there automatically.",
            MessageType.None);

        EditorGUILayout.Space(8);

        // Validation checklist
        EditorGUILayout.LabelField("Checklist", EditorStyles.boldLabel);
        Validate(out bool hasRoot, out int t1, out int t2);
        Check(hasRoot,                         "MapGeometry object found in scene");
        Check(t1 >= 1, $"Team 1 spawn point(s): {t1} found  (need ≥ 1)");
        Check(t2 >= 1, $"Team 2 spawn point(s): {t2} found  (need ≥ 1)");
        Check(!string.IsNullOrWhiteSpace(outputFolder), "Output folder selected");

        EditorGUILayout.Space(10);

        // Export button
        bool ready = hasRoot && t1 >= 1 && t2 >= 1 && !string.IsNullOrWhiteSpace(outputFolder);
        GUI.enabled = ready;
        if (GUILayout.Button("  Export Map Bundle  ", GUILayout.Height(46)))
            RunExport();
        GUI.enabled = true;

        if (!ready)
            EditorGUILayout.HelpBox(
                "Fix the checklist items above before exporting.", MessageType.Warning);

        EditorGUILayout.EndScrollView();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static void Check(bool ok, string label)
    {
        Color prev = GUI.color;
        GUI.color = ok ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.4f, 0.4f);
        EditorGUILayout.LabelField((ok ? "✔  " : "✘  ") + label);
        GUI.color = prev;
    }

    static void Validate(out bool hasRoot, out int t1Count, out int t2Count)
    {
        t1Count = 0; t2Count = 0;
        GameObject root = GameObject.Find("MapGeometry");
        hasRoot = root != null;
        if (!hasRoot) return;
        foreach (SpawnMarker m in root.GetComponentsInChildren<SpawnMarker>())
            if (m.team == 1) t1Count++; else t2Count++;
    }

    // ── Export logic ─────────────────────────────────────────────────────────

    void RunExport()
    {
        // Sanitize the map name to be safe as a filename and bundle name.
        string safe = new string(mapName
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "MyMap";

        string bundleName = safe.ToLower();          // Unity bundle names must be lowercase
        string finalPath  = Path.Combine(outputFolder, safe + ".bundle");

        // ── Step 1: Find the scene root ───────────────────────────────────────
        GameObject original = GameObject.Find("MapGeometry");
        if (original == null)
        {
            EditorUtility.DisplayDialog("Error", "'MapGeometry' not found in scene.", "OK");
            return;
        }

        // ── Step 2: Clone so we can modify without touching the live scene ─────
        GameObject copy = Instantiate(original);
        copy.name = "MapGeometry";

        // Rename SpawnMarker objects → T1Spawn_N / T2Spawn_N, then remove
        // the SpawnMarker component so no missing-script warnings appear in game.
        int idx1 = 0, idx2 = 0;
        foreach (SpawnMarker m in copy.GetComponentsInChildren<SpawnMarker>())
        {
            m.gameObject.name = m.team == 1
                ? $"T1Spawn_{idx1++}"
                : $"T2Spawn_{idx2++}";
            DestroyImmediate(m);
        }

        // Strip UplinkSpawnMarker — editor-only gizmo component, not needed in bundle.
        foreach (UplinkSpawnMarker m in copy.GetComponentsInChildren<UplinkSpawnMarker>())
            DestroyImmediate(m);

        // Strip ResupplySpawnMarker — editor-only gizmo component, not needed in bundle.
        foreach (ResupplySpawnMarker m in copy.GetComponentsInChildren<ResupplySpawnMarker>())
            DestroyImmediate(m);

        // Extract MapLightingConfig data before stripping the component.
        // We'll write it as a JSON TextAsset in Step 3c below.
        string lightingJson = null;
        MapLightingConfig lightingCfg = copy.GetComponentInChildren<MapLightingConfig>();
        if (lightingCfg != null)
        {
            var data = new LightingExportData
            {
                sunR         = lightingCfg.sunColor.r,
                sunG         = lightingCfg.sunColor.g,
                sunB         = lightingCfg.sunColor.b,
                sunIntensity = lightingCfg.sunIntensity,
                sunRotX      = lightingCfg.sunRotation.x,
                sunRotY      = lightingCfg.sunRotation.y,
                sunRotZ      = lightingCfg.sunRotation.z,
                ambR         = lightingCfg.ambientColor.r,
                ambG         = lightingCfg.ambientColor.g,
                ambB         = lightingCfg.ambientColor.b,
                fogEnabled   = lightingCfg.fogEnabled,
                fogR         = lightingCfg.fogColor.r,
                fogG         = lightingCfg.fogColor.g,
                fogB         = lightingCfg.fogColor.b,
                fogDensity   = lightingCfg.fogDensity,
            };
            lightingJson = JsonUtility.ToJson(data, prettyPrint: true);
            // Strip the MonoBehaviour — it references editor-only types not in-game.
            DestroyImmediate(lightingCfg);
        }

        // ── Step 3: Save the copy as a prefab in a temp Assets folder ──────────
        const string tempFolder = "Assets/_CMExportTemp";
        if (!AssetDatabase.IsValidFolder(tempFolder))
            AssetDatabase.CreateFolder("Assets", "_CMExportTemp");

        // ── Step 3a: Bake built-in meshes into explicit project assets ───────────
        // Unity's built-in primitive meshes (Cube, Plane, etc.) are internal Unity
        // resources. To guarantee they're included in the AssetBundle, we create
        // copies of them as actual .asset files in the temp folder.
        foreach (MeshFilter mf in copy.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh != null && !AssetDatabase.Contains(mf.sharedMesh))
            {
                Mesh baked = UnityEngine.Object.Instantiate(mf.sharedMesh);
                baked.name = mf.sharedMesh.name;
                AssetDatabase.CreateAsset(baked, tempFolder + "/" + baked.name + ".asset");
                mf.sharedMesh = baked;
            }
        }

        // ── Step 3b (pre): Capture texture references before materials are baked ──
        // We record which project texture assets are referenced and their tiling/offset
        // so the mod can load them from the bundle and apply them at runtime.
        // Must happen before Step 3b replaces sharedMaterials on the copy.
        var texEntryList = new System.Collections.Generic.List<TextureEntry>();
        var bundledTexPaths = new System.Collections.Generic.HashSet<string>();
        {
            var mrListForTex = copy.GetComponentsInChildren<MeshRenderer>(true);
            for (int ri = 0; ri < mrListForTex.Length; ri++)
            {
                var mr  = mrListForTex[ri];
                var mats = mr.sharedMaterials;
                for (int si = 0; si < mats.Length; si++)
                {
                    if (mats[si] == null) continue;

                    // Support both Built-in RP (_MainTex) and URP (_BaseMap).
                    Texture2D tex = (mats[si].HasProperty("_MainTex") ? mats[si].GetTexture("_MainTex") as Texture2D : null)
                                 ?? (mats[si].HasProperty("_BaseMap") ? mats[si].GetTexture("_BaseMap") as Texture2D : null);
                    if (tex == null) continue;

                    string texPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(texPath)) continue; // runtime texture — can't bundle

                    // Choose tiling/offset from whichever property the material uses.
                    string scaleProp = mats[si].HasProperty("_MainTex") ? "_MainTex" : "_BaseMap";
                    Vector2 tiling = mats[si].GetTextureScale(scaleProp);
                    Vector2 offset = mats[si].GetTextureOffset(scaleProp);

                    // Assign to bundle (once per unique texture asset).
                    if (!bundledTexPaths.Contains(texPath))
                    {
                        AssetImporter.GetAtPath(texPath).assetBundleName = bundleName;
                        bundledTexPaths.Add(texPath);
                        Debug.Log($"[Custom Maps] Texture bundled: {texPath}");
                    }

                    texEntryList.Add(new TextureEntry
                    {
                        ri  = ri,  si  = si,
                        tex = texPath.ToLower(),   // full asset path used as bundle key
                        tx  = tiling.x, ty = tiling.y,
                        ox  = offset.x, oy = offset.y,
                    });
                }
            }
        }

        // ── Step 3b: Bake materials with Unlit/Texture ───────────────────────────
        // Standard requires baked lighting; Unlit/Texture keeps geometry visible in
        // any scene and preserves UV vertex attributes in the bundle (Unlit/Color has
        // no UV input and causes the builder to strip UV channels from all meshes).
        int matIdx = 0;
        foreach (MeshRenderer mr in copy.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = mr.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                Color col = (mats[i] != null) ? mats[i].color : Color.gray;
                if (col.a < 0.1f) col = Color.gray; // avoid invisible-by-default colours
                Material baked = new Material(Shader.Find("Unlit/Texture"));
                baked.color = col;
                baked.name   = "MapMat_" + matIdx++;
                AssetDatabase.CreateAsset(baked, tempFolder + "/" + baked.name + ".mat");
                mats[i] = baked;
            }
            mr.sharedMaterials = mats;
        }

        // ── Step 3b.5: Collect per-renderer colour data → ColorConfig.json ─────────
        // AssetBundle materials are platform-specific (Windows shaders won't load on
        // Mac). Storing colours in a plain JSON TextAsset makes them cross-platform:
        // the mod reads this file instead of reading material.color at runtime.
        var ccSlotCounts = new System.Collections.Generic.List<int>();
        var ccR = new System.Collections.Generic.List<float>();
        var ccG = new System.Collections.Generic.List<float>();
        var ccB = new System.Collections.Generic.List<float>();
        var ccA = new System.Collections.Generic.List<float>();
        foreach (MeshRenderer mr in copy.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = mr.sharedMaterials;
            int slots = Mathf.Max(mats.Length, 1);
            ccSlotCounts.Add(slots);
            for (int i = 0; i < slots; i++)
            {
                Color col = (i < mats.Length && mats[i] != null)
                    ? mats[i].color
                    : new Color(0.55f, 0.55f, 0.55f, 1f);
                if (col.a < 0.1f) col = new Color(0.55f, 0.55f, 0.55f, 1f);
                ccR.Add(col.r); ccG.Add(col.g); ccB.Add(col.b); ccA.Add(col.a);
            }
        }
        string colorConfigJson = JsonUtility.ToJson(new ColorExportData
        {
            slotCounts = ccSlotCounts.ToArray(),
            r = ccR.ToArray(), g = ccG.ToArray(), b = ccB.ToArray(), a = ccA.ToArray(),
        });
        {
            string diskPath = Path.Combine(Application.dataPath, "_CMExportTemp", "ColorConfig.json");
            File.WriteAllText(diskPath, colorConfigJson);
            AssetDatabase.ImportAsset(tempFolder + "/ColorConfig.json");
            AssetImporter.GetAtPath(tempFolder + "/ColorConfig.json").assetBundleName = bundleName;
            Debug.Log("[Custom Maps] ColorConfig.json written to bundle.");
        }

        // ── Step 3b.7: Write TextureConfig.json ──────────────────────────────────
        // A JSON TextAsset listing which texture asset path maps to which renderer/slot,
        // plus its tiling and offset. The mod loads Texture2D assets by the stored path
        // and applies them at runtime using whatever shader the game supports.
        if (texEntryList.Count > 0)
        {
            string texJson = JsonUtility.ToJson(new TextureExportData
                { entries = texEntryList.ToArray() });
            string texDisk = Path.Combine(Application.dataPath, "_CMExportTemp", "TextureConfig.json");
            File.WriteAllText(texDisk, texJson);
            const string texAssetPath = tempFolder + "/TextureConfig.json";
            AssetDatabase.ImportAsset(texAssetPath);
            AssetImporter.GetAtPath(texAssetPath).assetBundleName = bundleName;
            Debug.Log($"[Custom Maps] TextureConfig.json written — {texEntryList.Count} slot(s).");
        }

        // ── Step 3c: Write LightingConfig JSON as a TextAsset in the bundle ──────
        // Unity treats .json files as TextAssets. The mod loads it with
        // bundle.LoadAsset<TextAsset>("LightingConfig") at runtime.
        const string lightingJsonAssetPath = tempFolder + "/LightingConfig.json";
        if (lightingJson != null)
        {
            string diskPath = Path.Combine(Application.dataPath, "_CMExportTemp", "LightingConfig.json");
            File.WriteAllText(diskPath, lightingJson);
            AssetDatabase.ImportAsset(lightingJsonAssetPath);
            AssetImporter.GetAtPath(lightingJsonAssetPath).assetBundleName = bundleName;
            Debug.Log("[Custom Maps] LightingConfig.json written to bundle.");
        }

        AssetDatabase.Refresh();

        const string prefabPath = tempFolder + "/MapGeometry.prefab";
        PrefabUtility.SaveAsPrefabAsset(copy, prefabPath);
        DestroyImmediate(copy);
        AssetDatabase.Refresh();

        // ── Step 4: Assign bundle name to the prefab ──────────────────────────
        AssetImporter.GetAtPath(prefabPath).assetBundleName = bundleName;

        // ── Step 5: Build (Windows 64-bit — matches the game) ─────────────────
        string buildDir = Path.Combine(Application.temporaryCachePath, "CMBuild");
        Directory.CreateDirectory(buildDir);

        BuildPipeline.BuildAssetBundles(
            buildDir,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);

        // ── Step 6: Copy the bundle to the chosen output folder ───────────────
        string builtFile = Path.Combine(buildDir, bundleName);
        if (File.Exists(builtFile))
        {
            File.Copy(builtFile, finalPath, overwrite: true);
            EditorUtility.DisplayDialog(
                "Export Successful!",
                $"Map '{mapName}' exported to:\n\n{finalPath}\n\n" +
                "Copy this .bundle file into the game's  CustomMaps/  folder to play on it.",
                "Great!");
            Debug.Log($"[Custom Maps] Exported → {finalPath}");
        }
        else
        {
            EditorUtility.DisplayDialog(
                "Export Failed",
                "Build finished but the .bundle file was not found.\n\n" +
                "Open  Window → General → Console  and look for red error messages.",
                "OK");
        }

        // ── Step 7: Cleanup ───────────────────────────────────────────────────
        AssetImporter.GetAtPath(prefabPath).assetBundleName = "";
        // Remove bundle name from texture assets so they don't linger in the build settings.
        foreach (var tp in bundledTexPaths)
            AssetImporter.GetAtPath(tp).assetBundleName = "";
        AssetDatabase.DeleteAsset(tempFolder);
        AssetDatabase.Refresh();
    }

    // Creates a wall Cube as a child of `parent` and registers it with Undo.
    static void CreateWall(Transform parent, Vector3 localPos, Vector3 localScale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(wall, "Create Map Template");
        wall.name = "Wall";
        wall.transform.SetParent(parent);
        wall.transform.localPosition = localPos;
        wall.transform.localScale    = localScale;
    }

    // ── DTO for per-renderer colour data ─────────────────────────────────────
    // Stores colours as flat float arrays so JsonUtility can handle them.
    // One entry per material slot, ordered by GetComponentsInChildren<MeshRenderer>
    // traversal — same order the mod uses at load time.
    // Must match ColorConfigData in CustomMapsPlugin.cs.
    [Serializable]
    class ColorExportData
    {
        public int[]   slotCounts; // number of material slots per renderer
        public float[] r, g, b, a; // flat: one value per material slot
    }

    // ── DTO for per-slot texture data ─────────────────────────────────────────
    // ri = renderer index, si = slot index (both match GetComponentsInChildren order).
    // tex = full lowercase asset path used as key to LoadAsset<Texture2D> at runtime.
    // Must match TextureConfigData / TextureEntry in CustomMapsPlugin.cs.
    [Serializable]
    class TextureExportData
    {
        public TextureEntry[] entries;
    }

    [Serializable]
    class TextureEntry
    {
        public int    ri, si;       // renderer index, slot index
        public string tex;          // bundle asset path (lowercase)
        public float  tx = 1f, ty = 1f; // tiling x, y
        public float  ox, oy;           // offset x, y
    }

    // ── DTO used to serialise MapLightingConfig fields as JSON ───────────────
    // Must match the field names in LightingData (CustomMapsPlugin.cs).
    [Serializable]
    class LightingExportData
    {
        public float sunR, sunG, sunB;
        public float sunIntensity;
        public float sunRotX, sunRotY, sunRotZ;
        public float ambR, ambG, ambB;
        public bool  fogEnabled;
        public float fogR, fogG, fogB;
        public float fogDensity;
    }
}
