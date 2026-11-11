# Test checklist

## Before you start

1. **Update the Fika server.** The installed 2.4.0 server is too old for the 2.4.6 client, so Fika closes the game on launch. Run this once in PowerShell, with the game and server closed:

   ```powershell
   & "D:\Tushonka - Hideout Siege\tools\update-fika-server.ps1"
   ```

   If it won't run, install `Fika.Server.Release.2.4.2.zip` from the Fika-Server-CSharp releases page by hand.

2. **Log in as `siegedev`** in the launcher. Your three real profiles won't load while their mods are parked. `siegedev` is a copy of `us2` with the modded items removed, full hideout, and an AK, armour and a rig.

## What to try

- [ ] The red "HIDEOUT IS BEING INVADED" banner appears at the top of the main menu, in the same dark style as the game's menus. Hovering **Start the Defense** and **Later** lights them up, and clicking anywhere on each one works.
- [ ] When you aren't invaded, the main menu shows a small "NEXT INVASION" countdown, which turns into the banner when it reaches 0.
- [ ] In the raid, a small readout sits a little below the top of the screen: BREACH and a timer, then the wave, hostiles left and the break timer. Announcements are smaller, and pickups show a small line lower down, only on your own screen.
- [ ] **Later** hides it. It comes back after your next raid.
- [ ] **Start the Defense** goes to the raid screen showing "Your Hideout". Choose Host Raid, then Start Raid.
- [ ] The security camera shows one scav walking down to your front door, setting a charge and walking back up, with no running, sliding or stutter, without jumping back up the stairs, and never through the door or a wall. Space skips it, and the charge is still on the door.
- [ ] Nobody's inside the hideout before the door blows (no "was inside the front door" in the log), and no attacker goes through a wall or a shut door during the fight ("Kept a ... from going through a wall" in the log shows where one would have).
- [ ] The scav on the camera has his gun on his back and the D-BREACH charge in his hand, hanging naturally, not floating or through his leg.
- [ ] He kneels and it moves from his hand on to the door without changing size: the right way up, label facing out, its back flat on the door, about handle height. It doesn't sink into the door or float off it.
- [ ] He has a gun in his hands again when he comes through the door after the blast.
- [ ] A scav breaching an inner door does the same: gun away, charge on, gun back as he leaves.
- [ ] No reloading or dropping sounds before the camera comes on.
- [ ] Once it's on the door its red lamp blinks and glows.
- [ ] Starting the camera, the door blowing and each wave starting don't freeze the game. The loading screen is about 10 seconds longer to pay for this.
- [ ] You wake up by the bed, facing down the hideout, without your own body in shot, and can move, shoot and take damage from then on.
- [ ] Guns hang on the weapon racks where your real hideout shows them, against the rack and side on, with magazines flat in the space left over. You can reach and **Take** every one.
- [ ] Armour, rig, backpack and helmet are at the gear rack, and **Take** puts each one on. Nothing needs searching.
- [ ] With an unbuilt rack, those things are on the floor by the bed instead.
- [ ] Guns fire straight away if you had a loaded magazine or spare. Meds and injectors are in your pockets with their quick slots still set.
- [ ] In co-op, you can see your friend's whole body, not just arms, and they can see yours.
- [ ] The hideout stays lit until you use the generator: "Turn off the generator" appears when you look at it, and the lights go out and come back for everyone.
- [ ] With the generator off, scavs switch on the light on their gun if they have one (and off when the power's back). Scavs without a light are all but blind: you can get within a few metres. FPS holds up better than it did with the red alarm.
- [ ] Taking any item leaves you able to look around within a second, including your first gun.
- [ ] Mannequins: each stands on its stand, dressed as in your real hideout. Looking at one offers the whole loadout or each piece, and what you take comes off it.
- [ ] Magazines on the rack can be taken between waves, or swapped for your emptiest when you've no room.
- [ ] Rack gear: guns on your real weapon racks appear in the siege, with matching stash magazines. **Back up your profile before testing this one.**
- [ ] After the raid: anything left on the racks is still on your real racks. Anything you took and died with is gone. Anything you took and extracted with is on you, and gone from the racks, with no duplicate.
- [ ] Quit or crash mid-siege: nothing has gone from your racks or stash.
- [ ] Medstation and Nutrition Unit: looking at them offers the action during prep and breaks, once per break, and says why not otherwise. Health and energy really change.
- [ ] Mounted gun (Security 2+, placed with Hideout Interiors): it stands where you put it with its tripod, Operate works, it fires and hits, and leaving it gives you your own gun back. A client sees it and the shots.
- [ ] With no mounted gun placed in Hideout Interiors, there's no gun in the siege ("No mounted gun" in the log).
- [ ] Second way in (Emergency Wall door built, wave 3+): a warning, a charge, a blast, then attackers from the side you aren't on. Nobody's visibly standing frozen beforehand.
- [ ] Bullets don't go through the doors, either way.
- [ ] With Hideout Extensions: every room you've built is there, with its doors and lamps, and none you haven't. Areas you moved are where you put them, and if you moved the Rest Space you wake up by its beds.
- [ ] With Hideout Extensions: attackers can follow you into the rooms, and switching the generator off darkens the rooms' lamps as well.
- [ ] With Hideout Interiors: things you moved or hid are as you left them, and your decorations are there.
- [ ] In co-op with those mods: the other player sees the host's rooms, layout and decorations, not their own.
- [ ] No stutter between waves: the breaks run as smoothly as the fights.
- [ ] Waves 1 and 2 are scavs and wave 3 onward is Black Division, whatever the defence rating (3 to 5 waves by rating). "WAVE 3 - BLACK DIVISION" shows when they first come.
- [ ] "THEY'VE CUT THE POWER" before wave 3 and never before a scav wave. After it the generator says "They've cut the power" and won't switch back on, for the rest of the raid, even if you'd switched it off yourself first. In co-op, neither player can switch it back on. The mounted gun stops by itself while it's out.
- [ ] Hideout Interiors' decor panel has Mounted gun in its Defences tab; placed, it's where the siege's gun stands and the way it faces. A second one won't go in ("1 of 1").
- [ ] Die in a siege: the most valuable stash things are gone (fewer with a better Security), you're badly hurt (less with a better Medstation), and you can start a raid straight away once you've healed. No RECOVERING on the menu.
- [ ] After losing, through the end-of-raid screens with NEXT: after the experience screen, HIDEOUT OVERRUN (laid out like the raid statistics) lists what was taken with each one's value, your health, energy and hydration, and your Security and Medstation, each section with its icon and its own heading. NEXT goes on to the heal screen, BACK back to the experience screen. Skip the screens with Main menu and it's on the main menu instead. It's shown once. The invasion banner doesn't show while the menu's still loading after a raid, and Start the Defense never leaves a blank menu.
- [ ] Take Mannequin 1's whole loadout, then die: it's gone from your real mannequin (the server log says how many items were taken). Take it and survive: it's on you, not on the mannequin. Leave it: still on the mannequin.
- [ ] A rig or backpack from your gear rack or a mannequin opens without searching, with everything in it shown.
- [ ] Attackers keep coming at you, and nobody jams in the tunnel round the earth bank.
- [ ] Through the tunnel wall: everyone who comes through it gets to you. Nobody's left standing still in the room it opens into (if one is, the log has "had no way to" with where it was).
- [ ] With two or more of Hideout Extensions' rooms built: over a few invasions the tunnel isn't always in the same room ("This invasion's tunnel goes to the ..." in the log). In co-op, both players see it in the same place.
- [ ] Raiders, Rogues and Black Division have night vision down while it's dark (and in co-op, the other player sees it too).
- [ ] The tunnel (wave 2 on, with Hideout Extensions' rooms): a ragged, scorched hole; a dug tunnel behind it with a bank of earth; nobody visible waiting before they come round it.
- [ ] Before the tunnel's wall is blown, you can't tell where it'll be: the wall's paint band and texture carry straight across ("The tunnel's hole is covered: the ... carried on over it" in the log).
- [ ] Nobody waiting to come through the tunnel turns up in the room next to it ("not where he waits" in the log shows one that was moved back).
- [ ] You can't walk into the tunnel once it's open, or through the cultist room's gate once it's blown: you stop where you were and "You can't go that way" shows. You never end up under the map ("Kept you out of" in the log).
- [ ] On the security camera, the scav walks down the steps with his feet on them (not floating at the landing's height), sets the charge and walks back up.
- [ ] Hideout Interiors' decor panel has a Defences tab (after Other, before Hidden) with Sandbags, Steel barricade, Barbed wire and Mounted gun, each showing "N of M". Past the limit it won't put another in and says why; deleting one frees a place.
- [ ] Sandbags in the siege stop bullets and attackers go round them.
- [ ] Steel barricade: bullets stop on the plate but go through the slit; you can stand between its props right up to the slit and shoot through it; attackers go round it. It looks right (olive plate, slit with a hood, props behind).
- [ ] Barbed wire: walking into it slows you to a crawl, stops you sprinting, and hurts your legs as you push on ("Caught in the wire"); out of it you're back to normal. Attackers walk into it and are slowed too. Bullets go through it.
- [ ] Sandbags or a steel barricade put in a doorway or out on the stairs aren't there in the siege (the log says why), and nothing jams.
- [ ] A wall of sandbags and steel built right across a room or corridor, you behind it: "THEY'RE BREACHING YOUR DEFENCES", one walks up, sets a charge on a piece of it, backs off, and that piece blows ("has been blown" in the log); they come through the gap. Sandbags stacked on blown ones come down with them. A wall with a way round it isn't blown, and nobody's ever moved to the other side of one.
- [ ] "THEY'RE BREACHING" / "THEY'RE BREACHING YOUR DEFENCES" only shows once he's kneeling at the door or the defence, never while he's still on his way.
- [ ] Kill the attacker going to breach a door or your defences while he's setting it: "BREACH STOPPED", nothing blows, and a few seconds later another one goes. Kill him on his way: nothing's said and nothing blows. Kill him after it's on: it still blows.
- [ ] A charge never appears on a door or defence with nobody kneeling at it ("didn't get to" in the log when one gave up).
- [ ] No freeze as the cinematic starts (your custom walls, floor and ceiling are already in) or when the security camera's REC dot first shows.
- [ ] With SAIN: hide somewhere out of sight; attackers come looking rather than waiting at the stairs, and fight as SAIN once they see you.
- [ ] With Black Division: its troops and raiders arrive armed (no "came without weapons" warning in the log).
- [ ] Attackers wait out of sight past the cultist room's gate, not at the top of the stairs. At the breach the gate blows with the front door and they come down the stairs.
- [ ] From wave 3 with Hideout Extensions' rooms built: "THEY'RE COMING THROUGH A WALL", a charge goes off in a room's wall, rubble, and attackers come in through the hole from a tunnel.
- [ ] Hide in an extension room behind its door: attackers blow every door between you and them.
- [ ] Mounted gun: with nobody on it, it turns slowly to attackers in front of it and fires short bursts that don't wipe a wave on their own; it stops when it's out (30 rounds a wave), and never fires through one of you. Operate still works.
- [ ] Front door upgrades: in the hideout, the prompt at the front door shows the cost and builds it. In the siege, a reinforced door holds the first charge ("THE DOOR HELD"), and its bars come off as it takes hits.
- [ ] Nobody attacks during the 60 seconds. The front door won't open, and the charge beeps for the last 10, with its lamp blinking faster and staying on just before it blows.
- [ ] The door blows and the attackers run down and through it one after another, without jamming on the stairs or popping through the door. Later waves come the same way.
- [ ] Every gun is on a rack, none left on the floor by the bed.
- [ ] Walk out of the blown door during a break, or shoot someone waiting up the stairs: the next wave starts straight away.
- [ ] Standing near the door when it blows blinds and concusses you for a few seconds. Staying back at the beds doesn't.
- [ ] Shut the Emergency Wall's door between you and them: nobody should come through it until one walks up, sets a charge, backs off and it blows.
- [ ] Looking at a station, lamp or radio during the siege doesn't freeze or crash anything, and neither does what's behind a gun when you take it.
- [ ] Taking your first gun, first magazine and first piece of gear doesn't freeze the game. You're held still for a moment as you reach for it, the same as any pickup in a raid.
- [ ] After winning **and** after losing, the Hideout opens normally, and another siege starts from there.
- [ ] The beep, static, siren and explosion sound right. This hasn't been heard yet, because all testing was muted.
- [ ] Fighting feels fair: try it with real gear and see whether attackers are too fast, too many, or too quiet.
- [ ] Bodies can be looted, and what you take is kept after leaving.
- [ ] Winning gives a minute to loot ("Loot them..." line), F10 leaves sooner, and no reward message arrives from Prapor.
- [ ] Stash station: Open stash shows an empty grid called Stash. Put loot in, win or lose: it's in your real stash afterwards, with nothing doubled. With a full stash, what doesn't fit comes by system message.
- [ ] Take a backpack from a gear stand, fill it, drop it, fill another: after the raid that loot is in your stash and the bags are still on your stands.
- [ ] Lose a Black Division siege: several things taken (4 at Security 3), and Hideout Overrun has a "Wrecked in your hideout" section (Security, Vents or Illumination down a level, or the front door's top upgrade). The hideout shows it at the lower level.
- [ ] Dying ends the raid as a normal death after about 9 seconds.
- [ ] A friend can join through Fika's server browser.
- [ ] A normal raid plays as usual afterwards.

To get the banner again straight away: F12 > Tushonka: Hideout Last Stand > Testing > Invade my hideout now.

## With your full mod list

`F:\SPT\_mods_removed_20261105\restore.ps1` puts the parked mods back. Black Division's Breacher, Support and Lead bots came without weapons in testing, even with WTT Armory, so this log line is worth checking:

`bots came without weapons`

If it appears, those roles were replaced by Black Division assault troops, and the siege carries on.
