using GameNetcodeStuff;
using HarmonyLib;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using static HostFixes.Plugin;

namespace HostFixes
{
    internal class Patches
    {


        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(GameNetworkManager), "LeaveCurrentSteamLobby")]
        class LeaveCurrentSteamLobby_Patch
        {
            public static void Prefix()
            {
                if (hostingLobby) 
                { 
                    connectionList.Clear(); 
                }
                hostingLobby = false;
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(HUDManager), "AddPlayerChatMessageClientRpc")]
        class AddPlayerChatMessageClientRpc_Patch
        {
            public static bool Prefix(HUDManager __instance, int playerId)
            {
                NetworkManager networkManager = __instance.NetworkManager;
                if (networkManager == null || !networkManager.IsListening)
                {
                    return false;
                }

                if (!networkManager.IsHost)
                {
                    if (playerId < 0 || playerId > StartOfRound.Instance.allPlayerScripts.Count())
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(ShipBuildModeManager), nameof(ShipBuildModeManager.PlaceShipObjectServerRpc))]
        class PlaceShipObjectServerRpc_Patch
        {
            public static bool Prefix(Vector3 newPosition)
            {
                if (StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(newPosition))
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(PlayerControllerB), "SwitchToItemSlot")]
        class SwitchToItemSlot_Patch
        {
            public static void Prefix(PlayerControllerB __instance, ref int slot)
            {
                slot = Mathf.Clamp(slot, 0, __instance.ItemSlots.Length - 1);
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(PlayerControllerB), "NextItemSlot")]
        class NextItemSlot_Patch
        {
            public static void Prefix(PlayerControllerB __instance)
            {
                __instance.currentItemSlot = Mathf.Clamp(__instance.currentItemSlot, 0, __instance.ItemSlots.Length - 1);
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnPlayerDC))]
        class OnPlayerDC_Patch
        {
            public static void Postfix(ulong clientId)
            {
                if (votedToLeaveEarlyPlayers.Contains(clientId))
                {
                    votedToLeaveEarlyPlayers.Remove(clientId);
                    TimeOfDay.Instance.votesForShipToLeaveEarly = votedToLeaveEarlyPlayers.Count;
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(StartOfRound), "OpenShipDoors")]
        class OpenShipDoors_Patch
        {
            public static void Prefix()
            {
                votedToLeaveEarlyPlayers.Clear();
            }
        }
    }
}
