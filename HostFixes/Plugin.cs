using BepInEx;
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

        private static Dictionary<ulong, int> VotedToLeaveEarlyPlayers = [];
        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            Harmony harmony = new(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(Terminal), nameof(Terminal.SyncGroupCreditsServerRpc))]
        class SyncGroupCreditsServerRpc_Patch
        {
            public static bool Prefix(Terminal __instance, int newGroupCredits)
            {
                Log.LogWarning($"__instance {__instance}");
                if (newGroupCredits > __instance.groupCredits)
                {
                    Log.LogError($"Attempt to increase credits using Terminal Cheat Commands. Attempted Credit Value: {newGroupCredits} Old Credit Value: {__instance.groupCredits}");
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.BuyShipUnlockableServerRpc))]
        class BuyShipUnlockableServerRpc_Patch
        {
            public static bool Prefix(int newGroupCreditsAmount)
            {
                Terminal TerminalInstance = FindObjectOfType<Terminal>();
                if (newGroupCreditsAmount > TerminalInstance.groupCredits)
                {
                    Log.LogError($"Attempt to increase credits while buying ship unlockables. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {TerminalInstance.groupCredits}");
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ChangeLevelServerRpc))]
        class ChangeLevelServerRpc_Patch
        {
            public static bool Prefix(int newGroupCreditsAmount)
            {
                Terminal TerminalInstance = FindObjectOfType<Terminal>();
                ;
                if (newGroupCreditsAmount > TerminalInstance.groupCredits)
                {
                    Log.LogError($"Attempt to increase credits while buying ship unlockables. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {TerminalInstance.groupCredits}");
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(GiftBoxItem), nameof(GiftBoxItem.OpenGiftBoxServerRpc))]
        class OpenGiftBoxServerRpc_Patch
        {
            public static bool Prefix(GiftBoxItem __instance)
            {
                GameObject objectInPresent = Traverse.Create(__instance).Field("objectInPresent").GetValue() as GameObject;
                if (objectInPresent)
                {
                    return true;
                }
                else
                {
                    Log.LogError($"Skipping extra OpenGiftBoxServerRpc calls.");
                    return false;
                }
            }

            public static void Postfix(GiftBoxItem __instance)
            {
                Traverse.Create(__instance).Field("objectInPresent").SetValue(null);
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
                    Log.LogError($"Player tried to place ship object outside the ship. {newPosition}");
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
                Mathf.Clamp(slot, 0, __instance.ItemSlots.Length - 1);
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetShipLeaveEarlyVotesText))]
        class SetShipLeaveEarlyVotesText_Patch
        {
            public static bool Prefix(HUDManager __instance)
            {
                int neededVotes = StartOfRound.Instance.connectedPlayersAmount + 1 - StartOfRound.Instance.livingPlayers;
                HUDManager.Instance.holdButtonToEndGameEarlyVotesText.text = $"({VotedToLeaveEarlyPlayers.Count}/{neededVotes} Votes)";
                return false;
            }
        }

        [HarmonyWrapSafe]
        [HarmonyPatch(typeof(StartOfRound), "OpenShipDoors")]
        class OpenShipDoors_Patch
        {
            public static void Prefix()
            {
                VotedToLeaveEarlyPlayers.Clear();
            }
        }

        public class MyServerRpcs
        {
            public void BuyItemsServerRpc(int[] boughtItems, int newGroupCredits, int numItemsInShip, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                int realPlayerId = StartOfRound.Instance.ClientPlayerList.GetValueSafe(clientId);
                Terminal terminal = FindObjectOfType<Terminal>();
                if (newGroupCredits < terminal.groupCredits)
                {
                    terminal.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) attempted to increase credits while buying items from Terminal. Attempted Credit Value: {newGroupCredits} Old Credit Value: {terminal.groupCredits}");
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
                if (!VotedToLeaveEarlyPlayers.ContainsKey(clientId) && StartOfRound.Instance.allPlayerScripts[realPlayerId].isPlayerDead)
                {
                    VotedToLeaveEarlyPlayers.Add(clientId, realPlayerId);
                    int neededVotes = StartOfRound.Instance.connectedPlayersAmount + 1 - StartOfRound.Instance.livingPlayers;
                    if (VotedToLeaveEarlyPlayers.Count >= Math.Max(neededVotes, 2))
                    {
                        VotedToLeaveEarlyPlayers.Clear();
                        TimeOfDay.Instance.SetShipLeaveEarlyClientRpc(TimeOfDay.Instance.normalizedTimeOfDay + 0.1f, TimeOfDay.Instance.votesForShipToLeaveEarly);
                    }
                    else
                    {
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
                    Log.LogInfo($"Despawn enemy {enemyNetworkObject}"); //TODO: Remove
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
                if (playerClientId == realPlayerId)
                {
                    if(StartOfRound.Instance.allPlayerScripts[playerClientId].isPlayerDead) //TODO: Add distance from lever check
                    {
                        Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to force end the game");
                        return;
                    }
                    StartOfRound.Instance.EndGameServerRpc(playerClientId);

                }
                else
                {
                    Log.LogError($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[realPlayerId].playerUsername}) tried to end the game while spoofing another player.");
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
                    codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                    codes[callLocation + 1].operand = typeof(MyServerRpcs).GetMethod(nameof(MyServerRpcs.BuyItemsServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch BuyItemsServerRpc");
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
                    codes[callLocation + 1].operand = typeof(MyServerRpcs).GetMethod(nameof(MyServerRpcs.AddPlayerChatMessageServerRpc));
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
                    codes[callLocation + 1].operand = typeof(MyServerRpcs).GetMethod(nameof(MyServerRpcs.SetShipLeaveEarlyServerRpc));
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
                    codes[callLocation + 1].operand = typeof(MyServerRpcs).GetMethod(nameof(MyServerRpcs.DespawnEnemyServerRpc));
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
                    codes[callLocation + 1].operand = typeof(MyServerRpcs).GetMethod(nameof(MyServerRpcs.EndGameServerRpc));
                }
                else
                {
                    Log.LogError("Could not patch EndGameServerRpc");
                }

                return codes.AsEnumerable();
            }
        }
    }
}