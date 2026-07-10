using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using Steamworks;

namespace MaxPlayersMod
{
    [BepInPlugin("com.peakzelo.maxplayersmod", "Max Players Mod", "1.0.0")]
    public class MaxPlayersPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony("com.peakzelo.maxplayersmod").PatchAll();
            Logger.LogInfo("Max Players Mod loaded — lobby cap removed (Steam max: 250)");
        }
    }

    // Skip the game's own player-count gate entirely — any number can connect.
    [HarmonyPatch(typeof(HardlineNetworkManager), "OnServerConnect")]
    static class OnServerConnectPatch
    {
        static bool Prefix() => false;
    }

    // Create the Steam lobby at Steam's maximum (250) instead of the game's hardcoded 5.
    [HarmonyPatch(typeof(SteamLobby), "HostLobby")]
    static class SteamLobbyCapPatch
    {
        static bool Prefix()
        {
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 250);
            return false;
        }
    }
}
