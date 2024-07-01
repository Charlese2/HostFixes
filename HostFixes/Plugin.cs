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

namespace HostFixes
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static List<ulong> votedToLeaveEarlyPlayers = [];
        internal static Dictionary<int, int>? moons;
        internal static Dictionary<int, int> unlockablePrices = [];
        internal static Dictionary<ulong, string> playerSteamNames = [];
        internal static Dictionary<ulong, Vector3> playerPositions = [];
        internal static Dictionary<ulong, bool> allowedMovement = [];
        internal static Dictionary<ulong, bool> onShip = [];
        internal static Dictionary<ulong, float> positionCacheUpdateTime = [];
        internal static bool terminalSoundPlaying;
        internal static Dictionary<int, bool> killingWeed = [];

        public static ConfigEntry<int> configMinimumVotesToLeaveEarly = null!;
        public static ConfigEntry<bool> configDisablePvpInShip = null!;
        public static ConfigEntry<bool> configLogSignalTranslatorMessages = null!;
        public static ConfigEntry<bool> configLogPvp = null!;
        public static ConfigEntry<bool> configExperimentalChanges = null!;
        public static ConfigEntry<bool> configExperimentalPositionCheck = null!;
        public static ConfigEntry<bool> configShipObjectRotationCheck = null!;
        public static ConfigEntry<bool> configLimitGrabDistance = null!;
        public static ConfigEntry<int> configLimitShipLeverDistance = null!;
        public static ConfigEntry<int> configLimitTeleporterButtonDistance = null!;

        private static Dictionary<int, bool> playerMovedShipObject = [];
        private static Dictionary<int, bool> reloadGunEffectsOnCooldown = [];
        private static Dictionary<int, bool> damagePlayerFromOtherClientOnCooldown = [];
        private static Dictionary<int, bool> startGameOnCoolown = [];
        private static Dictionary<int, bool> endGameOnCoolown = [];
        private static Dictionary<int, bool> shipLeverAnimationOnCooldown = [];
        private static Dictionary<int, bool> changeLevelCooldown = [];
        private static List<ulong> itemOnCooldown = [];
        private static bool shipLightsOnCooldown;
        private static bool buyShipUnlockableOnCooldown;
        private static bool pressTeleportButtonOnCooldown;

        public static Plugin Instance { get; private set; } = null!;
        public static Dictionary<ulong, uint> SteamIdtoConnectionIdMap { get; private set; } = [];
        public static Dictionary<uint, ulong> ConnectionIdtoSteamIdMap { get; private set; } = [];
        public static Dictionary<ulong, ulong> SteamIdtoClientIdMap { get; private set; } = [];
        public static Dictionary<ulong, ulong> ClientIdToSteamIdMap { get; private set; } = [];

        private static readonly MethodInfo BeginSendClientRpc = typeof(NetworkBehaviour).GetMethod("__beginSendClientRpc", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo EndSendClientRpc = typeof(NetworkBehaviour).GetMethod("__endSendClientRpc", BindingFlags.NonPublic | BindingFlags.Instance);

        private void Awake()
        {
            // Create separate GameObject to be the plugin Instance so Coroutines can be run.
            // It avoids destruction if the BepInEx Manager gets destroyed.
            // Needs to check the current game object as the plugin would end up recursivly adding it's self.
            if (GameObject.Find("HostFixes") == null && name != "HostFixes")
            {
                GameObject HostFixes = new("HostFixes") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(HostFixes);
                Instance = HostFixes.AddComponent<Plugin>();
                return;
            }

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
            configLimitShipLeverDistance = Config.Bind("General", "Limit ship lever distance", 5, "Limit distance that someone can pull the ship lever from. 0 to disable.");
            configLimitTeleporterButtonDistance = Config.Bind("General", "Limit teleporter button distance", 5, "Limit distance that someone can press the teleporter buttton from. 0 to disable.");

            Harmony harmony = new(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            SteamMatchmaking.OnLobbyCreated += ConnectionEvents.LobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined += ConnectionEvents.ConnectionAttempt;
            SteamMatchmaking.OnLobbyMemberLeave += ConnectionEvents.ConnectionCleanup;
            Log.LogMessage($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} is loaded!");
            InvokeRepeating(nameof(UpdatePlayerPositionCache), 0f, 1f);
            new HostFixesServerSendRpcs();
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

        internal static IEnumerator TerminalAwakeWait(Terminal terminal)
        {
            yield return null;
            unlockablePrices = terminal.terminalNodes.allKeywords[0/*Buy*/].compatibleNouns
                .Where(item => item.result.shipUnlockableID != -1 && item.result.itemCost != -1)
                .ToDictionary(item => item.result.shipUnlockableID, item => item.result.itemCost);
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

        private static IEnumerator ShipLeverAnimationCooldown(int sendingPlayer)
        {
            shipLeverAnimationOnCooldown[sendingPlayer] = true;
            yield return new WaitForSeconds(1f);
            shipLeverAnimationOnCooldown[sendingPlayer] = false;
        }

        private static IEnumerator StartGameCooldown(int sendingPlayer)
        {
            startGameOnCoolown[sendingPlayer] = true;
            yield return new WaitForSeconds(1f);
            startGameOnCoolown[sendingPlayer] = false;
        }

        private static IEnumerator EndGameCooldown(int sendingPlayer)
        {
            endGameOnCoolown[sendingPlayer] = true;
            yield return new WaitForSeconds(1f);
            endGameOnCoolown[sendingPlayer] = false;
        }

        private static IEnumerator ChangeLevelCooldown(int sendingPlayer)
        {
            changeLevelCooldown[sendingPlayer] = true;
            yield return new WaitForSeconds(0.25f);
            changeLevelCooldown[sendingPlayer] = false;
        }

        internal class ConnectionEvents
        {
            internal static void ConnectionAttempt(Lobby _, Friend member)
            {
                if (NetworkManager.Singleton.IsHost && !playerSteamNames.TryAdd(member.Id.Value, member.Name))
                {
                    Log.LogError($"SteamId: ({member.Id.Value}) Name: ({member.Name}) is already in the playerSteamNames list.");
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
                    Log.LogError($"Host is already in playerSteamNames.");
                }
            }
        }

        internal static void ServerStopped(bool _)
        {
            playerSteamNames.Clear();
            SteamIdtoConnectionIdMap.Clear();
            ConnectionIdtoSteamIdMap.Clear();
            SteamIdtoClientIdMap.Clear();
            ClientIdToSteamIdMap.Clear();
        }

        public class HostFixesServerReceiveRpcs
        {
            public void BuyItemsServerRpc(int[] boughtItems, int newGroupCredits, int numItemsInShip, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[BuyItemsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncGroupCreditsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0 || newGroupCredits < instance.groupCredits)
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[BuyShipUnlockableServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                Terminal terminal = FindObjectOfType<Terminal>();

                if (senderClientId == 0)
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

                if (senderClientId != 0 && terminal.groupCredits - unlockableCost != newGroupCreditsAmount)
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
                    Log.LogWarning($"Player #{senderClientId} ({StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername}) attempted to increase credits while buying ship unlockables. Attempted credit value: {newGroupCreditsAmount} Old credit value: {terminal.groupCredits}");
                }
            }

            public void ChangeLevelServerRpc(int levelID, int newGroupCreditsAmount, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ChangeLevelServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;

                if (changeLevelCooldown.TryGetValue(SenderPlayerId, out bool changedLevel) && changedLevel == true) return;

                Instance.StartCoroutine(ChangeLevelCooldown(SenderPlayerId));

                if (StartOfRound.Instance.allPlayerScripts[SenderPlayerId].isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to change the moon while they are dead on the server.");
                    return;
                }

                if (newGroupCreditsAmount < 0)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to set credits to a negative number ({newGroupCreditsAmount}).");
                    return;
                }

                if (!configExperimentalChanges.Value)
                {
                    StartOfRound.Instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
                    return;
                }

                Terminal terminal = FindObjectOfType<Terminal>();

                if (moons == null)
                {
                    Dictionary<string, int> moonCost = terminal.terminalNodes.allKeywords[27/*route*/].compatibleNouns
                        .GroupBy(moon => moon.noun).Select(moon => moon.First()) //Remove duplicate moons
                        .ToDictionary(compatibleNoun => compatibleNoun.noun.name, compatibleNoun => compatibleNoun.result.itemCost);
                    moons = StartOfRound.Instance.levels.ToDictionary(moon => moon.levelID, moon => moonCost.GetValueOrDefault(moon.PlanetName.Replace(" ", "-"), 0));
                }

                try
                {
                    int moonCost = moons[levelID];
                    if (senderClientId != 0 && terminal.groupCredits - moonCost != newGroupCreditsAmount)
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
                }
                catch (IndexOutOfRangeException)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) sent levelID ({levelID}) that is not in the moons array.");
                    return;
                }

                StartOfRound.Instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
            }

            public void AddPlayerChatMessageServerRpc(string chatMessage, int playerId, ServerRpcParams serverRpcParams)
            {
                if (string.IsNullOrWhiteSpace(chatMessage))
                {
                    return;
                }

                string sanitizedChatMessage;
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AddPlayerChatMessageServerRpc] Failed to get the playerId from senderClientId: ({senderClientId}) Message: ({chatMessage})");
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;

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
                    //Replace <> from received messages with () to prevent injected Text Tags.
                    sanitizedChatMessage = Regex.Replace(chatMessage, @"<(\S+?)>", "($+)");
                }
                catch (Exception exception)
                {
                    Log.LogError($"Player #{SenderPlayerId} ({username}) Regex Exception: {exception} Chat Message: ({chatMessage})");
                    return;
                }

                if (string.IsNullOrWhiteSpace(sanitizedChatMessage))
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) Chat message was empty after sanitization. Original Message: ({chatMessage})");
                    return;
                }

                if (playerId == SenderPlayerId)
                {
                    HUDManager.Instance.AddPlayerChatMessageServerRpc(sanitizedChatMessage, playerId);
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to send message as another player #({playerId}) Message: ({chatMessage})");
                }
            }

            public void AddTextMessageServerRpc(string chatMessage, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AddTextMessageServerRpc] Failed to get the playerId from senderClientId: ({senderClientId}) Message: ({chatMessage})");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                ulong steamId = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerSteamId;

                if (GameNetworkManager.Instance.disableSteam)
                {
                    HUDManager.Instance.AddTextMessageServerRpc(chatMessage);
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

                if (senderClientId == 0 ||
                    chatMessage.Equals($"{username} joined the ship.") ||
                    chatMessage.Equals($"{steamUsername} joined the ship.") ||
                    chatMessage.Equals($"{steamUsername}... joined the ship.") ||
                    chatMessage.Equals($"{username} was left behind.") ||
                    chatMessage.StartsWith($"[morecompanycosmetics];{SenderPlayerId};"))
                {
                    HUDManager.Instance.AddTextMessageServerRpc(chatMessage);
                }
                else
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({steamUsername}) tried to send message as the server: ({chatMessage})");
                }
            }

            public void SetShipLeaveEarlyServerRpc(ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetShipLeaveEarlyServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (!votedToLeaveEarlyPlayers.Contains(senderClientId) && StartOfRound.Instance.allPlayerScripts[SenderPlayerId].isPlayerDead)
                {
                    votedToLeaveEarlyPlayers.Add(senderClientId);
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PlaceShipObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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

                if (player.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to place a ship object while dead on the server.");
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[DespawnEnemyServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[EndGameServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (endGameOnCoolown.TryGetValue(SenderPlayerId, out bool endGameCalledOnCooldown) && endGameCalledOnCooldown == true)
                {
                    Instance.StartCoroutine(EndGameCooldown(SenderPlayerId));
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerClientId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to end the game while spoofing another player.");
                    return;
                }

                if (player.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to end the game while dead on the server.");
                    return;
                }

                StartMatchLever lever = FindFirstObjectByType<StartMatchLever>();
                float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                if (configLimitShipLeverDistance.Value > 1f && distanceToLever > configLimitShipLeverDistance.Value)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to end the game while too far away ({distanceToLever}).");
                    return;
                }

                StartOfRound.Instance.EndGameServerRpc(playerClientId);
            }

            public void PlayerLoadedServerRpc(ulong clientId, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId == 0)
                {
                    instance.PlayerLoadedServerRpc(clientId);
                    return;
                }

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PlayerLoadedServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (clientId != senderClientId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to call PlayerLoadedServerRpc for another client.");
                    return;
                }

                if (instance.fullyLoadedPlayers.Contains(clientId))
                {
                    return;
                }

                instance.PlayerLoadedServerRpc(clientId);
            }

            public void FinishedGeneratingLevelServerRpc(ulong clientId, RoundManager instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId == 0)
                {
                    instance.FinishedGeneratingLevelServerRpc(clientId);
                    return;
                }

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[FinishedGeneratingLevelServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (clientId != senderClientId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to call FinishedGeneratingLevelServerRpc for another client.");
                    return;
                }

                if (instance.playersFinishedGeneratingFloor.Contains(clientId))
                {
                    return;
                }

                instance.FinishedGeneratingLevelServerRpc(clientId);
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
                    instance.SendNewPlayerValuesServerRpc(newPlayerSteamId);
                    return;
                }

                if (!ClientIdToSteamIdMap.TryGetValue(senderClientId, out ulong senderSteamId))
                {
                    Log.LogError($"[SendNewPlayerValuesServerRpc] Could not get steamId ({senderSteamId}) in steamIdtoClientIdMap");
                    return;
                }

                if (senderSteamId != newPlayerSteamId)
                {
                    Log.LogWarning($"Client sent incorrect steamId. Player's steamId: ({senderSteamId}) Sent steamId: ({newPlayerSteamId})");
                }

                instance.SendNewPlayerValuesServerRpc(senderSteamId);
            }

            public void DamagePlayerFromOtherClientServerRpc(int damageAmount, Vector3 hitDirection, int playerWhoHit, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[DamagePlayerFromOtherClientServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[SenderPlayerId].playerUsername;
                PlayerControllerB sendingPlayer = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (senderClientId == 0 && playerWhoHit == -1) //Lethal Escape compatibility
                {
                    instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
                    return;
                }

                if (damagePlayerFromOtherClientOnCooldown.TryGetValue(SenderPlayerId, out bool onCooldown) && onCooldown == true) return;

                Instance.StartCoroutine(DamageOtherPlayerCooldown(SenderPlayerId));

                if (playerWhoHit != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({username}) tried to spoof ({damageAmount}) damage from player #{playerWhoHit} on ({instance.playerUsername}).");
                    return;
                }

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

                bool shovelHitForceIsUnmodified = FindFirstObjectByType<Shovel>(FindObjectsInactive.Include)?.shovelHitForce == 1;

                if (shovelHitForceIsUnmodified && damageAmount > 10)
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ShootGunServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ReloadGunEffectsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];

                if (reloadGunEffectsOnCooldown.TryGetValue(SenderPlayerId, out bool reloading) && reloading == true) return;

                Instance.StartCoroutine(ReloadGunEffectsCooldown(SenderPlayerId));

                int ammoInInventorySlot = instance.FindAmmoInInventory();
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[GrabObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                    instance.GrabObjectServerRpc(grabbedObject);
                    return;
                }

                try
                {
                    GameObject grabbedGameObject = (GameObject)grabbedObject;
                    float distanceToObject = Vector3.Distance(grabbedGameObject.transform.position, sendingPlayer.transform.position);
                    bool isNotBody = grabbedGameObject.GetComponent<RagdollGrabbableObject>() is null;
                    if (distanceToObject > instance.grabDistance * 2 && isNotBody)
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({username}) Object ({grabbedGameObject.name}) pickup distance ({distanceToObject}) is too far away. Could be desync.");
                        instance.GrabObjectClientRpc(false, grabbedObject);
                        return;
                    }

                    instance.GrabObjectServerRpc(grabbedObject);
                }
                catch (Exception e)
                {
                    Log.LogError($"Couldn't do grab distance check. Exception: {e}");
                }
            }

            public void SetShipLightsServerRpc(bool setLightsOn, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetShipLightsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UseSignalTranslatorServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[TeleportPlayerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
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

                Transform exitPoint = instance.exitPoint;

                if (exitPoint == null)
                {
                    instance.FindExitPoint();
                    exitPoint = instance.exitPoint;
                }

                playerPositions[(ulong)SenderPlayerId] = exitPoint.position;
                instance.TeleportPlayerServerRpc(playerObj);
            }

            public void PressTeleportButtonServerRpc(ShipTeleporter instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PressTeleportButtonServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                if (configLimitTeleporterButtonDistance.Value > 1f && teleporterButtonDistance > configLimitTeleporterButtonDistance.Value)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to press teleporter button from too far away ({teleporterButtonDistance})");
                    return;
                }

                instance.PressTeleportButtonServerRpc();
            }

            public void TeleportPlayerOutServerRpc(int playerObj, Vector3 teleportPos, ShipTeleporter instance, ServerRpcParams serverRpcParams)
            {

                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[TeleportPlayerOutServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                        instance.UpdatePlayerPositionServerRpc(newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded);
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

                        instance.UpdatePlayerPositionServerRpc(coalescePos, inElevator, inShipRoom, exhausted, isPlayerGrounded);
                        return;
                    }

                    allowedMovement[instance.playerClientId] = true;
                    instance.UpdatePlayerPositionServerRpc(newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded);
                }
                catch (Exception e)
                {
                    Log.LogError(e);
                    instance.UpdatePlayerPositionServerRpc(newPos, inElevator, inShipRoom, exhausted, isPlayerGrounded);
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
                    instance.UpdatePlayerRotationServerRpc(newRot, newYRot);
                }
                catch
                {
                    instance.UpdatePlayerRotationServerRpc(newRot, newYRot);
                }
            }

            public void UpdatePlayerRotationFullServerRpc(Vector3 playerEulers, Vector3 cameraRotation, bool syncingCameraRotation, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                try
                {
                    if (!allowedMovement[instance.playerClientId])
                    {
                        return;
                    }
                    instance.UpdatePlayerRotationFullServerRpc(playerEulers, cameraRotation, syncingCameraRotation);
                }
                catch
                {
                    instance.UpdatePlayerRotationFullServerRpc(playerEulers, cameraRotation, syncingCameraRotation);
                }
            }

            public void UpdatePlayerAnimationServerRpc(int animationState, float animationSpeed, PlayerControllerB instance, ServerRpcParams serverRpcParams)
            {
                try
                {
                    if (!allowedMovement[instance.playerClientId])
                    {
                        instance.UpdatePlayerAnimationServerRpc(-1437577361/*Standing Still Anim Hash*/, -1f);
                        return;
                    }

                    instance.UpdatePlayerAnimationServerRpc(animationState, animationSpeed);
                }
                catch
                {
                    instance.UpdatePlayerAnimationServerRpc(animationState, animationSpeed);
                }
            }

            public void UpdateUsedByPlayerServerRpc(int playerNum, InteractTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdateUsedByPlayerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.UpdateUsedByPlayerServerRpc(playerNum);
                    return;
                }

                if (playerNum != SenderPlayerId)
                {
                    Log.LogWarning($"[UpdateUsedByPlayerServerRpc] playerNum ({playerNum}) != SenderPlayerId ({SenderPlayerId})");
                    return;
                }

                instance.UpdateUsedByPlayerServerRpc(playerNum);
            }

            public void StopUsingServerRpc(int playerUsing, InteractTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[StopUsingServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.StopUsingServerRpc(playerUsing);
                    return;
                }

                if (playerUsing != SenderPlayerId)
                {
                    Log.LogWarning($"[StopUsingServerRpc] playerUsing ({playerUsing}) != SenderPlayerId ({SenderPlayerId})");
                    return;
                }

                instance.StopUsingServerRpc(playerUsing);
            }

            public void UpdateAnimServerRpc(bool setBool, bool playSecondaryAudios, int playerWhoTriggered, AnimatedObjectTrigger instance, ServerRpcParams serverRpcParams)
            {
                Transform interactableTransfrom = instance.transform;
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdateAnimServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.UpdateAnimServerRpc(setBool, playSecondaryAudios, playerWhoTriggered);
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

                if (instance.triggerAnimator.name.StartsWith("GarageDoorContainer"))
                {
                    interactableTransfrom = instance.transform.Find("LeverSwitchContainer");
                }

                float distanceToObject = Vector3.Distance(instance.transform.position, StartOfRound.Instance.allPlayerScripts[SenderPlayerId].transform.position);
                if (Vector3.Distance(interactableTransfrom.position, player.transform.position) > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to interact with ({instance.triggerAnimator.name}) from too far away ({distanceToObject})");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.UpdateAnimClientRpc(instance.boolValue, playSecondaryAudios, playerWhoTriggered, instance, clientRpcParams);
                    return;
                }

                instance.UpdateAnimServerRpc(setBool, playSecondaryAudios, playerWhoTriggered);
            }

            public void UpdateAnimTriggerServerRpc(AnimatedObjectTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[UpdateAnimTriggerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                instance.UpdateAnimTriggerServerRpc();
            }

            public void SyncAllPlayerLevelsServerRpc(int newPlayerLevel, int playerClientId, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncAllPlayerLevelsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[StartGameServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (startGameOnCoolown.TryGetValue(SenderPlayerId, out bool startGameCalledOnCooldown) && startGameCalledOnCooldown == true)
                {
                    Instance.StartCoroutine(StartGameCooldown(SenderPlayerId));
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
                if (configLimitShipLeverDistance.Value > 1f && distanceToLever > configLimitShipLeverDistance.Value)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to start the game while too far away ({distanceToLever}).");
                    return;
                }

                instance.StartGameServerRpc();
            }

            public void PlayLeverPullEffectsServerRpc(bool leverPulled, StartMatchLever instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PlayLeverPullEffectsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (shipLeverAnimationOnCooldown.TryGetValue(SenderPlayerId, out bool leverAnimationPlaying) && leverAnimationPlaying == true)
                {
                    Instance.StartCoroutine(ShipLeverAnimationCooldown(SenderPlayerId));
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (player.isPlayerDead)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to pull ship lever while they are dead on the server.");
                    return;
                }

                StartMatchLever lever = FindFirstObjectByType<StartMatchLever>();
                float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                if (configLimitShipLeverDistance.Value > 1f && distanceToLever > configLimitShipLeverDistance.Value)
                {
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.PlayLeverPullEffectsClientRpc(leverPulled, instance, clientRpcParams);
                    return;
                }

                instance.PlayLeverPullEffectsServerRpc(leverPulled);
            }

            public void SyncAlreadyHeldObjectsServerRpc(int joiningClientId, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncAlreadyHeldObjectsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                instance.SyncAlreadyHeldObjectsServerRpc(joiningClientId);
            }

            public void SyncShipUnlockablesServerRpc(StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SyncShipUnlockablesServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                            Log.LogError($"PlaceableShipObject #{placeableShipObjects[i].unlockableID} ({placeableShipObjects[i].name}) parent is null. Crash Prevented.");
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

            public void CheckAnimationGrabPlayerServerRpc(int monsterAnimationID, int playerID, DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[CheckAnimationGrabPlayerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
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
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[AttackPlayersServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (senderClientId != 0)
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
                if (itemOnCooldown.Contains(instance.NetworkObjectId))
                {
                    return;
                }

                if (instance.TryGetComponent(out RemoteProp _) || instance.TryGetComponent(out NoisemakerProp _))
                {
                    Instance.StartCoroutine(ActivateItemCooldown(instance.NetworkObjectId));
                }

                instance.ActivateItemServerRpc(onOff, buttonDown);
            }

            public void SetMagnetOnServerRpc(bool on, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetMagnetOnServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float magnetDistance = Vector3.Distance(player.transform.position, instance.magnetLever.transform.position);
                if (magnetDistance > 5f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to toggle magnet from to far away. ({magnetDistance})");
                }

                instance.SetMagnetOnServerRpc(on);
            }

            public void BuyVehicleServerRpc(int vehicleID, int newGroupCredits, bool useWarranty, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[BuyVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (senderClientId == 0)
                {
                    instance.BuyVehicleServerRpc(vehicleID, newGroupCredits, useWarranty);
                    return;
                }

                try
                {
                    int cost = instance.buyableVehicles[vehicleID].creditsWorth;
                    int spent = instance.groupCredits - newGroupCredits;
                    if (cost != spent && !instance.hasWarrantyTicket)
                    {
                        Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) credits spent does not equal cost of vehicle. Current credits: {instance.groupCredits} Vehicle cost: {cost} Spent: {spent}.");
                        return;
                    }
                }
                catch (IndexOutOfRangeException)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to buy a vehicle that is not in the buyable vehicles list. (vehicleID: {vehicleID})");
                    return;
                }

                instance.BuyVehicleServerRpc(vehicleID, newGroupCredits, useWarranty);
            }

            public void RemoveKeyFromIgnitionServerRpc(int driverId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[RemoveKeyFromIgnitionServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (driverId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing RemoveKeyFromIgnitionServerRpc on another player. ({driverId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to remove key from the ignition from too far away. ({vehicleDistance})");
                    return;
                }

                instance.RemoveKeyFromIgnitionServerRpc(SenderPlayerId);
            }

            public void RevCarServerRpc(int driverId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[RevCarServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (driverId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing RevCarServerRpc on another player. ({driverId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to rev car engine from too far away. ({vehicleDistance})");
                    return;
                }

                instance.RevCarServerRpc(SenderPlayerId);
            }

            public void StartIgnitionServerRpc(int driverId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[StartIgnitionServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (driverId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing StartIgnitionServerRpc on another player. ({driverId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to start car ignition from too far away. ({vehicleDistance})");
                    return;
                }

                instance.StartIgnitionServerRpc(SenderPlayerId);
            }

            public void CancelTryIgnitionServerRpc(int driverId, bool setKeyInSlot, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[CancelTryIgnitionServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (driverId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing CancelTryIgnitionServerRpc on another player. ({driverId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to cancel starting car ignition from too far away. ({vehicleDistance})");
                    return;
                }

                instance.CancelTryIgnitionServerRpc(driverId, instance.keyIsInIgnition);
            }

            public void PassengerLeaveVehicleServerRpc(int playerId, Vector3 exitPoint, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PassengerLeaveVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing PassengerLeaveVehicleServerRpc on another player. ({playerId})");
                    return;
                }

                instance.PassengerLeaveVehicleServerRpc(SenderPlayerId, exitPoint);
            }

            public void SetPlayerInControlOfVehicleServerRpc(int playerId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetPlayerInControlOfVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing SetPlayerInControlOfVehicleServerRpc on another player. ({playerId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried take control of vehicle from too far away. ({vehicleDistance})");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.CancelPlayerInControlOfVehicleClientRpc(SenderPlayerId, instance, clientRpcParams);
                    return;
                }

                instance.SetPlayerInControlOfVehicleServerRpc(SenderPlayerId);
            }

            public void RemovePlayerControlOfVehicleServerRpc(int playerId, Vector3 carLocation, Quaternion carRotation, bool setKeyInIgnition, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[RemovePlayerControlOfVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing RemovePlayerControlOfVehicleServerRpc on another player. ({playerId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogDebug($"Player #{SenderPlayerId} ({player.playerUsername}) tried remove control of vehicle from too far away. ({vehicleDistance})");
                }

                float syncDistance = Vector3.Distance(instance.transform.position, carLocation);
                if (syncDistance > 10f)
                {
                    Log.LogDebug($"Player #{SenderPlayerId} ({player.playerUsername}) sent a location that is too far away. ({syncDistance})");
                }

                instance.RemovePlayerControlOfVehicleServerRpc(playerId, carLocation, carRotation, setKeyInIgnition);
            }

            public void ShiftToGearServerRpc(int setGear, int playerId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ShiftToGearServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing ShiftToGearServerRpc on another player. ({playerId})");
                    return;
                }

                instance.ShiftToGearServerRpc(setGear, SenderPlayerId);
            }

            public void SetHonkServerRpc(bool honk, int playerId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetHonkServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (playerId != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing SetHonkServerRpc on another player. ({playerId})");
                    return;
                }

                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to honk vehicle horn from too far away. ({vehicleDistance})");
                    return;
                }

                instance.SetHonkServerRpc(honk, SenderPlayerId);
            }

            public void SetRadioStationServerRpc(int radioStation, int signalQuality, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetRadioStationServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to set vehicle radio station from too far away. ({vehicleDistance})");
                    return;
                }

                if (radioStation < 0 || radioStation >= instance.radioClips.Length)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to set the radio to a station that doesn't exist. (radioStation: {radioStation})");
                    return;
                }

                instance.SetRadioStationServerRpc(radioStation, signalQuality);
            }

            public void SetRadioOnServerRpc(bool on, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetRadioOnServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to turn vehicle radio on from too far away. ({vehicleDistance})");
                    return;
                }

                instance.SetRadioOnServerRpc(on);
            }

            public void CarBumpServerRpc(Vector3 vel, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (instance.OwnerClientId != senderClientId)
                {
                    return;
                }

                instance.CarBumpServerRpc(vel);
            }

            public void CarCollisionServerRpc(Vector3 vel, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (instance.OwnerClientId != senderClientId)
                {
                    return;
                }

                instance.CarCollisionServerRpc(vel);
            }

            public void DestroyCarServerRpc(int sentByClient, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[DestroyCarServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                if (sentByClient != SenderPlayerId)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried spoofing DestroyCarServerRpc on another player. ({sentByClient})");
                    return;
                }

                if (instance.OwnerClientId != senderClientId)
                {
                    return;
                }

                instance.DestroyCarServerRpc(SenderPlayerId);
            }

            public void PushTruckServerRpc(Vector3 pos, Vector3 dir, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PushTruckServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to push vehicle from too far away. ({vehicleDistance})");
                    return;
                }

                instance.PushTruckServerRpc(pos, dir);
            }

            public void PushTruckFromOwnerServerRpc(Vector3 pos, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[PushTruckFromOwnerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to push vehicle from the owner from too far away. ({vehicleDistance})");
                    return;
                }

                instance.PushTruckFromOwnerServerRpc(pos);
            }

            public void SetHoodOpenServerRpc(bool open, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SetHoodOpenServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (instance.OwnerClientId != senderClientId && vehicleDistance > 10f)
                {
                    return;
                }

                instance.SetHoodOpenServerRpc(open);
            }

            public void ToggleHeadlightsServerRpc(bool setLightsOn, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[ToggleHeadlightsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to toggle vehicle headlights from too far away. ({vehicleDistance})");
                    return;
                }

                instance.ToggleHeadlightsServerRpc(setLightsOn);
            }

            public void SpringDriverSeatServerRpc(VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int SenderPlayerId))
                {
                    Log.LogError($"[SpringDriverSeatServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[SenderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogWarning($"Player #{SenderPlayerId} ({player.playerUsername}) tried to eject driver out of vehicle from too far away. ({vehicleDistance})");
                    return;
                }

                instance.SpringDriverSeatServerRpc();
            }
        }

        public class HostFixesServerSendRpcs : NetworkBehaviour
        {
            public static HostFixesServerSendRpcs Instance = null!;
            public HostFixesServerSendRpcs()
            {
                Instance ??= this;
            }

            public void PlayLeverPullEffectsClientRpc(bool leverPulled, StartMatchLever instance, ClientRpcParams clientRpcParams = default)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(instance, [2951629574u, clientRpcParams, RpcDelivery.Reliable]);
                    bufferWriter.WriteValueSafe(in leverPulled, default);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 2951629574u, clientRpcParams, RpcDelivery.Reliable]);
                }
            }

            public void UpdateAnimClientRpc(bool setBool, bool playSecondaryAudios, int playerWhoTriggered, AnimatedObjectTrigger instance, ClientRpcParams clientRpcParams = default)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(instance, [848048148u, clientRpcParams, RpcDelivery.Reliable]);
                    bufferWriter.WriteValueSafe(in setBool, default);
                    bufferWriter.WriteValueSafe(in playSecondaryAudios, default);
                    bufferWriter.WriteValueSafe(in playerWhoTriggered, default);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 848048148u, clientRpcParams, RpcDelivery.Reliable]);
                }
            }

            public void CancelPlayerInControlOfVehicleClientRpc(int playerId, VehicleController instance, ClientRpcParams clientRpcParams)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(Instance, [1621098866u, clientRpcParams, RpcDelivery.Reliable]);
                    BytePacker.WriteValueBitPacked(bufferWriter, playerId);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 1621098866u, clientRpcParams, RpcDelivery.Reliable]);
                }
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
                        int callLocation = i;
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldloc, 12));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SyncShipUnlockablesServerRpc));

                        found = true;
                    }

                    if (codes.Count > 1000)
                    {
                        throw new Exception("Stuck in infinite loop while patching.");
                    }
                }

                if (!found)
                {
                    Log.LogError("Could not patch SyncAlreadyHeldObjectsServerRpc's manual call to SyncShipUnlockablesServerRpc");
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.BuyItemsServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SyncGroupCreditsServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PlayTerminalAudioServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.BuyShipUnlockableServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ChangeLevelServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.AddPlayerChatMessageServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.AddTextMessageServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetShipLeaveEarlyServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PlaceShipObjectServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.DespawnEnemyServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.EndGameServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PlayerLoadedServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PlayerLoadedServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class FinishedGeneratingLevelServerRpc_Transpile
            {
                [HarmonyPatch(typeof(RoundManager), "__rpc_handler_192551691")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "FinishedGeneratingLevelServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.FinishedGeneratingLevelServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch FinishedGeneratingLevelServerRpc");
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SendNewPlayerValuesServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.DamagePlayerFromOtherClientServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ShootGunServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ReloadGunEffectsServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.GrabObjectServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetShipLightsServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UseSignalTranslatorServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdatePlayerPositionServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.TeleportPlayerServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PressTeleportButtonServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.TeleportPlayerOutServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdatePlayerRotationServerRpc));
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
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_2609793477")]
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdatePlayerRotationFullServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdatePlayerAnimationServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdateUsedByPlayerServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.StopUsingServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdateAnimServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UpdateAnimTriggerServerRpc));
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
                        codes[callLocation + 1].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SyncAllPlayerLevelsServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.StartGameServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch StartGameServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class PlayLeverPullEffectsServerRpc_Transpile
            {
                [HarmonyPatch(typeof(StartMatchLever), "__rpc_handler_2406447821")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PlayLeverPullEffectsServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PlayLeverPullEffectsServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PlayLeverPullEffectsServerRpc");
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SyncAlreadyHeldObjectsServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SyncShipUnlockablesServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SyncShipUnlockablesServerRpc");
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.CheckAnimationGrabPlayerServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.AttackPlayersServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ChangeEnemyOwnerServerRpc));
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ActivateItemServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ActivateItemServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetMagnetOnServerRpc_Transpile
            {
                [HarmonyPatch(typeof(StartOfRound), "__rpc_handler_3212216718")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetMagnetOnServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetMagnetOnServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetMagnetOnServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class BuyVehicleServerRpc_Transpile
            {
                [HarmonyPatch(typeof(Terminal), "__rpc_handler_2452398197")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "BuyVehicleServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.BuyVehicleServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch BuyVehicleServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class RemoveKeyFromIgnitionServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_269855870")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "RemoveKeyFromIgnitionServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.RemoveKeyFromIgnitionServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch RemoveKeyFromIgnitionServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class RevCarServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_1319663544")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "RevCarServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.RevCarServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch RevCarServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class StartIgnitionServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2403570091")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "StartIgnitionServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.StartIgnitionServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch StartIgnitionServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class CancelTryIgnitionServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_4283235241")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "CancelTryIgnitionServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.CancelTryIgnitionServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch CancelTryIgnitionServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class PassengerLeaveVehicleServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2150817317")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PassengerLeaveVehicleServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PassengerLeaveVehicleServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PassengerLeaveVehicleServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetPlayerInControlOfVehicleServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2687785832")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetPlayerInControlOfVehicleServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetPlayerInControlOfVehicleServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetPlayerInControlOfVehicleServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class RemovePlayerControlOfVehicleServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2345405857")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "RemovePlayerControlOfVehicleServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.RemovePlayerControlOfVehicleServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch RemovePlayerControlOfVehicleServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class ShiftToGearServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_1427257619")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ShiftToGearServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ShiftToGearServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ShiftToGearServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetHonkServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_735895017")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetHonkServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetHonkServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetHonkServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetRadioStationServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_721150963")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetRadioStationServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetRadioStationServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetRadioStationServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetRadioOnServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2416589835")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetRadioOnServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetRadioOnServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetRadioOnServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class CarBumpServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2627964612")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "CarBumpServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.CarBumpServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch CarBumpServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class CarCollisionServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_1561649658")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "CarCollisionServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.CarCollisionServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch CarCollisionServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class DestroyCarServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_4012624473")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "DestroyCarServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.DestroyCarServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch DestroyCarServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class PushTruckServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_4058179333")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PushTruckServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PushTruckServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PushTruckServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class PushTruckFromOwnerServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_1326342869")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PushTruckFromOwnerServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PushTruckFromOwnerServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PushTruckFromOwnerServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetHoodOpenServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_3804995530")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetHoodOpenServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetHoodOpenServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetHoodOpenServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class ToggleHeadlightsServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_369816798")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ToggleHeadlightsServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ToggleHeadlightsServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ToggleHeadlightsServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SpringDriverSeatServerRpc_Transpile
            {
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_46143233")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SpringDriverSeatServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SpringDriverSeatServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SpringDriverSeatServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }
        }
    }
}