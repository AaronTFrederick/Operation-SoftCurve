using BepInEx;
using UnityEngine;
using Mirror;

namespace HostKickMod
{
    [BepInPlugin("com.peakzelo.hostkickmod", "Host Kick", "3.0.2")]
    [BepInProcess("Project Hardline.exe")]
    public class HostKickMod : BaseUnityPlugin
    {
        // Keybinds - made public static so FFAMod can access them
        public static KeyCode kickMenuKey = KeyCode.F8;

        // Kick Menu
        private bool showKickMenu = false;
        private Vector2 scrollPosition = Vector2.zero;
        private Rect windowRect = new(20, 20, 300, 400);

        // UI Styles
        private GUIStyle buttonStyle;
        private GUIStyle windowStyle;
        private GUIStyle labelStyle;
        private GUIStyle creditStyle;
        private bool stylesInitialized = false;

        // Cached UI strings (avoids per-OnGUI string allocations)
        private string hostLabel;
        private string clientLabel;

        private void Awake()
        {
            Logger.LogInfo("Host Kick Mod initialized!");

            // Build static UI labels once (keybinds don't change at runtime)
            hostLabel = $"HOST - {kickMenuKey}: Kick Menu";
        }

        private void Update()
        {
            // Toggle kick menu with configurable key (Host only)
            if (Input.GetKeyDown(kickMenuKey))
            {
                if (NetworkServer.active)
                {
                    showKickMenu = !showKickMenu;
                    Logger.LogInfo($"Kick menu toggled: {showKickMenu}");
                }
                else
                {
                    Logger.LogWarning("You must be the host to use the kick menu!");
                }
            }
        }

        private void OnGUI()
        {
            if (!stylesInitialized)
            {
                InitializeStyles();
            }

            // Draw kick menu (Host only)
            if (showKickMenu && NetworkServer.active)
            {
                windowRect = GUI.Window(12345, windowRect, DrawKickWindow, "Host Kick Menu", windowStyle);
            }

            // Pure drawing below this point - only needed during the repaint event.
            // OnGUI runs multiple times per frame (layout, input, repaint); skipping
            // the non-repaint passes avoids doing the crosshair math for nothing.
            if (Event.current.type != EventType.Repaint) return;

            // Show indicator that you're the host
            if (NetworkServer.active)
            {
                GUI.Label(new Rect(10, 10, 300, 25), hostLabel, labelStyle);
            }
            else
            {
                GUI.Label(new Rect(10, 10, 250, 25), clientLabel, labelStyle);
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

            // Credit style (previously allocated every OnGUI call)
            creditStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.UpperRight
            };
            creditStyle.normal.textColor = new Color(1f, 1f, 1f, 0.5f); // Semi-transparent white

            stylesInitialized = true;
        }

        private void DrawKickWindow(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.Label($"Connected Players: {NetworkServer.connections.Count}", labelStyle);
            GUILayout.Space(10);

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));

            // Track which connection to kick and do it AFTER iterating - calling
            // Disconnect() mid-enumeration can modify NetworkServer.connections
            // and throw InvalidOperationException.
            NetworkConnectionToClient connectionToKick = null;

            foreach (var connection in NetworkServer.connections.Values)
            {
                if (connection == null) continue;

                GUILayout.BeginHorizontal();

                // Display connection info
                string playerInfo = $"ID: {connection.connectionId}";

                // Try to get player name if available
                if (connection.identity != null)
                {
                    playerInfo += $" - {connection.identity.gameObject.name}";
                }

                GUILayout.Label(playerInfo, GUILayout.Width(180));

                // Kick button
                if (GUILayout.Button("KICK", buttonStyle, GUILayout.Width(80)))
                {
                    connectionToKick = connection;
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(5);
            }

            GUILayout.EndScrollView();

            if (connectionToKick != null)
            {
                KickPlayer(connectionToKick);
            }

            GUILayout.Space(10);
            if (GUILayout.Button("Close Menu", buttonStyle))
            {
                showKickMenu = false;
            }

            GUILayout.EndVertical();

            // Make window draggable
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        private void KickPlayer(NetworkConnectionToClient connection)
        {
            if (connection == null) return;

            try
            {
                Logger.LogInfo($"Kicking player with connection ID: {connection.connectionId}");

                // Disconnect the player
                connection.Disconnect();

                Logger.LogInfo($"Player {connection.connectionId} has been kicked!");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Failed to kick player: {ex.Message}");
            }
        }
    }
}