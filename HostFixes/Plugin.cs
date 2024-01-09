using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Unity.Netcode;
using UnityEngine;

namespace HostFixes
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static List<ulong> votedToLeaveEarlyPlayers = [];
        internal static bool hostingLobby;
        internal static Dictionary<ulong, string> playerSteamNames = [];

        private static ConfigEntry<int> configMinimumVotesToLeaveEarly;
        private static ConfigEntry<bool> configDisablePvpInShip;
        private static ConfigEntry<bool> configLogSignalTranslatorMessages;
        private static ConfigEntry<bool> configLogPvp;
        private static GameObject lastObjectInGift;

        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            configMinimumVotesToLeaveEarly = Config.Bind("General", "Minimum Votes To Leave Early", 1, "Minimum number of votes needed for the ship to leave early. Still requires that all the dead players have voted to leave.");
            configDisablePvpInShip = Config.Bind("General", "Disable PvP inside the ship", false, "If a player is inside the ship, they can't be damaged by other players.");
            configLogSignalTranslatorMessages = Config.Bind("Logging", "Log Signal Translator Messages", false, "Log messages that players send on the signal translator.");
            configLogPvp = Config.Bind("Logging", "Log PvP damage", false, "Log when a player damages another player.");

            Harmony harmony = new(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            SteamMatchmaking.OnLobbyCreated += ConnectionEvents.LobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined += ConnectionEvents.ConnectionAttempt;
            SteamMatchmaking.OnLobbyMemberLeave += ConnectionEvents.ConnectionCleanup;
            Log.LogMessage($"{PluginInfo.PLUGIN_NAME} is loaded!");
        }

        internal class ConnectionEvents
        {
            internal static void ConnectionAttempt(Lobby lobby, Friend member)
            {
                if (hostingLobby && !playerSteamNames.TryAdd(member.Id.Value, member.Name))
                {
                    Log.LogError($"{member.Id.Value} is already in the connection list.");
                }
            }

            internal static void ConnectionCleanup(Lobby lobby, Friend member)
            {
                if (hostingLobby && !playerSteamNames.Remove(member.Id.Value))
                {
                    Log.LogError($"{member.Id.Value} was not in the connection list.");
                }
            }

            internal static void LobbyCreated(Result result, Lobby lobby)
            {
                hostingLobby = true;
                if (result == Result.OK && !playerSteamNames.TryAdd(lobby.Owner.Id.Value, lobby.Owner.Name))
                {
                    Log.LogError($"{lobby.Owner.Id.Value} is already in the connection list.");
                }
            }
        }

        public class HostFixesServerRpcs
        {
            public void BuyItemsServerRpc(int[] boughtItems, int newGroupCredits, int numItemsInShip, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[BuyItemsServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                int cost = 0;

                // Add up each item's cost
                foreach (int item in boughtItems)
                {
                    try
                    {
                        Log.LogInfo($"item: {item}");
                        _ = instance.buyableItemsList[item];
                    }
                    catch
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to buy an item that was not in the host's shop. Item #{item}");
                        return;
                    }
                    cost += (int)(instance.buyableItemsList[item].creditsWorth * (float)(instance.itemSalesPercentages[item] / 100f));
                }
                
                if (instance.groupCredits - cost != newGroupCredits)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) new credit value does not match the calculated amount of new credits. Old Credit Value: {instance.groupCredits} Cost Of items: {cost} Attempted Credit Value: {newGroupCredits}");
                    return;
                }

                instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
            }

            public void SyncGroupCreditsServerRpc(int newGroupCredits, int numItemsInShip, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncGroupCreditsServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0 || newGroupCredits < instance.groupCredits)
                {
                    instance.SyncGroupCreditsServerRpc(newGroupCredits, numItemsInShip);
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attempted to increase credits while buying items from Terminal. Attempted Credit Value: {newGroupCredits} Old Credit Value: {instance.groupCredits}");
                }
            }

            public void BuyShipUnlockableServerRpc(int unlockableID, int newGroupCreditsAmount, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[BuyShipUnlockableServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }
                Terminal terminal = FindObjectOfType<Terminal>();

                if (clientId == 0 || newGroupCreditsAmount < terminal.groupCredits)
                {
                    StartOfRound.Instance.BuyShipUnlockableServerRpc(unlockableID, newGroupCreditsAmount);
                }
                else
                {
                    Log.LogWarning($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attempted to increase credits while buying ship unlockables. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {terminal.groupCredits}");
                }
            }

            public void ChangeLevelServerRpc(int levelID, int newGroupCreditsAmount, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ChangeLevelServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                Terminal terminal = FindObjectOfType<Terminal>();
                if (clientId == 0 || newGroupCreditsAmount < terminal.groupCredits)
                {
                    StartOfRound.Instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attempted to increase credits from changing levels. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {terminal.groupCredits}");
                }
            }

            public void AddPlayerChatMessageServerRpc(string chatMessage, int playerId, ServerRpcParams serverRpcParams)
            {
                string sanitizedChatMessage;
                ulong clientId = serverRpcParams.Receive.SenderClientId;

                if (string.IsNullOrEmpty(chatMessage))
                {
                    return;
                }

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AddPlayerChatMessageServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                if (playerId == 99 && (chatMessage.StartsWith($"[morecompanycosmetics];{SenderPlayerId}") || chatMessage.Equals("[replacewithdata]")))
                {
                    Traverse.Create(HUDManager.Instance).Method("AddPlayerChatMessageServerRpc", [chatMessage, playerId]).GetValue();
                    return;
                }

                if (playerId < 0 || playerId > StartOfRound.Instance.allPlayerScripts.Count())
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to chat with a playerId ({playerId}) that is not a valid player.");
                    return;
                }

                try
                {
                    sanitizedChatMessage = Regex.Replace(chatMessage, ">|[\\\\][nt]", ""); //Regex equates to >|[\\][nt]
                }
                catch (Exception exception)
                {
                    Log.LogError($"Player #{SenderPlayerId} ({username}) Regex Exception: {exception} Chat Message: ({chatMessage})");
                    return;
                }

                if (string.IsNullOrEmpty(sanitizedChatMessage))
                {
                    Log.LogWarning($"Client #{SenderPlayerId} ({username}) Chat message was empty after sanitization: ({chatMessage})");
                    return;
                }

                if (playerId == SenderPlayerId)
                {
                    Traverse.Create(HUDManager.Instance).Method("AddPlayerChatMessageServerRpc", [sanitizedChatMessage, playerId]).GetValue();
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to send message as another player: ({chatMessage})");
                }
            }

            public void AddTextMessageServerRpc(string chatMessage, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AddTextMessageServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                ulong steamId = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerSteamId;

                if (GameNetworkManager.Instance.disableSteam)
                {
                    Traverse.Create(HUDManager.Instance).Method("AddTextMessageServerRpc", [chatMessage]).GetValue();
                    return;
                }

                if (chatMessage.EndsWith(" started the ship."))
                {
                    return;
                }

                if (!playerSteamNames.TryGetValue(steamId, out string steamUsername))
                {
                    Log.LogError($"Failed to get steam username from playerlist for steamId: {steamId}");
                    return;
                }

                if (clientId == 0)
                {
                    Traverse.Create(HUDManager.Instance).Method("AddTextMessageServerRpc", [chatMessage]).GetValue();
                }
                else if (chatMessage.Equals($"{username} joined the ship.") || chatMessage.Equals($"{steamUsername} joined the ship.") || chatMessage.Equals($"{username} was left behind."))
                {
                    Traverse.Create(HUDManager.Instance).Method("AddTextMessageServerRpc", [chatMessage]).GetValue();
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({steamUsername}) tried to send message as the server: ({chatMessage})");
                }
            }

            public void SetShipLeaveEarlyServerRpc(ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetShipLeaveEarlyServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (!votedToLeaveEarlyPlayers.Contains(clientId) && StartOfRound.Instance.allPlayerScripts[SenderPlayerId].isPlayerDead)
                {
                    votedToLeaveEarlyPlayers.Add(clientId);
                    int neededVotes = StartOfRound.Instance.connectedPlayersAmount + 1 - StartOfRound.Instance.livingPlayers;
                    if (votedToLeaveEarlyPlayers.Count >= Math.Max(neededVotes, configMinimumVotesToLeaveEarly.Value))
                    {
                        TimeOfDay.Instance.votesForShipToLeaveEarly = votedToLeaveEarlyPlayers.Count;
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
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to force the vote to leave.");
                }
            }

            public void DespawnEnemyServerRpc(NetworkObjectReference enemyNetworkObject, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[DespawnEnemyServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0)
                {
                    RoundManager.Instance.DespawnEnemyServerRpc(enemyNetworkObject);
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attemped to despawn an enemy on the server: {enemyNetworkObject}");
                }
            }

            public void EndGameServerRpc(int playerClientId, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[EndGameServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
                if (playerClientId == SenderPlayerId)
                {
                    if (player.isPlayerDead || !player.isPlayerControlled) //TODO: Add distance from lever check
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to force end the game. Could be desynced from host.");
                        return;
                    }
                    StartOfRound.Instance.EndGameServerRpc(playerClientId);

                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to end the game while spoofing another player.");
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
                    if (StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to call the PlayerLoaded RPC for another client.");
                    }
                }
            }

            public void OpenGiftBoxServerRpc(GiftBoxItem instance, ServerRpcParams serverRpcParams)
            {
                GameObject objectInPresent = Traverse.Create(instance).Field("objectInPresent").GetValue() as GameObject;
                if (objectInPresent == null)
                {
                    instance.OpenGiftBoxServerRpc(); //Let the client clean up bugged giftbox.
                    return;
                }

                if (objectInPresent != lastObjectInGift || lastObjectInGift == null)
                {
                    lastObjectInGift = objectInPresent;
                    instance.OpenGiftBoxServerRpc();
                }
                else
                {
                    Log.LogWarning($"Preventing spawning extra items from OpenGiftBoxServerRpc calls.");
                }
            }

            public void SendNewPlayerValuesServerRpc(ulong newPlayerSteamId, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SendNewPlayerValuesServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (instance.actualClientId == clientId)
                {
                    Traverse.Create(instance).Method("SendNewPlayerValuesServerRpc", [newPlayerSteamId]).GetValue();
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({instance.playerUsername}) tried to call SendNewPlayerValuesServerRpc on another player.");
                }
            }

            public void DamagePlayerFromOtherClientServerRpc(int damageAmount, Vector3 hitDirection, int playerWhoHit, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[DamagePlayerFromOtherClientServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;

                if (configDisablePvpInShip.Value && StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(instance.transform.position))
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to pvp inside the ship.");
                    return;
                }

                if (playerWhoHit == SenderPlayerId)
                {
                    if (configLogPvp.Value) Log.LogWarning($"Client #{clientId} ({username}) damaged ({instance.playerUsername}) for ({damageAmount}) damage");
                    instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
                }
                else if (playerWhoHit == 0 && instance.playerClientId == (uint)SenderPlayerId)
                {
                    instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to spoof damage from player #{playerWhoHit} on {instance.playerUsername}.");
                }
            }

            public void UseSignalTranslatorServerRpc(string signalMessage, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UseSignalTranslatorServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (configLogSignalTranslatorMessages.Value)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) sent signal translator message: ({signalMessage})");
                }
                HUDManager.Instance.UseSignalTranslatorServerRpc(signalMessage);
            }
        }

        private class ServerRPCMessageHandlers
        {
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
            class AddTextMessageServerRpc_Transpile
            {
                [HarmonyPatch(typeof(HUDManager), "__rpc_handler_2787681914")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "AddTextMessageServerRpc")
                        {
                            callLocation = i;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.AddTextMessageServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch AddTextMessageServerRpc");
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
            
            [HarmonyPatch]
            class UseSignalTranslatorServerRpc_Transpile
            {
                [HarmonyPatch(typeof(HUDManager), "__rpc_handler_2436660286")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && (codes[i].operand as MethodInfo)?.Name == "UseSignalTranslatorServerRpc")
                        {
                            callLocation = i;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UseSignalTranslatorServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UseSignalTranslatorServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }
        }
    }
}