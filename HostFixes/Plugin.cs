using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Netcode;
using UnityEngine;

namespace HostFixes
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private static ConfigEntry<int> configMinimumVotesToLeaveEarly;

        private static List<ulong> votedToLeaveEarlyPlayers = [];

        private static GameObject lastObjectInGift;

        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            configMinimumVotesToLeaveEarly = Config.Bind("General", "Minimum Votes To Leave Early", 1, "Minimum number of votes needed for the ship to leave early. Still requires that all the dead players have voted to leave.");
            Harmony harmony = new(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Log.LogInfo($"{PluginInfo.PLUGIN_NAME} is loaded!");
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
                if(votedToLeaveEarlyPlayers.Contains(clientId))
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

        public class HostFixesServerRpcs
        {
            public void BuyItemsServerRpc(int[] boughtItems, int newGroupCredits, int numItemsInShip, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);

                if (newGroupCredits < instance.groupCredits)
                {
                    instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) attempted to increase credits while buying items from Terminal. Attempted Credit Value: {newGroupCredits} Old Credit Value: {instance.groupCredits}");
                }
            }
            
            public void SyncGroupCreditsServerRpc(int newGroupCredits, int numItemsInShip, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                if (newGroupCredits <= instance.groupCredits)
                {
                    instance.SyncGroupCreditsServerRpc(newGroupCredits, numItemsInShip);
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) attempted to increase credits while buying items from Terminal. Attempted Credit Value: {newGroupCredits} Old Credit Value: {instance.groupCredits}");
                }
            }

            public void BuyShipUnlockableServerRpc(int unlockableID, int newGroupCreditsAmount, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                Terminal terminal = FindObjectOfType<Terminal>();
                if (newGroupCreditsAmount < terminal.groupCredits)
                {
                    StartOfRound.Instance.BuyShipUnlockableServerRpc(unlockableID, newGroupCreditsAmount);
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) attempted to increase credits while buying ship unlockables. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {terminal.groupCredits}");
                }
            }
            
            public void ChangeLevelServerRpc(int levelID, int newGroupCreditsAmount, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                Terminal terminal = FindObjectOfType<Terminal>();
                if (newGroupCreditsAmount <= terminal.groupCredits)
                {
                    StartOfRound.Instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) attempted to increase credits from changing levels. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {terminal.groupCredits}");
                }
            }

            public void AddPlayerChatMessageServerRpc(string chatMessage, int playerId, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                if (playerId == 99)
                {
                    return;
                }    

                if (playerId < 0 || playerId > StartOfRound.Instance.allPlayerScripts.Count())
                {
                    Log.LogError($"[AddPlayerChatMessageServerRpc] Client #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to chat with a playerId ({playerId}) that is out of range.");
                    return;
                }

                if (playerId == realPlayerId)
                {
                    Traverse.Create(HUDManager.Instance).Method("AddPlayerChatMessageServerRpc", [chatMessage, playerId]).GetValue();
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to send message as another player: {chatMessage}");
                }
            }

            public void SetShipLeaveEarlyServerRpc(ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                if (!votedToLeaveEarlyPlayers.Contains(clientId) && StartOfRound.Instance.allPlayerScripts[realPlayerId].isPlayerDead)
                {
                    votedToLeaveEarlyPlayers.Add(clientId);
                    int neededVotes = StartOfRound.Instance.connectedPlayersAmount + 1 - StartOfRound.Instance.livingPlayers;
                    if (votedToLeaveEarlyPlayers.Count >= Math.Max(neededVotes, configMinimumVotesToLeaveEarly.Value))
                    {
                        TimeOfDay.Instance.SetShipLeaveEarlyClientRpc(TimeOfDay.Instance.normalizedTimeOfDay + 0.1f, TimeOfDay.Instance.votesForShipToLeaveEarly);
                    }
                    else
                    {
                        TimeOfDay.Instance.votesForShipToLeaveEarly++;
                        TimeOfDay.Instance.AddVoteForShipToLeaveEarlyClientRpc();
                    }
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to force the vote to leave.");
                }
            }

            public void DespawnEnemyServerRpc(NetworkObjectReference enemyNetworkObject, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                if (clientId == 0)
                {
                    RoundManager.Instance.DespawnEnemyServerRpc(enemyNetworkObject);
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) attemped to despawn an enemy on the server: {enemyNetworkObject}");
                }
            }

            public void EndGameServerRpc(int playerClientId, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
                if (playerClientId == realPlayerId)
                {
                    if(player.isPlayerDead || !player.isPlayerControlled) //TODO: Add distance from lever check
                    {
                        Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to force end the game. Could be desynced from host.");
                        return;
                    }
                    StartOfRound.Instance.EndGameServerRpc(playerClientId);

                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to end the game while spoofing another player.");
                }
            }

            public void PlayerLoadedServerRpc(ulong clientId, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId == 0)
                {
                    Traverse.Create(StartOfRound.Instance).Method("PlayerLoadedServerRpc", [clientId]).GetValue();
                    return;
                }

                if (clientId == senderClientId)
                {
                    Traverse.Create(StartOfRound.Instance).Method("PlayerLoadedServerRpc", [clientId]).GetValue();
                }
                else
                {
                    if (StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int realPlayerId))
                    {
                        Log.LogError($"Client #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to call the PlayerLoaded RPC for another client.");
                    }
                }
            }

            public void OpenGiftBoxServerRpc(GiftBoxItem instance, ServerRpcParams serverRpcParams)
            {
                GameObject objectInPresent = Traverse.Create(instance).Field("objectInPresent").GetValue() as GameObject;
                if (objectInPresent != lastObjectInGift)
                {
                    lastObjectInGift = objectInPresent;
                    instance.OpenGiftBoxServerRpc();
                }
                else
                {
                    Log.LogError($"Preventing spawning extra items from OpenGiftBoxServerRpc calls.");
                }
            }

            public void SendNewPlayerValuesServerRpc(ulong newPlayerSteamId, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                if (instance.actualClientId == clientId)
                {
                    Traverse.Create(instance).Method("SendNewPlayerValuesServerRpc", [newPlayerSteamId]).GetValue();
                }
                else
                {
                    Log.LogError($"Client #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to ");
                }
            }

            public void DamagePlayerFromOtherClientServerRpc(int damageAmount, Vector3 hitDirection, int playerWhoHit, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                if (playerWhoHit == realPlayerId)
                {
                    instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
                }
                else
                {
                    Log.LogError($"Client #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to spoof damage from another player.");
                }
            }
        }

        [HarmonyPatch]
        class BuyItemsServerRpc_Transpile
        {
            [HarmonyPatch(typeof(Terminal), "__rpc_handler_4003509079")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "BuyItemsServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched BuyItemsServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                    codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.BuyItemsServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch BuyItemsServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class SyncGroupCreditsServerRpc_Transpile
        {
            [HarmonyPatch(typeof(Terminal), "__rpc_handler_3085407145")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "SyncGroupCreditsServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched SyncGroupCreditsServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                    codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SyncGroupCreditsServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch SyncGroupCreditsServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class BuyShipUnlockableServerRpc_Transpile
        {
            [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_3953483456")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "BuyShipUnlockableServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched BuyShipUnlockableServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.BuyShipUnlockableServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch BuyShipUnlockableServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class ChangeLevelServerRpc_Transpile
        {
            [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_1134466287")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "ChangeLevelServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched ChangeLevelServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.ChangeLevelServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch ChangeLevelServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class AddPlayerChatMessageServerRpcHandler_Transpile
        {
            [HarmonyPatch(typeof(HUDManager), "__rpc_handler_2930587515")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "AddPlayerChatMessageServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched AddPlayerChatMessageServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.AddPlayerChatMessageServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch AddPlayerChatMessageServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class SetShipLeaveEarlyServerRpc_Transpile
        {
            [HarmonyPatch(typeof(TimeOfDay), "__rpc_handler_543987598")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "SetShipLeaveEarlyServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched SetShipLeaveEarlyServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SetShipLeaveEarlyServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch SetShipLeaveEarlyServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class DespawnEnemyServerRpc_Transpile
        {
            [HarmonyPatch(typeof(RoundManager), "__rpc_handler_3840785488")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "DespawnEnemyServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched DespawnEnemyServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.DespawnEnemyServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch DespawnEnemyServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class EndGameServerRpc_Transpile
        {
            [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_2028434619")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "EndGameServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched EndGameServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.EndGameServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch EndGameServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class PlayerLoadedServerRpc_Transpile
        {
            [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_4249638645")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "PlayerLoadedServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched PlayerLoadedServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.PlayerLoadedServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch PlayerLoadedServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class OpenGiftBoxServerRpc_Transpile
        {
            [HarmonyPatch(typeof(GiftBoxItem), "__rpc_handler_2878544999")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "OpenGiftBoxServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched OpenGiftBoxServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                    codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.OpenGiftBoxServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch OpenGiftBoxServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class SendNewPlayerValuesServerRpc_Transpile
        {
            [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_2504133785")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "SendNewPlayerValuesServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched SendNewPlayerValuesServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                    codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SendNewPlayerValuesServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch SendNewPlayerValuesServerRpc");
                }

                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch]
        class DamagePlayerFromOtherClientServerRpc_Transpile
        {
            [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_638895557")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
            {
                var found = false;
                var callLocation = -1;
                var codes = new List<CodeInstruction>(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "DamagePlayerFromOtherClientServerRpc")
                    {
                        callLocation = i;
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    Log.LogInfo("Patched DamagePlayerFromOtherClientServerRpc");
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                    codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.DamagePlayerFromOtherClientServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch DamagePlayerFromOtherClientServerRpc");
                }

                return codes.AsEnumerable();
            }
        }
    }
}