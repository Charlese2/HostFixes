using GameNetcodeStuff;
using HarmonyLib;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;
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
                SteamIdtoConnectionIdMap[identity.SteamId.Value] = connection.Id;
                ConnectionIdtoSteamIdMap[connection.Id] = identity.SteamId.Value;
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnDisconnected")]
        class SteamIdDictionary_Cleanup
        {
            public static void Prefix(ref Connection connection, ref ConnectionInfo info)
            {
                if (NetworkManager.Singleton?.IsListening == true)
                {
                    NetIdentity identity = Traverse.Create(info).Field<NetIdentity>("identity").Value;
                    if (!SteamIdtoConnectionIdMap.Remove(identity.SteamId.Value))
                    {
                        Log.LogError($"steamId: ({identity.SteamId.Value}) was not in steamIdtoConnectionIdMap.");
                    }

                    if (!ConnectionIdtoSteamIdMap.Remove(connection.Id))
                    {
                        Log.LogError($"connectionId: ({connection.Id}) was not in connectionIdtoSteamIdMap.");
                    }

                    if (!playerSteamNames.Remove(identity.SteamId.Value))
                    {
                        Log.LogError($"steamId: ({identity.SteamId.Value}) was not in playerSteamNames.");
                    }
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
                    if (ClientIdToSteamIdMap.TryGetValue(clientId, out ulong steamId))
                    {
                        if (!SteamIdtoClientIdMap.Remove(steamId))
                        {
                            Log.LogError($"({steamId}) was not in steamIdtoClientIdMap.");
                        }

                        ClientIdToSteamIdMap.Remove(clientId);
                    }
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(StartOfRound), "OnClientConnect")]
        class OnClientConnect
        {
            public static void Postfix(StartOfRound __instance, ulong clientId)
            {
                if (__instance.IsServer && clientId != 0 && !GameNetworkManager.Instance.disableSteam)
                {
                    if (StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int playerId))
                    {
                        StartOfRound.Instance.allPlayerScripts[playerId].playerSteamId = ClientIdToSteamIdMap[clientId];
                    }
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(GameNetworkManager), "ConnectionApproval")]
        class MapSteamIdToClientId
        {
            public static void Postfix(GameNetworkManager __instance, ref NetworkManager.ConnectionApprovalRequest request, ref NetworkManager.ConnectionApprovalResponse response)
            {
                if (!GameNetworkManager.Instance.disableSteam)
                {
                    NetworkConnectionManager networkConnectionManager = Traverse.Create(NetworkManager.Singleton).Field("ConnectionManager").GetValue<NetworkConnectionManager>();
                    ulong transportId = Traverse.Create(networkConnectionManager).Method("ClientIdToTransportId", [request.ClientNetworkId]).GetValue<ulong>();

                    if (ConnectionIdtoSteamIdMap.TryGetValue((uint)transportId, out ulong steamId))
                    {
                        SteamIdtoClientIdMap[steamId] = request.ClientNetworkId;
                        ClientIdToSteamIdMap[request.ClientNetworkId] = steamId;

                        if (StartOfRound.Instance?.KickedClientIds.Contains(steamId) == true)
                        {
                            response.Reason = "You cannot rejoin after being kicked.";
                            response.Approved = false;
                        }
                    }
                    else
                    {
                        Log.LogError($"[ConnectionApproval] Could not get steamId from transportId ({transportId})");
                    }
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(Terminal), "Awake")]
        class HostInitialization
        {
            public static void Postfix(Terminal __instance)
            {
                Instance.StartCoroutine(TerminalAwakeWait(__instance));
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(NetworkConnectionManager), "Initialize")]
        class SetupIdMap
        {
            public static void Prefix()
            {
                if (!GameNetworkManager.Instance.disableSteam)
                {
                    SteamIdtoClientIdMap[SteamClient.SteamId] = 0;
                    ClientIdToSteamIdMap[0] = SteamClient.SteamId;
                    SteamIdtoConnectionIdMap[SteamClient.SteamId] = 0;
                    ConnectionIdtoSteamIdMap[0] = SteamClient.SteamId;
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(NetworkManager), "OnEnable")]
        class RegisterNetworkManagerEvents
        {
            public static void Postfix()
            {
                NetworkManager.Singleton.OnServerStopped -= ServerStopped;
                NetworkManager.Singleton.OnServerStopped += ServerStopped;
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
                    if (playerId < 0 || playerId > StartOfRound.Instance.allPlayerScripts.Length)
                    {
                        return false;
                    }
                }
                return true;
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
                if (votedToLeaveEarlyPlayers.Remove(clientId))
                {
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
