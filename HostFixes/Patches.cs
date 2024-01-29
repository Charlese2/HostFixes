using GameNetcodeStuff;
using HarmonyLib;
using Netcode.Transports.Facepunch;
using Steamworks.Data;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using static HostFixes.Plugin;

namespace HostFixes
{
    internal class Patches
    {
        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnConnecting")]
        class Identity_Fix
        {
            public static void Prefix(ref Connection connection, ref ConnectionInfo info)
            {
                NetIdentity identity = Traverse.Create(info).Field<NetIdentity>("identity").Value;
                steamIdtoConnectionIdMap[identity.SteamId.Value] = connection.Id;
                connectionIdtoSteamIdMap[connection.Id] = identity.SteamId.Value;
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnDisconnected")]
        class SteamIdDictionary_Cleanup
        {
            public static void Prefix(ref Connection connection, ref ConnectionInfo info)
            {
                NetIdentity identity = Traverse.Create(info).Field<NetIdentity>("identity").Value;
                if (!steamIdtoConnectionIdMap.Remove(identity.SteamId.Value))
                {
                    Log.LogError($"steamId: ({identity.SteamId.Value}) was not in steamIdtoConnectionIdMap.");
                }

                if (!connectionIdtoSteamIdMap.Remove(connection.Id))
                {
                    Log.LogError($"steamId: ({connection.Id}) was not in connectionIdtoSteamIdMap.");
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(NetworkConnectionManager), "OnClientDisconnectFromServer")]
        class ClientIdToSteamId_Cleanup
        {
            public static void Prefix(ulong clientId)
            {
                if (NetworkManager.Singleton.IsHost)
                {
                    if (clientIdToSteamIdMap.TryGetValue(clientId, out ulong steamId))
                    {
                        if (!steamIdtoClientIdMap.Remove(steamId))
                        {
                            Log.LogError($"({steamId}) was not in steamIdtoClientIdMap.");
                        }

                        clientIdToSteamIdMap.Remove(clientId);
                    }
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(GameNetworkManager), "ConnectionApproval")]
        [HarmonyPriority(Priority.Last)]
        class MapSteamIdToClientId
        {
            public static void Postfix(GameNetworkManager __instance, ref NetworkManager.ConnectionApprovalRequest request, ref NetworkManager.ConnectionApprovalResponse response)
            {
                if (!GameNetworkManager.Instance.disableSteam)
                {
                    NetworkConnectionManager networkConnectionManager = Traverse.Create(NetworkManager.Singleton).Field("ConnectionManager").GetValue<NetworkConnectionManager>();
                    ulong transportId = Traverse.Create(networkConnectionManager).Method("ClientIdToTransportId", [request.ClientNetworkId]).GetValue<ulong>();

                    if (connectionIdtoSteamIdMap.TryGetValue((uint)transportId, out ulong steamId))
                    {
                        steamIdtoClientIdMap[steamId] = request.ClientNetworkId;
                        clientIdToSteamIdMap[request.ClientNetworkId] = steamId;

                        if (response?.Approved == true && StartOfRound.Instance.KickedClientIds.Contains(steamId))
                        {
                            response.Reason = "You cannot rejoin after being kicked.";
                            response.Approved = false;
                            Log.LogWarning($"A player tried to force rejoin after being kicked. steamId: ({steamId})");
                        }
                    }
                    else
                    {
                        Log.LogError($"Could not get steam id from transportId ({transportId})");
                    }
                }
            }
        }


        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(GameNetworkManager), "LeaveCurrentSteamLobby")]
        class HostCleanup
        {
            public static void Prefix()
            {
                if (NetworkManager.Singleton.IsHost)
                {
                    playerSteamNames.Clear();
                    steamIdtoConnectionIdMap.Clear();
                    connectionIdtoSteamIdMap.Clear();
                    steamIdtoClientIdMap.Clear();
                    clientIdToSteamIdMap.Clear();
                }
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
        [HarmonyPatch(typeof(ShipBuildModeManager), "Update")]
        class PlaceShipObjectServerRpc_Patch
        {
            public static void Postfix(Transform ___ghostObject)
            {
                if (configShipObjectRotationCheck.Value)
                {
                    if (___ghostObject.eulerAngles.x != 270f || ___ghostObject.eulerAngles.z != 0f)
                    {
                        ___ghostObject.eulerAngles = new Vector3(270f, ___ghostObject.eulerAngles.y, 0f);
                    }
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
