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
        internal static CompatibleNoun[] moons;
        internal static bool hostingLobby;
        internal static Dictionary<ulong, string> playerSteamNames = [];
        internal static Dictionary<ulong, Vector3> playerPositions = [];
        internal static Dictionary<ulong, bool> allowedMovement = [];
        internal static Dictionary<ulong, bool> onShip = [];
        internal static float positionCacheUpdateTime = 0;

        private static ConfigEntry<int> configMinimumVotesToLeaveEarly;
        private static ConfigEntry<bool> configDisablePvpInShip;
        private static ConfigEntry<bool> configLogSignalTranslatorMessages;
        private static ConfigEntry<bool> configLogPvp;
        private static ConfigEntry<bool> configExperimentalChanges;

        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            configMinimumVotesToLeaveEarly = Config.Bind("General", "Minimum Votes To Leave Early", 1, "Minimum number of votes needed for the ship to leave early. Still requires that all the dead players have voted to leave.");
            configDisablePvpInShip = Config.Bind("General", "Disable PvP inside the ship", false, "If a player is inside the ship, they can't be damaged by other players.");
            configLogSignalTranslatorMessages = Config.Bind("Logging", "Log Signal Translator Messages", false, "Log messages that players send on the signal translator.");
            configLogPvp = Config.Bind("Logging", "Log PvP damage", false, "Log when a player damages another player.");
            configExperimentalChanges = Config.Bind("Experimental", "Experimental Changes.", false, "Enable experimental changes that may trigger on legitimate players (Requires more testing)");

            Harmony harmony = new(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            SteamMatchmaking.OnLobbyCreated += ConnectionEvents.LobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined += ConnectionEvents.ConnectionAttempt;
            SteamMatchmaking.OnLobbyMemberLeave += ConnectionEvents.ConnectionCleanup;
            Log.LogMessage($"{PluginInfo.PLUGIN_NAME} is loaded!");
            InvokeRepeating(nameof(UpdatePlayerPositionCache), 0f, 1f);
        }

        private void UpdatePlayerPositionCache()
        {
            if (NetworkManager.Singleton?.IsHost == false || StartOfRound.Instance == null) return;
            positionCacheUpdateTime = Time.time;

            foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
            {
                if (player.isPlayerDead || !player.isPlayerControlled) 
                {
                    return;
                }
                playerPositions[player.playerClientId] = player.transform.localPosition;
            }
        }

        private static void MovementAllowed(ulong playerId, bool movementEnabled = true)
        {
            try
            {
                if (allowedMovement[playerId] != movementEnabled) Log.LogDebug($"Player #{playerId} ({StartOfRound.Instance.allPlayerScripts[playerId].playerUsername}) movement toggled to {movementEnabled}");
                allowedMovement[playerId] = movementEnabled;
            }
            catch
            {
                allowedMovement[playerId] = true;
            }
        }

        internal class ConnectionEvents
        {
            internal static void ConnectionAttempt(Lobby _, Friend member)
            {
                if (hostingLobby && !playerSteamNames.TryAdd(member.Id.Value, member.Name))
                {
                    Log.LogError($"{member.Id.Value} is already in the connection list.");
                }
            }

            internal static void ConnectionCleanup(Lobby _, Friend member)
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
                int count = 0;

                if (configExperimentalChanges.Value)
                {
                    if (instance.numberOfItemsInDropship + boughtItems.Length > 12)
                    {
                        Log.LogWarning($"Trying to buy too many items.");
                        return;
                    }

                    // Add up each item's cost
                    foreach (int item in boughtItems)
                    {
                        try
                        {
                            _ = instance.buyableItemsList[item];
                        }
                        catch
                        {
                            Log.LogWarning($"[Experimental] Player #{SenderPlayerId} ({username}) tried to buy an item that was not in the host's shop. Item #{item}");
                            return;
                        }
                        cost += (int)(instance.buyableItemsList[item].creditsWorth * (instance.itemSalesPercentages[item] / 100f));
                        count++;
                    }

                    if (instance.groupCredits - cost == newGroupCredits)
                    {
                        instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                        return;
                    }

                    if (instance.groupCredits - cost < newGroupCredits + count && instance.groupCredits - cost > newGroupCredits - count)
                    {
                        Log.LogWarning($"[Experimental] Credit value is slightly off.");
                        instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                        return;
                    }
                    Log.LogWarning($"[Experimental] Player #{SenderPlayerId} ({username}) new credit value does not match the calculated amount of new credits. Old Credit Value: {instance.groupCredits} Cost Of items: {cost} Attempted Credit Value: {newGroupCredits}");
                }
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
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attempted to increase credits while buying items from Terminal. Attempted credit value: {newGroupCredits} Old credit value: {instance.groupCredits}");
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

                if (configExperimentalChanges.Value)
                {
                    if (StartOfRound.Instance.unlockablesList.unlockables.Count <= 0 || unlockableID > StartOfRound.Instance.unlockablesList.unlockables.Count)
                    {
                        Log.LogWarning($"[Experimental] Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to by unlockable that is out of unlockables list. ({unlockableID}).");
                        return;
                    }

                    if (StartOfRound.Instance.unlockablesList.unlockables[unlockableID].alreadyUnlocked)
                    {
                        Log.LogWarning($"[Experimental] Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to unlock an unlockable twice");
                        return;
                    }

                    int unlockableCost = StartOfRound.Instance.unlockablesList.unlockables[unlockableID].shopSelectionNode.itemCost;
                    if (clientId != 0 && terminal.groupCredits - unlockableCost != newGroupCreditsAmount)
                    {
                        Log.LogWarning($"[Experimental] Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) calculated credit amount does not match sent credit amount for unlockable. Current credits: {terminal.groupCredits} Unlockable cost: {unlockableCost} Sent credit Amount: {newGroupCreditsAmount}");
                        return;
                    }
                }

                if (clientId == 0 || newGroupCreditsAmount < terminal.groupCredits)
                {
                    StartOfRound.Instance.BuyShipUnlockableServerRpc(unlockableID, newGroupCreditsAmount);
                }
                else
                {
                    Log.LogWarning($"Player #{clientId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attempted to increase credits while buying ship unlockables. Attempted credit value: {newGroupCreditsAmount} Old credit value: {terminal.groupCredits}");
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
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;

                if (StartOfRound.Instance.allPlayerScripts[SenderPlayerId].isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to change the moon while they are dead on the server.");
                    return;
                }

                if (newGroupCreditsAmount < 0)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried set credits to a negative number ({newGroupCreditsAmount}).");
                    return;
                }

                Terminal terminal = FindObjectOfType<Terminal>();

                if (configExperimentalChanges.Value)
                {
                    moons ??= terminal.terminalNodes.allKeywords[26/*route*/].compatibleNouns.GroupBy(moon => moon.noun).Select(noun => noun.First()).ToArray();// Remove duplicate moons from moons array.

                    int moonCost = moons[levelID].result.itemCost;
                    if (clientId != 0 && terminal.groupCredits - moonCost != newGroupCreditsAmount)
                    {
                        Log.LogWarning($"[Experimental] Player #{SenderPlayerId} ({username}) calculated credit amount does not match sent credit amount for moon. Current credits: {terminal.groupCredits} Moon cost: {moonCost} Sent credit Amount: {newGroupCreditsAmount}");
                        return;
                    }
                }
                else
                {
                    if (newGroupCreditsAmount > terminal.groupCredits)
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({username}) attempted to increase credits from changing levels. Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {terminal.groupCredits}");
                        return;
                    }
                }
                StartOfRound.Instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
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
                    Log.LogError($"[AddPlayerChatMessageServerRpc] Failed to get the playerId from clientId: ({clientId}) Message: ({chatMessage})");
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
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) Chat message was empty after sanitization: ({chatMessage})");
                    return;
                }

                if (playerId == SenderPlayerId)
                {
                    Traverse.Create(HUDManager.Instance).Method("AddPlayerChatMessageServerRpc", [sanitizedChatMessage, playerId]).GetValue();
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to send message as another player: ({chatMessage})");
                }
            }

            public void AddTextMessageServerRpc(string chatMessage, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AddTextMessageServerRpc] Failed to get the playerId from clientId: ({clientId}) Message: ({chatMessage})");
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

                if (clientId == 0 ||
                    chatMessage.Equals($"{username} joined the ship.") ||
                    chatMessage.Equals($"{steamUsername} joined the ship.") ||
                    chatMessage.Equals($"{username} was left behind."))
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

            public void PlaceShipObjectServerRpc(Vector3 newPosition, Vector3 newRotation, NetworkObjectReference objectRef, int playerWhoMoved, ShipBuildModeManager instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetShipLeaveEarlyServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (playerWhoMoved != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object while spoofing another player.");
                    return;
                }

                if (newRotation.x != 270f || newRotation.z != 0f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object with the wrong rotation.");
                    return;
                }

                if (!StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(newPosition))
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object ouside of the ship.");
                    return;
                }

                instance.PlaceShipObjectServerRpc(newPosition, newRotation, objectRef, playerWhoMoved);
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

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerClientId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to end the game while spoofing another player.");
                    return;
                }

                if (player.isPlayerDead || !player.isPlayerControlled)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to force end the game. Could be desynced from host.");
                    return;
                }

                var lever = FindFirstObjectByType<StartMatchLever>();
                if (Vector3.Distance(lever.transform.position, player.transform.position) > 5f)
                {
                    float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to end game while too far away ({distanceToLever}).");
                    return;
                }

                StartOfRound.Instance.EndGameServerRpc(playerClientId);
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
                PlayerControllerB sendingPlayer = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (sendingPlayer.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) while they are dead on the server.");
                    return;
                }

                if (Vector3.Distance(sendingPlayer.transform.position, instance.transform.position) > 10f
                    && Physics.Raycast(sendingPlayer.transform.position, instance.transform.position - sendingPlayer.transform.position, out RaycastHit hit, 100f)
                    && hit.transform != instance.transform)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) from too far away and out of line of sight.");
                    return;
                }

                if (configDisablePvpInShip.Value && StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(instance.transform.position))
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) inside the ship.");
                    return;
                }

                if (playerWhoHit == SenderPlayerId)
                {
                    if (configLogPvp.Value) Log.LogWarning($"Player #{SenderPlayerId} ({username}) damaged ({instance.playerUsername}) for ({damageAmount}) damage");
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

            public void SetShipLightsServerRpc(bool setLightsOn, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[GrabObjectServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                PlayerControllerB sendingPlayer = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (sendingPlayer.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to toggle ship lights while they are dead on the server.");
                    return;
                }

                StartOfRound.Instance.shipRoomLights.SetShipLightsServerRpc(setLightsOn);
            }

            public void UseSignalTranslatorServerRpc(string signalMessage, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UseSignalTranslatorServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;

                if (StartOfRound.Instance.allPlayerScripts[SenderPlayerId].isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to send a Signal Translator message while they are dead on the server. Message: ({signalMessage})");
                    return;
                }

                if (configLogSignalTranslatorMessages.Value)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) sent signal translator message: ({signalMessage})");
                }
                HUDManager.Instance.UseSignalTranslatorServerRpc(signalMessage);
            }

            public void UpdatePlayerPositionServerRpc(Vector3 newPos, bool inElevator, bool inShipRoom, bool exhausted, bool isPlayerGrounded, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                if (!configExperimentalChanges.Value)
                {
                    Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                    return;
                }

                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdatePlayerPositionServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (instance.isPlayerDead)
                {
                    MovementAllowed(instance.playerClientId, false);
                    return;
                }

                try
                {
                    if (onShip[instance.playerClientId] != inElevator)
                    {
                        playerPositions[instance.playerClientId] = instance.transform.localPosition;
                        onShip[instance.playerClientId] = inElevator;
                    }

                    float timeSinceLast = Time.time - positionCacheUpdateTime;

                    float downwardDotProduct = Vector3.Dot((newPos - instance.transform.localPosition).normalized, Vector3.down);
                    float maxSpeed = instance.movementSpeed * (10f / Mathf.Max(instance.carryWeight, 1.0f));
                    if (downwardDotProduct > 0.3f)
                    {
                        Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                        return;
                    }

                    if (Vector3.Distance(newPos, playerPositions[instance.playerClientId]) > (maxSpeed * timeSinceLast) + 1)
                    {
                        Vector3 coalescePos = Vector3.MoveTowards(instance.transform.localPosition, newPos, 1f);
                        MovementAllowed(instance.playerClientId, false);
                        Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [coalescePos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                        return;
                    }

                    MovementAllowed(instance.playerClientId, true);
                    Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                }
                catch
                {
                    onShip[instance.playerClientId] = inElevator;
                    Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                }
            }

            public void UpdatePlayerRotationServerRpc(short newRot, short newYRot, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                try
                {
                    if (!allowedMovement[instance.playerClientId])
                    {
                        return;
                    }
                    Traverse.Create(instance).Method("UpdatePlayerRotationServerRpc", [newRot, newYRot]).GetValue();
                }
                catch
                {
                    Traverse.Create(instance).Method("UpdatePlayerRotationServerRpc", [newRot, newYRot]).GetValue();
                }
            }

            public void UpdatePlayerRotationFullServerRpc(Vector3 playerEulers, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                try
                {
                    if (!allowedMovement[instance.playerClientId])
                    {
                        return;
                    }
                    Traverse.Create(instance).Method("UpdatePlayerRotationFullServerRpc", [playerEulers]).GetValue();
                }
                catch
                {
                    Traverse.Create(instance).Method("UpdatePlayerRotationFullServerRpc", [playerEulers]).GetValue();
                }
            }

            public void UpdatePlayerAnimationServerRpc(int animationState, float animationSpeed, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                try
                {
                    if (!allowedMovement[instance.playerClientId])
                    {
                        return;
                    }
                    Traverse.Create(instance).Method("UpdatePlayerAnimationServerRpc", [animationState, animationSpeed]).GetValue();
                }
                catch
                {
                    Traverse.Create(instance).Method("UpdatePlayerAnimationServerRpc", [animationState, animationSpeed]).GetValue();
                }
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "BuyItemsServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SyncGroupCreditsServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "BuyShipUnlockableServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ChangeLevelServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "AddPlayerChatMessageServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "AddTextMessageServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetShipLeaveEarlyServerRpc" })
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
            class PlaceShipObjectServerRpc_Transpile
            {
                [HarmonyPatch(typeof(ShipBuildModeManager), "__rpc_handler_861494715")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PlaceShipObjectServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.PlaceShipObjectServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PlaceShipObjectServerRpc");
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "DespawnEnemyServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "EndGameServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PlayerLoadedServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SendNewPlayerValuesServerRpc" })
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "DamagePlayerFromOtherClientServerRpc" })
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
            class SetShipLightsServerRpc_Transpile
            {
                [HarmonyPatch(typeof(ShipLights), "__rpc_handler_1625678258")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetShipLightsServerRpc" })
                        {
                            callLocation = i;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SetShipLightsServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetShipLightsServerRpc");
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
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UseSignalTranslatorServerRpc" })
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

            [HarmonyPatch]
            class UpdatePlayerPositionServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_2013428264")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdatePlayerPositionServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdatePlayerPositionServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdatePlayerPositionServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class UpdatePlayerRotationServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_588787670")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdatePlayerRotationServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdatePlayerRotationServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdatePlayerRotationServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class UpdatePlayerRotationFullServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_3789403418")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdatePlayerRotationFullServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdatePlayerRotationFullServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdatePlayerRotationFullServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class UpdatePlayerAnimationServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_3473255830")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdatePlayerAnimationServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdatePlayerAnimationServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdatePlayerAnimationServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }
        }
    }
}