using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HardlineLeaderboard
{
    // Changed from the original: the ApiKey config description below now points
    // at the /hostkey Discord command (see the HardRankBot project) instead of a
    // website profile page, since this build targets that bot instead of the
    // original hosted HardRank site.
    [BepInPlugin("com.fleeter.hardlineleaderboard", "Hardline Leaderboard", "1.0.0")]
    public class HardlineLeaderboardPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static ConfigEntry<string> ApiUrl;
        public static ConfigEntry<string> ApiKey;

        static readonly HttpClient _http = new HttpClient();

        void Awake()
        {
            Log = Logger;
            ApiUrl = Config.Bind("General", "ApiUrl", "",
                "URL of the leaderboard backend (ask whoever runs your community's HardRank bot)");
            ApiKey = Config.Bind("General", "ApiKey", "",
                "Your personal hosting API key — get one with /hostkey in Discord");

            new Harmony("com.fleeter.hardlineleaderboard").PatchAll();
            Log.LogInfo("Hardline Leaderboard loaded");
        }

        static string JsonEscape(string s) =>
            s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string BuildJson(MatchPayload payload)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"map\":\"{JsonEscape(payload.map)}\",");
            sb.Append($"\"matchType\":\"{JsonEscape(payload.matchType)}\",");
            sb.Append($"\"winningTeam\":{payload.winningTeam},");
            sb.Append($"\"team1Wins\":{payload.team1Wins},");
            sb.Append($"\"team2Wins\":{payload.team2Wins},");
            sb.Append("\"players\":[");
            for (int i = 0; i < payload.players.Count; i++)
            {
                var p = payload.players[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"name\":\"{JsonEscape(p.name)}\",");
                sb.Append($"\"steamId\":\"{JsonEscape(p.steamId ?? "")}\",");
                sb.Append($"\"team\":{p.team},");
                sb.Append($"\"score\":{p.score},");
                sb.Append($"\"kills\":{p.kills},");
                sb.Append($"\"assists\":{p.assists},");
                sb.Append($"\"deaths\":{p.deaths},");
                sb.Append($"\"disconnected\":{(p.disconnected ? "true" : "false")}");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static void ReportMatch(MatchPayload payload)
        {
            var json = BuildJson(payload);
            Log.LogInfo($"Payload JSON: {json}");
            // Fire-and-forget on a thread pool thread so we never stall the game loop.
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post,
                        $"{ApiUrl.Value}/api/match")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Add("X-Api-Key", ApiKey.Value);
                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                        Log.LogInfo($"Match reported — HTTP {(int)resp.StatusCode}");
                    else
                        Log.LogError($"Match report failed — HTTP {(int)resp.StatusCode}: {body}");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Failed to report match: {ex.Message}");
                }
            });
        }
    }

    // -------------------------------------------------------------------------
    // Data classes — JsonUtility requires public fields + [Serializable]
    // -------------------------------------------------------------------------

    [Serializable]
    public class PlayerPayload
    {
        public string name;
        public string steamId;
        public int    team;
        public int    score;
        public int    kills;
        public int    assists;
        public int    deaths;
        public bool   disconnected;
    }

    [Serializable]
    public class MatchPayload
    {
        public string map;
        public string matchType;
        public int winningTeam;
        public int team1Wins;
        public int team2Wins;
        public List<PlayerPayload> players;
    }

    // -------------------------------------------------------------------------
    // Harmony patches
    // -------------------------------------------------------------------------

    // Tracks players who disconnect mid-match so we can penalise them.
    // Keyed by HumanName; cleared after each EndGame report.
    static class DisconnectTracker
    {
        public static readonly Dictionary<string, PlayerPayload> Departed =
            new Dictionary<string, PlayerPayload>();
    }

    // Fires just before a Human object is destroyed (i.e. player disconnected).
    [HarmonyPatch(typeof(Human), "OnDestroy")]
    static class HumanOnDestroyPatch
    {
        static void Prefix(Human __instance)
        {
            if (!NetworkServer.active) return;
            if (string.IsNullOrEmpty(__instance.HumanName)) return;

            // Snapshot the player's stats at disconnect time
            DisconnectTracker.Departed[__instance.HumanName] = new PlayerPayload
            {
                name         = __instance.HumanName,
                steamId      = "",
                team         = __instance.Team,
                score        = __instance.Score,
                kills        = __instance.Kills,
                assists      = __instance.Assists,
                deaths       = __instance.Deaths,
                disconnected = true
            };

            HardlineLeaderboardPlugin.Log.LogInfo(
                $"Disconnect tracked: {__instance.HumanName} (team {__instance.Team})");
        }
    }

    [HarmonyPatch(typeof(RoundsHardlineGameManager), "EndGame")]
    static class EndGamePatch
    {
        static readonly Dictionary<string, string> MapNames = new Dictionary<string, string>
        {
            { "level1", "Compound"    },
            { "level2", "Shipyard"    },
            { "level3", "Fortress"    },
            { "level4", "Lost Island" },
            { "level5", "Frost"       },
        };

        // Prefix runs before EndGame destroys player objects
        static void Prefix(RoundsHardlineGameManager __instance, int winningTeam)
        {
            if (!NetworkServer.active) return;

            // Get the host's Steam ID to mark them as verified
            string hostSteamId = "";
            string hostName    = "";
            try
            {
                if (SteamAPI.IsSteamRunning())
                    hostSteamId = SteamUser.GetSteamID().m_SteamID.ToString();
                var localHuman = NetworkClient.localPlayer?.GetComponent<Human>();
                if (localHuman != null) hostName = localHuman.HumanName;
            }
            catch { }

            var players = new List<PlayerPayload>();
            foreach (var human in UnityEngine.Object.FindObjectsOfType<Human>(true))
            {
                if (string.IsNullOrEmpty(human.HumanName)) continue;

                players.Add(new PlayerPayload
                {
                    name         = human.HumanName,
                    steamId      = (!string.IsNullOrEmpty(hostSteamId) && human.HumanName == hostName)
                                     ? hostSteamId : "",
                    team         = human.Team,
                    score        = human.Score,
                    kills        = human.Kills,
                    assists      = human.Assists,
                    deaths       = human.Deaths,
                    disconnected = false
                });

                // This player finished normally — remove from disconnect tracker if present
                DisconnectTracker.Departed.Remove(human.HumanName);
            }

            // Anyone still in Departed quit before the match ended
            foreach (var dep in DisconnectTracker.Departed.Values)
            {
                HardlineLeaderboardPlugin.Log.LogInfo(
                    $"Adding disconnected player to report: {dep.name}");
                players.Add(dep);
            }
            DisconnectTracker.Departed.Clear();

            var sceneName   = SceneManager.GetActiveScene().name;
            var displayName = MapNames.TryGetValue(sceneName.ToLower(), out var n) ? n : sceneName;

            HardlineLeaderboardPlugin.Log.LogInfo(
                $"EndGame Prefix — map={displayName} players={players.Count} winner={winningTeam}");

            var payload = new MatchPayload
            {
                map         = displayName,
                matchType   = "Round",
                winningTeam = winningTeam,
                team1Wins   = __instance.Team1Wins,
                team2Wins   = __instance.Team2Wins,
                players     = players
            };

            HardlineLeaderboardPlugin.ReportMatch(payload);
        }
    }
}
