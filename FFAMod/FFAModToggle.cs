using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using Mirror;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace FFAMod
{
    // Mod-defined network messages for the plain FFA round-win/restart/game-win flow.
    // Mirror's Weaver (which auto-generates message serialization) only runs against the
    // game's own assembly at build time, never against this mod's DLL, so these are wired
    // up manually via Reader<T>/Writer<T> in FFAModToggle.RegisterFFANetworkMessages.
    internal struct FFARoundWinMessage : NetworkMessage
    {
        public string winnerName;
        public int score;
    }

    internal struct FFARestartRoundMessage : NetworkMessage
    {
    }

    internal struct FFAGameWinMessage : NetworkMessage
    {
        public string winnerName;
    }

    [BepInPlugin("com.peakzelo.ffamod", "Free For All Mode Toggle", "4.6.7")]
    // Removed BepInProcess so it works on both Windows and Mac
    public class FFAModToggle : BaseUnityPlugin
    {

        // Track used spawn positions to prevent spawning on top of each other
        private static List<Transform> usedSpawnPositions = new List<Transform>();
        private static Dictionary<Vector3, float> activeSpawnPositions = new Dictionary<Vector3, float>(); // Track positions with timestamp
        private static float spawnPositionTimeout = 5f; // Time before a spawn position becomes available again
        private static bool isResettingSpawns = false;
        private static System.Random spawnRandom = new System.Random(); // Use System.Random for better distribution
        private static object spawnLock = new object(); // Thread safety for spawn selection

        // FFA weapon tier progression - increases every 2 completed rounds
        private static int ffaCompletedRounds = 0;

        // Custom spawn points for specific maps (collected via F6)
        private static Dictionary<string, List<Vector3>> customMapSpawns = new Dictionary<string, List<Vector3>>()
        {
            {
                "level1", new List<Vector3>()  // Compound map
                {
                    new Vector3(264.7566f, 11.62088f, 413.8766f),
                    new Vector3(254.5119f, 8.373138f, 426.4711f),
                    new Vector3(218.5967f, 13.9926f, 408.3055f),
                    new Vector3(293.7034f, 7.67872f, 399.9081f),
                    new Vector3(312.255f, 4.505035f, 450.6898f),
                    new Vector3(266.6603f, 7.914889f, 421.6767f)
                }
            },
            {
                "level2", new List<Vector3>()  // Shipyard map
                {
                    new Vector3(21.39629f, -0.5049999f, 39.26278f),
                    new Vector3(-0.2652665f, -0.5049999f, 39.50876f),
                    new Vector3(-15.00573f, -0.505f, 24.28817f),
                    new Vector3(-14.34395f, -0.4006001f, 4.500097f),
                    new Vector3(-5.939525f, -0.505f, 21.87281f),
                    new Vector3(17.99793f, -0.3616391f, 17.14673f)
                }
            },
            {
                "level3", new List<Vector3>()  // Level 3 map
                {
                    new Vector3(-2.997097f, 3.406296f, 36.16267f),
                    new Vector3(-22.42639f, 3.406297f, 28.62449f),
                    new Vector3(-12.86422f, 3.406299f, 16.55714f),
                    new Vector3(-42.06099f, 3.406301f, 2.713515f),
                    new Vector3(-43.00544f, 3.406298f, 23.95444f),
                    new Vector3(-29.67885f, 3.406295f, 42.39952f),
                    new Vector3(-43.28857f, 0.1062984f, 16.08615f),
                    new Vector3(-33.97502f, 0.1063007f, 3.545799f),
                    new Vector3(-23.4622f, 0.1063006f, 2.751948f),
                    new Vector3(-26.08475f, 0.106297f, 25.8184f),
                    new Vector3(-16.96184f, 0.1062959f, 32.29234f),
                    new Vector3(-4.005488f, 0.1062943f, 42.25777f),
                    new Vector3(-3.115857f, 0.1062964f, 28.70715f)
                }
            },
            {
                "level4", new List<Vector3>()  // Level 4 map
                {
                    new Vector3(399.0235f, 11.57107f, 455.9523f),
                    new Vector3(372.2059f, 5.441811f, 519.1681f),
                    new Vector3(372.2074f, 15.09752f, 570.8201f),
                    new Vector3(300.5229f, 18.79935f, 539.171f),
                    new Vector3(318.6195f, 15.108f, 508.1286f),
                    new Vector3(324.0158f, 14.94996f, 496.7177f),
                    new Vector3(333.5532f, 15.11f, 483.7884f),
                    new Vector3(304.7079f, 13.08234f, 444.0976f),
                    new Vector3(230.6507f, 8.836293f, 475.3601f),
                    new Vector3(248.1366f, 12.55542f, 476.2947f)
                }
            },
            {
                "level5", new List<Vector3>()  // Level 5 map
                {
                    new Vector3(25.5104f, 30.02943f, -3.280473f),
                    new Vector3(-6.63164f, 29.87868f, -8.156416f),
                    new Vector3(-21.52309f, 30.04172f, -13.34181f),
                    new Vector3(-3.265413f, 43.01818f, 4.558913f),
                    new Vector3(-11.91967f, 45.60149f, -9.918157f),
                    new Vector3(-15.9237f, 41.99507f, 19.20911f),
                    new Vector3(6.455793f, 42.49228f, 2.657346f),
                    new Vector3(14.45212f, 42.05228f, -8.067226f),
                    new Vector3(9.320208f, 41.19827f, -34.16364f),
                    new Vector3(-28.94631f, 41.83363f, -20.29543f)
                }
            }
        };
        private static List<Transform> customSpawnPoints = new List<Transform>();
        private static bool customSpawnsGenerated = false;
        private static string currentMapName = "";

        // Static logger for Harmony patches
        public static ManualLogSource StaticLogger;
        public static FFAModToggle Instance;

        // Local FFA mode toggle (each player controls their own)
        private static bool localFFAMode = false;
        private bool showFFAMenu = false;
        private Rect ffaMenuRect = new Rect(Screen.width / 2 - 150, Screen.height / 2 - 100, 300, 220);

        // Gun Game mode
        private static bool gunGameMode = false;
        private static Dictionary<uint, int> gunGamePlayerLevel = new Dictionary<uint, int>(); // netId -> weapon level
        private static Dictionary<uint, float> gunGameDeathTimes = new Dictionary<uint, float>(); // netId -> death time
        private static float gunGameRespawnDelay = 3f; // Seconds before respawning dead players

        // SERVER-SIDE Gun Game tracking (for win detection when clients win)
        // The server tracks kills/demotions independently so it can detect when ANY player wins
        private static Dictionary<uint, int> serverGunGameKills = new Dictionary<uint, int>();
        private static Dictionary<uint, int> serverGunGameDemotions = new Dictionary<uint, int>();
        private static bool gunGameEnded = false; // Prevent double-win calls

        // Track last kill info for knife demotion (set in HitAnotherTarget, read in AddKill)
        private static Human gunGameLastKillTarget = null;
        private static bool gunGameLastKillWasKnife = false;

        public static void SetLastKillInfo(Human target, bool wasKnife)
        {
            gunGameLastKillTarget = target;
            gunGameLastKillWasKnife = wasKnife;
        }

        public static Human GetLastKillTarget() { return gunGameLastKillTarget; }
        public static bool WasLastKillKnife() { return gunGameLastKillWasKnife; }
        public static void ClearLastKillInfo() { gunGameLastKillTarget = null; gunGameLastKillWasKnife = false; }

        // Gun Game weapon progression (0 = pistol, final = knife)
        // NOTE: LoadItem adds "Weapon_" prefix automatically, so just use the base name
        private static List<string> gunGameWeapons = new List<string>()
        {
            "Glock",        // Level 0 - Pistol
            "M45",          // Level 1 - Pistol 2
            "X22",          // Level 2 - Machine Pistol
            "MP5",          // Level 3 - SMG
            "P90",          // Level 4 - SMG 2
            "MPLCarbine",   // Level 5 - Carbine
            "M416",         // Level 6 - Assault Rifle
            "AK15",         // Level 7 - Assault Rifle 2
            "SA58",         // Level 8 - Battle Rifle
            "M870",         // Level 9 - Shotgun
            "M249",         // Level 10 - LMG
            "Scout",        // Level 11 - Sniper
            "M24",          // Level 12 - Sniper 2
            "Knife"         // Level 13 - FINAL (get a kill to win)
        };

        // FFA team assignment system - use player names as keys for persistence
        private static Dictionary<string, int> ffaPlayerTeams = new Dictionary<string, int>();

        // ==================== NATIVE FFA ROUND-FLOW REPLICATION ====================
        // Plain "Free For All" round win / restart / game win used to rely on FFA support
        // baked directly into the game's compiled assembly (a RoundsHardlineGameManager.
        // FFARoundMode/FFARoundWin/FFARestartRound/FFAEndGame chain plus three Mirror RPCs).
        // Reimplemented entirely here so it works against a stock game install: state lives
        // in this mod instead of on RoundsHardlineGameManager, and round-win/restart/game-win
        // sync uses Mirror's manual NetworkMessage registration (Reader<T>/Writer<T>) instead
        // of Weaver-generated RPCs, since Weaver never runs against this mod's own assembly.
        private static Dictionary<uint, int> ffaRoundWinScores = new Dictionary<uint, int>();
        private static Dictionary<uint, string> ffaRoundWinNames = new Dictionary<uint, string>();
        private static bool ffaRoundWinEndFlag = false;
        private const float ffaRoundWinRestartDelay = 4f;
        private const int ffaRoundWinRequiredWins = 5;

        // Set LobbyUIManager.isFreeForAllMode via reflection when it exists (only true when
        // running against an assembly with the native FFA UI polish baked in - Scoreboard /
        // RoundsUserInterface / MultiplayerNetworkManager read it for cosmetic display only).
        // No-ops on a stock install so this mod compiles and runs the same either way.
        private static readonly System.Reflection.FieldInfo nativeIsFreeForAllModeField =
            typeof(LobbyUIManager).GetField("isFreeForAllMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        private static void SetNativeFFAFlag(bool value)
        {
            nativeIsFreeForAllModeField?.SetValue(null, value);
        }

        private static bool GetNativeFFAFlag()
        {
            return nativeIsFreeForAllModeField != null && (bool)nativeIsFreeForAllModeField.GetValue(null);
        }

        private static readonly System.Reflection.FieldInfo gameUIField =
            typeof(HardlineGameManager).GetField("gameUI", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo roundEndFlagField =
            typeof(RoundsHardlineGameManager).GetField("roundEndFlag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static void ShowGameNotification(HardlineGameManager manager, string message)
        {
            var gameUI = gameUIField?.GetValue(manager);
            if (gameUI == null) return;
            var showNotificationMethod = gameUI.GetType().GetMethod("ShowNotification",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            showNotificationMethod?.Invoke(gameUI, new object[] { message });
        }

        // Registers the mod's own network messages with Mirror so round-win/restart/game-win
        // state can be broadcast to clients without needing Weaver-generated RPCs.
        private static void RegisterFFANetworkMessages()
        {
            Writer<FFARoundWinMessage>.write = (writer, msg) =>
            {
                writer.WriteString(msg.winnerName);
                writer.WriteInt(msg.score);
            };
            Reader<FFARoundWinMessage>.read = reader => new FFARoundWinMessage
            {
                winnerName = reader.ReadString(),
                score = reader.ReadInt()
            };

            Writer<FFARestartRoundMessage>.write = (writer, msg) => { };
            Reader<FFARestartRoundMessage>.read = reader => new FFARestartRoundMessage();

            Writer<FFAGameWinMessage>.write = (writer, msg) => writer.WriteString(msg.winnerName);
            Reader<FFAGameWinMessage>.read = reader => new FFAGameWinMessage { winnerName = reader.ReadString() };

            NetworkClient.RegisterHandler<FFARoundWinMessage>(OnFFARoundWinMessage);
            NetworkClient.RegisterHandler<FFARestartRoundMessage>(OnFFARestartRoundMessage);
            NetworkClient.RegisterHandler<FFAGameWinMessage>(OnFFAGameWinMessage);
        }

        // Server-side: detects the last player standing and drives the round-win flow.
        // Equivalent to the native FFARoundMode check (same gameStartTimer countdown guard).
        public static void RunFFARoundModeCheck(RoundsHardlineGameManager manager)
        {
            if (manager.NetworkgameStartTimer > 0) return; // countdown/loadout phase

            Player lastPlayerAlive = null;
            int aliveCount = 0;
            foreach (Player p in UnityEngine.Object.FindObjectsOfType<Player>())
            {
                if (p.Health > 0f)
                {
                    aliveCount++;
                    lastPlayerAlive = p;
                }
            }

            if (aliveCount == 1 && lastPlayerAlive != null && !ffaRoundWinEndFlag)
            {
                RunFFARoundWin(manager, lastPlayerAlive);
            }
        }

        public static void RunFFARoundWin(RoundsHardlineGameManager manager, Player winner)
        {
            if (ffaRoundWinEndFlag) return;
            ffaRoundWinEndFlag = true;
            roundEndFlagField?.SetValue(manager, true);

            uint netId = winner.netId;
            if (!ffaRoundWinScores.ContainsKey(netId))
            {
                ffaRoundWinScores[netId] = 0;
                ffaRoundWinNames[netId] = winner.HumanName;
            }
            ffaRoundWinScores[netId]++;
            int currentScore = ffaRoundWinScores[netId];
            string winnerName = winner.HumanName;

            // Weapon tier progression (read by FFAGenerateRandomLoadoutPatch) advances every
            // 2 completed rounds, same as the native flow this replaces.
            IncrementFFACompletedRounds();

            StaticLogger.LogInfo($"FFA: {winnerName} wins the round! Score: {currentScore}/{ffaRoundWinRequiredWins}");
            ShowGameNotification(manager, $"{winnerName} wins the round! ({currentScore}/{ffaRoundWinRequiredWins})");
            NetworkServer.SendToAll(new FFARoundWinMessage { winnerName = winnerName, score = currentScore });

            if (currentScore >= ffaRoundWinRequiredWins)
            {
                RunFFAEndGame(manager, winner);
            }
            else if (Instance != null)
            {
                Instance.StartCoroutine(DelayedFFARestartRound(manager));
            }
        }

        private static IEnumerator DelayedFFARestartRound(RoundsHardlineGameManager manager)
        {
            yield return new WaitForSeconds(ffaRoundWinRestartDelay);
            RunFFARestartRound(manager);
        }

        public static void RunFFARestartRound(RoundsHardlineGameManager manager)
        {
            ffaRoundWinEndFlag = false;
            roundEndFlagField?.SetValue(manager, false);
            manager.RestartGameCharacter();
            NetworkServer.SendToAll(new FFARestartRoundMessage());
        }

        private static readonly System.Reflection.FieldInfo serverCloseOnGameEndDelayField =
            typeof(RoundsHardlineGameManager).GetField("serverCloseOnGameEndDelay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo clientLeaveOnGameEndDelayField =
            typeof(RoundsHardlineGameManager).GetField("clientLeaveOnGameEndDelay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        public static void RunFFAEndGame(RoundsHardlineGameManager manager, Player winner)
        {
            string winnerName = winner.HumanName;
            StaticLogger.LogInfo($"FFA: {winnerName} wins the game!");
            ShowGameNotification(manager, $"{winnerName} wins the game!");
            NetworkServer.SendToAll(new FFAGameWinMessage { winnerName = winnerName });

            StaticLogger.LogInfo("FFA: === Final Scores ===");
            foreach (var score in ffaRoundWinScores.OrderByDescending(x => x.Value))
            {
                string playerName = ffaRoundWinNames.ContainsKey(score.Key) ? ffaRoundWinNames[score.Key] : "Unknown";
                StaticLogger.LogInfo($"FFA:   {playerName}: {score.Value}");
            }

            // Shut down the server / return to lobby after a delay - same as the native
            // FFAEndGame flow this replaces (RunFFAEndGame is always called server-side).
            float shutdownDelay = manager.isServer
                ? (float?)serverCloseOnGameEndDelayField?.GetValue(manager) ?? 8f
                : (float?)clientLeaveOnGameEndDelayField?.GetValue(manager) ?? 6f;
            manager.Invoke("ShutdownGame", shutdownDelay);

            ffaRoundWinScores.Clear();
            ffaRoundWinNames.Clear();
        }

        // Client-side handlers - mirror the native UserCode_RpcFFA* methods' "!isServer" guard,
        // since the server already showed/handled these locally when it computed the result.
        private static void OnFFARoundWinMessage(FFARoundWinMessage msg)
        {
            if (NetworkServer.active) return;

            // Keep this client's weapon tier counter in sync with the server's (same as the
            // native UserCode_RpcFFARoundWin flow this replaces).
            IncrementFFACompletedRounds();
            StaticLogger.LogInfo($"FFA: Client synced round counter. Total rounds: {GetFFACompletedRounds()}, Weapon tier: {GetFFAWeaponTier()}");

            var manager = UnityEngine.Object.FindObjectOfType<RoundsHardlineGameManager>();
            if (manager != null)
            {
                ShowGameNotification(manager, $"{msg.winnerName} wins the round! ({msg.score}/{ffaRoundWinRequiredWins})");
            }
        }

        private static void OnFFARestartRoundMessage(FFARestartRoundMessage msg)
        {
            if (NetworkServer.active) return;
            ffaRoundWinEndFlag = false;
            var manager = UnityEngine.Object.FindObjectOfType<RoundsHardlineGameManager>();
            if (manager != null)
            {
                roundEndFlagField?.SetValue(manager, false);
                manager.RestartGameCharacter();
            }
        }

        private static void OnFFAGameWinMessage(FFAGameWinMessage msg)
        {
            if (NetworkServer.active) return;
            var manager = UnityEngine.Object.FindObjectOfType<RoundsHardlineGameManager>();
            if (manager != null)
            {
                ShowGameNotification(manager, $"{msg.winnerName} wins the game!");
            }
        }

        // Keybind configuration
        private static KeyCode ffaMenuKey = KeyCode.F7;
        private static KeyCode logSpawnKey = KeyCode.F6;
        private static KeyCode keybindMenuKey = KeyCode.F5;

        private bool showKeybindMenu = false;
        private Rect keybindMenuRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 200, 400, 400);
        private bool isWaitingForKey = false;
        private string keyToRebind = "";

        private Harmony harmony;

        private void Awake()
        {
            StaticLogger = Logger;
            Instance = this;
            Logger.LogInfo("Free For All Mode Toggle initialized!");
            Logger.LogInfo("FFA Mod v4.6.7 - Fixed rainbow name overwriting FFA score");
            Logger.LogInfo("Applying Harmony patches...");

            // Apply Harmony patches
            harmony = new Harmony("com.peakzelo.ffamod");
            harmony.PatchAll();

            // RoundsHardlineGameManager may have its own hidden "new" override of
            // GetSpawnPositionForTeam if running against an assembly with native FFA support
            // baked in. Only patch it if it actually exists - FFASpawnPatchBase (auto-patched
            // above via PatchAll) already covers a stock game install fully on its own.
            var nativeSpawnMethod = AccessTools.DeclaredMethod(typeof(RoundsHardlineGameManager), "GetSpawnPositionForTeam");
            if (nativeSpawnMethod != null)
            {
                harmony.Patch(nativeSpawnMethod, prefix: new HarmonyMethod(typeof(FFASpawnPatch), nameof(FFASpawnPatch.Prefix)));
                Logger.LogInfo("FFA: Native GetSpawnPositionForTeam override detected - patched directly.");
            }

            RegisterFFANetworkMessages();

            Logger.LogInfo("Harmony patches applied successfully!");

            // Start rainbow name scanning coroutine
            StartCoroutine(RainbowNameScanCoroutine());
        }

        private void OnDestroy()
        {
            // Unpatch when mod is unloaded
            harmony?.UnpatchSelf();
        }

        private void OnLevelWasLoaded(int level)
        {
            Logger.LogInfo($"Level loaded: {level}");

            // Detect map name
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.ToLower();
            currentMapName = sceneName;
            Logger.LogInfo($"Current map: {currentMapName}");

            // Reset custom spawns when scene changes
            customSpawnsGenerated = false;
            customSpawnPoints.Clear();

            // Reset FFA completed rounds counter when map changes
            ResetFFACompletedRounds();
        }

        private void Update()
        {
            // Handle keybind input when waiting for a key
            if (isWaitingForKey)
            {
                if (Input.anyKeyDown)
                {
                    foreach (KeyCode keyCode in System.Enum.GetValues(typeof(KeyCode)))
                    {
                        if (Input.GetKeyDown(keyCode))
                        {
                            AssignKeybind(keyToRebind, keyCode);
                            isWaitingForKey = false;
                            keyToRebind = "";
                            break;
                        }
                    }
                }
                return; // Don't process other inputs while waiting for key
            }

            // Toggle keybind menu
            if (Input.GetKeyDown(keybindMenuKey))
            {
                showKeybindMenu = !showKeybindMenu;
                Logger.LogInfo($"Keybind Menu toggled: {showKeybindMenu}");
            }

            // Toggle FFA menu
            if (Input.GetKeyDown(ffaMenuKey))
            {
                showFFAMenu = !showFFAMenu;
                Logger.LogInfo($"FFA Menu toggled: {showFFAMenu}");
            }

            // Log current player position for spawn collection
            if (Input.GetKeyDown(logSpawnKey))
            {
                // Find local player
                Player localPlayer = null;
                foreach (Player p in FindObjectsOfType<Player>())
                {
                    if (p.hasAuthority)
                    {
                        localPlayer = p;
                        break;
                    }
                }

                if (localPlayer != null)
                {
                    Vector3 pos = localPlayer.transform.position;
                    Quaternion rot = localPlayer.transform.rotation;
                    Logger.LogInfo($"=== SPAWN POSITION COLLECTED ===");
                    Logger.LogInfo($"Position: new Vector3({pos.x}f, {pos.y}f, {pos.z}f)");
                    Logger.LogInfo($"Rotation: new Quaternion({rot.x}f, {rot.y}f, {rot.z}f, {rot.w}f)");
                    Logger.LogInfo($"================================");
                }
                else
                {
                    Logger.LogWarning("No local player found to log position");
                }
            }
        }

        private void OnGUI()
        {
            try
            {
                // Show FFA mode status in corner
                if (localFFAMode)
                {
                    GUIStyle statusStyle = new GUIStyle(GUI.skin.label);
                    statusStyle.fontSize = 14;
                    statusStyle.fontStyle = FontStyle.Bold;
                    statusStyle.normal.textColor = Color.green;
                    statusStyle.alignment = TextAnchor.UpperLeft;

                    GUI.Label(new Rect(10, 60, 300, 25), $"FFA MODE: ENABLED ({ffaMenuKey})", statusStyle);
                }
                else
                {
                    GUIStyle statusStyle = new GUIStyle(GUI.skin.label);
                    statusStyle.fontSize = 12;
                    statusStyle.normal.textColor = Color.yellow;
                    statusStyle.alignment = TextAnchor.UpperLeft;

                    GUI.Label(new Rect(10, 60, 300, 25), $"Press {ffaMenuKey} for FFA Mode | {keybindMenuKey} for Keybinds", statusStyle);
                }

                // Show FFA toggle menu
                if (showFFAMenu)
                {
                    ffaMenuRect = GUI.Window(99999, ffaMenuRect, DrawFFAMenu, "Free For All Mode");
                }

                // Show keybind configuration menu
                if (showKeybindMenu)
                {
                    keybindMenuRect = GUI.Window(99998, keybindMenuRect, DrawKeybindMenu, "Keybind Configuration");
                }
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"OnGUI error: {ex.Message}");
            }
        }

        private void DrawFFAMenu(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.Space(10);

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 14;
            labelStyle.alignment = TextAnchor.MiddleCenter;
            labelStyle.wordWrap = true;

            GUILayout.Label("Select Game Mode", labelStyle);

            GUILayout.Space(10);

            // Button style
            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 14;
            buttonStyle.padding = new RectOffset(10, 10, 10, 10);

            Color originalColor = GUI.backgroundColor;

            // FFA Mode Button
            string ffaButtonText = localFFAMode ? "FFA Mode: ON" : "FFA Mode: OFF";
            GUI.backgroundColor = localFFAMode ? Color.green : Color.gray;

            if (GUILayout.Button(ffaButtonText, buttonStyle, GUILayout.Height(40)))
            {
                // Disable Gun Game if enabling FFA
                if (!localFFAMode && gunGameMode)
                {
                    gunGameMode = false;
                    Logger.LogInfo("Gun Game disabled (switching to FFA)");
                }

                localFFAMode = !localFFAMode;

                // Set the game's FFA flag (if present) so any native FFA UI polish stays in sync
                SetNativeFFAFlag(localFFAMode || gunGameMode);

                Logger.LogInfo($"FFA Mode: {localFFAMode}");
            }

            GUILayout.Space(5);

            // Gun Game Mode Button
            string gunGameButtonText = gunGameMode ? "Gun Game: ON" : "Gun Game: OFF";
            GUI.backgroundColor = gunGameMode ? Color.cyan : Color.gray;

            if (GUILayout.Button(gunGameButtonText, buttonStyle, GUILayout.Height(40)))
            {
                // Disable FFA if enabling Gun Game
                if (!gunGameMode && localFFAMode)
                {
                    localFFAMode = false;
                    Logger.LogInfo("FFA disabled (switching to Gun Game)");
                }

                gunGameMode = !gunGameMode;

                // Reset Gun Game state when enabling
                if (gunGameMode)
                {
                    gunGamePlayerLevel.Clear();
                    serverGunGameKills.Clear();
                    serverGunGameDemotions.Clear();
                    gunGameEnded = false;
                    Logger.LogInfo("Gun Game enabled - all players start at level 0 (Pistol)");
                }

                // Gun Game also needs the native FFA flag (if present) kept in sync
                SetNativeFFAFlag(localFFAMode || gunGameMode);

                Logger.LogInfo($"Gun Game Mode: {gunGameMode}");
            }

            GUI.backgroundColor = originalColor;

            GUILayout.Space(10);

            // Show current mode status
            GUIStyle statusStyle = new GUIStyle(GUI.skin.label);
            statusStyle.fontSize = 11;
            statusStyle.alignment = TextAnchor.MiddleCenter;
            statusStyle.normal.textColor = Color.yellow;

            if (gunGameMode)
            {
                GUILayout.Label("Gun Game: Get kills to upgrade weapons.\nFirst to get a knife kill wins!", statusStyle);
            }
            else if (localFFAMode)
            {
                GUILayout.Label("Free For All: Last player standing wins!", statusStyle);
            }
            else
            {
                GUILayout.Label("No custom mode active - Team play", statusStyle);
            }

            GUILayout.Space(10);

            if (GUILayout.Button("Close", GUILayout.Height(30)))
            {
                showFFAMenu = false;
            }

            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void DrawKeybindMenu(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.Space(10);

            GUIStyle titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 16;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.alignment = TextAnchor.MiddleCenter;

            GUILayout.Label("Configure Keybinds", titleStyle);

            GUILayout.Space(15);

            // Status message when waiting for key
            if (isWaitingForKey)
            {
                GUIStyle waitStyle = new GUIStyle(GUI.skin.label);
                waitStyle.fontSize = 14;
                waitStyle.normal.textColor = Color.yellow;
                waitStyle.alignment = TextAnchor.MiddleCenter;
                waitStyle.wordWrap = true;

                GUILayout.Label("Press any key to bind...\n(ESC to cancel)", waitStyle);
                GUILayout.Space(10);

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    isWaitingForKey = false;
                    keyToRebind = "";
                    Logger.LogInfo("Keybind cancelled");
                }
            }

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 13;
            labelStyle.alignment = TextAnchor.MiddleLeft;

            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.fontSize = 12;

            // FFA Menu keybind
            GUILayout.BeginHorizontal();
            GUILayout.Label("FFA Menu Toggle:", labelStyle, GUILayout.Width(180));
            if (GUILayout.Button($"[{ffaMenuKey}]", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                StartRebind("ffaMenu");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Log Spawn keybind
            GUILayout.BeginHorizontal();
            GUILayout.Label("Log Spawn Position:", labelStyle, GUILayout.Width(180));
            if (GUILayout.Button($"[{logSpawnKey}]", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                StartRebind("logSpawn");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Keybind Menu keybind
            GUILayout.BeginHorizontal();
            GUILayout.Label("Keybind Menu:", labelStyle, GUILayout.Width(180));
            if (GUILayout.Button($"[{keybindMenuKey}]", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                StartRebind("keybindMenu");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Host Kick Menu keybind
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host Kick Menu:", labelStyle, GUILayout.Width(180));
            if (GUILayout.Button($"[{HostKickMod.HostKickMod.kickMenuKey}]", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                StartRebind("kickMenu");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Crosshair Menu keybind
            GUILayout.BeginHorizontal();
            GUILayout.Label("Crosshair Menu:", labelStyle, GUILayout.Width(180));
            if (GUILayout.Button($"[{HostKickMod.HostKickMod.crosshairMenuKey}]", buttonStyle, GUILayout.Width(100), GUILayout.Height(30)))
            {
                StartRebind("crosshairMenu");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(20);

            // Close button
            if (GUILayout.Button("Close", GUILayout.Height(35)))
            {
                showKeybindMenu = false;
            }

            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // Client-side round tracking - use timer since RoundEndFlag isn't synced
        private static float lastTimerValue = -1f;
        private static bool hasRespawnedThisRound = false;

        // Helper to strip color tags from rainbow-formatted text
        private static string StripColorTags(string text)
        {
            return System.Text.RegularExpressions.Regex.Replace(text, @"<color=#[A-Fa-f0-9]{6}>|</color>", "");
        }

        // Hook into the game's spawn system to ensure unique spawn positions
        private void LateUpdate()
        {
            // ===== RAINBOW NAME UPDATE (runs every frame to prevent flicker) =====
            rainbowHue = (rainbowHue + rainbowSpeed * Time.deltaTime) % 1f;
            try
            {
                var deadIds = new List<int>();
                foreach (var kvp in rainbowTrackedById)
                {
                    int id = kvp.Key;
                    Text textComp = kvp.Value;
                    if (textComp == null)
                    {
                        deadIds.Add(id);
                        continue;
                    }
                    // Get current text and strip any existing rainbow formatting
                    // This allows other systems (like FFA score fix) to update the text
                    // without being overwritten by cached original
                    string currentText = textComp.text;
                    string plainText = currentText.Contains("<color=") ? StripColorTags(currentText) : currentText;

                    // Apply rainbow to the current plain text (not cached original)
                    if (plainText.Contains(RAINBOW_USERNAME + " (dev)"))
                    {
                        // Already has (dev) suffix, just update rainbow colors
                        textComp.text = plainText.Replace(RAINBOW_USERNAME + " (dev)", MakeRainbowText(RAINBOW_USERNAME + " (dev)"));
                    }
                    else if (plainText.Contains(RAINBOW_USERNAME))
                    {
                        // First time seeing this text, add (dev) suffix
                        textComp.text = plainText.Replace(RAINBOW_USERNAME, MakeRainbowText(RAINBOW_USERNAME + " (dev)"));
                    }
                }
                foreach (int id in deadIds)
                {
                    rainbowTrackedById.Remove(id);
                    rainbowOriginalById.Remove(id);
                }
            }
            catch (System.Exception) { }
            // ===== END RAINBOW NAME UPDATE =====

            // Handle Gun Game mode separately from FFA
            if (!localFFAMode && !gunGameMode)
            {
                usedSpawnPositions.Clear();
                activeSpawnPositions.Clear();
                lastTimerValue = -1f;
                hasRespawnedThisRound = false;
                return;
            }

            // Gun Game respawn logic
            if (gunGameMode && !localFFAMode)
            {
                // Enforce the native FFA flag (if present) so players on unique teams can damage each other
                if (!GetNativeFFAFlag())
                {
                    SetNativeFFAFlag(true);
                }

                // Find local player (the one we have authority over)
                Player localPlayer = null;
                foreach (Player p in FindObjectsOfType<Player>())
                {
                    if (p.hasAuthority)
                    {
                        localPlayer = p;
                        break;
                    }
                }

                if (localPlayer != null)
                {
                    uint netId = localPlayer.netId;

                    // Detect death and track respawn timing
                    if (localPlayer.Health <= 0 && localPlayer.CallDeathFlag)
                    {
                        if (!gunGameDeathTimes.ContainsKey(netId))
                        {
                            gunGameDeathTimes[netId] = Time.time;
                            Logger.LogInfo($"Gun Game: {localPlayer.HumanName} died, respawning in {gunGameRespawnDelay}s");
                        }

                        // Respawn after delay
                        if (Time.time - gunGameDeathTimes[netId] >= gunGameRespawnDelay)
                        {
                            Logger.LogInfo($"Gun Game: Respawning {localPlayer.HumanName}");
                            gunGameDeathTimes.Remove(netId);

                            // ResetCharacter re-initializes the player (health, visuals, position)
                            localPlayer.ResetCharacter();
                            // ReplicateReset syncs the respawn to other machines
                            localPlayer.ReplicateReset();
                        }
                    }
                    else if (localPlayer.Health > 0)
                    {
                        // Player is alive, clear death tracking
                        if (gunGameDeathTimes.ContainsKey(netId))
                        {
                            gunGameDeathTimes.Remove(netId);
                        }
                    }
                }

                // Manage spawn position tracking
                RoundsHardlineGameManager gm = FindObjectOfType<RoundsHardlineGameManager>();
                if (gm != null && gm.RoundEndFlag && !isResettingSpawns)
                {
                    isResettingSpawns = true;
                    activeSpawnPositions.Clear();
                }
                else if (gm != null && !gm.RoundEndFlag && isResettingSpawns)
                {
                    isResettingSpawns = false;
                }

                return;
            }

            // Check if we need to reset spawn tracking (new round starting)
            RoundsHardlineGameManager gameManager = FindObjectOfType<RoundsHardlineGameManager>();
            if (gameManager != null)
            {
                // Make sure the native FFA flag (if present) stays set on ALL clients
                if (!GetNativeFFAFlag())
                {
                    SetNativeFFAFlag(true);
                    Logger.LogInfo("FFA: Re-enabled native isFreeForAllMode flag");
                }

                // CLIENT-SIDE: Detect when we need to respawn by watching timer transitions
                if (!gameManager.isServer)
                {
                    Player localPlayer = null;

                    // Find local player
                    foreach (Player p in FindObjectsOfType<Player>())
                    {
                        if (p.hasAuthority)
                        {
                            localPlayer = p;
                            break;
                        }
                    }

                    if (localPlayer != null)
                    {
                        float currentTimer = gameManager.NetworkgameStartTimer;

                        // Detect timer transition from <=0 to >0 (round restart countdown starting)
                        if (lastTimerValue <= 0 && currentTimer > 0 && !hasRespawnedThisRound)
                        {
                            Logger.LogInfo($"FFA: Client detected round restart (Health={localPlayer.Health}, Timer={lastTimerValue}->{currentTimer}), calling RestartGameCharacter");
                            gameManager.RestartGameCharacter();
                            hasRespawnedThisRound = true;
                        }
                        // Reset flag when round is playing
                        else if (currentTimer <= 0)
                        {
                            hasRespawnedThisRound = false;
                        }

                        lastTimerValue = currentTimer;
                    }
                }

                // Reset spawn tracking when round restarts (server-side)
                if (gameManager.RoundEndFlag && !isResettingSpawns)
                {
                    isResettingSpawns = true;
                    usedSpawnPositions.Clear();
                    activeSpawnPositions.Clear();
                    Logger.LogInfo("FFA: Round ended, reset spawn tracking and active spawn positions");
                }
                else if (!gameManager.RoundEndFlag && isResettingSpawns)
                {
                    isResettingSpawns = false;
                    Logger.LogInfo("FFA: New round started");
                }
            }
        }


        // Generate custom spawn points for specific maps
        private static void GenerateCustomSpawns()
        {
            if (customSpawnsGenerated)
            {
                return;
            }

            StaticLogger.LogInfo($"Checking for custom spawns for map: {currentMapName}");

            if (customMapSpawns.ContainsKey(currentMapName))
            {
                List<Vector3> positions = customMapSpawns[currentMapName];
                StaticLogger.LogInfo($"Found {positions.Count} custom spawn positions for {currentMapName}");

                for (int i = 0; i < positions.Count; i++)
                {
                    GameObject customSpawn = new GameObject($"CustomFFASpawn_{currentMapName}_{i}");
                    customSpawn.transform.position = positions[i];
                    customSpawn.transform.rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0);
                    DontDestroyOnLoad(customSpawn);
                    customSpawnPoints.Add(customSpawn.transform);
                    StaticLogger.LogInfo($"Created custom spawn {i} at {positions[i]}");
                }
            }
            else
            {
                StaticLogger.LogInfo($"No custom spawns defined for map: {currentMapName}");
            }

            customSpawnsGenerated = true;
        }

        // Public static method that can be called to get a unique spawn position
        public static Transform GetUniqueFFASpawnPosition(RoundsHardlineGameManager gameManager)
        {
            if (gameManager == null)
            {
                StaticLogger.LogWarning("GetUniqueFFASpawnPosition: gameManager is null!");
                return null;
            }

            // Collect all available spawn points
            List<Transform> allSpawns = new List<Transform>();

            // Add team 1 spawns
            if (gameManager.Team1Spawns != null)
            {
                foreach (GameObject spawn in gameManager.Team1Spawns)
                {
                    if (spawn != null)
                    {
                        allSpawns.Add(spawn.transform);
                        StaticLogger.LogInfo($"Added Team1 spawn at {spawn.transform.position}");
                    }
                }
            }

            // Add team 2 spawns
            if (gameManager.Team2Spawns != null)
            {
                foreach (GameObject spawn in gameManager.Team2Spawns)
                {
                    if (spawn != null)
                    {
                        allSpawns.Add(spawn.transform);
                        StaticLogger.LogInfo($"Added Team2 spawn at {spawn.transform.position}");
                    }
                }
            }

            // Add AI spawner positions for map coverage, but validate they're safe
            // Try both Spawner components and TeamSpawn components
            Spawner[] spawners = Object.FindObjectsOfType<Spawner>();
            TeamSpawn[] teamSpawns = Object.FindObjectsOfType<TeamSpawn>();

            StaticLogger.LogInfo($"Found {spawners.Length} Spawner objects and {teamSpawns.Length} TeamSpawn objects");

            // Add Spawner objects
            foreach (Spawner spawner in spawners)
            {
                if (spawner != null && IsSpawnPointSafe(spawner.transform))
                {
                    allSpawns.Add(spawner.transform);
                    StaticLogger.LogInfo($"Added safe Spawner at {spawner.transform.position}");
                }
                else if (spawner != null)
                {
                    StaticLogger.LogWarning($"Rejected unsafe Spawner at {spawner.transform.position}");
                }
            }

            // Add TeamSpawn objects (but skip ones already in team1Spawns/team2Spawns to avoid duplicates)
            foreach (TeamSpawn teamSpawn in teamSpawns)
            {
                if (teamSpawn != null)
                {
                    // Check if this TeamSpawn is already in our team lists
                    bool alreadyAdded = false;
                    if (gameManager.Team1Spawns != null)
                    {
                        foreach (GameObject spawn in gameManager.Team1Spawns)
                        {
                            if (spawn != null && spawn.transform == teamSpawn.transform)
                            {
                                alreadyAdded = true;
                                break;
                            }
                        }
                    }
                    if (!alreadyAdded && gameManager.Team2Spawns != null)
                    {
                        foreach (GameObject spawn in gameManager.Team2Spawns)
                        {
                            if (spawn != null && spawn.transform == teamSpawn.transform)
                            {
                                alreadyAdded = true;
                                break;
                            }
                        }
                    }

                    if (!alreadyAdded && IsSpawnPointSafe(teamSpawn.transform))
                    {
                        allSpawns.Add(teamSpawn.transform);
                        StaticLogger.LogInfo($"Added safe TeamSpawn at {teamSpawn.transform.position}");
                    }
                    else if (!alreadyAdded)
                    {
                        StaticLogger.LogWarning($"Rejected unsafe TeamSpawn at {teamSpawn.transform.position}");
                    }
                }
            }

            // Generate and add custom spawn points for this map
            GenerateCustomSpawns();
            foreach (Transform customSpawn in customSpawnPoints)
            {
                if (customSpawn != null)
                {
                    allSpawns.Add(customSpawn);
                }
            }
            StaticLogger.LogInfo($"Added {customSpawnPoints.Count} custom spawn points for {currentMapName}");

            StaticLogger.LogInfo($"Total spawn points collected: {allSpawns.Count}");
            StaticLogger.LogInfo($"Already used spawns: {usedSpawnPositions.Count}");

            if (allSpawns.Count == 0)
            {
                StaticLogger.LogError("No spawn points found!");
                return null;
            }

            // Use lock to ensure thread-safe spawn selection
            lock (spawnLock)
            {
                // Filter out spawns that are currently occupied by another player
                List<Transform> availableSpawns = new List<Transform>();
                foreach (Transform spawn in allSpawns)
                {
                    if (!IsSpawnPositionOccupied(spawn.position))
                    {
                        availableSpawns.Add(spawn);
                    }
                    else
                    {
                        StaticLogger.LogInfo($"Spawn at {spawn.position} is currently occupied, skipping");
                    }
                }

                StaticLogger.LogInfo($"Available unoccupied spawns: {availableSpawns.Count}");

                // If all spawns are occupied, allow reusing them (emergency fallback)
                if (availableSpawns.Count == 0)
                {
                    StaticLogger.LogWarning("All spawn positions are occupied! Using fallback spawn selection...");
                    availableSpawns = allSpawns;
                }

                // Shuffle available spawns for better distribution
                // This helps prevent the same spawn being picked first by multiple clients
                for (int i = availableSpawns.Count - 1; i > 0; i--)
                {
                    int j = spawnRandom.Next(i + 1);
                    Transform temp = availableSpawns[i];
                    availableSpawns[i] = availableSpawns[j];
                    availableSpawns[j] = temp;
                }

                // Pick the first spawn after shuffle (more random than Range)
                Transform chosenSpawn = availableSpawns[0];

                // Mark this spawn position as occupied
                MarkSpawnPositionUsed(chosenSpawn.position);

                // Also add to old tracking system for compatibility
                usedSpawnPositions.Add(chosenSpawn);

                StaticLogger.LogInfo($"Selected spawn at {chosenSpawn.position}. Active spawn positions: {activeSpawnPositions.Count}");

                return chosenSpawn;
            }
        }

        // Validate if a spawn point is safe for player spawning
        private static bool IsSpawnPointSafe(Transform spawnPoint)
        {
            Vector3 pos = spawnPoint.position;

            // Check if spawn is too high (likely ceiling/sky spawns)
            // Most maps have playable areas below Y=25
            if (pos.y > 25f)
            {
                StaticLogger.LogWarning($"Spawn rejected: too high (y={pos.y})");
                return false;
            }

            // Use Physics.Raycast to check if there's ground below the spawn point
            // This catches spawns floating in the air or outside the map
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out hit, 10f))
            {
                // Found ground within 10 units below - this is good
                StaticLogger.LogInfo($"Spawn validated: ground found {hit.distance}m below at {pos}");
                return true;
            }
            else
            {
                // No ground found - likely outside map or too high up
                StaticLogger.LogWarning($"Spawn rejected: no ground found below {pos}");
                return false;
            }
        }

        // Check if a spawn position is currently occupied by another player
        private static bool IsSpawnPositionOccupied(Vector3 position)
        {
            float currentTime = Time.time;
            float positionTolerance = 0.1f; // Positions within 0.1 units are considered the same

            // Clean up expired spawn positions
            List<Vector3> expiredPositions = new List<Vector3>();
            foreach (var kvp in activeSpawnPositions)
            {
                if (currentTime - kvp.Value > spawnPositionTimeout)
                {
                    expiredPositions.Add(kvp.Key);
                }
            }
            foreach (var pos in expiredPositions)
            {
                activeSpawnPositions.Remove(pos);
            }

            // Check if any active spawn position is too close to this one
            foreach (var kvp in activeSpawnPositions)
            {
                if (Vector3.Distance(kvp.Key, position) < positionTolerance)
                {
                    return true; // Position is occupied
                }
            }

            return false; // Position is available
        }

        // Mark a spawn position as occupied
        private static void MarkSpawnPositionUsed(Vector3 position)
        {
            activeSpawnPositions[position] = Time.time;
        }

        // Public static method to check if FFA mode is active (includes Gun Game)
        public static bool IsFFAModeActive()
        {
            return localFFAMode || gunGameMode;
        }

        // Public static method to check if Gun Game mode is active
        public static bool IsGunGameModeActive()
        {
            return gunGameMode;
        }

        // Get player's current Gun Game level
        public static int GetGunGameLevel(uint netId)
        {
            if (gunGamePlayerLevel.ContainsKey(netId))
            {
                return gunGamePlayerLevel[netId];
            }
            return 0; // Start at level 0
        }

        // Set player's Gun Game level
        public static void SetGunGameLevel(uint netId, int level)
        {
            gunGamePlayerLevel[netId] = level;
        }

        // Increment player's Gun Game level (on kill)
        public static bool IncrementGunGameLevel(uint netId)
        {
            int currentLevel = GetGunGameLevel(netId);
            int newLevel = currentLevel + 1;

            if (newLevel >= gunGameWeapons.Count)
            {
                // Player has won! They got a knife kill
                return true; // Return true to indicate win
            }

            gunGamePlayerLevel[netId] = newLevel;
            StaticLogger.LogInfo($"Gun Game: Player {netId} advanced to level {newLevel} ({gunGameWeapons[newLevel]})");
            return false; // Not a win yet
        }

        // Get the weapon name for a Gun Game level
        public static string GetGunGameWeapon(int level)
        {
            if (level >= 0 && level < gunGameWeapons.Count)
            {
                return gunGameWeapons[level];
            }
            return gunGameWeapons[0]; // Default to pistol
        }

        // Reset all Gun Game levels
        public static void ResetGunGameLevels()
        {
            gunGamePlayerLevel.Clear();
            StaticLogger.LogInfo("Gun Game: All player levels reset");
        }

        // Get max Gun Game level (knife level)
        public static int GetGunGameMaxLevel()
        {
            return gunGameWeapons.Count - 1;
        }

        // Gun Game winner tracking - used to allow round win only on knife kill
        private static Player gunGameWinner = null;

        public static void SetGunGameWinner(Player winner)
        {
            gunGameWinner = winner;
        }

        public static Player GetGunGameWinner()
        {
            return gunGameWinner;
        }

        public static void ClearGunGameWinner()
        {
            gunGameWinner = null;
        }

        // SERVER-SIDE Gun Game tracking methods
        // These allow the server to detect wins even when a client gets the winning kill
        public static void ServerTrackKill(uint netId)
        {
            if (!serverGunGameKills.ContainsKey(netId))
                serverGunGameKills[netId] = 0;
            serverGunGameKills[netId]++;
        }

        public static void ServerTrackDemotion(uint netId)
        {
            if (!serverGunGameDemotions.ContainsKey(netId))
                serverGunGameDemotions[netId] = 0;
            serverGunGameDemotions[netId]++;
        }

        public static int GetServerGunGameLevel(uint netId)
        {
            int kills = serverGunGameKills.ContainsKey(netId) ? serverGunGameKills[netId] : 0;
            int demotions = serverGunGameDemotions.ContainsKey(netId) ? serverGunGameDemotions[netId] : 0;
            int level = kills - demotions;
            return System.Math.Max(0, System.Math.Min(level, GetGunGameMaxLevel()));
        }

        public static void ResetServerGunGameTracking()
        {
            serverGunGameKills.Clear();
            serverGunGameDemotions.Clear();
            // Note: Don't reset gunGameEnded here - it's managed separately by SetGunGameEnded
            StaticLogger.LogInfo("Gun Game: Reset server tracking");
        }

        public static bool IsGunGameEnded() { return gunGameEnded; }
        public static void SetGunGameEnded(bool ended) { gunGameEnded = ended; }

        // Fix inventory after Gun Game weapon change:
        // - Current weapon goes in primary slot (slot 1)
        // - Knife always in melee slot (slot 5)
        // - Other slots cleared to Unarmed
        // This prevents old weapons from lingering and ensures knife is always available
        public static void FixGunGameInventory(Player player)
        {
            if (player == null || player.HumanInventory == null) return;

            try
            {
                var inv = player.HumanInventory;

                // Put current weapon in primary slot
                inv.GiveItemAsPrimary(player.Item, true);
                inv.CurrentlySelected = 1;

                // Clear secondary and equipment slots
                var unarmed = (Resources.Load("Weapon_Unarmed") as GameObject).GetComponent<PlayerItem>();
                if (unarmed != null)
                {
                    inv.GiveItemAsSecondary(unarmed, false);
                    inv.GiveItemAsEquipment1(unarmed, false);
                    inv.GiveItemAsEquipment2(unarmed, false);
                }

                // Ensure knife is always in melee slot (slot 5)
                var knife = (Resources.Load("Weapon_Knife") as GameObject).GetComponent<PlayerItem>();
                if (knife != null)
                {
                    inv.GiveItemAsMeleeWeapon(knife);
                }

                StaticLogger.LogInfo($"Gun Game: Fixed inventory for {player.HumanName} - primary={player.Item?.name}, melee=Knife");
            }
            catch (System.Exception ex)
            {
                StaticLogger.LogError($"Gun Game: FixGunGameInventory failed - {ex.Message}");
            }
        }

        // Get or create unique team for player (doesn't set it, just returns it)
        // Uses hash of player name to deterministically assign team numbers
        public static int GetOrCreateFFATeam(Player player)
        {
            string playerName = player.HumanName;

            if (!ffaPlayerTeams.ContainsKey(playerName))
            {
                // Use hash of player name to get consistent team number across all clients
                int hash = playerName.GetHashCode();
                // Map to team number 10-1000 range to avoid conflicts with regular teams
                int teamNumber = 10 + (System.Math.Abs(hash) % 990);

                ffaPlayerTeams[playerName] = teamNumber;
                StaticLogger.LogInfo($"FFA: Created new team {teamNumber} for {playerName} (hash-based)");
            }
            return ffaPlayerTeams[playerName];
        }

        // Assign unique team to player in FFA mode
        public static int AssignFFATeam(Player player)
        {
            int teamNumber = GetOrCreateFFATeam(player);
            player.Team = teamNumber;
            return player.Team;
        }

        // FFA weapon tier progression methods
        public static void IncrementFFACompletedRounds()
        {
            ffaCompletedRounds++;
        }

        public static int GetFFACompletedRounds()
        {
            return ffaCompletedRounds;
        }

        public static int GetFFAWeaponTier()
        {
            // Tier increases every 2 completed rounds, capped at tier 4 (max tier in game)
            int tier = ffaCompletedRounds / 2;
            return System.Math.Min(tier, 4);
        }

        public static void ResetFFACompletedRounds()
        {
            ffaCompletedRounds = 0;
            ffaRoundWinScores.Clear();
            ffaRoundWinNames.Clear();
            ffaRoundWinEndFlag = false;
            StaticLogger.LogInfo("FFA: Reset completed rounds counter and scores");
        }

        public static string GetCurrentMapName()
        {
            return currentMapName;
        }

        // Keybind configuration methods
        private void AssignKeybind(string bindName, KeyCode key)
        {
            switch (bindName)
            {
                case "ffaMenu":
                    ffaMenuKey = key;
                    Logger.LogInfo($"FFA Menu key set to: {key}");
                    break;
                case "logSpawn":
                    logSpawnKey = key;
                    Logger.LogInfo($"Log Spawn key set to: {key}");
                    break;
                case "keybindMenu":
                    keybindMenuKey = key;
                    Logger.LogInfo($"Keybind Menu key set to: {key}");
                    break;
                case "kickMenu":
                    HostKickMod.HostKickMod.kickMenuKey = key;
                    Logger.LogInfo($"Host Kick Menu key set to: {key}");
                    break;
                case "crosshairMenu":
                    HostKickMod.HostKickMod.crosshairMenuKey = key;
                    Logger.LogInfo($"Crosshair Menu key set to: {key}");
                    break;
            }
        }

        private void StartRebind(string bindName)
        {
            isWaitingForKey = true;
            keyToRebind = bindName;
            Logger.LogInfo($"Waiting for new key for: {bindName}");
        }

        // ==================== RAINBOW NAME SYSTEM FOR PeakZelo ====================
        private static readonly string RAINBOW_USERNAME = "PeakZelo";
        private static float rainbowHue = 0f;
        private static float rainbowSpeed = 0.5f; // Full color cycle every 2 seconds

        // Store Text component references and their original text for fast per-frame updates
        private static Dictionary<int, Text> rainbowTrackedById = new Dictionary<int, Text>();
        private static Dictionary<int, string> rainbowOriginalById = new Dictionary<int, string>();

        public static string MakeRainbowText(string text, float hueOffset = 0f)
        {
            string result = "";
            for (int i = 0; i < text.Length; i++)
            {
                float hue = (rainbowHue + hueOffset + (float)i / text.Length) % 1f;
                Color color = Color.HSVToRGB(hue, 1f, 1f);
                string hex = ColorUtility.ToHtmlStringRGB(color);
                result += $"<color=#{hex}>{text[i]}</color>";
            }
            return result;
        }

        // Coroutine scans for NEW texts to track (runs less frequently to save CPU)
        private IEnumerator RainbowNameScanCoroutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.5f); // Scan every 0.5 seconds

                try
                {
                    Text[] allTexts = FindObjectsOfType<Text>();

                    foreach (var textComp in allTexts)
                    {
                        if (textComp == null) continue;
                        int id = textComp.GetInstanceID();

                        // Skip if already tracked
                        if (rainbowTrackedById.ContainsKey(id)) continue;

                        // Check if this text contains PeakZelo (with or without rainbow formatting)
                        string plainText = textComp.text.Contains("<color=") ? StripColorTags(textComp.text) : textComp.text;
                        if (!string.IsNullOrEmpty(plainText) && plainText.Contains(RAINBOW_USERNAME))
                        {
                            // Start tracking this text (no need to store original - we work on current text)
                            rainbowTrackedById[id] = textComp;
                            textComp.supportRichText = true;
                            StaticLogger.LogInfo($"Rainbow: Now tracking text '{plainText}'");
                        }
                    }
                }
                catch (System.Exception) { }
            }
        }
    }

    // Overrides spawn position selection in FFA mode on RoundsHardlineGameManager's own
    // "new"-hidden GetSpawnPositionForTeam override. Only present (and only patched, see
    // FFAModToggle.Awake) when running against an assembly with native FFA support baked in;
    // FFASpawnPatchBase below covers a stock game install on its own.
    public class FFASpawnPatch
    {
        public static bool Prefix(RoundsHardlineGameManager __instance, ref Transform __result)
        {
            // Only intercept if local FFA mode is active
            if (!FFAModToggle.IsFFAModeActive())
            {
                return true; // Let original method run
            }

            // Get unique spawn position
            __result = FFAModToggle.GetUniqueFFASpawnPosition(__instance);

            if (__result != null)
            {
                FFAModToggle.StaticLogger.LogInfo($"FFA Spawn: Assigned spawn position at {__result.position}");
            }

            // Return false to skip original method
            return false;
        }
    }

    // Also patch base class to catch all cases
    [HarmonyPatch(typeof(HardlineGameManager), "GetSpawnPositionForTeam")]
    public class FFASpawnPatchBase
    {
        static bool Prefix(HardlineGameManager __instance, ref Transform __result)
        {
            // Only intercept if local FFA mode is active and this is a RoundsHardlineGameManager
            if (!FFAModToggle.IsFFAModeActive() || !(__instance is RoundsHardlineGameManager))
            {
                return true; // Let original method run
            }

            // Get unique spawn position
            __result = FFAModToggle.GetUniqueFFASpawnPosition(__instance as RoundsHardlineGameManager);

            return false;
        }
    }


    // Patch GoToSpawn to assign unique teams in FFA and Gun Game
    [HarmonyPatch(typeof(Player), "GoToSpawn")]
    public class FFAGoToSpawnPatch
    {
        static void Prefix(Player __instance)
        {
            // Assign unique teams for both FFA and Gun Game
            // This lets all players damage each other (everyone vs everyone)
            if (FFAModToggle.IsFFAModeActive())
            {
                int teamNumber = FFAModToggle.GetOrCreateFFATeam(__instance);
                __instance.Team = teamNumber;

                string mode = FFAModToggle.IsGunGameModeActive() ? "Gun Game" : "FFA";
                FFAModToggle.StaticLogger.LogInfo($"{mode}: Assigned {__instance.HumanName} to team {__instance.Team}");
            }
        }

        static void Postfix(Player __instance)
        {
            if (FFAModToggle.IsFFAModeActive())
            {
                string mode = FFAModToggle.IsGunGameModeActive() ? "Gun Game" : "FFA";
                FFAModToggle.StaticLogger.LogInfo($"{mode}: Player {__instance.HumanName} spawned at {__instance.transform.position} on team {__instance.Team}");
            }
        }
    }

    // Patch RestartGameCharacter to detect respawns
    [HarmonyPatch(typeof(HardlineGameManager), "RestartGameCharacter")]
    public class FFARestartCharacterPatch
    {
        static void Prefix(HardlineGameManager __instance)
        {
            if (FFAModToggle.IsFFAModeActive())
            {
                FFAModToggle.StaticLogger.LogInfo($"FFA: RestartGameCharacter called! isServer={__instance.isServer}");
            }
        }

        static void Postfix(HardlineGameManager __instance)
        {
            if (FFAModToggle.IsFFAModeActive())
            {
                FFAModToggle.StaticLogger.LogInfo($"FFA: RestartGameCharacter completed!");
            }
        }
    }

    // Patch GenerateRandomLoadout to use FFA weapon tier progression
    [HarmonyPatch(typeof(RoundsHardlineGameManager), "GenerateRandomLoadout")]
    public class FFAGenerateRandomLoadoutPatch
    {
        static void Prefix(ref int tier)
        {
            if (!FFAModToggle.IsFFAModeActive())
            {
                return; // Let original method use team-based tier
            }

            // In FFA mode, use tier based on completed rounds instead of opposing team wins
            int ffaTier = FFAModToggle.GetFFAWeaponTier();
            FFAModToggle.StaticLogger.LogInfo($"FFA: Generating loadout with tier {ffaTier} (rounds completed: {FFAModToggle.GetFFACompletedRounds()})");
            tier = ffaTier;
        }
    }

    // ==================== GUN GAME PATCHES ====================

    // Clear PlayersKilled BEFORE the Contains check in HitAnotherTarget
    // so every kill registers in Gun Game (normally the list prevents duplicate kills
    // on the same target, but Gun Game has no round resets to clear it)
    [HarmonyPatch(typeof(PlayerFirearm), "HitAnotherTarget")]
    public class GunGameFirearmHitPatch
    {
        static void Prefix(PlayerFirearm __instance, bool killShot, Human target)
        {
            if (FFAModToggle.IsGunGameModeActive() && __instance.User != null)
            {
                __instance.User.PlayersKilled.Clear();
                // Firearms are never knives - store target info for AddKill
                if (killShot && target != null)
                {
                    FFAModToggle.SetLastKillInfo(target, false);
                }
            }
        }
    }

    [HarmonyPatch(typeof(PlayerItem), "HitAnotherTarget")]
    public class GunGameItemHitPatch
    {
        static void Prefix(PlayerItem __instance, bool killShot, Human target)
        {
            if (FFAModToggle.IsGunGameModeActive() && __instance.User != null)
            {
                __instance.User.PlayersKilled.Clear();
                // This patch only fires for non-PlayerFirearm items (MeleeWeapon/knife)
                // because PlayerFirearm overrides HitAnotherTarget and has its own patch.
                // In Gun Game, the only non-firearm weapon that can kill is the knife.
                if (killShot && target != null)
                {
                    bool isKnife = true;
                    FFAModToggle.StaticLogger.LogInfo($"Gun Game: Melee/knife kill detected! Killer={__instance.User?.HumanName}, Target={target?.HumanName}");
                    FFAModToggle.SetLastKillInfo(target, isKnife);
                }
            }
        }
    }

    // Patch Human.AddKill to upgrade weapon in Gun Game mode
    [HarmonyPatch(typeof(Human), "AddKill")]
    public class GunGameAddKillPatch
    {
        static void Postfix(Human __instance)
        {
            if (!FFAModToggle.IsGunGameModeActive())
            {
                return;
            }

            // Only process for players (not AI)
            if (!(__instance is Player))
            {
                return;
            }

            Player player = __instance as Player;
            uint netId = player.netId;

            FFAModToggle.StaticLogger.LogInfo($"Gun Game: {player.HumanName} got a kill!");

            // Check if this was a knife kill at max level
            int currentLevel = FFAModToggle.GetGunGameLevel(netId);
            int maxLevel = FFAModToggle.GetGunGameMaxLevel();

            if (currentLevel >= maxLevel)
            {
                // Check if game already ended (server-side detection might have already handled this)
                if (FFAModToggle.IsGunGameEnded())
                {
                    FFAModToggle.StaticLogger.LogInfo($"Gun Game: Win already processed by server, skipping AddKill win");
                    return;
                }

                // Player is on the final weapon and got a kill - they win!
                FFAModToggle.StaticLogger.LogInfo($"======================================");
                FFAModToggle.StaticLogger.LogInfo($"GUN GAME WINNER: {player.HumanName}!");
                FFAModToggle.StaticLogger.LogInfo($"======================================");

                // Mark that we have a Gun Game winner - this allows FFARoundWin to proceed
                FFAModToggle.SetGunGameWinner(player);
                FFAModToggle.SetGunGameEnded(true);

                // Reset all levels for new game
                FFAModToggle.ResetGunGameLevels();

                // Trigger FULL GAME END (not just a round) via FFAEndGame
                // This shows "wins the game!" notification and shuts down the lobby
                RoundsHardlineGameManager gm = UnityEngine.Object.FindObjectOfType<RoundsHardlineGameManager>();
                if (gm != null && gm.isServer)
                {
                    // Set round-end flags to prevent further game logic
                    var ffaRoundEndFlagField = typeof(RoundsHardlineGameManager).GetField("ffaRoundEndFlag",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var roundEndFlagField = typeof(RoundsHardlineGameManager).GetField("roundEndFlag",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (ffaRoundEndFlagField != null) ffaRoundEndFlagField.SetValue(gm, true);
                    if (roundEndFlagField != null) roundEndFlagField.SetValue(gm, true);

                    // End the game (mod-side implementation - see RunFFAEndGame)
                    FFAModToggle.StaticLogger.LogInfo($"Gun Game: Triggering game end for {player.HumanName} - GAME OVER!");
                    FFAModToggle.RunFFAEndGame(gm, player);
                }

                return;
            }

            // Increment level and give new weapon
            bool hasWon = FFAModToggle.IncrementGunGameLevel(netId);

            if (!hasWon)
            {
                // Give the player their new weapon
                int newLevel = FFAModToggle.GetGunGameLevel(netId);
                string newWeapon = FFAModToggle.GetGunGameWeapon(newLevel);

                FFAModToggle.StaticLogger.LogInfo($"Gun Game: {player.HumanName} upgraded to level {newLevel}: {newWeapon}");

                // Load the new weapon for the player (with network sync and ammo)
                if (player.hasAuthority)
                {
                    player.ForceSetItemString(newWeapon);

                    // Set ammo - prefab defaults have 0 ammo, need to fill it
                    if (player.Item is PlayerFirearm)
                    {
                        var firearm = player.Item as PlayerFirearm;
                        firearm.Ammo = firearm.MaxAmmo;
                        firearm.ReserveAmmo = firearm.StartingReserveAmmo;
                        firearm.Chambered = true;
                        FFAModToggle.StaticLogger.LogInfo($"Gun Game: Set ammo {firearm.Ammo}/{firearm.ReserveAmmo} for {newWeapon}");
                    }

                    // Fix inventory: new weapon in primary slot, knife always in melee slot,
                    // old weapon removed from inventory
                    FFAModToggle.FixGunGameInventory(player);

                    FFAModToggle.StaticLogger.LogInfo($"Gun Game: Loaded {newWeapon} for {player.HumanName}");
                }
            }

            // Knife demotion: if the kill was with a knife, demote the victim one level
            bool wasKnife = FFAModToggle.WasLastKillKnife();
            Human lastTarget = FFAModToggle.GetLastKillTarget();
            FFAModToggle.StaticLogger.LogInfo($"Gun Game: Kill info - wasKnife={wasKnife}, target={lastTarget?.HumanName ?? "null"}");

            if (wasKnife)
            {
                if (lastTarget != null && lastTarget is Player)
                {
                    Player victimPlayer = lastTarget as Player;
                    uint victimNetId = victimPlayer.netId;
                    int victimLevel = FFAModToggle.GetGunGameLevel(victimNetId);

                    if (victimLevel > 0)
                    {
                        int newVictimLevel = victimLevel - 1;
                        FFAModToggle.SetGunGameLevel(victimNetId, newVictimLevel);
                        string demotedWeapon = FFAModToggle.GetGunGameWeapon(newVictimLevel);
                        FFAModToggle.StaticLogger.LogInfo($"Gun Game: {victimPlayer.HumanName} DEMOTED to level {newVictimLevel} ({demotedWeapon}) by knife kill from {player.HumanName}!");
                    }
                    else
                    {
                        FFAModToggle.StaticLogger.LogInfo($"Gun Game: {victimPlayer.HumanName} already at level 0, no demotion");
                    }
                }
                else
                {
                    FFAModToggle.StaticLogger.LogInfo($"Gun Game: Knife kill but victim is null or not a Player");
                }
            }

            FFAModToggle.ClearLastKillInfo();
        }
    }

    // Patch GoToSpawn to give correct Gun Game weapon on spawn
    [HarmonyPatch(typeof(Player), "GoToSpawn")]
    public class GunGameGoToSpawnPatch
    {
        static void Postfix(Player __instance)
        {
            if (!FFAModToggle.IsGunGameModeActive())
            {
                return;
            }

            FFAModToggle.StaticLogger.LogInfo($"Gun Game GoToSpawn: {__instance.HumanName} - isLocalPlayer={__instance.isLocalPlayer}, hasAuthority={__instance.hasAuthority}, isServer={__instance.isServer}");

            uint netId = __instance.netId;
            int level = FFAModToggle.GetGunGameLevel(netId);
            string weapon = FFAModToggle.GetGunGameWeapon(level);

            FFAModToggle.StaticLogger.LogInfo($"Gun Game: {__instance.HumanName} spawning with level {level} weapon: {weapon}");

            // Try starting coroutine for all players, let the coroutine decide
            FFAModToggle.StaticLogger.LogInfo($"Gun Game: Starting weapon coroutine for {__instance.HumanName}");
            __instance.StartCoroutine(GiveGunGameWeaponDelayed(__instance, weapon));
        }

        private static System.Collections.IEnumerator GiveGunGameWeaponDelayed(Player player, string weapon)
        {
            FFAModToggle.StaticLogger.LogInfo($"Gun Game: Coroutine started for {player?.HumanName}, waiting 0.5s...");
            yield return new WaitForSeconds(0.5f);

            FFAModToggle.StaticLogger.LogInfo($"Gun Game: Coroutine resumed. Player null? {player == null}");

            if (player != null && player.Health > 0)
            {
                FFAModToggle.StaticLogger.LogInfo($"Gun Game: Player {player.HumanName} - hasAuthority={player.hasAuthority}");

                try
                {
                    // Use ForceSetItemString for network sync (sets NetworkitemString SyncVar)
                    player.ForceSetItemString(weapon);

                    // Set ammo - weapon prefabs default to 0 ammo, need to fill
                    if (player.Item is PlayerFirearm)
                    {
                        var firearm = player.Item as PlayerFirearm;
                        firearm.Ammo = firearm.MaxAmmo;
                        firearm.ReserveAmmo = firearm.StartingReserveAmmo;
                        firearm.Chambered = true;
                        FFAModToggle.StaticLogger.LogInfo($"Gun Game: Set ammo {firearm.Ammo}/{firearm.ReserveAmmo} for {weapon}");
                    }

                    // Fix inventory: weapon in primary slot, knife in melee slot, clear others
                    FFAModToggle.FixGunGameInventory(player);

                    FFAModToggle.StaticLogger.LogInfo($"Gun Game: Loaded {weapon} for {player.HumanName}");
                }
                catch (System.Exception ex)
                {
                    FFAModToggle.StaticLogger.LogError($"Gun Game: ForceSetItemString failed - {ex.Message}");
                }
            }
            else
            {
                FFAModToggle.StaticLogger.LogInfo($"Gun Game: Skipped weapon load - player null or dead");
            }
        }
    }

    // Patch RoundMode to skip ALL round-end logic for Gun Game
    // This prevents both team wins and FFA wins from triggering
    // Respawning is handled separately in the mod's LateUpdate
    [HarmonyPatch(typeof(RoundsHardlineGameManager), "RoundMode")]
    [HarmonyPriority(Priority.High)]
    public class GunGameRoundModePatch
    {
        static bool Prefix()
        {
            if (!FFAModToggle.IsGunGameModeActive())
            {
                return true; // Let normal round logic run for non-Gun-Game modes
            }

            // Skip ALL round-end detection for Gun Game
            // No team wins, no FFA last-player-standing wins
            // Dead players are respawned individually via LateUpdate
            return false;
        }
    }

    // Plain "Free For All" round-win detection (last player standing, first-to-5), reimplemented
    // entirely mod-side - see FFAModToggle's "NATIVE FFA ROUND-FLOW REPLICATION" region.
    [HarmonyPatch(typeof(RoundsHardlineGameManager), "RoundMode")]
    [HarmonyPriority(Priority.High)]
    public class FFANativeRoundModePatch
    {
        static bool Prefix(RoundsHardlineGameManager __instance)
        {
            if (!FFAModToggle.IsFFAModeActive() || FFAModToggle.IsGunGameModeActive())
            {
                return true; // Not plain FFA mode - let normal/Gun Game round logic run
            }

            if (!__instance.isServer)
            {
                return false; // Round-win detection is server-authoritative; clients wait for the message
            }

            FFAModToggle.RunFFARoundModeCheck(__instance);
            return false; // Skip the original team-based RoundMode entirely
        }
    }

    // Block loadout selection in Gun Game - players get weapons automatically
    [HarmonyPatch(typeof(RoundsHardlineGameManager), "OpenLoadoutSelect")]
    public class GunGameBlockLoadoutSelectPatch
    {
        static bool Prefix()
        {
            if (!FFAModToggle.IsGunGameModeActive())
            {
                return true; // Let normal loadout selection happen
            }

            FFAModToggle.StaticLogger.LogInfo("Gun Game: Blocking loadout selection (weapons assigned automatically)");
            return false; // Skip loadout selection entirely
        }
    }

    // ==================== VICTIM-SIDE DEMOTION + SERVER-SIDE WIN DETECTION ====================
    // This patch runs on ALL machines (including victim's machine AND server).
    // It handles:
    // 1. VICTIM-SIDE: Updates the victim's local level when knifed (so they respawn with correct weapon)
    // 2. SERVER-SIDE: Tracks all kills/demotions and detects when ANY player wins (host OR client)
    [HarmonyPatch(typeof(HardlineGameManager), "HitAnotherPlayer")]
    public class GunGameVictimDemotionPatch
    {
        static void Postfix(Human causer, Human target, Vector3 hitPos, Vector3 hitRot, bool killShot, HardlineGameManager __instance)
        {
            if (!FFAModToggle.IsGunGameModeActive()) return;
            if (!killShot || target == null || causer == null) return;

            // Detect if the kill was with a knife (non-firearm weapon)
            bool isKnifeKill = (causer.Item != null && !(causer.Item is PlayerFirearm)) || causer.NetworkitemString == "Knife";

            // ===== VICTIM-SIDE DEMOTION (runs on victim's authority machine) =====
            if (target.hasAuthority && target is Player && isKnifeKill)
            {
                Player victimPlayer = target as Player;
                uint victimNetId = victimPlayer.netId;
                int currentLevel = FFAModToggle.GetGunGameLevel(victimNetId);

                FFAModToggle.StaticLogger.LogInfo($"Gun Game VICTIM-SIDE: {victimPlayer.HumanName} killed by knife from {causer.HumanName}. Current level: {currentLevel}");

                if (currentLevel > 0)
                {
                    FFAModToggle.SetGunGameLevel(victimNetId, currentLevel - 1);
                    string demotedWeapon = FFAModToggle.GetGunGameWeapon(currentLevel - 1);
                    FFAModToggle.StaticLogger.LogInfo($"Gun Game VICTIM-SIDE: {victimPlayer.HumanName} DEMOTED to level {currentLevel - 1} ({demotedWeapon})");
                }
            }

            // ===== SERVER-SIDE KILL TRACKING AND WIN DETECTION =====
            // This allows clients to win - the server tracks all kills independently
            if (__instance.isServer && causer is Player)
            {
                // Check if game already ended (prevent double-win)
                if (FFAModToggle.IsGunGameEnded()) return;

                Player killerPlayer = causer as Player;
                uint killerNetId = killerPlayer.netId;

                // Check level BEFORE tracking this kill (we need the level at time of kill, not after)
                int serverLevel = FFAModToggle.GetServerGunGameLevel(killerNetId);
                int maxLevel = FFAModToggle.GetGunGameMaxLevel();

                // Track this kill on the server (after checking level)
                FFAModToggle.ServerTrackKill(killerNetId);

                // Track demotion if knife kill on a player
                if (isKnifeKill && target is Player)
                {
                    Player victimPlayer = target as Player;
                    FFAModToggle.ServerTrackDemotion(victimPlayer.netId);
                }

                FFAModToggle.StaticLogger.LogInfo($"Gun Game SERVER: {killerPlayer.HumanName} kill tracked. Server level: {serverLevel}/{maxLevel}, IsKnife: {isKnifeKill}");

                if (serverLevel >= maxLevel && isKnifeKill)
                {
                    // WINNER! Server detected the win
                    FFAModToggle.StaticLogger.LogInfo($"======================================");
                    FFAModToggle.StaticLogger.LogInfo($"GUN GAME SERVER WINNER: {killerPlayer.HumanName}!");
                    FFAModToggle.StaticLogger.LogInfo($"======================================");

                    // Prevent double-win
                    FFAModToggle.SetGunGameEnded(true);

                    // Reset tracking
                    FFAModToggle.ResetServerGunGameTracking();
                    FFAModToggle.ResetGunGameLevels();

                    // Call FFAEndGame to end the entire game
                    RoundsHardlineGameManager gm = __instance as RoundsHardlineGameManager;
                    if (gm != null)
                    {
                        // Set round-end flags
                        var ffaRoundEndFlagField = typeof(RoundsHardlineGameManager).GetField("ffaRoundEndFlag",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var roundEndFlagField = typeof(RoundsHardlineGameManager).GetField("roundEndFlag",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (ffaRoundEndFlagField != null) ffaRoundEndFlagField.SetValue(gm, true);
                        if (roundEndFlagField != null) roundEndFlagField.SetValue(gm, true);

                        // End the game (mod-side implementation - see RunFFAEndGame)
                        FFAModToggle.StaticLogger.LogInfo($"Gun Game SERVER: Triggering game end for {killerPlayer.HumanName} - GAME OVER!");
                        FFAModToggle.RunFFAEndGame(gm, killerPlayer);
                    }
                }
            }
        }
    }

    // ==================== RAINBOW KILL TEXT PATCH ====================
    // When PeakZelo appears in kill feed text, apply rainbow colors + (dev) tag
    [HarmonyPatch(typeof(KillText), "CreateNewKillText")]
    public class PeakZeloKillTextPatch
    {
        static void Postfix(KillText __instance, string playerName)
        {
            if (playerName == null || !playerName.Contains("PeakZelo")) return;

            try
            {
                // KillText creates child objects with Text components
                var texts = __instance.GetComponentsInChildren<Text>(true);
                foreach (var t in texts)
                {
                    if (t != null && t.text != null && t.text.Contains("PeakZelo"))
                    {
                        t.supportRichText = true;
                        t.text = t.text.Replace("PeakZelo", FFAModToggle.MakeRainbowText("PeakZelo (dev)"));
                    }
                }
            }
            catch (System.Exception) { }
        }
    }

}
