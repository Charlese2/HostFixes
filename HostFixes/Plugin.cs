using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using LobbyCompatibility.Enums;
using LobbyCompatibility.Features;
using Steamworks;
using Steamworks.Data;
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
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static List<ulong> votedToLeaveEarlyPlayers = [];
        internal static Dictionary<int, int> moons = [];
        internal static Dictionary<int, int> vehicleCosts = [];
        internal static Dictionary<int, int> unlockablePrices = [];
        internal static Dictionary<ulong, string> playerSteamNames = [];
        internal static Dictionary<ulong, Vector3> playerPositions = [];
        internal static Dictionary<ulong, bool> allowedMovement = [];
        internal static Dictionary<ulong, bool> onShip = [];
        internal static Dictionary<ulong, float> positionCacheUpdateTime = [];
        internal static bool terminalSoundPlaying;

        public static ConfigEntry<int> configMinimumVotesToLeaveEarly = null!;
        public static ConfigEntry<bool> configDisablePvpInShip = null!;
        public static ConfigEntry<bool> configLogSignalTranslatorMessages = null!;
        public static ConfigEntry<bool> configLogPvp = null!;
        public static ConfigEntry<bool> configLogShipObjects = null!;
        public static ConfigEntry<bool> configCheckPrices = null!;
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
        private static HashSet<ShipTeleporter> pressTeleportButtonOnCooldown = [];

        public static Plugin Instance { get; private set; } = null!;
        public static Dictionary<ulong, uint> SteamIdtoConnectionIdMap { get; private set; } = [];
        public static Dictionary<uint, ulong> ConnectionIdtoSteamIdMap { get; private set; } = [];
        public static Dictionary<ulong, ulong> SteamIdtoClientIdMap { get; private set; } = [];
        public static Dictionary<ulong, ulong> ClientIdToSteamIdMap { get; private set; } = [];

        private static readonly MethodInfo BeginSendClientRpc = typeof(NetworkBehaviour).GetMethod(
                "__beginSendClientRpc",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
        private static readonly MethodInfo EndSendClientRpc = typeof(NetworkBehaviour).GetMethod(
                "__endSendClientRpc",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

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
            configMinimumVotesToLeaveEarly = Config.Bind("General", "Minimum Votes To Leave Early", 1,
                "Minimum number of votes needed for the ship to leave early. Still requires that all the dead players have voted to leave.");
            configDisablePvpInShip = Config.Bind("General", "Disable PvP inside the ship", false,
                "If a player is inside the ship, they can't be damaged by other players.");
            configLogSignalTranslatorMessages = Config.Bind("Logging", "Log Signal Translator Messages", false,
                "Log messages that players send on the signal translator.");
            configLogPvp = Config.Bind("Logging", "Log PvP damage", false,
                "Log when a player damages another player.");
            configLogShipObjects = Config.Bind("Logging", "Log movement of ship furniture.", false,
                "Log when a player moves ship unlockables.");
            configCheckPrices = Config.Bind("General", "Check prices", false,
                "Check if the price on the terminal matches what is sent by the client.");
            configExperimentalChanges = Config.Bind("Experimental", "Experimental Changes.", false,
                "Enable experimental changes that may trigger on legitimate players (Requires more testing)");
            configExperimentalPositionCheck = Config.Bind("Experimental", "Experimental Position Checks.", false,
                "Enable experimental checks to prevent extreme client teleporting (Requires more testing)");
            configShipObjectRotationCheck = Config.Bind("General", "Check ship object rotation", true,
                "Only allow ship objects to be placed if the they are still upright.");
            configLimitGrabDistance = Config.Bind("General", "Limit grab distance", false,
                "Limit the grab distance to twice of the hosts grab distance. Defaulted to off because of grabbable desync.");
            configLimitShipLeverDistance = Config.Bind("General", "Limit ship lever distance", 5,
                "Limit distance that someone can pull the ship lever from. 0 to disable.");
            configLimitTeleporterButtonDistance = Config.Bind("General", "Limit teleporter button distance", 5,
                "Limit distance that someone can press the teleporter buttton from. 0 to disable.");

            Harmony harmony = new(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            SteamMatchmaking.OnLobbyCreated += ConnectionEvents.LobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined += ConnectionEvents.ConnectionAttempt;
            SteamMatchmaking.OnLobbyMemberLeave += ConnectionEvents.ConnectionCleanup;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("BMX.LobbyCompatibility"))
            {
                LobbyCompatibility();
            }
            InvokeRepeating(nameof(UpdatePlayerPositionCache), 0f, 1f);
            new HostFixesServerSendRpcs();
            Log.LogMessage($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} is loaded!");
        }

        private void LobbyCompatibility()
        {
            PluginHelper.RegisterPlugin(MyPluginInfo.PLUGIN_GUID, System.Version.Parse(MyPluginInfo.PLUGIN_VERSION), CompatibilityLevel.ServerOnly, VersionStrictness.None);
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
            unlockablePrices = terminal.terminalNodes.allKeywords
                .First(keyword => keyword.name == "Buy").compatibleNouns
                .Where(item => item.result.shipUnlockableID != -1 && item.result.itemCost != -1)
                .ToDictionary(
                    item => item.result.shipUnlockableID,
                    item => item.result.terminalOptions.First(option => option.noun.name == "Confirm").result.itemCost);
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

        private static IEnumerator PressTeleportButtonCooldown(ShipTeleporter instance)
        {
            pressTeleportButtonOnCooldown.Add(instance);
            yield return new WaitForSeconds(0.65833f);
            pressTeleportButtonOnCooldown.Remove(instance);
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
                    Log.LogWarning($"SteamId: ({member.Id.Value}) Name: ({member.Name}) is already in the playerSteamNames list.");
                }
            }

            internal static void ConnectionCleanup(Lobby _, Friend member)
            {
                if (NetworkManager.Singleton.IsHost)
                {
                    if (!GameNetworkManager.Instance.steamIdsInLobby.Remove(member.Id.Value))
                    {
                        Log.LogWarning($"({member.Id.Value}) already removed from steamIdsInLobby.");
                    }
                }
            }

            internal static void LobbyCreated(Result result, Lobby lobby)
            {
                if (result == Result.OK && !playerSteamNames.TryAdd(lobby.Owner.Id.Value, lobby.Owner.Name))
                {
                    Log.LogWarning($"Host is already in playerSteamNames.");
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
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[BuyItemsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (!configCheckPrices.Value)
                {
                    instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                int cost = 0;

                if (instance.numberOfItemsInDropship + boughtItems.Length > 12)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to buy too many items.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        instance.groupCredits,
                        instance.numberOfItemsInDropship,
                        instance.hasWarrantyTicket,
                        instance,
                        clientRpcParams
                    );
                    return;
                }

                if (newGroupCredits < 0)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried tried to set credits to a negative number while buying items.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        instance.groupCredits,
                        instance.numberOfItemsInDropship,
                        instance.hasWarrantyTicket,
                        instance,
                        clientRpcParams
                    );
                    return;
                }

                Dictionary<int, int> boughtItemsCount = boughtItems.GroupBy(item => item).ToDictionary(item => item.Key, item => item.Count());
                foreach (int item in boughtItemsCount.Keys)
                {
                    if (item < 0 || item >= instance.buyableItemsList.Length || item >= instance.itemSalesPercentages.Length)
                    {
                        Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to buy an item that was not in the host's shop. Item #{item}");
                        ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                        HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                            instance.groupCredits,
                            instance.numberOfItemsInDropship,
                            instance.hasWarrantyTicket,
                            instance,
                            clientRpcParams
                        );
                        return;
                    }

                    cost += (int)(instance.buyableItemsList[item].creditsWorth * (instance.itemSalesPercentages[item] / 100f) * boughtItemsCount[item]);
                }

                if (instance.groupCredits - cost != newGroupCredits)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) credits spent on items does not match item price. " +
                        $"Spent credits: {instance.groupCredits - cost} Cost Of items: {cost}");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        instance.groupCredits,
                        instance.numberOfItemsInDropship,
                        instance.hasWarrantyTicket,
                        instance,
                        clientRpcParams
                    );
                    return;
                }

                instance.BuyItemsServerRpc(boughtItems, newGroupCredits, numItemsInShip);
            }

            public void PlayTerminalAudioServerRpc(int clipIndex, Terminal instance, ServerRpcParams _)
            {
                if (terminalSoundPlaying) return;

                Instance.StartCoroutine(TerminalSoundCooldown());
                instance.PlayTerminalAudioServerRpc(clipIndex);
            }

            public void BuyShipUnlockableServerRpc(int unlockableID, int newGroupCreditsAmount, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[BuyShipUnlockableServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                Terminal terminal = FindObjectOfType<Terminal>();

                if (senderClientId == 0)
                {
                    instance.BuyShipUnlockableServerRpc(unlockableID, newGroupCreditsAmount);
                    return;
                }

                if (buyShipUnlockableOnCooldown) return;

                Instance.StartCoroutine(BuyShipUnlockableCooldown());

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;

                if (unlockableID < 0 || unlockableID > StartOfRound.Instance.unlockablesList.unlockables.Count)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to buy unlockable that is out of unlockables list. ({unlockableID}).");
                    return;
                }

                if (StartOfRound.Instance.unlockablesList.unlockables[unlockableID].alreadyUnlocked)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to unlock an unlockable multiple times");
                    return;
                }

                if (!unlockablePrices.TryGetValue(unlockableID, out int unlockableCost))
                {
                    Log.LogError($"Could not find price of ship unlockable #{unlockableID}");
                    return;
                }

                if (newGroupCreditsAmount < 0)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried tried to set credits to a negative number unlocking ship unlockable.");
                    return;
                }

                if (senderClientId != 0 && terminal.groupCredits - unlockableCost != newGroupCreditsAmount)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) calculated credit amount does not match sent credit amount for unlockable. " +
                        $"Current credits: {terminal.groupCredits} Unlockable cost: {unlockableCost} Sent credit Amount: {newGroupCreditsAmount}");
                    return;
                }

                if (newGroupCreditsAmount < terminal.groupCredits)
                {
                    instance.BuyShipUnlockableServerRpc(unlockableID, newGroupCreditsAmount);
                }
                else
                {
                    Log.LogInfo($"Player #{senderClientId} ({username}) attempted to increase credits while buying ship unlockables." +
                        $" Attempted credit value: {newGroupCreditsAmount} Old credit value: {terminal.groupCredits}");
                }
            }

            public void ChangeLevelServerRpc(int levelID, int newGroupCreditsAmount, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ChangeLevelServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;

                if (changeLevelCooldown.TryGetValue(senderPlayerId, out bool changedLevel) && changedLevel == true) return;

                Instance.StartCoroutine(ChangeLevelCooldown(senderPlayerId));

                if (senderClientId == 0)
                {
                    instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
                    return;
                }

                Terminal terminal = FindObjectOfType<Terminal>();

                if (StartOfRound.Instance.allPlayerScripts[senderPlayerId].isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to change the moon while they are dead on the server.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        terminal.groupCredits,
                        terminal.numberOfItemsInDropship,
                        terminal.hasWarrantyTicket,
                        terminal,
                        clientRpcParams
                    );
                    return;
                }

                if (newGroupCreditsAmount < 0)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to set credits to a negative number while changing levels.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        terminal.groupCredits,
                        terminal.numberOfItemsInDropship,
                        terminal.hasWarrantyTicket,
                        terminal,
                        clientRpcParams
                    );
                    return;
                }

                if (!configCheckPrices.Value)
                {
                    instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
                    return;
                }

                if (moons.Count == 0)
                {
                    moons = terminal.terminalNodes.allKeywords
                        .First(keyword => keyword.name == "Route").compatibleNouns
                        .GroupBy(moon => moon.noun).Select(moon => moon.First()) //Remove duplicate moons
                        .Select(moon => moon.result.terminalOptions.First(option => option.noun.name == "Confirm"))
                        .ToDictionary(option => option.result.buyRerouteToMoon, option => option.result.itemCost);
                }

                if (!moons.ContainsKey(levelID))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) sent levelID ({levelID}) that is not in the moons array.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        terminal.groupCredits,
                        terminal.numberOfItemsInDropship,
                        terminal.hasWarrantyTicket,
                        terminal,
                        clientRpcParams
                    );
                    return;
                }

                int moonCost = moons[levelID];
                if (terminal.groupCredits - moonCost != newGroupCreditsAmount)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) calculated credit amount does not match sent credit amount for moon. " +
                        $"Spent credits: {terminal.groupCredits - newGroupCreditsAmount} Moon cost: {moonCost}");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                        terminal.groupCredits,
                        terminal.numberOfItemsInDropship,
                        terminal.hasWarrantyTicket,
                        terminal,
                        clientRpcParams
                    );
                    return;
                }
                else
                {
                    if (newGroupCreditsAmount > terminal.groupCredits)
                    {
                        Log.LogInfo($"Player #{senderPlayerId} ({username}) attempted to increase credits from changing levels. " +
                            $"Attempted Credit Value: {newGroupCreditsAmount} Old Credit Value: {terminal.groupCredits}");
                        ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                        HostFixesServerSendRpcs.Instance.SyncTerminalValuesClientRpc(
                            terminal.groupCredits,
                            terminal.numberOfItemsInDropship,
                            terminal.hasWarrantyTicket,
                            terminal,
                            clientRpcParams
                        );
                        return;
                    }
                }

                instance.ChangeLevelServerRpc(levelID, newGroupCreditsAmount);
            }

            public void AddPlayerChatMessageServerRpc(string chatMessage, int playerId, HUDManager instance, ServerRpcParams serverRpcParams)
            {
                if (string.IsNullOrWhiteSpace(chatMessage))
                {
                    return;
                }

                string sanitizedChatMessage;
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[AddPlayerChatMessageServerRpc] Failed to get the playerId from senderClientId: ({senderClientId}) " +
                        $"Message: ({chatMessage})");
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;

                if (StartOfRound.Instance.allPlayerScripts[senderPlayerId].isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried sending a chat message while they are dead on the server. " +
                        $"Message: ({chatMessage})");
                    return;
                }

                if (playerId < 0 || playerId > StartOfRound.Instance.allPlayerScripts.Length)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to chat with a playerId ({playerId}) that is not a valid player. " +
                        $"Message: ({chatMessage})");
                    return;
                }

                //Replace <> from received messages with () to prevent injected Text Tags.
                sanitizedChatMessage = Regex.Replace(chatMessage, @"<(\S+?)>", "($+)");

                if (string.IsNullOrWhiteSpace(sanitizedChatMessage))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) Chat message was empty after sanitization. Original Message: ({chatMessage})");
                    return;
                }

                if (playerId == senderPlayerId)
                {
                    instance.AddPlayerChatMessageServerRpc(sanitizedChatMessage, playerId);
                }
                else
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to send message as another player #({playerId}) Message: ({chatMessage})");
                }
            }

            public void AddTextMessageServerRpc(string chatMessage, HUDManager instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[AddTextMessageServerRpc] Failed to get the playerId from senderClientId: ({senderClientId}) Message: ({chatMessage})");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                ulong steamId = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerSteamId;

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
                    chatMessage.StartsWith($"[morecompanycosmetics];{senderPlayerId};"))
                {
                    instance.AddTextMessageServerRpc(chatMessage);
                }
                else
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({steamUsername}) tried to send message as the server: ({chatMessage})");
                }
            }

            public void SetShipLeaveEarlyServerRpc(TimeOfDay instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SetShipLeaveEarlyServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (!votedToLeaveEarlyPlayers.Contains(senderClientId) && StartOfRound.Instance.allPlayerScripts[senderPlayerId].isPlayerDead)
                {
                    votedToLeaveEarlyPlayers.Add(senderClientId);
                    int neededVotes = StartOfRound.Instance.connectedPlayersAmount + 1 - StartOfRound.Instance.livingPlayers;
                    if (votedToLeaveEarlyPlayers.Count >= System.Math.Max(neededVotes, configMinimumVotesToLeaveEarly.Value))
                    {
                        instance.votesForShipToLeaveEarly = votedToLeaveEarlyPlayers.Count;
                        instance.SetShipLeaveEarlyClientRpc(instance.normalizedTimeOfDay + 0.1f, instance.votesForShipToLeaveEarly);
                    }
                    else
                    {
                        instance.votesForShipToLeaveEarly++;
                        instance.AddVoteForShipToLeaveEarlyClientRpc();
                    }
                }
                else
                {
                    string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to force the vote to leave.");
                }
            }

            public void PlaceShipObjectServerRpc(
                Vector3 newPosition,
                Vector3 newRotation,
                NetworkObjectReference objectRef,
                int playerWhoMoved,
                ShipBuildModeManager instance,
                ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[PlaceShipObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (playerMovedShipObject.TryGetValue(senderPlayerId, out bool moved) && moved == true) return;

                Instance.StartCoroutine(PlaceShipObjectCooldown(senderPlayerId));

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (playerWhoMoved != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to place a ship object while spoofing another player.");
                    return;
                }

                if (player.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to place a ship object while dead on the server.");
                    return;
                }

                GameObject gameObject = objectRef;

                if (gameObject is null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to move a ship object that doesn't exist.");
                    return;
                }

                PlaceableShipObject placeableShipObject = gameObject.GetComponentInChildren<PlaceableShipObject>();

                if (placeableShipObject is null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to move a ship object using an invalid object. ({gameObject.name})");
                }

                if (configShipObjectRotationCheck.Value)
                {
                    if (Mathf.RoundToInt(newRotation.x) != Mathf.RoundToInt(placeableShipObject.mainMesh.transform.eulerAngles.x) ||
                        Mathf.RoundToInt(newRotation.z) != Mathf.RoundToInt(placeableShipObject.mainMesh.transform.eulerAngles.z))
                    {
                        Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) " +
                            $"tried to place a ship object ({placeableShipObject.parentObject?.name}) with the wrong rotation. " +
                            $"x: ({newRotation.x}) ({placeableShipObject.mainMesh.transform.eulerAngles.x}) " +
                            $"z: ({newRotation.z}) ({placeableShipObject.mainMesh.transform.eulerAngles.z})");
                        return;
                    }
                }

                if (!StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(newPosition))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to place a ship object ouside of the ship.");
                    return;
                }

                if (senderClientId == 0) 
                {
                    instance.PlaceShipObjectServerRpc(newPosition, newRotation, objectRef, playerWhoMoved);
                    return;
                }

                if (placeableShipObject?.parentObject?.GetType() == typeof(ShipTeleporter) &&
                    placeableShipObject.parentObject.TryGetComponent(out ShipTeleporter teleporter) &&
                    teleporter.isInverseTeleporter &&
                    pressTeleportButtonOnCooldown.Contains(teleporter))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) moved inverse teleporter while it is currently teleporting.");
                }

                if (configLogShipObjects.Value)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) moved ship object. ({placeableShipObject?.parentObject?.name})");
                }

                instance.PlaceShipObjectServerRpc(newPosition, newRotation, objectRef, playerWhoMoved);
            }

            public void DespawnEnemyServerRpc(NetworkObjectReference enemyNetworkObject, RoundManager instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[DespawnEnemyServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                NetworkObject networkObject = enemyNetworkObject;

                GameObject gameObject = enemyNetworkObject;

                if (senderClientId == 0)
                {
                    instance.DespawnEnemyServerRpc(enemyNetworkObject);
                }
                else
                {
                    string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) attemped to despawn an enemy on the server: " +
                        $"{networkObject?.name} {gameObject?.name} {enemyNetworkObject.NetworkObjectId}");
                }
            }

            public void EndGameServerRpc(int playerClientId, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[EndGameServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.EndGameServerRpc(playerClientId);
                    return;
                }

                if (endGameOnCoolown.TryGetValue(senderPlayerId, out bool endGameCalledOnCooldown) && endGameCalledOnCooldown == true)
                {
                    Instance.StartCoroutine(EndGameCooldown(senderPlayerId));
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerClientId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to end the game while spoofing another player.");
                    return;
                }

                if (player.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to end the game while dead on the server.");
                    return;
                }

                StartMatchLever lever = FindFirstObjectByType<StartMatchLever>();
                float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                if (configLimitShipLeverDistance.Value > 1f && distanceToLever > configLimitShipLeverDistance.Value)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to end the game while too far away ({distanceToLever}).");
                    return;
                }

                instance.EndGameServerRpc(playerClientId);
            }

            public void PlayerLoadedServerRpc(ulong clientId, StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId == 0)
                {
                    instance.PlayerLoadedServerRpc(clientId);
                    return;
                }

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[PlayerLoadedServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (clientId != senderClientId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to call PlayerLoadedServerRpc for another client.");
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

                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[FinishedGeneratingLevelServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (clientId != senderClientId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to call FinishedGeneratingLevelServerRpc for another client.");
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
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SendNewPlayerValuesServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (instance.actualClientId != senderClientId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) " +
                        $"tried to call SendNewPlayerValuesServerRpc with input value ({newPlayerSteamId}) " +
                        $"on player #{instance.playerClientId} ({instance.playerUsername}).");
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

                if (instance.OwnerClientId != senderClientId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) SteamId:({senderSteamId}) sent SendNewPlayerValuesServerRpc for the wrong player.");
                    return;
                }

                if (senderSteamId != newPlayerSteamId)
                {
                    Log.LogInfo($"Client sent incorrect steamId. Player's steamId: ({senderSteamId}) Sent steamId: ({newPlayerSteamId})");
                }

                instance.SendNewPlayerValuesServerRpc(senderSteamId);
            }

            public void DamagePlayerFromOtherClientServerRpc(
                int damageAmount,
                Vector3 hitDirection,
                int playerWhoHit,
                PlayerControllerB instance,
                ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[DamagePlayerFromOtherClientServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                PlayerControllerB sendingPlayer = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (senderClientId == 0 && playerWhoHit == -1) //Lethal Escape compatibility
                {
                    instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
                    return;
                }

                if (damagePlayerFromOtherClientOnCooldown.TryGetValue(senderPlayerId, out bool onCooldown) && onCooldown == true) return;

                instance.StartCoroutine(DamageOtherPlayerCooldown(senderPlayerId));

                if (playerWhoHit != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to spoof ({damageAmount}) damage from player #{playerWhoHit} " +
                        $"on ({instance.playerUsername}).");
                    return;
                }

                if (sendingPlayer.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to damage ({instance.playerUsername}) while they are dead on the server.");
                    return;
                }

                if (Vector3.Distance(sendingPlayer.transform.position, instance.transform.position) > 10f)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to damage ({instance.playerUsername}) from too far away.");
                    return;
                }

                bool shovelHitForceIsUnmodified = FindFirstObjectByType<Shovel>(FindObjectsInactive.Include)?.shovelHitForce == 1;

                if (shovelHitForceIsUnmodified && damageAmount > 20)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to damage ({instance.playerUsername}) for extra damage ({damageAmount})");
                    return;
                }

                if (configDisablePvpInShip.Value && StartOfRound.Instance.shipInnerRoomBounds.bounds.Contains(instance.transform.position))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to damage ({instance.playerUsername}) inside the ship.");
                    return;
                }

                if (configLogPvp.Value) Log.LogInfo($"Player #{senderPlayerId} ({username}) damaged ({instance.playerUsername}) for ({damageAmount}) damage.");

                instance.DamagePlayerFromOtherClientServerRpc(damageAmount, hitDirection, playerWhoHit);
            }

            public void ShootGunServerRpc(Vector3 shotgunPosition, Vector3 shotgunForward, ShotgunItem instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ShootGunServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (Vector3.Distance(instance.transform.position, shotgunPosition) > player.grabDistance + 7)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to shoot shotgun from too far away from shotgun position.");
                    return;
                }

                if (instance.shellsLoaded < 1)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to shoot shotgun with no ammo.");
                    return;
                }

                instance.ShootGunClientRpc(shotgunPosition, shotgunForward);
            }

            public void ReloadGunEffectsServerRpc(bool start, ShotgunItem instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ReloadGunEffectsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (reloadGunEffectsOnCooldown.TryGetValue(senderPlayerId, out bool reloading) && reloading == true) return;

                Instance.StartCoroutine(ReloadGunEffectsCooldown(senderPlayerId));

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
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[GrabObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                PlayerControllerB sendingPlayer = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (sendingPlayer.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to pickup an object while they are dead on the server.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.GrabObjectClientRpc(false, grabbedObject, instance, clientRpcParams);
                    return;
                }

                GameObject grabbedGameObject = grabbedObject;

                if (grabbedGameObject == null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) sent a network object that does not exist. ({grabbedObject.NetworkObjectId})");
                    return;
                }

                if (!configLimitGrabDistance.Value)
                {
                    instance.GrabObjectServerRpc(grabbedObject);
                    return;
                }

                float distanceToObject = Vector3.Distance(grabbedGameObject.transform.position, sendingPlayer.transform.position);
                bool isNotBody = grabbedGameObject.GetComponent<RagdollGrabbableObject>() is null;

                if (instance.isInHangarShipRoom && grabbedGameObject.TryGetComponent(out GrabbableObject grabbableObject) == true && grabbableObject.isInShipRoom)
                {
                    instance.GrabObjectServerRpc(grabbedObject);
                    return;
                }

                if (distanceToObject > instance.grabDistance + 7 && isNotBody)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) " +
                        $"Object ({grabbedGameObject.name}) pickup distance ({distanceToObject}) is too far away. Could be desync.");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.GrabObjectClientRpc(false, grabbedObject, instance, clientRpcParams);
                    return;
                }

                instance.GrabObjectServerRpc(grabbedObject);
            }

            public void ThrowObjectServerRpc(
                NetworkObjectReference grabbedObject,
                bool droppedInElevator,
                bool droppedInShipRoom,
                Vector3 targetFloorPosition,
                int floorYRot,
                PlayerControllerB instance,
                ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ThrowObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (!configExperimentalChanges.Value)
                {
                    instance.ThrowObjectServerRpc(grabbedObject, droppedInElevator, droppedInShipRoom, targetFloorPosition, floorYRot);
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;

                GameObject thrownObject = grabbedObject;

                if (thrownObject is null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to throw an object that doesn't exist. ({grabbedObject.m_NetworkObjectId})");
                    return;
                }

                if (thrownObject.TryGetComponent<StunGrenadeItem>(out _))
                {
                    instance.ThrowObjectServerRpc(grabbedObject, droppedInElevator, droppedInShipRoom, targetFloorPosition, floorYRot);
                    return;
                }

                if (!thrownObject.TryGetComponent(out GrabbableObject _))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to throw an object that isn't a GrabbleObject. ({thrownObject.name})");
                    return;
                }

                Vector3 placeLocalPosition;
                Vector3 targetFloorWorldPosition;

                if (droppedInElevator)
                {
                    targetFloorWorldPosition = instance.playersManager.elevatorTransform.TransformPoint(targetFloorPosition);
                    placeLocalPosition = instance.playersManager.elevatorTransform.InverseTransformPoint(thrownObject.transform.position);
                }
                else
                {
                    targetFloorWorldPosition = instance.playersManager.propsContainer.TransformPoint(targetFloorPosition);
                    placeLocalPosition = instance.playersManager.propsContainer.InverseTransformPoint(thrownObject.transform.position);
                }

                float throwDistance = Vector3.Distance(
                    new Vector3(instance.transform.position.x, instance.transform.position.z, 0f),
                    new Vector3(targetFloorWorldPosition.x, targetFloorWorldPosition.z, 0f)
                );

                if (throwDistance > instance.grabDistance + 7)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) threw an object to far away. ({throwDistance}) ({thrownObject.name})");
                    instance.ThrowObjectServerRpc(grabbedObject, instance.isInElevator, instance.isInHangarShipRoom, placeLocalPosition, floorYRot);
                    return;
                }

                instance.ThrowObjectServerRpc(grabbedObject, droppedInElevator, droppedInShipRoom, targetFloorPosition, floorYRot);
            }

            public void PlaceObjectServerRpc(
                NetworkObjectReference grabbedObject,
                NetworkObjectReference parentObject,
                Vector3 placePositionOffset,
                bool matchRotationOfParent,
                PlayerControllerB instance,
                ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ThrowObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (!configExperimentalChanges.Value)
                {
                    instance.PlaceObjectServerRpc(grabbedObject, parentObject, placePositionOffset, matchRotationOfParent);
                    return;
                }

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;

                GameObject grabbedItem = grabbedObject;

                if (grabbedItem == null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) sent a grabbed item that doesn't exist ({grabbedObject.NetworkObjectId})");
                    return;
                }

                GameObject itemParent = parentObject;

                if (itemParent == null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) sent a parent object that doesn't exist ({parentObject.NetworkObjectId})");
                    return;
                }

                float placeDistance = Vector3.Distance(instance.transform.position, itemParent.transform.position);

                if (placeDistance > instance.grabDistance + 7)
                {
                    Vector3 placeLocalPosition;

                    if (instance.isInElevator)
                    {
                        placeLocalPosition = instance.playersManager.elevatorTransform.InverseTransformPoint(grabbedItem.transform.position);
                    }
                    else
                    {
                        placeLocalPosition = instance.playersManager.propsContainer.InverseTransformPoint(grabbedItem.transform.position);
                    }

                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to place an object to far away. ({placeDistance})");
                    instance.ThrowObjectServerRpc(
                        grabbedObject,
                        instance.isInElevator,
                        instance.isInHangarShipRoom,
                        placeLocalPosition,
                        (int)instance.transform.localEulerAngles.y
                    );
                    return;
                }


                instance.PlaceObjectServerRpc(grabbedObject, parentObject, placePositionOffset, matchRotationOfParent);
            }

            public void AddObjectToDeskServerRpc(NetworkObjectReference grabbableObjectNetObject, DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ThrowObjectServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (!configExperimentalChanges.Value)
                {
                    instance.AddObjectToDeskServerRpc(grabbableObjectNetObject);
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                string username = player.playerUsername;
                GameObject grabbableObject = grabbableObjectNetObject;

                if (grabbableObject is null)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) sent a grabbable object that doesn't exist. ({grabbableObjectNetObject.NetworkObjectId})");
                    return;
                }

                float deskDistance = Vector3.Distance(player.transform.position, instance.deskObjectsContainer.transform.position);
                if (deskDistance > player.grabDistance + 7)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) put item on desk too far away. ({deskDistance}) " +
                        $"{grabbableObject.GetComponent<GrabbableObject>()?.name}");
                    return;
                }
                instance.AddObjectToDeskServerRpc(grabbableObjectNetObject);
            }

            public void SetTimesHeardNoiseServerRpc(float valueChange, DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId != 0)
                {
                    return;
                }

                instance.SetTimesHeardNoiseServerRpc(valueChange * (StartOfRound.Instance.connectedPlayersAmount + 1));
            }

            public void SetShipLightsServerRpc(bool setLightsOn, ShipLights instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
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

                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;
                PlayerControllerB sendingPlayer = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (sendingPlayer.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to toggle ship lights while they are dead on the server.");
                    return;
                }

                instance.SetShipLightsServerRpc(setLightsOn);
            }

            public void UseSignalTranslatorServerRpc(string signalMessage, HUDManager instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[UseSignalTranslatorServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }
                string username = StartOfRound.Instance.allPlayerScripts[senderPlayerId].playerUsername;

                if (StartOfRound.Instance.allPlayerScripts[senderPlayerId].isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) tried to send a Signal Translator message while they are dead on the server." +
                        $" Message: ({signalMessage})");
                    return;
                }

                if (configLogSignalTranslatorMessages.Value)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({username}) sent signal translator message: ({signalMessage})");
                }
                instance.UseSignalTranslatorServerRpc(signalMessage);
            }

            public void TeleportPlayerServerRpc(int playerObj, EntranceTeleport instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[TeleportPlayerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.TeleportPlayerServerRpc(playerObj);
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (playerObj != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to teleport another player using an entrance teleport)");
                    return;
                }

                float distanceFromDoor = Vector3.Distance(instance.entrancePoint.position, player.transform.position);

                if (distanceFromDoor > 10f)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) too far away from entrance to teleport ({distanceFromDoor})");
                    return;
                }

                Transform exitPoint = instance.exitPoint;

                if (exitPoint == null)
                {
                    instance.FindExitPoint();
                    exitPoint = instance.exitPoint;
                }

                playerPositions[(ulong)senderPlayerId] = exitPoint.position;
                instance.TeleportPlayerServerRpc(playerObj);
            }

            public void PressTeleportButtonServerRpc(ShipTeleporter instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[PressTeleportButtonServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (pressTeleportButtonOnCooldown.Contains(instance)) return;

                Instance.StartCoroutine(PressTeleportButtonCooldown(instance));

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (player.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to press teleporter button while they are dead on the server.");
                    return;
                }

                float teleporterButtonDistance = Vector3.Distance(player.transform.position, instance.buttonTrigger.transform.position);
                if (configLimitTeleporterButtonDistance.Value > 1f && teleporterButtonDistance > configLimitTeleporterButtonDistance.Value)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to press teleporter button from too far away " +
                        $"({teleporterButtonDistance})");
                    return;
                }

                instance.PressTeleportButtonServerRpc();
            }

            public void TeleportPlayerOutServerRpc(int playerObj, Vector3 teleportPos, ShipTeleporter instance, ServerRpcParams serverRpcParams)
            {

                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[TeleportPlayerOutServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (playerObj != senderPlayerId)
                {
                    Log.LogInfo($"[TeleportPlayerOutServerRpc] playerObj ({playerObj}) != senderPlayerId ({senderPlayerId})");
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                playerPositions[player.playerClientId] = player.transform.localPosition;
                instance.TeleportPlayerOutServerRpc(playerObj, teleportPos);
            }

            public void UpdatePlayerPositionServerRpc(
                Vector3 newPos,
                bool inElevator,
                bool inShipRoom,
                bool exhausted,
                bool isPlayerGrounded,
                PlayerControllerB instance,
                ServerRpcParams _)
            {
                if (instance.isPlayerDead)
                {
                    allowedMovement[instance.playerClientId] = false;
                    return;
                }

                Vector3 position = inElevator ? instance.transform.localPosition : instance.transform.position;
                if (!onShip.TryGetValue(instance.playerClientId, out bool isOnShip) || isOnShip != inElevator)
                {
                    playerPositions[instance.playerClientId] = position;
                    positionCacheUpdateTime[instance.playerClientId] = Time.time;
                    onShip[instance.playerClientId] = inElevator;
                }

                float timeSinceLast = Time.time - positionCacheUpdateTime[instance.playerClientId];

                float downwardDotProduct = Vector3.Dot((newPos - position).normalized, Vector3.down);
                float maxDistancePerTick = instance.movementSpeed *
                    (10f / Mathf.Max(instance.carryWeight, 1.0f)) / NetworkManager.Singleton.NetworkTickSystem.TickRate;
                if (downwardDotProduct > 0.3f ||
                    StartOfRound.Instance.suckingPlayersOutOfShip ||
                    StartOfRound.Instance.inShipPhase ||
                    !configExperimentalPositionCheck.Value)
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

            public void UpdatePlayerRotationServerRpc(short newRot, short newYRot, PlayerControllerB instance, ServerRpcParams _)
            {
                if (allowedMovement.ContainsKey(instance.playerClientId) && !allowedMovement[instance.playerClientId])
                {
                    return;
                }

                instance.UpdatePlayerRotationServerRpc(newRot, newYRot);
            }

            public void UpdatePlayerRotationFullServerRpc(
                Vector3 playerEulers,
                Vector3 cameraRotation,
                bool syncingCameraRotation,
                PlayerControllerB instance,
                ServerRpcParams _)
            {
                if (allowedMovement.ContainsKey(instance.playerClientId) && !allowedMovement[instance.playerClientId])
                {
                    return;
                }

                instance.UpdatePlayerRotationFullServerRpc(playerEulers, cameraRotation, syncingCameraRotation);
            }

            public void UpdatePlayerAnimationServerRpc(int animationState, float animationSpeed, PlayerControllerB instance, ServerRpcParams _)
            {
                if (allowedMovement.ContainsKey(instance.playerClientId) && !allowedMovement[instance.playerClientId])
                {
                    for (int i = 0; i < instance.playerBodyAnimator.layerCount; i++)
                    {
                        if (instance.playerBodyAnimator.HasState(i, -1437577361/*Standing Still Anim Hash*/)) return;
                    }

                    instance.UpdatePlayerAnimationServerRpc(-1437577361/*Standing Still Anim Hash*/, -1f);
                    return;
                }

                instance.UpdatePlayerAnimationServerRpc(animationState, animationSpeed);
            }

            public void UpdateUsedByPlayerServerRpc(int playerNum, InteractTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[UpdateUsedByPlayerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.UpdateUsedByPlayerServerRpc(playerNum);
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerNum != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to call UpdateUsedByPlayerServerRpc from another player. ({playerNum})");
                    return;
                }

                instance.UpdateUsedByPlayerServerRpc(senderPlayerId);
            }

            public void StopUsingServerRpc(int playerUsing, InteractTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[StopUsingServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.StopUsingServerRpc(playerUsing);
                    return;
                }

                if (playerUsing != senderPlayerId)
                {
                    Log.LogInfo($"[StopUsingServerRpc] playerUsing ({playerUsing}) != senderPlayerId ({senderPlayerId})");
                    return;
                }

                instance.StopUsingServerRpc(playerUsing);
            }

            public void UpdateAnimServerRpc(
                bool setBool,
                bool playSecondaryAudios,
                int playerWhoTriggered,
                AnimatedObjectTrigger instance,
                ServerRpcParams serverRpcParams)
            {
                Transform interactableTransfrom = instance.transform;
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[UpdateAnimServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.UpdateAnimServerRpc(setBool, playSecondaryAudios, playerWhoTriggered);
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (playerWhoTriggered != senderPlayerId)
                {
                    Log.LogInfo($"[UpdateAnimServerRpc] " +
                        $"playerWhoTriggered ({playerWhoTriggered}) != senderPlayerId ({senderPlayerId}) ({instance.triggerAnimator.name})");
                    return;
                }

                if (player.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) " +
                        $"tried to interact with an animated object ({instance.triggerAnimator.name}) while they are dead on the server.");
                    return;
                }

                if (instance.triggerAnimator?.name.StartsWith("GarageDoorContainer") == true)
                {
                    interactableTransfrom = instance.transform.Find("LeverSwitchContainer");
                }

                float distanceToObject = Vector3.Distance(instance.transform.position, StartOfRound.Instance.allPlayerScripts[senderPlayerId].transform.position);
                if (Vector3.Distance(interactableTransfrom.position, player.transform.position) > player.grabDistance + 7)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to interact with ({instance.triggerAnimator.name}) from too far away" +
                        $" ({distanceToObject})");
                    ClientRpcParams clientRpcParams = new() { Send = new() { TargetClientIds = [senderClientId] } };
                    HostFixesServerSendRpcs.Instance.UpdateAnimClientRpc(instance.boolValue, playSecondaryAudios, playerWhoTriggered, instance, clientRpcParams);
                    return;
                }

                instance.UpdateAnimServerRpc(setBool, playSecondaryAudios, playerWhoTriggered);
            }

            public void UpdateAnimTriggerServerRpc(AnimatedObjectTrigger instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[UpdateAnimTriggerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                instance.UpdateAnimTriggerServerRpc();
            }

            public void SyncAllPlayerLevelsServerRpc(int newPlayerLevel, int playerClientId, HUDManager instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SyncAllPlayerLevelsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (playerClientId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to temporarily set another players level.");
                    return;
                }

                instance.SyncAllPlayerLevelsServerRpc(newPlayerLevel, senderPlayerId);
            }

            public void StartGameServerRpc(StartOfRound instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[StartGameServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (startGameOnCoolown.TryGetValue(senderPlayerId, out bool startGameCalledOnCooldown) && startGameCalledOnCooldown == true)
                {
                    Instance.StartCoroutine(StartGameCooldown(senderPlayerId));
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (player.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to start the game while they are dead on the server.");
                    return;
                }

                StartMatchLever lever = FindFirstObjectByType<StartMatchLever>();
                float distanceToLever = Vector3.Distance(lever.transform.position, player.transform.position);
                if (configLimitShipLeverDistance.Value > 1f && distanceToLever > configLimitShipLeverDistance.Value)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to start the game while too far away ({distanceToLever}).");
                    return;
                }

                if (GameNetworkManager.Instance.gameHasStarted == false)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to force start the game.");
                    return;
                }

                instance.StartGameServerRpc();
            }

            public void PlayLeverPullEffectsServerRpc(bool leverPulled, StartMatchLever instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[PlayLeverPullEffectsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (shipLeverAnimationOnCooldown.TryGetValue(senderPlayerId, out bool leverAnimationPlaying) && leverAnimationPlaying == true)
                {
                    Instance.StartCoroutine(ShipLeverAnimationCooldown(senderPlayerId));
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (player.isPlayerDead)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to pull ship lever while they are dead on the server.");
                    return;
                }

                float distanceToLever = Vector3.Distance(instance.transform.position, player.transform.position);
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
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SyncAlreadyHeldObjectsServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if ((ulong)joiningClientId != senderClientId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing SyncAlreadyHeldObjectsServerRpc on another player.");
                    return;
                }

                instance.SyncAlreadyHeldObjectsServerRpc((int)senderClientId);
            }

            public void CheckAnimationGrabPlayerServerRpc(int monsterAnimationID, int playerID, DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[CheckAnimationGrabPlayerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerID != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing CheckAnimationGrabPlayerServerRpc on another player.");
                    return;
                }

                instance.CheckAnimationGrabPlayerServerRpc(monsterAnimationID, playerID);
            }

            public void AttackPlayersServerRpc(DepositItemsDesk instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[AttackPlayersServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (senderClientId != 0)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried calling AttackPlayersServerRpc.");
                    return;
                }

                instance.AttackPlayersServerRpc();
            }

            public void ChangeEnemyOwnerServerRpc(ulong clientId, EnemyAI instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ChangeEnemyOwnerServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (senderClientId == 0)
                {
                    instance.ChangeEnemyOwnerServerRpc(clientId);
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (player.isPlayerDead)
                {
                    return;
                }

                instance.ChangeEnemyOwnerServerRpc(clientId);
            }

            public void ActivateItemServerRpc(bool onOff, bool buttonDown, GrabbableObject instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ActivateItemServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                if (itemOnCooldown.Contains(instance.NetworkObjectId)) return;

                if (instance.playerHeldBy is null)
                {
                    PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried activate an item that is not held by anyone.");
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
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SetMagnetOnServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                float magnetDistance = Vector3.Distance(player.transform.position, instance.magnetLever.transform.position);
                if (magnetDistance > player.grabDistance + 7)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to toggle magnet from to far away. ({magnetDistance})");
                }

                instance.SetMagnetOnServerRpc(on);
            }

            public void BuyVehicleServerRpc(int vehicleID, int newGroupCredits, bool useWarranty, Terminal instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[BuyVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (senderClientId == 0)
                {
                    instance.BuyVehicleServerRpc(vehicleID, newGroupCredits, useWarranty);
                    return;
                }

                if (!configCheckPrices.Value)
                {
                    instance.BuyVehicleServerRpc(vehicleID, newGroupCredits, useWarranty);
                    return;
                }

                if (vehicleCosts.Count == 0)
                {
                    vehicleCosts = instance.terminalNodes.allKeywords
                        .First(keyword => keyword.name == "Buy").compatibleNouns
                        .Where(noun => noun.result.buyVehicleIndex != -1)
                        .GroupBy(noun => noun.result.buyItemIndex).Select(noun => noun.First()) //Remove duplicate vehicles
                        .ToDictionary(vehicleNoun => vehicleNoun.result.buyVehicleIndex, vehicleNoun => vehicleNoun.result.itemCost);
                }

                if (newGroupCredits < 0)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried tried to set credits to a negative number.");
                    return;
                }

                if (!vehicleCosts.ContainsKey(vehicleID))
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to buy a vehicle that is not in the buyable vehicles list. " +
                        $"(vehicleID: {vehicleID})");
                    return;
                }

                int cost = (int)(vehicleCosts[vehicleID] * (instance.itemSalesPercentages[vehicleID + instance.buyableItemsList.Length] / 100f));
                int spent = instance.groupCredits - newGroupCredits;
                if (cost != spent && !instance.hasWarrantyTicket)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) credits spent does not equal cost of vehicle. " +
                        $"Current credits: {instance.groupCredits} Vehicle cost: {cost} Spent: {spent}.");
                    return;
                }

                instance.BuyVehicleServerRpc(vehicleID, newGroupCredits, useWarranty);
            }

            public void RemoveKeyFromIgnitionServerRpc(int driverId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[RemoveKeyFromIgnitionServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (driverId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing RemoveKeyFromIgnitionServerRpc on another player. ({driverId})");
                    return;
                }

                instance.RemoveKeyFromIgnitionServerRpc(senderPlayerId);
            }

            public void RevCarServerRpc(int driverId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[RevCarServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (driverId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing RevCarServerRpc on another player. ({driverId})");
                    return;
                }

                instance.RevCarServerRpc(senderPlayerId);
            }

            public void StartIgnitionServerRpc(int driverId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[StartIgnitionServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (driverId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing StartIgnitionServerRpc on another player. ({driverId})");
                    return;
                }

                instance.StartIgnitionServerRpc(senderPlayerId);
            }

            public void CancelTryIgnitionServerRpc(int driverId, bool setKeyInSlot, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[CancelTryIgnitionServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (driverId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing CancelTryIgnitionServerRpc on another player. ({driverId})");
                    return;
                }

                instance.CancelTryIgnitionServerRpc(senderPlayerId, setKeyInSlot);
            }

            public void PassengerLeaveVehicleServerRpc(int playerId, Vector3 exitPoint, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[PassengerLeaveVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing PassengerLeaveVehicleServerRpc on another player. ({playerId})");
                    return;
                }

                instance.PassengerLeaveVehicleServerRpc(senderPlayerId, exitPoint);
            }

            public void SetPlayerInControlOfVehicleServerRpc(int playerId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SetPlayerInControlOfVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing SetPlayerInControlOfVehicleServerRpc on another player. " +
                        $"({playerId})");
                    return;
                }

                instance.SetPlayerInControlOfVehicleServerRpc(senderPlayerId);
            }

            public void RemovePlayerControlOfVehicleServerRpc(
                int playerId,
                Vector3 carLocation,
                Quaternion carRotation,
                bool setKeyInIgnition,
                VehicleController instance,
                ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[RemovePlayerControlOfVehicleServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing RemovePlayerControlOfVehicleServerRpc on another player. " +
                        $"({playerId})");
                    return;
                }

                instance.RemovePlayerControlOfVehicleServerRpc(senderPlayerId, carLocation, carRotation, setKeyInIgnition);
            }

            public void ShiftToGearServerRpc(int setGear, int playerId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[ShiftToGearServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing ShiftToGearServerRpc on another player. ({playerId})");
                    return;
                }

                instance.ShiftToGearServerRpc(setGear, senderPlayerId);
            }

            public void SetHonkServerRpc(bool honk, int playerId, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SetHonkServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (playerId != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing SetHonkServerRpc on another player. ({playerId})");
                    return;
                }

                instance.SetHonkServerRpc(honk, senderPlayerId);
            }

            public void SetRadioStationServerRpc(int radioStation, int signalQuality, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SetRadioStationServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (radioStation < 0 || radioStation >= instance.radioClips.Length)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to set the radio to a station that doesn't exist. " +
                        $"(radioStation: {radioStation})");
                    return;
                }

                instance.SetRadioStationServerRpc(radioStation, signalQuality);
            }

            public void CarBumpServerRpc(Vector3 vel, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId != 0)
                {
                    return;
                }

                instance.CarBumpServerRpc(vel);
            }

            public void CarCollisionServerRpc(Vector3 vel, float magn, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;

                if (senderClientId != 0)
                {
                    return;
                }

                instance.CarCollisionServerRpc(vel, magn);
            }

            public void DestroyCarServerRpc(int sentByClient, VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[DestroyCarServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                if (sentByClient != senderPlayerId)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried spoofing DestroyCarServerRpc on another player. ({sentByClient})");
                    return;
                }

                instance.DestroyCarServerRpc(senderPlayerId);
            }

            public void SpringDriverSeatServerRpc(VehicleController instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[SpringDriverSeatServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];
                float vehicleDistance = Vector3.Distance(player.transform.position, instance.transform.position);
                if (vehicleDistance > 10f)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to eject driver out of vehicle from too far away. ({vehicleDistance})");
                    return;
                }

                instance.SpringDriverSeatServerRpc();
            }

            public void CreateMimicServerRpc(bool inFactory, Vector3 playerPositionAtDeath, HauntedMaskItem instance, ServerRpcParams serverRpcParams)
            {
                ulong senderClientId = serverRpcParams.Receive.SenderClientId;
                if (!StartOfRound.Instance.ClientPlayerList.TryGetValue(senderClientId, out int senderPlayerId))
                {
                    Log.LogError($"[CreateMimicServerRpc] Failed to get the playerId from senderClientId: {senderClientId}");
                    return;
                }

                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[senderPlayerId];

                if (player.isPlayerDead == false)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to create mimic without dying.");
                    return;
                }
                
                if (instance.previousPlayerHeldBy != player)
                {
                    Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) tried to create mimic without holding mask.");
                    return;
                }

                instance.NetworkObject.Despawn(true);

                Log.LogInfo($"Player #{senderPlayerId} ({player.playerUsername}) created a Mimic.");
                instance.CreateMimicServerRpc(inFactory, playerPositionAtDeath);
            }
        }

        public class HostFixesServerSendRpcs : NetworkBehaviour
        {
            public static HostFixesServerSendRpcs Instance = null!;
            public HostFixesServerSendRpcs()
            {
                Instance = this;
            }

            public void PlayLeverPullEffectsClientRpc(bool leverPulled, StartMatchLever instance, ClientRpcParams clientRpcParams = default)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(instance, [2951629574u, clientRpcParams, RpcDelivery.Reliable]);
                    bufferWriter.WriteValueSafe(in leverPulled);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 2951629574u, clientRpcParams, RpcDelivery.Reliable]);
                }
            }

            public void UpdateAnimClientRpc(
                bool setBool,
                bool playSecondaryAudios,
                int playerWhoTriggered,
                AnimatedObjectTrigger instance,
                ClientRpcParams clientRpcParams = default)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(instance, [848048148u, clientRpcParams, RpcDelivery.Reliable]);
                    bufferWriter.WriteValueSafe(in setBool);
                    bufferWriter.WriteValueSafe(in playSecondaryAudios);
                    BytePacker.WriteValueBitPacked(bufferWriter, playerWhoTriggered);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 848048148u, clientRpcParams, RpcDelivery.Reliable]);
                }
            }

            public void GrabObjectClientRpc(
                bool grabValidated,
                NetworkObjectReference grabbedObject,
                PlayerControllerB instance,
                ClientRpcParams clientRpcParams = default)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(instance, [2552479808u, clientRpcParams, RpcDelivery.Reliable]);
                    bufferWriter.WriteValueSafe(in grabValidated);
                    bufferWriter.WriteValueSafe(in grabbedObject);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 2552479808u, clientRpcParams, RpcDelivery.Reliable]);
                }
            }

            public void SyncTerminalValuesClientRpc(int groupCredits, int numItemsInDropship, bool vehicleWarranty, Terminal instance, ClientRpcParams clientRpcParams)
            {
                if (__rpc_exec_stage != __RpcExecStage.Client && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost))
                {
                    FastBufferWriter bufferWriter = (FastBufferWriter)BeginSendClientRpc.Invoke(instance, [1505747100u, clientRpcParams, RpcDelivery.Reliable]);
                    BytePacker.WriteValueBitPacked(bufferWriter, groupCredits);
                    BytePacker.WriteValueBitPacked(bufferWriter, numItemsInDropship);
                    bufferWriter.WriteValueSafe(in vehicleWarranty);
                    EndSendClientRpc.Invoke(instance, [bufferWriter, 1505747100u, clientRpcParams, RpcDelivery.Reliable]);
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
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PlayTerminalAudioServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.BuyShipUnlockableServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ChangeLevelServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.AddPlayerChatMessageServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.AddTextMessageServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetShipLeaveEarlyServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.DespawnEnemyServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.EndGameServerRpc));
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
            class ThrowObjectServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_2376977494")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "ThrowObjectServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.ThrowObjectServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch ThrowObjectServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class PlaceObjectServerRpc_Transpile
            {
                [HarmonyPatch(typeof(PlayerControllerB), "__rpc_handler_3830452098")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "PlaceObjectServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.PlaceObjectServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch PlaceObjectServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class AddObjectToDeskServerRpc_Transpile
            {
                [HarmonyPatch(typeof(DepositItemsDesk), "__rpc_handler_4150038830")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "AddObjectToDeskServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.AddObjectToDeskServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch AddObjectToDeskServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }

            [HarmonyPatch]
            class SetTimesHeardNoiseServerRpc_Transpile
            {
                [HarmonyPatch(typeof(DepositItemsDesk), "__rpc_handler_745684781")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "SetTimesHeardNoiseServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetTimesHeardNoiseServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch SetTimesHeardNoiseServerRpc");
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SetShipLightsServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.UseSignalTranslatorServerRpc));
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
                        codes.Insert(callLocation, new CodeInstruction(OpCodes.Ldarg_0));
                        codes.Insert(callLocation + 1, new CodeInstruction(OpCodes.Ldarg_2));
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.SyncAllPlayerLevelsServerRpc));
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
                [HarmonyPatch(typeof(VehicleController), "__rpc_handler_2778459828")]
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

            [HarmonyPatch]
            class CreateMimicServerRpc_Transpile
            {
                [HarmonyPatch(typeof(HauntedMaskItem), "__rpc_handler_1065539967")]
                [HarmonyTranspiler]
                public static IEnumerable<CodeInstruction> UseServerRpcParams(IEnumerable<CodeInstruction> instructions)
                {
                    var found = false;
                    var callLocation = -1;
                    var codes = new List<CodeInstruction>(instructions);
                    for (int i = 0; i < codes.Count; i++)
                    {
                        if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo { Name: "CreateMimicServerRpc" })
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
                        codes[callLocation + 2].operand = typeof(HostFixesServerReceiveRpcs).GetMethod(nameof(HostFixesServerReceiveRpcs.CreateMimicServerRpc));
                    }
                    else
                    {
                        Log.LogError("Could not patch CreateMimicServerRpc");
                    }

                    return codes.AsEnumerable();
                }
            }
        }
    }
}