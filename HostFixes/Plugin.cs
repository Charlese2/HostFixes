using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Unity.Netcode;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;

namespace HostFixes
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static List<ulong> votedToLeaveEarlyPlayers = [];
        internal static CompatibleNoun[] moons;
        internal static Dictionary<int, int> unlockablePrices = [];
        internal static Dictionary<ulong, string> playerSteamNames = [];
        internal static Dictionary<ulong, Vector3> playerPositions = [];
        internal static Dictionary<ulong, bool> allowedMovement = [];
        internal static Dictionary<ulong, bool> onShip = [];
        internal static Dictionary<ulong, float> positionCacheUpdateTime = [];
        internal static bool terminalSoundPlaying;

        private static ConfigEntry<int> configMinimumVotesToLeaveEarly;
        private static ConfigEntry<bool> configDisablePvpInShip;
        private static ConfigEntry<bool> configLogSignalTranslatorMessages;
        private static ConfigEntry<bool> configLogPvp;
        private static ConfigEntry<bool> configExperimentalChanges;
        private static ConfigEntry<bool> configExperimentalPositionCheck;
        internal static ConfigEntry<bool> configShipObjectRotationCheck;
        private static ConfigEntry<bool> configLimitGrabDistance;

        private static Dictionary<int, bool> playerMovedShipObject = [];
        private static Dictionary<int, bool> reloadGunEffectsOnCooldown = [];
        private static Dictionary<int, bool> damagePlayerFromOtherClientOnCooldown = [];
        private static List<ulong> itemOnCooldown = [];
        private static bool shipLightsOnCooldown;
        private static bool buyShipUnlockableOnCooldown;
        private static bool pressTeleportButtonOnCooldown;

        public static Plugin Instance { get; private set; }
        public static Dictionary<ulong, uint> SteamIdtoConnectionIdMap { get; private set; } = [];
        public static Dictionary<uint, ulong> ConnectionIdtoSteamIdMap { get; private set; } = [];
        public static Dictionary<ulong, ulong> SteamIdtoClientIdMap { get; private set; } = [];
        public static Dictionary<ulong, ulong> ClientIdToSteamIdMap { get; private set; } = [];

        private void Awake()
        {
            // Plugin startup logic
            Log = Logger;
            configMinimumVotesToLeaveEarly = Config.Bind("General", "Minimum Votes To Leave Early", 1, "Minimum number of votes needed for the ship to leave early. Still requires that all the dead players have voted to leave.");
            configDisablePvpInShip = Config.Bind("General", "Disable PvP inside the ship", false, "If a player is inside the ship, they can't be damaged by other players.");
            configLogSignalTranslatorMessages = Config.Bind("Logging", "Log Signal Translator Messages", false, "Log messages that players send on the signal translator.");
            configLogPvp = Config.Bind("Logging", "Log PvP damage", false, "Log when a player damages another player.");
            configExperimentalChanges = Config.Bind("Experimental", "Experimental Changes.", false, "Enable experimental changes that may trigger on legitimate players (Requires more testing)");
            configExperimentalPositionCheck = Config.Bind("Experimental", "Experimental Position Checks.", false, "Enable experimental checks to prevent extreme client teleporting (Requires more testing)");
            configShipObjectRotationCheck = Config.Bind("General", "Check ship object rotation", true, "Only allow ship objects to be placed if the they are still upright.");
            configLimitGrabDistance = Config.Bind("General", "Limit grab distance", false, "Limit the grab distance to twice of the hosts grab distance. Defaulted to off because of grabbable desync.");

            Harmony harmony = new(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            SteamMatchmaking.OnLobbyCreated += ConnectionEvents.LobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined += ConnectionEvents.ConnectionAttempt;
            SteamMatchmaking.OnLobbyMemberLeave += ConnectionEvents.ConnectionCleanup;
            Log.LogMessage($"{PluginInfo.PLUGIN_NAME} is loaded!");
            InvokeRepeating(nameof(UpdatePlayerPositionCache), 0f, 1f);
            Instance ??= this;
        }

        private void OnDestroy()
        {
            SteamMatchmaking.OnLobbyCreated -= ConnectionEvents.LobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined -= ConnectionEvents.ConnectionAttempt;
            SteamMatchmaking.OnLobbyMemberLeave -= ConnectionEvents.ConnectionCleanup;
            CancelInvoke(nameof(UpdatePlayerPositionCache));
            if(Instance == this)
            {
                Instance = null;
            }
        }

        private void UpdatePlayerPositionCache()
        {
            if (NetworkManager.Singleton?.IsHost == false || StartOfRound.Instance == null) return;

            foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
            {
                if (player.isPlayerDead || !player.isPlayerControlled) 
                {
                    return;
                }
                playerPositions[player.playerClientId] = player.transform.localPosition;
                positionCacheUpdateTime[player.playerClientId] = Time.time;
            }
        }

        internal static IEnumerator TerminalSoundCooldown()
        {
            terminalSoundPlaying = true;
            yield return new WaitForSeconds(1f);
            terminalSoundPlaying = false;
        }

        private static IEnumerator PlaceShipObjectCooldown(int player)
        {
            playerMovedShipObject[player] = true;
            yield return new WaitForSeconds(1f);
            playerMovedShipObject[player] = false;
        }

        private static IEnumerator ReloadGunEffectsCooldown(int player)
        {
            reloadGunEffectsOnCooldown[player] = true;
            yield return new WaitForSeconds(2.5f);
            reloadGunEffectsOnCooldown[player] = false;
        }

        private static IEnumerator ShipLightsCooldown()
        {
            shipLightsOnCooldown = true;
            yield return new WaitForSeconds(0.5f);
            shipLightsOnCooldown = false;
        }

        private static IEnumerator BuyShipUnlockableCooldown()
        {
            buyShipUnlockableOnCooldown = true;
            yield return new WaitForSeconds(0.5f);
            buyShipUnlockableOnCooldown = false;
        }

        private static IEnumerator PressTeleportButtonCooldown()
        {
            pressTeleportButtonOnCooldown = true;
            yield return new WaitForSeconds(0.5f);
            pressTeleportButtonOnCooldown = false;
        }

        private static IEnumerator ActivateItemCooldown(ulong itemNetworkId)
        {
            itemOnCooldown.Add(itemNetworkId);
            yield return new WaitForSeconds(1f);
            itemOnCooldown.Remove(itemNetworkId);
        }

        private static IEnumerator DamageOtherPlayerCooldown(int sendingPlayer)
        {
            damagePlayerFromOtherClientOnCooldown[sendingPlayer] = true;
            yield return new WaitForSeconds(0.65f);
            damagePlayerFromOtherClientOnCooldown[sendingPlayer] = false;
        }

        internal class ConnectionEvents
        {
            internal static void ConnectionAttempt(Lobby _, Friend member)
            {
                if (NetworkManager.Singleton.IsHost && !playerSteamNames.TryAdd(member.Id.Value, member.Name))
                {
                    Log.LogError($"SteamId: ({member.Id.Value}) Name: ({member.Name}) is already in the connection list.");
                }
            }

            internal static void ConnectionCleanup(Lobby _, Friend member)
            {
                if (NetworkManager.Singleton.IsHost)
                {
                    if (!GameNetworkManager.Instance.steamIdsInLobby.Remove(member.Id.Value))
                    {
                        Log.LogError($"({member.Id.Value}) already removed from steamIdsInLobby.");
                    }
                }
            }

            internal static void LobbyCreated(Result result, Lobby lobby)
            {
                if (result == Result.OK && !playerSteamNames.TryAdd(lobby.Owner.Id.Value, lobby.Owner.Name))
                {
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

                if (!configExperimentalChanges.Value)
                {
                    instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                int cost = 0;
                int count = 0;

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
                        Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to buy an item that was not in the host's shop. Item #{item}");
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
                    Log.LogWarning($"Credit value is slightly off. Old Credit Value: {instance.groupCredits} Cost Of items: {cost} New Credit Value: {newGroupCredits}");
                    instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                    return;
                }

                Log.LogWarning($"Player #{SenderPlayerId} ({username}) new credit value does not match the calculated amount of new credits. Old Credit Value: {instance.groupCredits} Cost Of items: {cost} Attempted Credit Value: {newGroupCredits}");
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

            public void PlayTerminalAudioServerRpc(int clipIndex, Terminal instance)
            {
                if (terminalSoundPlaying) return;

                Instance.StartCoroutine(TerminalSoundCooldown());
                instance.PlayTerminalAudioServerRpc(clipIndex);
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

                if (clientId == 0)
                {
                    StartOfRound.Instance.BuyShipUnlockableServerRpc(unlockableID, newGroupCreditsAmount);
                    return;
                }

                if (buyShipUnlockableOnCooldown) return;

                Instance.StartCoroutine(BuyShipUnlockableCooldown());

                if (unlockableID < 0 || unlockableID > StartOfRound.Instance.unlockablesList.unlockables.Count)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to buy unlockable that is out of unlockables list. ({unlockableID}).");
                    return;
                }

                if (StartOfRound.Instance.unlockablesList.unlockables[unlockableID].alreadyUnlocked)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) tried to unlock an unlockable multiple times");
                    return;
                }

                if (!unlockablePrices.TryGetValue(unlockableID, out int unlockableCost))
                {
                    Log.LogError($"Could not find price of ship unlockable #{unlockableID}");
                    return;
                }

                if (clientId != 0 && terminal.groupCredits - unlockableCost != newGroupCreditsAmount)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) calculated credit amount does not match sent credit amount for unlockable. Current credits: {terminal.groupCredits} Unlockable cost: {unlockableCost} Sent credit Amount: {newGroupCreditsAmount}");
                    return;
                }

                if (newGroupCreditsAmount < terminal.groupCredits)
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

                if (!configExperimentalChanges.Value)
                {
                    StartOfRound.Instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
                    return;
                }

                Terminal terminal = FindObjectOfType<Terminal>();

                moons ??= terminal.terminalNodes.allKeywords[26/*route*/].compatibleNouns.GroupBy(moon => moon.noun).Select(noun => noun.First()).ToArray();// Remove duplicate moons from moons array.

                int moonCost = moons[levelID].result.itemCost;
                if (clientId != 0 && terminal.groupCredits - moonCost != newGroupCreditsAmount)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) calculated credit amount does not match sent credit amount for moon. Current credits: {terminal.groupCredits} Moon cost: {moonCost} Sent credit Amount: {newGroupCreditsAmount}");
                    return;
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
                if (string.IsNullOrEmpty(chatMessage))
                {
                    return;
                }

                string sanitizedChatMessage;
                ulong clientId = serverRpcParams.Receive.SenderClientId;

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

                if (StartOfRound.Instance.allPlayerScripts[SenderPlayerId].isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried sending a chat message while they are dead on the server. Message: ({chatMessage})");
                    return;
                }

                if (playerId < 0 || playerId > StartOfRound.Instance.allPlayerScripts.Length)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to chat with a playerId ({playerId}) that is not a valid player. Message: ({chatMessage})");
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
                    Log.LogError($"Failed to get steam username from playerlist for steamId: {steamId} Message: ({chatMessage})");
                    return;
                }

                if (clientId == 0 ||
                    chatMessage.Equals($"{username} joined the ship.") ||
                    chatMessage.Equals($"{steamUsername} joined the ship.") ||
                    chatMessage.Equals($"{steamUsername}... joined the ship.") ||
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
                    Log.LogError($"[PlaceShipObjectServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (playerMovedShipObject.TryGetValue(SenderPlayerId, out bool moved) && moved == true) return;

                Instance.StartCoroutine(PlaceShipObjectCooldown(SenderPlayerId));

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (playerWhoMoved != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object while spoofing another player.");
                    return;
                }

                if (configShipObjectRotationCheck.Value)
                {
                    try
                    {
                        GameObject networkObject = (GameObject)objectRef;
                        PlaceableShipObject placeableShipObject = networkObject.GetComponentInChildren<PlaceableShipObject>() ?? throw new Exception("PlaceableShipObject Not Found");
                        if (Mathf.RoundToInt(newRotation.x) != Mathf.RoundToInt(placeableShipObject.mainMesh.transform.eulerAngles.x) || Mathf.RoundToInt(newRotation.z) != Mathf.RoundToInt(placeableShipObject.mainMesh.transform.eulerAngles.z))
                        {
                            Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object ({placeableShipObject.name}) with the wrong rotation. x: ({newRotation.x}) ({placeableShipObject.mainMesh.transform.eulerAngles.x}) z: ({newRotation.z}) ({placeableShipObject.mainMesh.transform.eulerAngles.z})");
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning(e);
                        if (newRotation.x != 270f || newRotation.z != 0f) //Usually true for most ship objects
                        {
                            Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object with the wrong rotation.");
                            return;
                        }
                    }
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

                StartMatchLever lever = FindFirstObjectByType<StartMatchLever>();
                float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                if (distanceToLever > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to end the game while too far away ({distanceToLever}).");
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

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PlayerLoadedServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (clientId != senderClientId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to call PlayerLoadedServerRpc for another client.");
                    return;
                }

                Traverse.Create(StartOfRound.Instance).Method("PlayerLoadedServerRpc", [clientId]).GetValue();
            }

            public void SendNewPlayerValuesServerRpc(ulong newPlayerSteamId, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SendNewPlayerValuesServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (instance.actualClientId != senderClientId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to call SendNewPlayerValuesServerRpc with input value ({newPlayerSteamId}) on player #{instance.playerClientId} ({instance.playerUsername}).");
                    return;
                }

                if (GameNetworkManager.Instance.disableSteam)
                {
                    Traverse.Create(instance).Method("SendNewPlayerValuesServerRpc", [newPlayerSteamId]).GetValue();
                    return;
                }

                if (!ClientIdToSteamIdMap.TryGetValue(senderClientId, out ulong senderSteamId))
                {
                    Log.LogError($"[SendNewPlayerValuesServerRpc] Could not get steamId ({senderSteamId}) in steamIdtoClientIdMap");
                    return;
                }

                if (senderSteamId != newPlayerSteamId)
                {
                    Log.LogWarning($"Client sent incorrect steamId. senderSteamId: ({senderSteamId}) newPlayerSteamId: ({newPlayerSteamId})");
                }

                Traverse.Create(instance).Method("SendNewPlayerValuesServerRpc", [senderSteamId]).GetValue();
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

                if (damagePlayerFromOtherClientOnCooldown.TryGetValue(SenderPlayerId, out bool onCooldown) && onCooldown == true) return;

                if (playerWhoHit != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to spoof damage from player #{playerWhoHit} on {instance.playerUsername}.");
                    return;
                }

                Instance.StartCoroutine(DamageOtherPlayerCooldown(SenderPlayerId));

                if (sendingPlayer.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) while they are dead on the server.");
                    return;
                }

                if (Vector3.Distance(sendingPlayer.transform.position, instance.transform.position) > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) from too far away.");
                    return;
                }

                int shovelHitForce = FindFirstObjectByType<Shovel>(FindObjectsInactive.Include).shovelHitForce;

                if (shovelHitForce == 1 && damageAmount > 10)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) for extra damage ({damageAmount})");
                    return;
                }

                if (configDisablePvpInShip.Value && StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(instance.transform.position))
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to damage ({instance.playerUsername}) inside the ship.");
                    return;
                }

                if (configLogPvp.Value) Log.LogWarning($"Player #{SenderPlayerId} ({username}) damaged ({instance.playerUsername}) for ({damageAmount}) damage.");

                instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
            }

            public void ShootGunServerRpc(Vector3 shotgunPosition, Vector3 shotgunForward, ShotgunItem instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ShootGunServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (Vector3.Distance(instance.transform.position, shotgunPosition) > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to shoot shotgun from too far away from shotgun position.");
                    return;
                }

                if (instance.shellsLoaded < 1)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to shoot shotgun with no ammo.");
                    return;
                }

                instance.ShootGunClientRpc(shotgunPosition, shotgunForward);
            }

            public void ReloadGunEffectsServerRpc(bool start, ShotgunItem instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ReloadGunEffectsServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (reloadGunEffectsOnCooldown.TryGetValue(SenderPlayerId, out bool reloading) && reloading == true) return;

                Instance.StartCoroutine(ReloadGunEffectsCooldown(SenderPlayerId));

                int ammoInInventorySlot = Traverse.Create(instance).Method("FindAmmoInInventory").GetValue<int>();
                if (ammoInInventorySlot == -1)
                {
                    return;
                }

                if (instance.shellsLoaded >= 2)
                {
                    return;
                }

                player.DestroyItemInSlot(ammoInInventorySlot);

                instance.shellsLoaded++;
                instance.ReloadGunEffectsServerRpc(start);
            }

            public void GrabObjectServerRpc(NetworkObjectReference grabbedObject, PlayerControllerB instance, ServerRpcParams serverRpcParams)
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
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to pickup an object while they are dead on the server.");
                    return;
                }

                if (!configLimitGrabDistance.Value)
                {
                    Traverse.Create(instance).Method("GrabObjectServerRpc", [grabbedObject]).GetValue();
                    return;
                }

                try
                {
                    GameObject grabbedGameObject = (GameObject)grabbedObject;
                    float distanceToObject = Vector3.Distance(grabbedGameObject.transform.position, sendingPlayer.transform.position);
                    if (distanceToObject > instance.grabDistance * 2)
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({username}) Object ({grabbedGameObject.name}) pickup distance ({distanceToObject}) is too far away. Could be desync.");
                        Traverse.Create(instance).Method("GrabObjectClientRpc", [false, grabbedObject]).GetValue();
                        return;
                    }
                    Traverse.Create(instance).Method("GrabObjectServerRpc", [grabbedObject]).GetValue();
                }
                catch (Exception e)
                {
                    Log.LogError($"Couldn't do grab distance check. Exception: {e}");
                    Traverse.Create(instance).Method("GrabObjectServerRpc", [grabbedObject]).GetValue();
                }
            }

            public void SetShipLightsServerRpc(bool setLightsOn, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetShipLightsServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0)
                {
                    StartOfRound.Instance.shipRoomLights.SetShipLightsServerRpc(setLightsOn);
                    return;
                }

                if (shipLightsOnCooldown) return;

                Instance.StartCoroutine(ShipLightsCooldown());

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

            public void TeleportPlayerServerRpc(int playerObj, EntranceTeleport instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[TeleportPlayerServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0)
                {
                    instance.TeleportPlayerServerRpc(playerObj);
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (playerObj != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to teleport another player using an entrance teleport)");
                    return;
                }

                float distanceFromDoor = Vector3.Distance(instance.entrancePoint.position, player.transform.position);

                if (distanceFromDoor > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) too far away from entrance to teleport ({distanceFromDoor})");
                    return;
                }

                Transform exitPoint = Traverse.Create(instance).Field("exitPoint").GetValue<Transform>();

                if (exitPoint == null)
                {
                    instance.FindExitPoint();
                    exitPoint = Traverse.Create(instance).Field("exitPoint").GetValue<Transform>();
                }

                playerPositions[(ulong)SenderPlayerId] = exitPoint.position;
                instance.TeleportPlayerServerRpc(playerObj);
            }

            public void PressTeleportButtonServerRpc(ShipTeleporter instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PressTeleportButtonServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (pressTeleportButtonOnCooldown) return;

                Instance.StartCoroutine(PressTeleportButtonCooldown());

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (player.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to press teleporter button while they are dead on the server.");
                    return;
                }

                float teleporterButtonDistance = Vector3.Distance(player.transform.position, instance.buttonTrigger.transform.position);
                if (teleporterButtonDistance > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to press teleporter button from too far away ({teleporterButtonDistance})");
                    return;
                }

                instance.PressTeleportButtonServerRpc();
            }

            public void TeleportPlayerOutServerRpc(int playerObj, Vector3 teleportPos, ShipTeleporter instance, ServerRpcParams serverRpcParams)
            {

                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[TeleportPlayerOutServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (playerObj != SenderPlayerId)
                {
                    Log.LogWarning($"[TeleportPlayerOutServerRpc] playerObj ({playerObj}) != SenderPlayerId ({SenderPlayerId})");
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                playerPositions[player.playerClientId] = player.transform.localPosition;
                instance.TeleportPlayerOutServerRpc(playerObj, teleportPos);
            }

            public void UpdatePlayerPositionServerRpc(Vector3 newPos, bool inElevator, bool inShipRoom, bool exhausted, bool isPlayerGrounded, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdatePlayerPositionServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (instance.isPlayerDead)
                {
                    allowedMovement[instance.playerClientId] = false;
                    return;
                }

                try
                {
                    Vector3 position = inElevator ? instance.transform.localPosition : instance.transform.position;
                    if (!onShip.TryGetValue(instance.playerClientId, out bool isOnShip) || isOnShip != inElevator)
                    {
                        playerPositions[instance.playerClientId] = position;
                        positionCacheUpdateTime[instance.playerClientId] = Time.time;
                        onShip[instance.playerClientId] = inElevator;
                    }

                    float timeSinceLast = Time.time - positionCacheUpdateTime[instance.playerClientId];

                    float downwardDotProduct = Vector3.Dot((newPos - position).normalized, Vector3.down);
                    float maxDistancePerTick = instance.movementSpeed * (10f / Mathf.Max(instance.carryWeight, 1.0f)) / NetworkManager.Singleton.NetworkTickSystem.TickRate;
                    if (downwardDotProduct > 0.3f || StartOfRound.Instance.suckingPlayersOutOfShip || StartOfRound.Instance.inShipPhase || !configExperimentalPositionCheck.Value)
                    {
                        Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                        allowedMovement[instance.playerClientId] = true;
                        return;
                    }
                    if (Vector3.Distance(newPos, position) > maxDistancePerTick * 2)
                    {
                        Vector3 coalescePos = Vector3.MoveTowards(position, newPos, instance.movementSpeed * 5f / NetworkManager.Singleton.NetworkTickSystem.TickRate);
                        if (Vector3.Distance(newPos, playerPositions[instance.playerClientId]) > 100f)
                        {
                            allowedMovement[instance.playerClientId] = false;
                            return;
                        }

                        Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [coalescePos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                        return;
                    }

                    allowedMovement[instance.playerClientId] = true;
                    Traverse.Create(instance).Method("UpdatePlayerPositionServerRpc", [newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded]).GetValue();
                }
                catch (Exception e)
                {
                    Log.LogError(e);
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
                        Traverse.Create(instance).Method("UpdatePlayerAnimationServerRpc", [-1437577361/*Standing Still Anim Hash*/, -1f]).GetValue();
                        return;
                    }

                    Traverse.Create(instance).Method("UpdatePlayerAnimationServerRpc", [animationState, animationSpeed]).GetValue();
                }
                catch
                {
                    Traverse.Create(instance).Method("UpdatePlayerAnimationServerRpc", [animationState, animationSpeed]).GetValue();
                }
            }

            public void UpdateUsedByPlayerServerRpc(int playerNum, InteractTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdateUsedByPlayerServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0)
                {
                    Traverse.Create(instance).Method("UpdateUsedByPlayerServerRpc", [playerNum]).GetValue();
                    return;
                }

                if (playerNum != SenderPlayerId)
                {
                    Log.LogWarning($"[UpdateUsedByPlayerServerRpc] playerNum ({playerNum}) != SenderPlayerId ({SenderPlayerId})");
                    return;
                }

                Traverse.Create(instance).Method("UpdateUsedByPlayerServerRpc", [playerNum]).GetValue();
            }

            public void StopUsingServerRpc(int playerUsing, InteractTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[StopUsingServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0)
                {
                    Traverse.Create(instance).Method("StopUsingServerRpc", [playerUsing]).GetValue();
                    return;
                }

                if (playerUsing != SenderPlayerId)
                {
                    Log.LogWarning($"[StopUsingServerRpc] playerUsing ({playerUsing}) != SenderPlayerId ({SenderPlayerId})");
                    return;
                }

                Traverse.Create(instance).Method("StopUsingServerRpc", [playerUsing]).GetValue();
            }

            public void UpdateAnimServerRpc(bool setBool, bool playSecondaryAudios, int playerWhoTriggered, AnimatedObjectTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdateAnimServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId == 0)
                {
                    Traverse.Create(instance).Method("UpdateAnimServerRpc", [setBool, playSecondaryAudios, playerWhoTriggered]).GetValue();
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (playerWhoTriggered != SenderPlayerId)
                {
                    Log.LogWarning($"[UpdateAnimServerRpc] playerWhoTriggered ({playerWhoTriggered}) != SenderPlayerId ({SenderPlayerId}) ({instance.triggerAnimator.name})");
                    return;
                }

                if (player.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to interact with an animated object ({instance.triggerAnimator.name}) while they are dead on the server.");
                    return;
                }

                float distanceToObject = Vector3.Distance(instance.transform.position, StartOfRound.Instance.allPlayerScripts[SenderPlayerId].transform.position);
                if (Vector3.Distance(instance.transform.position, player.transform.position) > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to interact with ({instance.triggerAnimator.name}) from too far away ({distanceToObject})");
                    return;
                }

                Traverse.Create(instance).Method("UpdateAnimServerRpc", [setBool, playSecondaryAudios, playerWhoTriggered]).GetValue();
            }

            public void UpdateAnimTriggerServerRpc(AnimatedObjectTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdateAnimTriggerServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                Traverse.Create(instance).Method("UpdateAnimTriggerServerRpc").GetValue();
            }

            public void SyncAllPlayerLevelsServerRpc(int newPlayerLevel, int playerClientId, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncAllPlayerLevelsServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (playerClientId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to temporarily set another players level.");
                    return;
                }

                HUDManager.Instance.SyncAllPlayerLevelsServerRpc(newPlayerLevel, playerClientId);
            }

            public void StartGameServerRpc(StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[StartGameServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (player.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to start the game while they are dead on the server.");
                    return;
                }

                StartMatchLever lever = FindFirstObjectByType<StartMatchLever>();
                float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                if (distanceToLever > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to start the game while too far away ({distanceToLever}).");
                    return;
                }

                instance.StartGameServerRpc();
            }

            public void SyncAlreadyHeldObjectsServerRpc(int joiningClientId, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncAlreadyHeldObjectsServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                instance.SyncAlreadyHeldObjectsServerRpc(joiningClientId);
            }

            public void SyncShipUnlockablesServerRpc(StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncShipUnlockablesServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                try
                {
                    PlaceableShipObject[] placeableShipObjects = [.. FindObjectsOfType<PlaceableShipObject>().OrderBy(x => x.unlockableID)];

                    for (int i = 0; i < placeableShipObjects.Length; i++)
                    {
                        if (placeableShipObjects[i].parentObject == null)
                        {
                            Log.LogError($"Player #{SenderPlayerId} ({player.playerUsername}) PlaceableShipObject #{placeableShipObjects[i].unlockableID} ({placeableShipObjects[i].name}) parent is null. Crash Prevented. ");
                            return;
                        }
                    }

                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    return;
                }

                instance.SyncShipUnlockablesServerRpc();
            }

            public void SetPatienceServerRpc(float valueChange, DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetPatienceServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (clientId != 0)
                {
                    return;
                }

                instance.SetPatienceServerRpc(valueChange);
            }

            public void CheckAnimationGrabPlayerServerRpc(int monsterAnimationID, int playerID, DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[CheckAnimationGrabPlayerServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerID != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing CheckAnimationGrabPlayerServerRpc on another player.");
                    return;
                }

                instance.CheckAnimationGrabPlayerServerRpc(monsterAnimationID, playerID);
            }

            public void AttackPlayersServerRpc(DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AttackPlayersServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (clientId != 0)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried calling AttackPlayersServerRpc.");
                    return;
                }

                instance.AttackPlayersServerRpc();
            }

            public void ChangeEnemyOwnerServerRpc(ulong clientId, EnemyAI instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ChangeEnemyOwnerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                instance.ChangeEnemyOwnerServerRpc(clientId);
            }

            public void ActivateItemServerRpc(bool onOff, bool buttonDown, GrabbableObject instance, ServerRpcParams serverRpcParams)
            {
                ulong clientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(clientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ChangeEnemyOwnerServerRpc] Failed to get the playerId from clientId: {clientId}");
                    return;
                }

                if (itemOnCooldown.Contains(instance.NetworkObjectId))
                {
                    return;
                }

                if (instance.TryGetComponent(out RemoteProp _))
                {
                    Instance.StartCoroutine(ActivateItemCooldown(instance.NetworkObjectId));
                }

                Traverse.Create(instance).Method("ActivateItemServerRpc", [onOff, buttonDown]).GetValue();
            }
        }

        [HarmonyPatch]
        class Fix_SyncShipUnlockablesServerRpc_Crash
        {
            [HarmonyPatch(typeof(StartOfRound), "SyncAlreadyHeldObjectsServerRpc")]
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> RedirectLocalCallToPluginRpc(IEnumerable<CodeInstruction> instructions)
            {
                bool found = false;
                List<CodeInstruction> codes = new(instructions);
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo { Name: "SyncShipUnlockablesServerRpc", ReflectedType.Name: "StartOfRound" })
                    {
                        if(codes.Count > 1000)
                        {
                            throw new Exception("Stuck in infinite loop while patching.");
                        }

                        int callLocation = i;
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldloc, 12));
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SyncShipUnlockablesServerRpc));

                        found = true;
                    }
                }

                if (!found)
                {
                    Log.LogError("Could not patch BuyItemsServerRpc");
                }

                return codes.AsEnumerable();
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
            class PlayTerminalAudioServerRpc_Transpile
            {
                [HarmonyPatch(typeof(Terminal), "__rpc_handler_1713627637")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PlayTerminalAudioServerRpc" })
                        {
                            callLocation = i;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.PlayTerminalAudioServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PlayTerminalAudioServerRpc");
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
            class ShootGunServerRpc_Transpile
            {
                [HarmonyPatch(typeof(ShotgunItem), "__rpc_handler_1329927282")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ShootGunServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.ShootGunServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ShootGunServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class ReloadGunEffectsServerRpc_Transpile
            {
                [HarmonyPatch(typeof(ShotgunItem), "__rpc_handler_3349119596")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ReloadGunEffectsServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.ReloadGunEffectsServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ReloadGunEffectsServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class GrabObjectServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_1554282707")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "GrabObjectServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.GrabObjectServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch GrabObjectServerRpc");
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
            class TeleportPlayerServerRpc_Transpile
            {
                [HarmonyPatch(typeof(EntranceTeleport), "__rpc_handler_4279190381")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "TeleportPlayerServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.TeleportPlayerServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch TeleportPlayerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class PressTeleportButtonServerRpc_Transpile
            {
                [HarmonyPatch(typeof(ShipTeleporter), "__rpc_handler_389447712")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PressTeleportButtonServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.PressTeleportButtonServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PressTeleportButtonServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class TeleportPlayerOutServerRpc_Transpile
            {
                [HarmonyPatch(typeof(ShipTeleporter), "__rpc_handler_3033548568")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "TeleportPlayerOutServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.TeleportPlayerOutServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch TeleportPlayerOutServerRpc");
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

            [HarmonyPatch]
            class UpdateUsedByPlayerServerRpc_Transpile
            {
                [HarmonyPatch(typeof(InteractTrigger), "__rpc_handler_1430497838")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdateUsedByPlayerServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdateUsedByPlayerServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdateUsedByPlayerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class StopUsingServerRpc_Transpile
            {
                [HarmonyPatch(typeof(InteractTrigger), "__rpc_handler_880620475")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "StopUsingServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.StopUsingServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch StopUsingServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class UpdateAnimServerRpc_Transpile
            {
                [HarmonyPatch(typeof(AnimatedObjectTrigger), "__rpc_handler_1461767556")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdateAnimServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdateAnimServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdateAnimServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class UpdateAnimTriggerServerRpc_Transpile
            {
                [HarmonyPatch(typeof(AnimatedObjectTrigger), "__rpc_handler_2219526317")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "UpdateAnimTriggerServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.UpdateAnimTriggerServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch UpdateAnimTriggerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SyncAllPlayerLevelsServerRpc_Transpile
            {
                [HarmonyPatch(typeof(HUDManager), "__rpc_handler_4217433937")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SyncAllPlayerLevelsServerRpc" })
                        {
                            callLocation = i;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        codes.Insert(callLocation + 0, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 1].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SyncAllPlayerLevelsServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SyncAllPlayerLevelsServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class StartGameServerRpc_Transpile
            {
                [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_1089447320")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "StartGameServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.StartGameServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch StartGameServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SyncAlreadyHeldObjectsServerRpc_Transpile
            {
                [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_682230258")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SyncAlreadyHeldObjectsServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SyncAlreadyHeldObjectsServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SyncAlreadyHeldObjectsServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SyncShipUnlockablesServerRpc_Transpile
            {
                [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_744998938")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SyncShipUnlockablesServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SyncShipUnlockablesServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SyncShipUnlockablesServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetPatienceServerRpc_Transpile
            {
                [HarmonyPatch(typeof(DepositItemsDesk), "__rpc_handler_892728304")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetPatienceServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.SetPatienceServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetPatienceServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class CheckAnimationGrabPlayerServerRpc_Transpile
            {
                [HarmonyPatch(typeof(DepositItemsDesk), "__rpc_handler_1392297385")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "CheckAnimationGrabPlayerServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.CheckAnimationGrabPlayerServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch CheckAnimationGrabPlayerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class AttackPlayersServerRpc_Transpile
            {
                [HarmonyPatch(typeof(DepositItemsDesk), "__rpc_handler_3230280218")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "AttackPlayersServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.AttackPlayersServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch AttackPlayersServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class ChangeEnemyOwnerServerRpc_Transpile
            {
                [HarmonyPatch(typeof(EnemyAI), "__rpc_handler_3587030867")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ChangeEnemyOwnerServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.ChangeEnemyOwnerServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ChangeEnemyOwnerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class ActivateItemServerRpc_Transpile
            {
                [HarmonyPatch(typeof(GrabbableObject), "__rpc_handler_4280509730")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ActivateItemServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerRpcs).GetMethod(nameof(HostFixesServerRpcs.ActivateItemServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ChangeEnemyOwnerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }
        }
    }
}