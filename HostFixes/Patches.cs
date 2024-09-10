using GameNetcodeStuff;
using HarmonyLib;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using static HostFixes.Plugin;

namespace HostFixes
{
    internal class Patches
    {
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SyncShipUnlockablesServerRpc))]
        internal static class Fix_SyncShipUnlockablesServerRpc_Crash
        {
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                bool found = false;
                int location = -1;
                List<CodeInstruction> codes = new(instructions);

                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo { Name: "FindObjectsOfType" })
                    {
                        location = i;
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    codes.Insert(location + 1, Transpilers.EmitDelegate<Func<IEnumerable<PlaceableShipObject>, IEnumerable<PlaceableShipObject>>>(
                        placeableShipObjects => placeableShipObjects.Where(
                            placeableShipObject => placeableShipObject.parentObject != null)
                        )
                    );
                }
                else
                {
                    Log.LogError("Could not patch SyncShipUnlockablesServerRpc's Crash");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.SyncAlreadyHeldObjectsClientRpc))]
        internal static class SyncAlreadyHeldObjectsToCallingClient
        {
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                bool found = false;
                int location = -1;
                List<CodeInstruction> codes = new(instructions);

                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo { Name: "__beginSendClientRpc" })
                    {
                        location = i;
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    codes.Insert(location - 1, new CodeInstruction(OpCodes.Ldarg, 5)); //int32 syncWithClient
                    codes.Insert(location, Transpilers.EmitDelegate<Func<ClientRpcParams, ulong, ClientRpcParams>>((clientRpcParams, senderClientId) =>
                    {
                        return clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    }));
                }
                else
                {
                    Log.LogError("Could not patch SyncAlreadyHeldObjectsClientRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(FacepunchTransport), "Steamworks.ISocketManager.OnConnecting")]
        class Identity_Fix
        {
            public static void Prefix(ref Connection connection, ref ConnectionInfo info)
            {
                SteamIdtoConnectionIdMap[info.identity.SteamId.Value] = connection.Id;
                ConnectionIdtoSteamIdMap[connection.Id] = info.identity.SteamId.Value;
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
                    SteamIdtoConnectionIdMap.Remove(info.identity.SteamId.Value);
                    ConnectionIdtoSteamIdMap.Remove(connection.Id);
                    playerSteamNames.Remove(info.identity.SteamId.Value);
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
                        SteamIdtoClientIdMap.Remove(steamId);
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
            public static void Postfix(
                GameNetworkManager __instance,
                ref NetworkManager.ConnectionApprovalRequest request,
                ref NetworkManager.ConnectionApprovalResponse response)
            {
                if (!__instance.disableSteam)
                {
                    ulong transportId = NetworkManager.Singleton.ConnectionManager.ClientIdToTransportId(request.ClientNetworkId);

                    if (ConnectionIdtoSteamIdMap.TryGetValue((uint)transportId, out ulong steamId))
                    {
                        SteamIdtoClientIdMap[steamId] = request.ClientNetworkId;
                        ClientIdToSteamIdMap[request.ClientNetworkId] = steamId;

                        if (StartOfRound.Instance?.KickedClientIds.Contains(steamId) == true)
                        {
                            if (response.Reason == "")
                            {
                                response.Reason = "You cannot rejoin after being kicked.";
                            }
                            response.Approved = false;
                        }

                        string[] payload = Encoding.ASCII.GetString(request.Payload).Split(",");
                        if (payload.Length >= 2 && payload[1] != steamId.ToString())
                        {
                            Log.LogInfo($"SteamID sent by client ({payload[1]}) doesn't match SteamID from steam ({steamId}).");
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
                    if (playerId < 0 || playerId >= StartOfRound.Instance.allPlayerScripts.Length)
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
