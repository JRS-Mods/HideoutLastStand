# Changelog

What changed in each version of Tushonka: Hideout Last Stand, newest first. How everything works now is in the [README](README.md).

## 0.2.0 (13 November 2026)

### Changed

- **A stash box replaces your stash in the siege.** The Stash station no longer opens your real stash. It opens an empty box, 10 by 6, and what you put in it goes to your real stash afterwards (by message if the stash is full). You fight with what you brought and what's on your racks and mannequins. The box's size is `StashBoxColumns` and `StashBoxRows` in the server mod's `config.json`.
- **Stash boxes are only kept if the hideout holds.** If you go down but the others hold out, what's in your box is still yours. If everyone dies, the attackers have every box. Before, what you'd put away was kept even on a defeat.
- **On a defeat they search your weapon racks and mannequins as well as your stash.** They take the most valuable things wherever they are. How many they take hasn't changed (2 to 6, by threat level and Security), and money and cases with things in them are still left alone.
- **HollywoodFX and HollywoodGraphics now run in the siege** as in any raid: impacts, muzzle effects, blood, explosions and the bloom. In 0.1.3 they were kept out of the siege to stop the black screen.
- **Attackers aren't hurt by their own breaching charges.**

### New

- **Every player has their own stash box in co-op.** Same station, same **Open stash**, and each player's box goes to their own stash. Before, only the host could use the stash.
- **In co-op, the hideout belongs to whoever started the defence.** If they die they lose what they carried, and get the theft, the wrecked station and the injuries. A friend who dies helping loses what they carried and comes round badly hurt, but their own stash and hideout aren't touched, and the result doesn't count towards their own invasions. Before, a friend who died was robbed and had their own hideout wrecked too.
- **Ask before you take.** In co-op, the hideout's gear on the racks and mannequins belongs to its owner, and the gear each player came in with belongs to that player. Looking at another player's gear offers **Ask ... for this** in place of Take. They see who's asking and for what, and press F7 to allow or F8 to decline (both can be changed in the F12 settings). With no answer in 20 seconds you can ask again, and once the owner is dead anyone can take it.
- **Bigger waves in co-op.** Each wave is 50% bigger for every player after the first, and one more attacker comes at a time for each extra player, up to 10. The host's **Bigger waves per extra player (%)** setting decides; 0 turns it off.
- **Breaching blasts look the part.** A jet of dust, bits of the door or wall bouncing across the floor, sparks, and smoke that hangs in the doorway.
- **The tunnel looks dug.** Lumpy earth walls, timber frames, fallen stones and a bare bulb, with rubble thrown into the room when the wall goes.
- **The charge on the door ticks.** You can hear its timer from close by, through the door.

### Fixed

- **You could hear attackers before the wave.** The ones waiting past the cultist gate or in the tunnel are now silent until they come in.
- **Sounds during the security camera.** The game is silent while the camera's picture is up, so you no longer hear your own gear being put on the racks or feet on the stairs.
- **Cutting the power put out your own flashlight.** Lights on guns and helmets aren't on the hideout's power.
- **Being turned back at the foot of the stairs.** "You can't go that way" only applies round the cultist gate and the tunnel now.
- **Falling out of the hideout.** If you end up outside it (through a blown wall, or round the back of a room), you're put back where you last stood.

### Not tested yet

- A Fika headless hosting with the new stash boxes and the ask-before-taking prompt.
- The on-screen prompt and its keys: the answers were tested through the code, not by pressing them.

## 0.1.3 (12 November 2026)

### Fixed

- **Black screen with HollywoodFX.** The siege showed only the security camera's green overlay and then a black screen, the wave timer broke and the pause menu glitched. HollywoodGraphics' camera effects were kept out of the siege.

### New

- **A Fika headless can host the siege.** It defends the hideout of the player who asked for it, not its own empty one: your stations and defence rating, your walls, floor and ceiling, Hideout Extensions' rooms, Hideout Interiors' things, and your racks. Start it from the **Start the Defense** alert, then choose the headless.

## 0.1.2 (12 November 2026)

### Fixed

- **The siege wouldn't start with Tushonka Territories** ("Specified argument was out of the range of valid values"). Territories' faction spawns and kill counter now stay out of the siege. This also stops the next raid logging "Tried to start raid for kill counter with a raid already started!"

## 0.1.1 (12 November 2026)

### New

- **Your stash is in the siege.** The Stash station opened your real stash, to take things out of and put loot into. (Replaced by the stash box in 0.2.0.)
- **Defend from day one.** Without a weapon rack you keep your guns on you; without a gear stand you keep your armour, rig, backpack and helmet on.
- **Option to hide announcements.** F12, Siege, **Announcements** turns off the big messages like THEY'RE BREACHING. The wave counter and timers stay.

### Fixed

- **Attackers standing still, running around, or stuck in the tunnel wall.** With SAIN installed, attackers walking in on the siege's orders could freeze in place.
- **ORBIT taking over attackers** and sending them off in odd directions.
- **Attackers walking off toward doors.** The siege deals with doors itself.
- **Spawn camping.** New attackers appear out of sight past the cultist room's gate instead of on the stairs, and no longer spawn inside each other.
- **Black Division fighting scavs** left over from an earlier wave.
- **The siege wouldn't start with Lots of Loot Redux** ("The given key 'suburbs' was not present").
- **No attackers with APBS** ("Map 'Suburbs' not found"). APBS treats the siege like Factory.
- **Items lost after a server restart.** Gear taken from your racks could vanish in a later siege.
- **Rack gear came out found in raid.** Things taken from your racks keep their original found-in-raid status.

## 0.1.0 (11 November 2026)

First release. Your hideout gets attacked: an alert appears on the menus, you click **Start the Defense**, and you load into a raid set in your own hideout, with your stations at their real levels. Get your guns off the racks and your gear off the mannequins, then hold out as they blow the door and come through in waves: scavs first, then Black Division from wave 3. They cut the power, they come through walls, and if you go down they raid your stash and wreck part of your hideout.
