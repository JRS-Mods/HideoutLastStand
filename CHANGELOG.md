# Changelog

What changed in each version of Tushonka: Hideout Last Stand, newest first. How everything works now is in the [README](README.md).

## 0.3.0 (13 November 2026)

### Changed

- **The waves before the last are mixed, and get harder.** Every siege used to be scavs for two waves, then Black Division for the rest. Now the first wave is scavs (with a Raider among them from 25% defences), then Raiders and PMCs, then Smugglers and Rogues at 50% and up. How many attackers there are in those waves hasn't changed.
- **The last wave is a boss or Black Division.** Each invasion rolls for it by the new **Last wave** settings. Black Division now only come as that last wave, and only once your defences reach 50%: below that it's always a boss.

### New

- **Bosses.** Reshala comes with guards and scavs, Glukhar with guards and Raiders, Killa and Tagilla alone; Sanitar, Kaban, Kollontay and the Goons can come too, the tougher ones only against better defences. The banner names him.
- **A slider for Black Division and each boss.** Under Last wave in the F12 settings: each one's chance of being the last wave. They always add up to 100, so pushing one up brings the others down, keeping their sizes next to each other. 0 and he never comes.
- **Bosses have more health in co-op.** There's only ever one boss, so where a wave would be bigger for more players, he has that much more health instead.

### Fixed

- **Attackers appearing inside the hideout.** With no tunnel to come through, some of each wave waited behind the Emergency Wall's door. In a hideout built out past that wall, the far side of the door is one of your own rooms, so they stood in the middle of your hideout. They only come that way now while the door is shut, nothing else leads round to the far side, and none of you can see where they'd wait. Otherwise the whole wave comes by the front door.
- **No tunnel in a hideout with a built shooting range.** The range's turnstile cut the range, and Hideout Extensions' range extension beyond it, off from the rest of the hideout, so attackers were never given a tunnel there. In a siege the turnstile no longer stops anyone, you included.
- **Attackers running on the spot by the cultist gate.** Some of a wave could stand on one spot with a way to you and never take it, and were only moved once nobody was looking at the front door, which could be minutes. One that has a way to you and hasn't moved for 5 seconds is now walked along it a pace at a time until he's going.
- **Attackers' voices between waves.** The ones waiting furthest up the stairs past the cultist gate could still be heard talking, and so could any voice started another way (by another mod's bots, or sent from another player's game). All of them are silent until the wave starts now.
- **No countdown to the next invasion on starting the game.** It only appeared once you'd been to another screen and come back to the main menu.

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
