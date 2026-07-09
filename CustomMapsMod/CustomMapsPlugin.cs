using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CustomMapsMod
{
    [BepInPlugin("com.peakzelo.custommaps", "Custom Maps", "1.0.1")]
    public class CustomMapsPlugin : BaseUnityPlugin
    {
        public static ManualLogSource StaticLogger;

        // Bundle filenames (sorted), populated at startup from the CustomMaps/ folder.
        public static List<string> customMapBundles = [];

        // How many entries the lobby dropdown had BEFORE we added custom maps.
        // Indices >= this value are custom maps.
        public static int builtinMapCount = 0;

        // Set when a custom-map game is about to start.
        // Cleared after the scene loads and the map is applied.
        public static string pendingBundle = null;

        // Holds the instantiated MapGeometry until HardlineGameManager.Start fires
        // and we can inject spawn points into it.
        public static GameObject pendingMapGeo = null;

        // Persistent reference to the active custom map geometry.
        // Kept alive so AllPlayersLoadedPatch can re-inject spawn points even if
        // GameManagerStartPatch already cleared pendingMapGeo.
        public static GameObject activeMapGeo = null;

        // Raw map index from the most recent RpcSetMapSelected call, stored before
        // any IsCustomMapIndex check so it survives even when builtinMapCount == 0.
        // Used by LobbyStartPatch to backfill pendingBundle for guests who receive
        // the RPC before LobbyUIManager.Start() has run.
        public static int lastReceivedMapIndex = -1;

        // World position chosen this round for the uplink.
        // Set in OnSceneLoaded (random pick from all UplinkSpawn markers),
        // used by UplinkRepositioner and AllPlayersLoadedPatch.Postfix.
        public static Vector3 activeUplinkTarget = Vector3.zero;

        // Positions chosen for the resupply stations (AmmoCrate) this round.
        // One entry per ResupplySpawn marker found in the bundle.
        public static List<Vector3> activeResupplyTargets = [];

        // ── Runtime-created resources (materials, converted textures) ────────────
        // These are created with `new Material(...)` / `new Texture2D(...)` at map
        // load and are NOT owned by the AssetBundle, so Unity never frees them when
        // the scene unloads. Without explicit cleanup, every custom-map match played
        // in a session leaks its full set of materials + textures.
        private static readonly List<UnityEngine.Object> createdResources = [];

        internal static void TrackResource(UnityEngine.Object res)
        {
            if (res != null) createdResources.Add(res);
        }

        private static void DestroyTrackedResources()
        {
            if (createdResources.Count == 0) return;
            foreach (UnityEngine.Object res in createdResources)
            {
                if (res != null) UnityEngine.Object.Destroy(res);
            }
            StaticLogger.LogInfo($"Custom Maps: Freed {createdResources.Count} runtime resource(s) from previous map.");
            createdResources.Clear();

            // Bundle-loaded assets (unconverted textures, the MapGeometry prefab)
            // survive bundle.Unload(false); once nothing references them this
            // async sweep reclaims that memory too.
            Resources.UnloadUnusedAssets();
        }

        // ── Cached spawn positions for GoToSpawnPatch ─────────────────────────────
        // GoToSpawn fires for every player on every round restart; scanning the
        // entire map hierarchy (GetComponentsInChildren<Transform>) each time can
        // hitch on large maps. Positions are static markers, so cache them per map.
        private static GameObject spawnCacheGeo;      // which mapGeo the cache belongs to
        private static List<Vector3> cachedT1Spawns = [];
        private static List<Vector3> cachedT2Spawns = [];

        internal static List<Vector3> GetCachedSpawnPositions(int team)
        {
            if (activeMapGeo == null) return null;

            // Rebuild the cache when the active map changes (one scan per map load)
            if (!ReferenceEquals(spawnCacheGeo, activeMapGeo))
            {
                cachedT1Spawns.Clear();
                cachedT2Spawns.Clear();
                foreach (Transform t in activeMapGeo.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("T1Spawn", StringComparison.OrdinalIgnoreCase))
                        cachedT1Spawns.Add(t.position);
                    else if (t.name.StartsWith("T2Spawn", StringComparison.OrdinalIgnoreCase))
                        cachedT2Spawns.Add(t.position);
                }
                spawnCacheGeo = activeMapGeo;
                StaticLogger.LogInfo(
                    $"Custom Maps: Spawn cache built — {cachedT1Spawns.Count} T1, {cachedT2Spawns.Count} T2.");
            }

            List<Vector3> teamList = team == 2 ? cachedT2Spawns : cachedT1Spawns;
            if (teamList.Count > 0) return teamList;

            // Fall back to the other team's spawns if this team has none
            List<Vector3> other = team == 2 ? cachedT1Spawns : cachedT2Spawns;
            return other.Count > 0 ? other : null;
        }

        // ── Cached reflection lookups ─────────────────────────────────────────────
        // GetField / GetProperty walk the type's member tables every call; resolve
        // them once. (LobbyStartPatch / StartGamePatch already did this.)
        internal static readonly FieldInfo Team1SpawnsField =
            typeof(HardlineGameManager).GetField("team1Spawns",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        internal static readonly FieldInfo Team2SpawnsField =
            typeof(HardlineGameManager).GetField("team2Spawns",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        internal static readonly PropertyInfo HumanTeamProperty =
            typeof(Human).GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);

        // ── Cached UplinkStations root ────────────────────────────────────────────
        // MoveUplinksToPosition runs every second for 30 s after load plus on every
        // round; GameObject.Find scans the whole scene each call. Cache the root
        // (Unity's overloaded == null detects scene-change destruction).
        private static Transform cachedUplinkStationsRoot;

        internal static Transform GetUplinkStationsRoot()
        {
            if (cachedUplinkStationsRoot != null) return cachedUplinkStationsRoot;
            GameObject go = GameObject.Find("UplinkStations");
            cachedUplinkStationsRoot = go?.transform;
            return cachedUplinkStationsRoot;
        }

        private Harmony harmony;

        private void Awake()
        {
            StaticLogger = Logger;
            Logger.LogInfo("Custom Maps v1.0.1");

            // ── Scan CustomMaps/ folder ───────────────────────────────────────────
            string customMapsDir = Path.Combine(Application.dataPath, "..", "CustomMaps");
            if (!Directory.Exists(customMapsDir))
            {
                Directory.CreateDirectory(customMapsDir);
                Logger.LogInfo("Custom Maps: Created empty CustomMaps/ folder next to the game.");
            }

            customMapBundles = [.. Directory.GetFiles(customMapsDir, "*.bundle")
                .Select(f => Path.GetFileName(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];

            Logger.LogInfo($"Custom Maps: Found {customMapBundles.Count} bundle(s):");
            foreach (string b in customMapBundles)
                Logger.LogInfo($"  {b}");

            // ── Register scene-loaded callback (fires on ALL machines) ─────────────
            SceneManager.sceneLoaded += OnSceneLoaded;

            harmony = new Harmony("com.peakzelo.custommaps");
            harmony.PatchAll();
            Logger.LogInfo("Custom Maps patches applied!");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            harmony?.UnpatchSelf();
        }

        // ── Called on every machine when any scene finishes loading ────────────────
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Only care about the main (non-additive) scene change.
            if (mode == LoadSceneMode.Additive) return;

            // The previous scene (and any custom map geometry in it) is gone at this
            // point, so runtime-created materials/textures from the last custom map
            // are unreferenced — free them before doing anything else. Safe to run
            // whether or not the new scene uses a custom map.
            if (activeMapGeo == null)
            {
                DestroyTrackedResources();
            }

            if (string.IsNullOrEmpty(pendingBundle)) return;

            string bundleFileName = pendingBundle;
            pendingBundle = null; // clear before any early return so we don't retry

            StaticLogger.LogInfo($"Custom Maps: Scene '{scene.name}' loaded. Applying '{bundleFileName}'...");

            // ── Load the AssetBundle from disk ────────────────────────────────────
            string bundlePath = Path.Combine(Application.dataPath, "..", "CustomMaps", bundleFileName);
            if (!File.Exists(bundlePath))
            {
                StaticLogger.LogError($"Custom Maps: Bundle not found at: {bundlePath}");
                return;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                StaticLogger.LogError("Custom Maps: AssetBundle.LoadFromFile returned null.");
                return;
            }

            // ── Log all asset names in the bundle for debugging ───────────────────
            StaticLogger.LogInfo("Custom Maps: Bundle contains: " +
                string.Join(", ", bundle.GetAllAssetNames()));

            // ── Load the MapGeometry prefab from the bundle ───────────────────────
            GameObject mapPrefab = bundle.LoadAsset<GameObject>("MapGeometry");
            if (mapPrefab == null)
            {
                StaticLogger.LogError(
                    "Custom Maps: 'MapGeometry' prefab not found in bundle.\n" +
                    "Bundle contains: " + string.Join(", ", bundle.GetAllAssetNames()));
                bundle.Unload(true);
                return;
            }

            // ── Instantiate the custom geometry ───────────────────────────────────
            GameObject mapGeo = UnityEngine.Object.Instantiate(mapPrefab, Vector3.zero, Quaternion.identity);
            mapGeo.name = "CustomMapGeometry";
            activeMapGeo = mapGeo; // keep reference for AllPlayersLoaded re-injection
            StaticLogger.LogInfo("Custom Maps: MapGeometry instantiated.");

            // Debug: verify colliders survived the bundle round-trip.
            Collider[] cols = mapGeo.GetComponentsInChildren<Collider>(true);
            StaticLogger.LogInfo($"Custom Maps: MapGeometry has {cols.Length} collider(s).");
            foreach (Collider c in cols)
                StaticLogger.LogInfo($"  {c.gameObject.name}: {c.GetType().Name}  isTrigger={c.isTrigger}  layer={c.gameObject.layer}");

            // ── Load per-renderer colour data (cross-platform JSON) ───────────────
            // AssetBundle materials are platform-specific — loading a Windows bundle
            // on Mac gives gray colours because the compiled shader data differs.
            // The exporter packs a ColorConfig.json TextAsset with plain float arrays
            // so colours survive cross-platform loading.
            ColorConfigData colorConfig = null;
            {
                TextAsset colorAsset = bundle.LoadAsset<TextAsset>("ColorConfig");
                if (colorAsset != null)
                {
                    try { colorConfig = JsonUtility.FromJson<ColorConfigData>(colorAsset.text); }
                    catch (Exception ex)
                    { StaticLogger.LogWarning($"Custom Maps: Could not parse ColorConfig: {ex.Message}"); }
                }
                StaticLogger.LogInfo(colorConfig != null
                    ? $"Custom Maps: ColorConfig loaded — {colorConfig.r?.Length ?? 0} colour slot(s)."
                    : "Custom Maps: No ColorConfig in bundle — falling back to material colours.");
            }

            // ── Load texture config and pre-load all referenced Texture2D assets ──────
            // Textures must be loaded before bundle.Unload(false) — after that the bundle
            // data is released but already-loaded assets stay alive in memory.
            TextureConfigData textureConfig = null;
            Dictionary<string, Texture2D> textureMap = new(StringComparer.OrdinalIgnoreCase);
            {
                TextAsset texAsset = bundle.LoadAsset<TextAsset>("TextureConfig")
                                  ?? bundle.LoadAsset<TextAsset>("assets/_cmexporttemp/textureconfig.json")
                                  ?? bundle.LoadAsset<TextAsset>("textureconfig");
                if (texAsset != null)
                {
                    StaticLogger.LogInfo($"Custom Maps: TextureConfig asset loaded ({texAsset.text?.Length ?? -1} chars): {texAsset.text?.Substring(0, Mathf.Min(120, texAsset.text?.Length ?? 0))}");
                    try { textureConfig = TextureConfigParser.Parse(texAsset.text); }
                    catch (Exception ex)
                    { StaticLogger.LogWarning($"Custom Maps: Could not parse TextureConfig: {ex.Message}"); }
                    StaticLogger.LogInfo($"Custom Maps: TextureConfig parse — config={textureConfig != null}, entries={(textureConfig?.entries != null ? textureConfig.entries.Count.ToString() : "NULL")}");
                }
                else
                {
                    StaticLogger.LogWarning("Custom Maps: TextureConfig asset is NULL — tried 3 name variants.");
                }
                if (textureConfig?.entries != null)
                {
                    foreach (TextureEntry entry in textureConfig.entries)
                    {
                        if (textureMap.ContainsKey(entry.tex)) continue;
                        Texture2D tex = bundle.LoadAsset<Texture2D>(entry.tex);
                        if (tex != null)
                        {
                            // DXT textures from a Windows-target bundle render black on Mac Metal.
                            // Blit to RGBA32 via GPU to get a platform-native, always-renderable texture.
                            if (tex.format == TextureFormat.DXT1 || tex.format == TextureFormat.DXT5 ||
                                tex.format == TextureFormat.DXT1Crunched || tex.format == TextureFormat.DXT5Crunched)
                            {
                                RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0,
                                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                                Graphics.Blit(tex, rt);
                                Texture2D rgba = new(tex.width, tex.height, TextureFormat.RGBA32, false);
                                RenderTexture prevRT = RenderTexture.active;
                                RenderTexture.active = rt;
                                rgba.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                                rgba.Apply();
                                RenderTexture.active = prevRT;
                                RenderTexture.ReleaseTemporary(rt);
                                tex = rgba;
                                TrackResource(rgba); // runtime-created, freed on next map load
                                Color sp = rgba.GetPixel(512, 512);
                                StaticLogger.LogInfo($"Custom Maps: Converted to RGBA32 — pixel(512,512)=({sp.r:F2},{sp.g:F2},{sp.b:F2},{sp.a:F2})");
                            }
                            tex.filterMode = FilterMode.Point; // keep pixel-art textures crisp
                            textureMap[entry.tex] = tex;
                            StaticLogger.LogInfo($"Custom Maps: Loaded texture '{entry.tex}' — format={tex.format} size={tex.width}x{tex.height}");
                        }
                        else
                            StaticLogger.LogWarning($"Custom Maps: Texture '{entry.tex}' not found in bundle.");
                    }
                    StaticLogger.LogInfo(
                        $"Custom Maps: TextureConfig loaded — {textureConfig.entries.Count} slot(s), " +
                        $"{textureMap.Count} unique texture(s).");
                }
                else
                    StaticLogger.LogInfo("Custom Maps: No TextureConfig in bundle — geometry will use flat colours.");
            }

            // ── Force-apply a visible material using the game's own shaders ─────────
            // Shaders compiled into the AssetBundle may not exist in the game's shader
            // cache, making renderers draw nothing. We replace all materials with a
            // plain gray material built from the game's Standard shader at runtime.
            ApplyFallbackMaterial(mapGeo, colorConfig, textureConfig, textureMap);

            // ── Ensure the camera renders our geometry (layer 0) ─────────────────────
            // The game camera may exclude layer 0 from its culling mask. Geometry and
            // colliders both live on layer 0 — we just need the camera to render it.
            // CameraRenderFixer polls LateUpdate until Camera.main is available, then
            // ORs layer 0 into every camera's culling mask and destroys itself.
            mapGeo.AddComponent<CameraRenderFixer>();

            // ── Hide Level1's terrain / buildings (keep game-system objects) ───────
            HideBaseSceneGeometry(mapGeo);

            // ── Collect UplinkSpawn markers and pick one randomly ─────────────────
            // If the map maker places multiple UplinkSpawn children, the uplink is
            // sent to a random one each game.  Falls back to world origin if absent.
            List<Vector3> uplinkTargets = [];
            foreach (Transform t in mapGeo.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.Equals("UplinkSpawn", StringComparison.OrdinalIgnoreCase))
                    uplinkTargets.Add(t.position);
            }
            if (uplinkTargets.Count > 0)
            {
                activeUplinkTarget = uplinkTargets[UnityEngine.Random.Range(0, uplinkTargets.Count)];
                StaticLogger.LogInfo(
                    $"Custom Maps: {uplinkTargets.Count} UplinkSpawn(s) found — selected {activeUplinkTarget}.");
            }
            else
            {
                activeUplinkTarget = Vector3.zero;
                StaticLogger.LogInfo("Custom Maps: No UplinkSpawn found — uplink placed at world origin.");
            }

            // ── Collect ResupplySpawn markers ─────────────────────────────────────
            // Map makers place "ResupplySpawn" (or "ResupplySpawn_N") empties to
            // control where the level's AmmoCrate resupply stations appear.
            activeResupplyTargets = [];
            foreach (Transform t in mapGeo.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("ResupplySpawn", StringComparison.OrdinalIgnoreCase))
                    activeResupplyTargets.Add(t.position);
            }
            if (activeResupplyTargets.Count > 0)
            {
                StaticLogger.LogInfo(
                    $"Custom Maps: {activeResupplyTargets.Count} ResupplySpawn(s) found.");
                RepositionResupplyStations();
            }

            // ── Start the uplink repositioner ─────────────────────────────────────
            // The game's own UplinkStation / RoundsHardlineGameManager scripts may
            // reposition the uplink several seconds after scene load (e.g. at round
            // start).  UplinkRepositioner keeps overriding for 30 s at 1 s intervals
            // so every game-side reset is caught and corrected.
            // Runs on ALL clients independently — Mirror does NOT auto-sync scene-
            // object transforms.
            UplinkRepositioner uplinkRepos = mapGeo.AddComponent<UplinkRepositioner>();
            uplinkRepos.mapGeo = mapGeo;

            // ── Inject spawn points into the HardlineGameManager ──────────────────
            // HardlineGameManager is a Mirror-spawned network object; it may not exist
            // yet when sceneLoaded fires. Save mapGeo so GameManagerStartPatch can
            // inject spawn points the moment HardlineGameManager.Start() runs.
            HardlineGameManager gameManager = UnityEngine.Object.FindObjectOfType<HardlineGameManager>();
            if (gameManager != null)
            {
                InjectSpawnPoints(gameManager, mapGeo);
            }
            else
            {
                StaticLogger.LogInfo("Custom Maps: HardlineGameManager not ready yet — will inject spawn points in Start().");
                pendingMapGeo = mapGeo;
            }

            // ── Apply custom lighting (optional) ──────────────────────────────────
            // The exporter writes a LightingConfig.json TextAsset into the bundle
            // when the map has a MapLightingConfig component. If absent, the scene's
            // default lighting is used unchanged.
            TextAsset lightingAsset = bundle.LoadAsset<TextAsset>("LightingConfig");
            if (lightingAsset != null)
            {
                StaticLogger.LogInfo("Custom Maps: Found LightingConfig in bundle — applying.");
                ApplyLightingConfig(lightingAsset.text);
            }
            else
            {
                StaticLogger.LogInfo("Custom Maps: No LightingConfig in bundle — using scene defaults.");
            }

            // Unload bundle metadata but keep loaded assets alive in memory.
            bundle.Unload(false);

            StaticLogger.LogInfo("Custom Maps: Custom map applied successfully!");
        }

        // ── Hides Level1 scene geometry while preserving game-system objects ───────
        private static void HideBaseSceneGeometry(GameObject preserveObject)
        {
            int hidden = 0;
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root == preserveObject) continue;
                if (ShouldPreserve(root)) continue;

                // Only touch objects that actually have renderable geometry.
                bool hasGeometry = root.GetComponentInChildren<Renderer>() != null
                                || root.GetComponentInChildren<Terrain>() != null;
                if (!hasGeometry) continue;

                // Do NOT call SetActive(false) on the root — any NetworkIdentity on the
                // object would be deactivated, breaking Mirror's client-readiness handshake
                // and causing "waiting for players" to hang forever on the host.
                // Instead, disable only the visual and physics components so the GO stays
                // in Mirror's scene-object registry but is invisible and has no collision.
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
                foreach (Terrain t in root.GetComponentsInChildren<Terrain>(true))
                    t.enabled = false;
                foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
                    c.enabled = false;
                hidden++;
            }
            StaticLogger.LogInfo($"Custom Maps: Hid {hidden} base-scene geometry object(s).");
        }

        // Returns true if the object should be kept visible (is a game-system object,
        // not terrain / map geometry).
        private static bool ShouldPreserve(GameObject go)
        {
            // Always keep anything that has a NetworkBehaviour (game manager etc.)
            if (go.GetComponentInChildren<NetworkBehaviour>() != null) return true;

            string lower = go.name.ToLower();
            return lower.Contains("manager")       // HardlineGameManager, etc.
                || lower == "loadedweapons"
                || lower.Contains("loadedweapons")
                || lower.Contains("uplink")        // UplinkStations
                || lower.Contains("chat")          // ChatHandler
                || lower.Contains("canvas")        // Unity UI canvases
                || lower.Contains("eventsystem")   // Unity EventSystem
                || lower.Contains("light")         // Directional Light etc.
                || lower.Contains("camera")        // Cameras
                || lower.Contains("hud")
                || lower.Contains("ui");
        }

        // ── Reads T1Spawn_* / T2Spawn_* child objects from the custom map and
        //    injects them into HardlineGameManager's team spawn lists. ──────────────
        public static void InjectSpawnPoints(HardlineGameManager manager, GameObject mapGeo)
        {
            List<GameObject> t1 = [];
            List<GameObject> t2 = [];

            foreach (Transform t in mapGeo.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("T1Spawn", StringComparison.OrdinalIgnoreCase))
                    t1.Add(t.gameObject);
                else if (t.name.StartsWith("T2Spawn", StringComparison.OrdinalIgnoreCase))
                    t2.Add(t.gameObject);
            }

            StaticLogger.LogInfo($"Custom Maps: Found {t1.Count} T1 spawn(s), {t2.Count} T2 spawn(s).");

            if (t1.Count == 0) StaticLogger.LogWarning("Custom Maps: No T1Spawn_* objects found in MapGeometry!");
            if (t2.Count == 0) StaticLogger.LogWarning("Custom Maps: No T2Spawn_* objects found in MapGeometry!");

            FieldInfo t1Field = Team1SpawnsField;
            FieldInfo t2Field = Team2SpawnsField;

            // If either field isn't found, dump all HardlineGameManager fields so we can
            // identify the correct name from the log.
            if (t1Field == null || t2Field == null)
            {
                StaticLogger.LogWarning("Custom Maps: 'team1Spawns' or 'team2Spawns' field NOT found on HardlineGameManager!");
                StaticLogger.LogWarning("Custom Maps: Available HardlineGameManager fields:");
                foreach (FieldInfo f in typeof(HardlineGameManager).GetFields(
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                    StaticLogger.LogWarning($"  [{f.FieldType.Name}] {f.Name}");
            }
            else
            {
                StaticLogger.LogInfo("Custom Maps: team1Spawns and team2Spawns fields located successfully.");
            }

            if (t1Field?.GetValue(manager) is List<GameObject> existingT1 && t1.Count > 0)
            {
                for (int i = 0; i < existingT1.Count; i++)
                {
                    GameObject go = existingT1[i];
                    if (go != null && !go.transform.IsChildOf(mapGeo.transform))
                        go.transform.position = t1[i % t1.Count].transform.position;
                }
                StaticLogger.LogInfo($"Custom Maps: Moved {existingT1.Count} Level1 T1 spawn object(s) to custom positions.");
            }

            if (t2Field?.GetValue(manager) is List<GameObject> existingT2 && t2.Count > 0)
            {
                for (int i = 0; i < existingT2.Count; i++)
                {
                    GameObject go = existingT2[i];
                    if (go != null && !go.transform.IsChildOf(mapGeo.transform))
                        go.transform.position = t2[i % t2.Count].transform.position;
                }
                StaticLogger.LogInfo($"Custom Maps: Moved {existingT2.Count} Level1 T2 spawn object(s) to custom positions.");
            }

            // ── Step 2: also replace the list references ─────────────────────────
            t1Field?.SetValue(manager, t1);
            t2Field?.SetValue(manager, t2);

            StaticLogger.LogInfo($"Custom Maps: InjectSpawnPoints — t1={t1Field != null}, t2={t2Field != null}, " +
                                 $"t1Count={t1.Count}, t2Count={t2.Count}.");
        }

        // ── Fixes mesh/material issues on bundle geometry ────────────────────────────
        // Two problems can make geometry invisible after an AssetBundle round-trip:
        //   1. MeshFilter.sharedMesh is null (built-in mesh stripped from bundle)
        //   2. Material uses a shader the game's render pipeline doesn't support
        //
        // If the game uses URP, Shader.Find("Standard") returns non-null but URP
        // silently ignores Built-in RP materials — they render as invisible.
        // We borrow the shader from an uplink station (confirmed camera-visible)
        // so we always use the right shader for whatever RP this game runs.
        private static void ApplyFallbackMaterial(GameObject mapGeo, ColorConfigData colorConfig = null,
            TextureConfigData textureConfig = null, Dictionary<string, Texture2D> textureMap = null)
        {
            // ── Borrow shader from any renderer inside the uplink hierarchy ──────────
            // The actual mesh renderers are on child objects, not the root "Uplink Station"
            // object itself, so we match by root name rather than the renderer's own name.
            Shader sh = null;
            foreach (MeshRenderer mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (mr.sharedMaterial?.shader == null) continue;
                string rootName = mr.transform.root.name.ToLower();
                if (!rootName.Contains("uplink")) continue;
                sh = mr.sharedMaterial.shader;
                StaticLogger.LogInfo(
                    $"Custom Maps: Borrowed shader '{sh.name}' from '{mr.gameObject.name}'" +
                    $" (root: '{mr.transform.root.name}').");
                break;
            }

            // ── Fallbacks in render-pipeline order ────────────────────────────────────
            sh ??= Shader.Find("Universal Render Pipeline/Lit")
              ?? Shader.Find("Universal Render Pipeline/Unlit")
              ?? Shader.Find("Unlit/Color")
              ?? Shader.Find("Standard")
              ?? Shader.Find("Sprites/Default");

            if (sh == null)
            {
                StaticLogger.LogWarning("Custom Maps: Could not find any usable shader — geometry will be invisible.");
                return;
            }

            StaticLogger.LogInfo($"Custom Maps: Using shader '{sh.name}' for fallback materials (per-renderer color).");

            // Build a (rendererIndex, slotIndex) → TextureEntry lookup for O(1) access.
            Dictionary<(int, int), TextureEntry> texLookup = [];
            if (textureConfig?.entries != null)
                foreach (TextureEntry e in textureConfig.entries)
                    texLookup[(e.ri, e.si)] = e;

            // ── Grab the built-in Cube mesh via a temp primitive ──────────────────────
            // Built-in meshes (Cube, etc.) may be stripped from the AssetBundle.
            // Creating a temp primitive gives us a guaranteed valid mesh reference.
            GameObject tmpCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cubeMesh = tmpCube.GetComponent<MeshFilter>().sharedMesh;
            GameObject.DestroyImmediate(tmpCube);

            // ── Apply to all MeshRenderers in the custom geometry ─────────────────────
            MeshRenderer[] renderers = mapGeo.GetComponentsInChildren<MeshRenderer>(true);
            StaticLogger.LogInfo($"Custom Maps: Found {renderers.Length} MeshRenderer(s) in MapGeometry.");

            int fixedMesh = 0;
            int colorFlat = 0; // index into colorConfig's flat r/g/b/a arrays

            for (int ri = 0; ri < renderers.Length; ri++)
            {
                MeshRenderer mr = renderers[ri];
                mr.enabled = true;

                // ── Determine slot count and colours ──────────────────────────────────
                // Priority: ColorConfig JSON (cross-platform) → bundle material colour
                // → default gray.
                int slotCount;
                if (colorConfig != null && ri < colorConfig.slotCounts.Length)
                    slotCount = Mathf.Max(colorConfig.slotCounts[ri], 1);
                else
                    slotCount = Mathf.Max(mr.sharedMaterials.Length, 1);

                Material[] srcMats = mr.sharedMaterials;
                Material[] newMats = new Material[slotCount];

                for (int si = 0; si < slotCount; si++)
                {
                    Color col;
                    if (colorConfig != null && colorConfig.r != null && colorFlat < colorConfig.r.Length)
                    {
                        col = new Color(colorConfig.r[colorFlat], colorConfig.g[colorFlat],
                                        colorConfig.b[colorFlat], colorConfig.a[colorFlat]);
                        colorFlat++;
                    }
                    else
                    {
                        col = (si < srcMats.Length && srcMats[si] != null)
                            ? srcMats[si].color
                            : new Color(0.55f, 0.55f, 0.55f, 1f);
                    }
                    if (col.a < 0.1f) col = new Color(0.55f, 0.55f, 0.55f, 1f);

                    // ── Check for a texture on this slot ──────────────────────────────
                    Texture2D tex = null;
                    texLookup.TryGetValue((ri, si), out TextureEntry texEntry);
                    if (texEntry != null && textureMap != null)
                        textureMap.TryGetValue(texEntry.tex, out tex);

                    Material m;
                    if (tex != null)
                    {
                        // Assign atlas texture to FlatKit's _BaseMap. With _BaseColor=white
                        // and _TextureImpact=1 the output is exactly the texture colour.
                        m = new Material(sh);
                        TrackResource(m); // runtime-created, freed on next map load
                        m.SetColor("_BaseColor", Color.white);
                        m.SetColor("_ColorDim", Color.white);
                        m.SetColor("_ColorDimSteps", Color.white);
                        m.SetColor("_ColorDimCurve", Color.white);
                        m.SetColor("_ColorDimExtra", Color.white);
                        m.SetFloat("_LightContribution", 0f);
                        m.SetTexture("_BaseMap", tex);
                        m.SetTexture("_MainTex", tex);
                        m.SetFloat("_TextureImpact", 1f);
                        m.EnableKeyword("_TEXTUREBLENDINGMODE_MULTIPLY");
                        m.SetTextureScale("_BaseMap", new Vector2(texEntry.tx, texEntry.ty));
                        m.SetTextureOffset("_BaseMap", new Vector2(texEntry.ox, texEntry.oy));
                        m.SetTextureScale("_MainTex", new Vector2(texEntry.tx, texEntry.ty));
                        m.SetTextureOffset("_MainTex", new Vector2(texEntry.ox, texEntry.oy));
                    }
                    else
                    {
                        // Colour-only slot — existing behaviour.
                        m = new Material(sh);
                        TrackResource(m); // runtime-created, freed on next map load
                        m.color = col;
                        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
                    }
                    newMats[si] = m;
                }
                mr.sharedMaterials = newMats;

                // Disable shadow casting to prevent shadow acne / flickering on thin geometry.
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                MeshFilter mf = mr.GetComponent<MeshFilter>();
                string meshName = mf?.sharedMesh?.name ?? "NULL";
                if (mf != null && mf.sharedMesh == null)
                {
                    mf.sharedMesh = cubeMesh;
                    meshName = cubeMesh.name + " (fixed)";
                    fixedMesh++;
                }

                StaticLogger.LogInfo(
                    $"  '{mr.gameObject.name}': enabled={mr.enabled}  mesh={meshName}  shader={sh.name}");
            }

            StaticLogger.LogInfo(
                $"Custom Maps: Fallback applied — {renderers.Length} renderer(s), {fixedMesh} null mesh(es) fixed.");
        }

        // ── Moves every uplink station to the given world position ──────────────────
        // Searches ALL objects including inactive ones (uplinks may be inside a hidden
        // Level1 root). Moves each individual UplinkStation child so that whichever
        // one the game activates, it lands exactly at target.
        // `target` comes from the "UplinkSpawn" child of MapGeometry (map-maker defined).
        internal static void MoveUplinksToPosition(Vector3 target)
        {
            // The game stores all uplink instances as children of "UplinkStations".
            // RoundsHardlineGameManager.SetUplinkNumber activates one child per round,
            // chosen randomly.  Moving the parent root would only be correct for one
            // child's offset; instead we move EVERY child so any activated one lands
            // at the target.
            Transform uplinkStationsRoot = GetUplinkStationsRoot();
            if (uplinkStationsRoot == null)
            {
                StaticLogger.LogInfo("Custom Maps: 'UplinkStations' not found — skipping.");
                return;
            }

            int movedCount = 0;

            for (int i = 0; i < uplinkStationsRoot.childCount; i++)
            {
                Transform child = uplinkStationsRoot.GetChild(i);

                // Find the "Uplink Station" visual model within this child so we can
                // correct for any baked offset between the child pivot and the model.
                Transform model = null;
                foreach (Transform sub in child.GetComponentsInChildren<Transform>(true))
                {
                    if (sub.name.Equals("Uplink Station", StringComparison.OrdinalIgnoreCase))
                    {
                        model = sub;
                        break;
                    }
                }

                // Move the child so its visual model lands at target.
                // transform.position works even on inactive GameObjects.
                if (model != null)
                    child.position = target - (model.position - child.position);
                else
                    child.position = target;

                movedCount++;
            }

            StaticLogger.LogInfo(
                $"Custom Maps: Repositioned {movedCount} UplinkStation child(ren) → {target}.");
        }

        // ── Moves Level1's AmmoCrate (resupply station) objects to the positions
        //    specified by "ResupplySpawn" markers in the map bundle. ────────────────
        // Called after the bundle is loaded and from AllPlayersLoadedPatch.Postfix so
        // the positions are correct even if the game resets them at round-start.
        public static void RepositionResupplyStations()
        {
            if (activeResupplyTargets == null || activeResupplyTargets.Count == 0) return;

            GameObject[] crates;
            try { crates = GameObject.FindGameObjectsWithTag("AmmoCrate"); }
            catch
            {
                StaticLogger.LogWarning(
                    "Custom Maps: 'AmmoCrate' tag not found in scene — " +
                    "ResupplySpawn markers present but no ammo crates to move.");
                return;
            }

            StaticLogger.LogInfo(
                $"Custom Maps: Repositioning resupply stations — " +
                $"{crates.Length} AmmoCrate(s) in scene, {activeResupplyTargets.Count} ResupplySpawn(s).");

            int moveCount = Math.Min(crates.Length, activeResupplyTargets.Count);
            for (int i = 0; i < moveCount; i++)
            {
                GameObject go = crates[i];
                go.transform.position = activeResupplyTargets[i];

                // Re-enable the full parent chain (GameObjects) so the object is active.
                Transform t = go.transform;
                while (t != null) { t.gameObject.SetActive(true); t = t.parent; }

                // Re-enable renderers on this object and all children.
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                    r.enabled = true;

                // Re-enable colliders on this object and all children.
                foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                    c.enabled = true;

                // Also re-enable renderers AND colliders on ANCESTOR objects.
                // HideBaseSceneGeometry disables ALL renderers/colliders in Level1's
                // root hierarchy. We re-enable the crate's own children above, but the
                // crate may be nested inside a parent that also has Renderer components
                // (e.g. a platform or holder mesh). Walk up 10 levels to cover them.
                Transform ancestor = go.transform.parent;
                for (int walk = 0; walk < 10 && ancestor != null; walk++, ancestor = ancestor.parent)
                {
                    foreach (Renderer r in ancestor.GetComponents<Renderer>())
                        r.enabled = true;
                    foreach (Collider c in ancestor.GetComponents<Collider>())
                        c.enabled = true;
                }

                // Propagate the "AmmoCrate" tag to all child GameObjects.
                // Player.CastInteractionRay checks raycastHit.transform.tag == "AmmoCrate".
                // If the raycast hits a child collider whose tag is not "AmmoCrate",
                // the interaction check silently fails. Tagging all descendants ensures
                // any part of the crate that the ray hits is recognized.
                foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                    child.gameObject.tag = "AmmoCrate";

                StaticLogger.LogInfo(
                    $"Custom Maps:   AmmoCrate[{i}] '{go.name}' → {activeResupplyTargets[i]}.");
            }

            // If there are more game crates than ResupplySpawn markers, hide the extras
            // underground so they don't appear at their original Level1 positions.
            for (int i = moveCount; i < crates.Length; i++)
            {
                crates[i].transform.position = new Vector3(0f, -500f, 0f);
                StaticLogger.LogInfo(
                    $"Custom Maps:   AmmoCrate[{i}] '{crates[i].name}' moved underground " +
                    $"(no ResupplySpawn assigned).");
            }
        }

        // Helper: is the given dropdown index a custom map?
        public static bool IsCustomMapIndex(int index) =>
            builtinMapCount > 0 && index >= builtinMapCount;

        // ── Applies lighting from a JSON string serialized by the exporter ──────────
        // Controls the scene directional light, ambient light, and fog.
        // Called before bundle.Unload so the TextAsset reference is still valid.
        private static void ApplyLightingConfig(string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            LightingData ld;
            try { ld = JsonUtility.FromJson<LightingData>(json); }
            catch (Exception ex)
            {
                StaticLogger.LogWarning($"Custom Maps: Could not parse LightingConfig JSON: {ex.Message}");
                return;
            }

            // Ambient
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(ld.ambR, ld.ambG, ld.ambB);

            // Fog
            RenderSettings.fog = ld.fogEnabled;
            RenderSettings.fogColor = new Color(ld.fogR, ld.fogG, ld.fogB);
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = ld.fogDensity;

            // Directional light — first active one in the scene
            Light sun = null;
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                if (l.type == LightType.Directional) { sun = l; break; }

            if (sun != null)
            {
                sun.color = new Color(ld.sunR, ld.sunG, ld.sunB);
                sun.intensity = ld.sunIntensity;
                sun.transform.rotation = Quaternion.Euler(ld.sunRotX, ld.sunRotY, ld.sunRotZ);
                sun.gameObject.SetActive(true);
                StaticLogger.LogInfo(
                    $"Custom Maps: Sun → color=({ld.sunR:F2},{ld.sunG:F2},{ld.sunB:F2})" +
                    $" intensity={ld.sunIntensity} rot=({ld.sunRotX},{ld.sunRotY},{ld.sunRotZ})");
            }
            else
            {
                StaticLogger.LogWarning("Custom Maps: No directional light in scene — skipping sun config.");
            }

            StaticLogger.LogInfo(
                $"Custom Maps: Lighting applied — ambient=({ld.ambR:F2},{ld.ambG:F2},{ld.ambB:F2})" +
                $" fog={ld.fogEnabled}");
        }
    }

    // ── UplinkRepositioner ───────────────────────────────────────────────────────
    // Attached to MapGeometry at scene load. Waits 2 seconds before repositioning
    // the uplink station so the game's own UplinkStation Awake/Start scripts finish
    // setting their initial position first — otherwise the model-offset calculation
    // reads stale data and gives a wrong result (notably on Mac).
    // Runs on ALL clients; Mirror does not auto-sync scene-object transform changes.
    public class UplinkRepositioner : MonoBehaviour
    {
        public GameObject mapGeo;

        private IEnumerator Start()
        {
            // The game's own uplink-placement logic (UplinkStation.Start / Awake and
            // RoundsHardlineGameManager round-start code) runs at unpredictable times
            // after the scene loads and can override any position we set too early.
            // We keep repositioning every 1 s for 30 s so every game reset is caught.
            float deadline = Time.time + 30f;
            while (Time.time < deadline)
            {
                yield return new WaitForSeconds(1f);
                if (mapGeo == null) { Destroy(this); yield break; }
                CustomMapsPlugin.MoveUplinksToPosition(CustomMapsPlugin.activeUplinkTarget);
            }
            Destroy(this);
        }
    }

    // ── CameraRenderFixer ────────────────────────────────────────────────────────
    // Attached to the MapGeometry root at scene-load time.
    // Polls LateUpdate until all cameras are available, then adds layer 0 (Default)
    // to every camera's culling mask so our custom geometry becomes visible.
    // Destroys itself after patching.
    public class CameraRenderFixer : MonoBehaviour
    {
        private bool done = false;

        private void LateUpdate()
        {
            if (done) return;

            // Only patch Camera.main — patching ALL cameras (e.g. the weapon/depth
            // camera FPS games use) causes the map geometry to be rendered twice at
            // different depths, producing the flickering visual artifact.
            Camera cam = Camera.main;
            if (cam == null) return;

            int before = cam.cullingMask;
            cam.cullingMask |= (1 << 0); // add layer 0 (Default)
            CustomMapsPlugin.StaticLogger.LogInfo(
                $"Custom Maps: Camera '{cam.name}' culling mask " +
                $"0x{before:X8} → 0x{cam.cullingMask:X8}");

            done = true;
            Destroy(this);
        }
    }

    // ── ColorConfigData ──────────────────────────────────────────────────────────
    // Matches ColorExportData in CustomMapExporter.cs.
    // Flat arrays: one entry per material slot, ordered by the same
    // GetComponentsInChildren<MeshRenderer> traversal used at export time.
    [Serializable]
    public class ColorConfigData
    {
        public int[] slotCounts; // number of material slots per renderer
        public float[] r, g, b, a; // one value per material slot (flat)
    }

    // ── TextureConfigData ────────────────────────────────────────────────────
    // Matches TextureExportData / TextureEntry in CustomMapExporter.cs.
    // NOTE: JsonUtility cannot deserialize List<CustomClass> from mod assemblies at
    // runtime, so we use a hand-written parser (ParseTextureConfig) instead.
    public class TextureConfigData
    {
        public List<TextureEntry> entries;
    }

    public class TextureEntry
    {
        public int ri, si;
        public string tex;
        public float tx = 1f, ty = 1f;
        public float ox, oy;
    }

    // ── Manual JSON parser for TextureConfig ─────────────────────────────────
    // Unity's JsonUtility cannot deserialize List<CustomClass> defined in a BepInEx
    // mod assembly, so we parse the known format by hand with Regex.
    internal static class TextureConfigParser
    {
        // All patterns are precompiled once. The old version constructed a fresh
        // Regex per field per entry (7 regex compiles × N entries per map load).
        private static readonly System.Text.RegularExpressions.Regex ObjRx =
            new(@"\{[^{}]*\}",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> IntRx = [];
        private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> FltRx = [];
        private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> StrRx = [];

        private static System.Text.RegularExpressions.Regex GetRx(
            Dictionary<string, System.Text.RegularExpressions.Regex> cache, string key, string pattern)
        {
            if (!cache.TryGetValue(key, out System.Text.RegularExpressions.Regex rx))
            {
                rx = new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                cache[key] = rx;
            }
            return rx;
        }

        internal static TextureConfigData Parse(string json)
        {
            TextureConfigData result = new() { entries = [] };
            int arrStart = json.IndexOf('[');
            int arrEnd = json.LastIndexOf(']');
            if (arrStart < 0 || arrEnd <= arrStart) return result;
            string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

            foreach (System.Text.RegularExpressions.Match m in ObjRx.Matches(arr))
            {
                string obj = m.Value;
                TextureEntry e = new()
                {
                    ri = Int(obj, "ri", 0),
                    si = Int(obj, "si", 0),
                    tex = Str(obj, "tex"),
                    tx = Flt(obj, "tx", 1f),
                    ty = Flt(obj, "ty", 1f),
                    ox = Flt(obj, "ox", 0f),
                    oy = Flt(obj, "oy", 0f),
                };
                result.entries.Add(e);
            }
            return result;
        }

        private static int Int(string obj, string key, int def)
        {
            System.Text.RegularExpressions.Match m = GetRx(IntRx, key, $@"""{key}""\s*:\s*(-?\d+)").Match(obj);
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : def;
        }
        private static float Flt(string obj, string key, float def)
        {
            System.Text.RegularExpressions.Match m = GetRx(FltRx, key, $@"""{key}""\s*:\s*(-?[\d.Ee+\-]+)").Match(obj);
            return m.Success && float.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
        }
        private static string Str(string obj, string key)
        {
            System.Text.RegularExpressions.Match m = GetRx(StrRx, key, $@"""{key}""\s*:\s*""([^""]*)""").Match(obj);
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    // ── LightingData ─────────────────────────────────────────────────────────────
    // Serializable DTO that maps exactly to the JSON written by the Unity exporter.
    // Fields match MapLightingConfig on the template side.
    [Serializable]
    public class LightingData
    {
        // Sun / directional light
        public float sunR = 1f, sunG = 0.96f, sunB = 0.84f;
        public float sunIntensity = 1f;
        public float sunRotX = 50f, sunRotY = -30f, sunRotZ = 0f;
        // Ambient
        public float ambR = 0.21f, ambG = 0.23f, ambB = 0.26f;
        // Fog
        public bool fogEnabled = false;
        public float fogR = 0.5f, fogG = 0.5f, fogB = 0.5f;
        public float fogDensity = 0.01f;
    }

    // ── PATCH 1 ─────────────────────────────────────────────────────────────────
    // LobbyUIManager.Start — inject custom map entries into the map dropdown and
    // expand the possibleMapIcons array so Update() doesn't crash on out-of-range.
    [HarmonyPatch(typeof(LobbyUIManager), "Start")]
    public class LobbyStartPatch
    {
        private static readonly FieldInfo mapSelectField =
            typeof(LobbyUIManager).GetField("mapSelect",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo possibleMapIconsField =
            typeof(LobbyUIManager).GetField("possibleMapIcons",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static void Postfix(LobbyUIManager __instance)
        {
            if (CustomMapsPlugin.customMapBundles.Count == 0) return;

            Dropdown mapSelect = mapSelectField?.GetValue(__instance) as Dropdown;
            if (mapSelect == null)
            {
                CustomMapsPlugin.StaticLogger.LogWarning(
                    "Custom Maps: Could not find 'mapSelect' field on LobbyUIManager.");
                return;
            }

            // Record built-in count BEFORE we add our entries.
            CustomMapsPlugin.builtinMapCount = mapSelect.options.Count;

            // Add one dropdown entry per bundle file.
            List<Dropdown.OptionData> newOptions = [.. CustomMapsPlugin.customMapBundles.Select(b => new Dropdown.OptionData("[Custom] " + Path.GetFileNameWithoutExtension(b)))];
            mapSelect.AddOptions(newOptions);

            // Expand possibleMapIcons so that Update()'s indexed access doesn't throw.
            // Custom-map slots are left as null → RawImage.texture = null is valid.
            if (possibleMapIconsField?.GetValue(__instance) is Texture[] icons)
            {
                Texture[] expanded = new Texture[icons.Length + CustomMapsPlugin.customMapBundles.Count];
                Array.Copy(icons, expanded, icons.Length);
                possibleMapIconsField.SetValue(__instance, expanded);
            }

            CustomMapsPlugin.StaticLogger.LogInfo($"Custom Maps: Added {CustomMapsPlugin.customMapBundles.Count} custom map(s) to dropdown " + $"(built-in count: {CustomMapsPlugin.builtinMapCount}).");

            // ── Backfill pendingBundle if the host had already selected a custom map ──
            // RpcMapSelectedPatch fires before LobbyUIManager.Start() on the guest, so
            // builtinMapCount was 0 when it first ran and IsCustomMapIndex returned false.
            // lastReceivedMapIndex preserved the raw index; re-evaluate it now.
            int lastIdx = CustomMapsPlugin.lastReceivedMapIndex;
            if (CustomMapsPlugin.IsCustomMapIndex(lastIdx))
            {
                int ci = lastIdx - CustomMapsPlugin.builtinMapCount;
                if (ci >= 0 && ci < CustomMapsPlugin.customMapBundles.Count)
                {
                    CustomMapsPlugin.pendingBundle = CustomMapsPlugin.customMapBundles[ci];
                    CustomMapsPlugin.StaticLogger.LogInfo(
                        $"Custom Maps: LobbyStart — backfilled pendingBundle from pre-selected index {lastIdx}" +
                        $" → '{CustomMapsPlugin.pendingBundle}'.");
                }
            }
        }
    }

    // ── PATCH 2 ─────────────────────────────────────────────────────────────────
    // LobbyUIManager.StartGame — when a custom map is selected, temporarily swap
    // the dropdown to index 0 (Level1) so the ORIGINAL StartGame runs intact with
    // all its multiplayer initialisation (RPCs, scene change, etc.).
    // The custom geometry is applied by OnSceneLoaded once Level1 has loaded.
    // We must NOT block the original here — doing so skips multiplayer sync that
    // causes the guest to be dropped from the session.
    [HarmonyPatch(typeof(LobbyUIManager), "StartGame")]
    public class StartGamePatch
    {
        private static readonly FieldInfo mapSelectField =
            typeof(LobbyUIManager).GetField("mapSelect",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static int savedIndex = -1;

        private static bool Prefix(LobbyUIManager __instance)
        {
            Dropdown mapSelect = mapSelectField?.GetValue(__instance) as Dropdown;
            if (mapSelect == null) return true;

            int selectedIndex = mapSelect.value;
            if (!CustomMapsPlugin.IsCustomMapIndex(selectedIndex)) return true; // built-in — run as-is

            // Record which bundle to apply after Level1 loads.
            int ci = selectedIndex - CustomMapsPlugin.builtinMapCount;

            // Redirect to the first built-in map slot (index 0 = Level1) so the
            // original StartGame uses a valid scene name and runs its full
            // multiplayer sync.
            // Use SetValueWithoutNotify so the Dropdown's onValueChanged listener
            // does NOT fire — that listener calls RpcSetMapSelected which would send
            // a spurious index-0 RPC to the guest, potentially clearing pendingBundle
            // at the wrong moment and causing other side-effects.
            savedIndex = selectedIndex;
            mapSelect.SetValueWithoutNotify(0);
            CustomMapsPlugin.pendingBundle = CustomMapsPlugin.customMapBundles[ci];

            CustomMapsPlugin.StaticLogger.LogInfo(
                $"Custom Maps: StartGame — redirecting to built-in map 0 (Level1) " +
                $"for bundle '{CustomMapsPlugin.pendingBundle}'.");

            return true; // let the original run — it handles all multiplayer sync
        }

        private static void Postfix(LobbyUIManager __instance)
        {
            // Restore the dropdown display without triggering onValueChanged.
            if (savedIndex >= 0)
            {
                Dropdown mapSelect = mapSelectField?.GetValue(__instance) as Dropdown;
                mapSelect?.SetValueWithoutNotify(savedIndex);
                savedIndex = -1;
            }
        }
    }

    // ── PATCH 3 (new) ────────────────────────────────────────────────────────────
    // HardlineGameManager.Start — fires after Mirror spawns the manager.
    // If sceneLoaded ran before the manager existed, inject spawn points now.
    [HarmonyPatch(typeof(HardlineGameManager), "Start")]
    public class GameManagerStartPatch
    {
        private static void Postfix(HardlineGameManager __instance)
        {
            if (CustomMapsPlugin.pendingMapGeo == null) return;
            CustomMapsPlugin.StaticLogger.LogInfo(
                "Custom Maps: HardlineGameManager.Start fired — injecting spawn points now.");
            CustomMapsPlugin.InjectSpawnPoints(__instance, CustomMapsPlugin.pendingMapGeo);
            CustomMapsPlugin.pendingMapGeo = null;
        }
    }

    // ── PATCH 4 ─────────────────────────────────────────────────────────────────
    // HardlineGameManager.AllPlayersLoaded — fires after all clients have loaded
    // the scene and players have been spawned.
    // Re-inject spawn lists so any spawning that happens inside the method uses
    // our custom positions.
    [HarmonyPatch(typeof(HardlineGameManager), "AllPlayersLoaded")]
    public class AllPlayersLoadedPatch
    {
        private static void Prefix(HardlineGameManager __instance)
        {
            if (CustomMapsPlugin.activeMapGeo == null) return;
            CustomMapsPlugin.StaticLogger.LogInfo(
                "Custom Maps: AllPlayersLoaded — re-injecting spawn points.");
            CustomMapsPlugin.InjectSpawnPoints(__instance, CustomMapsPlugin.activeMapGeo);
        }

        // Postfix fires AFTER the original AllPlayersLoaded, which may reposition the
        // uplink and resupply stations as part of round setup.  Override those resets.
        private static void Postfix(HardlineGameManager __instance)
        {
            if (CustomMapsPlugin.activeMapGeo == null) return;
            CustomMapsPlugin.StaticLogger.LogInfo(
                "Custom Maps: AllPlayersLoaded (post) — repositioning uplink and resupply.");
            CustomMapsPlugin.MoveUplinksToPosition(
                CustomMapsPlugin.activeUplinkTarget);
            CustomMapsPlugin.RepositionResupplyStations();
        }
    }

    // ── PATCH 5 ─────────────────────────────────────────────────────────────────
    // RoundsHardlineGameManager.SetUplinkNumber — fires each round when the game
    // picks which UplinkStation child to activate. After this runs, the selected
    // child's Awake()/Start() may alter its state; re-apply our target position
    // immediately so the uplink always appears where the map marker says.
    [HarmonyPatch(typeof(RoundsHardlineGameManager), "SetUplinkNumber")]
    public class SetUplinkNumberPatch
    {
        private static void Postfix(int number)
        {
            if (CustomMapsPlugin.activeMapGeo == null) return;
            CustomMapsPlugin.StaticLogger.LogInfo(
                "Custom Maps: SetUplinkNumber (post) — repositioning uplink station.");
            CustomMapsPlugin.MoveUplinksToPosition(
                CustomMapsPlugin.activeUplinkTarget);

            // UplinkStation.Start() sets Visible=false which disables renderers on
            // sub-objects. If DisableUplink() also called SetActive(false) on any
            // sub-object, SetActive(true) on the parent won't re-enable them.
            // Force-enable every sub-object and renderer in the selected child so
            // the uplink is always visible in the correct position.
            Transform uplinkStationsRoot = CustomMapsPlugin.GetUplinkStationsRoot();
            if (uplinkStationsRoot != null && number < uplinkStationsRoot.childCount)
            {
                Transform chosen = uplinkStationsRoot.GetChild(number);
                foreach (Transform sub in chosen.GetComponentsInChildren<Transform>(true))
                    sub.gameObject.SetActive(true);
                foreach (Renderer r in chosen.GetComponentsInChildren<Renderer>(true))
                    r.enabled = true;
                CustomMapsPlugin.StaticLogger.LogInfo(
                    $"Custom Maps: SetUplinkNumber (post) — enabled child[{number}] renderers.");
            }
        }
    }

    // ── PATCH 6 ─────────────────────────────────────────────────────────────────
    // LobbyUIManager.UserCode_RpcSetMapSelected — clients receive map-selection
    // changes from the host every frame. When a custom map is selected, save the
    // bundle name so OnSceneLoaded can apply it on the client too.
    [HarmonyPatch(typeof(LobbyUIManager), "UserCode_RpcSetMapSelected")]
    public class RpcMapSelectedPatch
    {
        private static int lastMap = -1; // suppress per-frame log spam

        private static void Postfix(int map)
        {
            // Always store the raw index BEFORE the dedup check so LobbyStartPatch
            // can backfill pendingBundle if builtinMapCount was 0 when this first fired.
            CustomMapsPlugin.lastReceivedMapIndex = map;

            if (map == lastMap) return;
            lastMap = map;

            if (CustomMapsPlugin.IsCustomMapIndex(map))
            {
                int ci = map - CustomMapsPlugin.builtinMapCount;
                if (ci < CustomMapsPlugin.customMapBundles.Count)
                {
                    CustomMapsPlugin.pendingBundle = CustomMapsPlugin.customMapBundles[ci];
                    CustomMapsPlugin.StaticLogger.LogInfo(
                        $"Custom Maps: Host selected custom map '{CustomMapsPlugin.pendingBundle}'.");
                }
            }
            else
            {
                // Host switched back to a built-in map.
                CustomMapsPlugin.pendingBundle = null;
            }
        }
    }

    // ── PATCH 6 ─────────────────────────────────────────────────────────────────
    // Player.GoToSpawn — runs AFTER the original GoToSpawn so the normal game flow
    // (networking signals, velocity reset, etc.) completes first, and we just
    // override the final position with the correct custom-map spawn.
    //
    // Handles ALL players (not just local) so the server correctly positions
    // remote players too — Mirror then syncs those positions to all clients.
    [HarmonyPatch(typeof(Player), "GoToSpawn")]
    public class GoToSpawnPatch
    {
        private static bool teamReadErrorLogged = false;

        private static void Postfix(Player __instance)
        {
            if (CustomMapsPlugin.activeMapGeo == null) return;

            // Read the player's team (1 or 2) via the cached PropertyInfo —
            // resolving it with GetProperty() on every spawn was wasted work.
            int team = 0;
            try
            {
                if (CustomMapsPlugin.HumanTeamProperty != null)
                    team = (int)CustomMapsPlugin.HumanTeamProperty.GetValue(__instance);
            }
            catch (Exception ex)
            {
                if (!teamReadErrorLogged)
                {
                    teamReadErrorLogged = true;
                    CustomMapsPlugin.StaticLogger.LogWarning(
                        $"Custom Maps: Could not read player team: {ex.Message}");
                }
            }

            // Spawn positions come from a per-map cache built on first use —
            // previously this scanned every Transform in the map geometry (twice,
            // when the team-specific list was empty) on every single spawn.
            // The cache handles the fall-back-to-other-team case internally.
            List<Vector3> positions = CustomMapsPlugin.GetCachedSpawnPositions(team);

            if (positions == null || positions.Count == 0) return; // no spawns found — leave where original put them

            Vector3 spawnPos = positions[UnityEngine.Random.Range(0, positions.Count)];

            // Teleport — disable CharacterController first to avoid physics conflicts.
            CharacterController cc = __instance.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            __instance.transform.position = spawnPos;
            if (cc != null) cc.enabled = true;

            CustomMapsPlugin.StaticLogger.LogInfo(
                $"Custom Maps: GoToSpawnPatch — team={team}" +
                $" candidates={positions.Count} → {spawnPos}.");
        }
    }
}