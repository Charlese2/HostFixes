# 1.0.23
- Fixed belt bag scrap check blocking anything but scrap.
# 1.0.22
- Fixed Shotgun reload animation
# 1.0.21
- Fixed ambience clips being blocked if the host is dead.
# 1.0.20
- Added basic GUI to show the last 10 log entires on screen. (F9 to toggle)
# 1.0.19
- Added config to log moving ship objects.
- Added config to prevent picking up more than 1 two handed item.
- Added check to prevent activating items without them being held.
- Added check to prevent multiple mimics from being created from a mask.
- Added check to prevent sending signal translator messages from across the map.
- Added checks to prevent interacting with the magnet/car while in space.
- Added logging when a client tries to spoof their SteamID.
- Added ship horn checks.
- Added belt bag checks.
- Added more vehicle interaction logging.
- Added more network sound cooldowns.
- Changed logging to use less severe category.
- Fixed being able to set credits to a negative value.
# 1.0.18
- Fixed `SyncShipUnlockablesServerRpc` crash instead canceling the server rpc if it would have crashed.
# 1.0.17
- Added experimental checks to dropping items.
- Added code to register this mod in the `LobbyCompatibility` mod as server only.
- Changed buying checks to it's own config option instead of being experimental.
- Fixed item buying cost check being off if buying a lot of items.
- Fixed buying cruiser.
- Fixed catching the wrong exception on 2 checks.
# 1.0.16
- Added a check to prevent clients from force starting the game when the host has not pulled the lever at least once in a session.
- Added check to prevent clients from buying the cruiser for free.
- Added distance check to pressing the eject buttion on the vehicle.
- Fixed host item sales resetting to no sale when lobby first starts.
# 1.0.15
- Fixed moon cost values not calculating correctly when experimental changes were enabled.
- Fixed MoreCompany cosmetics getting blocked from using chat to sync again.
# 1.0.14
- Fixed paying for moons being blocked in v50 when experimental changes were enabled.
- Fixed using warehouse doors on the client being blocked due to the it checking the distance from the middle of the door instead of the door lever.
# 1.0.13
- Fixed error from trying to remove from `playerSteamNames` dictionary twice. Didn't cause an issue, it just caused an error to print into the log.
- Removed temporary fix for monitor order when Radar Booster is activated as you need it on both sides to not cause issues.
# 1.0.12
- Fixed some errors being printed because some caches were not cleared out.
# 1.0.11
- Removed initial GUI that was added too soon.
# 1.0.10
- Fixed errors preventing some actions if `HideManagerGameObject` in BepInEx config was not set to true.
# 1.0.9
- Added `NoisemakerProp` to ActivateItem cooldown.
- Added logging in `OnAwake` and `OnDestroy` in the plugin.
# 1.0.8
- Added player checks to `FinishedGeneratingLevelServerRpc`.
- Added config to set max allowed lever interaction distance.
- Added client side fix to crew monitor getting incorrectly sorted when a radar booster got activated.
- Fixed *KeyNotFoundException* happing on startup in some circumstances.
- Fixed not rejecting clients that were in the kicked list in some circumstances.
- Fixed more instances of the lever getting stuck for others when preventing lever pull.
- Changed mapping steamID to player on join instead of waiting for them to send their name.
# 1.0.7
- Fixed Lethal Escape compatibility
# 1.0.6
- Added small cooldown to `DamagePlayerFromOtherClientServerRpc`.
- Added check to prevent extra shovel damage if the host doesn't have the mod.
- Added cooldown to Remote item to prevent ship light spamming.
- Fixed adding multiple shotgun shots from a single shotgun shell.
# 1.0.5
- Added check to authenticate joining player's SteamID.
- Added a one second cooldown to the terminal sound that plays for everyone.
- Added a one second cooldown to placing ship objects per player.
- Added a half second cooldown to flipping the ship lights using the light switch.
- Added a half second cooldown Buying Ship Unlockables.
- Added a half second cooldown to pressing the teleporter button.
- Added price check to buying ship unlockables.
- Added price check to traveling to paid moons.
- Added check to prevent dead players from using chat.
- Added check to prevent placement of abnormaly rotated ship objects. (Config toggle)
- Added death and distance check to player vs player melee damage.
- Added check to prevent shooting shotgun while it is empty.
- Added check to limit grab distance to twice of the hosts grab distance. (Config toggle)
- Added check to block sending signal translator message while dead.
- Added check to prevent setting another players level temporarily.
- Added check to prevent forcing the ship to start while not at the lever.
- Added a check to prevent manualy setting the company patience while not the host.
- [Experimental] Added check to prevent extreme client teleports in one tick.
- Fixed a crash that ocured when syncing ship unlockables while on a moon with extra light switches.
# 1.0.4
- Added player checks to `AddTextMessageServerRpc`
- Added sanitization to remove Text Mesh Pro `Text Tags` from being sent in the chat.
- Added logging on Signal Translator to show who sent the message. (Config toggle)
- Added logging on player vs player damage. (Config toggle)
- Added config to disable player vs player in the ship.
- Added config to enable experimental changes that 'could' stop legitimate user actions.
- [Experimental] Calculate the value of bought items and deny the transaction if it doesn't match what the client sent.
- Removed OpenGiftBoxServerRpc checks as it may be fixed.
# 1.0.3
- Fixed host not being able to buy ship unlockables. (decor)
# 1.0.2
- Fixed More Company being blocked from using the chat to sync cosmetics.
- Ajusted this Plugin's Logging to use more accurate logging levels.
# 1.0.1
- Added player checks to `SendNewPlayerValuesServerRpc` and `DamagePlayerFromOtherClientServerRpc`.
- Removed the player loaded indicator as it would break with certain mods.
- Fixed vote to leave early UI when vote has passed.
# 1.0.0
- Initial release.