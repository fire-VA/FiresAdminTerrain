## 0.2.1430

### Fixed
- Far terrain regions are sized to whole cells, so their edges meet exactly (no thin sky lines across the far land).
- A relog holds you in place until there is ground under you, instead of dropping you onto terrain that hasn't been built yet.

## 0.2.1429

New
- Paint your own custom biomes with the Chaos Hammer and on the large map, using Expand World's biome data (needs Expand World Data with biome data on). Admins can draw a closed outline on the map to paint a whole area at once.
- Biome painting on the large map has its own button and panel, so it works without Fires RPG Maker. With RPG Maker it sits under the Territory button.
- The biome brush can keep or wipe the builds inside a painted area (a toggle on the brush).
- One world regeneration command (fires_regen) now works on any world, not only imported ones, and can redo locations, plants and spawners for the whole world in one run. The old name still works.
- Regeneration options: ignore safe zones (builds are never removed, the reset happens around them), thin every type evenly by a percentage, keep ore from being re-rolled, and keep chosen dungeons where they are.
- Zones far past the world edge that are not a build area are only cleaned out, never regenerated.

Changed
- Terrain Mode is no longer a setting: each world keeps its own choice (heightmap or voxel) from when it was created. Old worlds are read once from your old setting.
- Settings that used to live in Fires Unified Core (cave ore regrow, undo, location loot refresh, snow) are now FAT's own, with the same names and defaults, and the server keeps them in sync.
- Ocean swim settings now come from Advanced Player Movement when it is installed.
- Lily pads grow in deeper water and the reeds stay along the shallows.
- Fire rings in the snow blend softly instead of making perfect circles.
- The ruler on the snow is drawn on top of the snow instead of under it.
- Frozen lake ice looks less like TV static up close.
- Player trails in the snow are narrower: a companion, tame or mount walking with you no longer cuts a second wide trail.
- With Vedr installed, Vedr draws the sky rainbow and owns the snow sparkle setting, so you only have one of each.
- Distant tree billboards are off by default for now (they could show through hills and look blocky up close). Your own setting is kept.

Fixed
- Environment boxes now really change the ground texture inside them (they never did on Valheim 1.0).
- Rivers and placed water no longer break when you log out and back in.
- Using the water mop no longer stacks water levels when you redraw, and removed water stays removed for players who were away.
- No more giant spikes along rivers on super flat worlds.
- Swimming under winter ice no longer drops you out of the water as soon as you go under.
- Sea waves no longer show swelling up through sea ice.
- River water and waterfalls fit the slope better and match the river's level.
- Terrain loads from the closest ground outward, so the ground under you is ready first, and the ground no longer flickers between two meshes while it loads.
- A long regeneration scan no longer drops the player from the server.
- The Deep North ring at the far edge of the world is now included in regeneration.
- Location pieces no longer double up when a location is placed again.
- Custom biome painting works again with Expand World Data 1.79 and newer.
- The map now shows painted biomes, instead of the colours from before painting.
- The shovel works on Vedr snow on normal (heightmap) ground the same as on voxel ground.
- Snow lowered by terrain tools now fills back in.
- Seasonal flowers no longer show where no grass is drawn.
- Things players built or placed are never removed by the "remove far content" cleanup.
- Expand World Data's plant placement works again alongside FAT (it was cut out by an earlier FAT build).

Performance
- Much smoother in winter on normal (heightmap) worlds: cave and deep-ground checks are skipped where there is no voxel ground.
- Distant tree billboards: only the directions the camera can see are drawn, and the far billboards are stored on the graphics card.
- Ice cover near you is cheaper to refresh while you move.
- A river stroke now re-grows grass once instead of on every dab, and terrain edits no longer re-roll grass over ground that did not change.
- Building pieces on islands check their ground far less often.
- Season snow is already in place when a zone first builds, without a second terrain pass.

Removed
- The Terrain Mode setting (see Changed).

Note: no world reset is needed. Old worlds pick up their terrain mode once, automatically.

## 0.2.1335
- Terrain settings for snow, undo, mineral seeds, loot and ocean now live in this mod's own config or the mod that owns them; your saved values are copied over.
- Rivers are kept between sessions, and the mop now removes water disks.
- A refused dungeon entrance no longer retries every frame.
- Stricter size checks on terrain and water data received over the network.

## 0.2.1327
- Fixed: a new world could inherit chunk-by-chunk / voxel settings from a deleted world with the same name. (world)
- Deleting a world now also removes its FiresAdminTerrain settings files. (world)

## 0.2.1326
- Snow on vanilla terrain is now drawn by the game's own terrain, raised by Core, so zone borders no longer tear or show seams. FiresAdminTerrain keeps winter snow on its own voxel ground. (snow)
- Snow trails on voxel ground cut in proportion to the snow depth and are refilled by Core, so gardens are no longer wiped. Trails mark any change you make, so they melt back in spring. (snow)
- New "Deep snow slow outside the snow biomes" (0.5): seasonal snow outside the Deep North and Mountains slows you less than the vanilla deep-snow slow. (snow)
- No voxel snow under water or ice. (snow)
- Ice follows the seasons: it thaws by about day 3.5 of spring, there is no ice in summer or autumn, and the thaw now follows Core's season timing. (ice)
- Ice looks the way it collides: you stand on what you see, you swim below solid ice, frozen water stops flowing, and splash effects stay off on ice. Waves no longer show over frozen sea ice. (ice)
- Water fills down to the riverbed over caves, ponds lie flat, and waterfalls follow the cliff face. (water)
- The terrain height override settings moved here from Core, with the same values. (terrain)
- On a super flat world, locations you place on purpose now spawn. (terrain)
- Fixed a world generation error that could leave a black zone, shrines placed under-filled, and stale zone rules. (world)
- Admin tools for the new World Studio: raise, dig and smooth ground, place and remove locations, water and vegetation, all with undo. (admin)
- Many performance fixes for snow, ice cover and terrain updates. (Core)

## 0.2.1240
- Admin location reset: a reset zone is reset whole, the reset waits for the locations to finish loading, and it checks the result afterwards.
- Resetting a location also cleans up the dropped items and creatures inside its footprint before the location is placed again.
- Admin world regeneration: faster, and resetting locations no longer doubles or strips the vegetation around them - a reset zone gets its location and its plants back together.
- Location resets can add the shrines and other location types you name, placed with their own data.
- Water tools: draw rivers point by point, ramps (new ramp tool), waterfalls and a mop; placed water looks and works like generated water, with seamless sheets and ice.
- Lakes freeze in the Deep North and in winter; ice is slippery and see-through.
- Sky islands: richer floating islands with their own plants, water and snow; plain lattice islands removed.
- Snow on voxel ground: deep snow, walk trenches ("Snow trail trench"), smooth heightmap snow that matches vanilla.
- Fires melt the snow around them; cave fires and pathing in caves work properly.
- Many stutter and freeze fixes for the Deep North, terrain builds and water tools.
- New admin command to regenerate vegetation and locations in chosen zones while keeping player builds ("Regen verbose logging", off).

## 0.2.1051
- Bug fixes and improvements.

## 0.2.1006
- Smoother flying and exploring: far fewer hitches while terrain, shores and caves load.
- Forced-biome territories (with Fires RPGmaker): a territory now grows its forced biome's plants and gets that biome's locations, and the old locations there are replaced. The start temple, boss altars and traders always stay, and player builds are never touched.
- A territory regenerates in the background without freezing the server. Areas nobody has visited yet regrow the next time they load, even after a restart.
- Chaos Hammer biome paint no longer removes or swaps locations. When you finish a stroke, it adds the painted biome's locations where they fit, and never over your builds.
- Super flat worlds: painted areas and forced-biome territories grow their biome's plants, grass and creatures. The rest of the world stays empty.
## 0.2.984
- A player's first join to a Fires world now downloads the server's finished world and water instead of building it, using far less memory.
- If your PC already has that world, nothing is downloaded at all.
- Fixed players being dropped during a big first-join download.
- Ice, snow, removed water and drained pools can now be undone like other terrain edits.
- Terrain tools: mouse wheel sets the size, Shift + mouse wheel sets the height (PgUp / PgDn still work).

## 0.2.976
- Cleaner settings screen: the basics show first, the rest is under Show Advanced (your saved values are kept).
- The server and every player must update to this version together.
- Corrected help text for the tools, the Chunk Generator and new worlds.
- Terrain and water tools keep working after logging out and back in.

## 0.2.971
- Dedicated servers no longer spam grass errors.
- Fixed the Super flat ground turning white after moving between worlds.

## 0.2.969
- New World option: a Super flat admin build world (flat, small, only the spawn temple, no plants or creatures).
- Super flat build grid that stays sharp at any distance.
- Fixed a crash and an FPS drop while terrain loads; players stuck inside terrain on load are lifted out.
- Faster world loads, and the minimap no longer redraws on every join.
- Fixed seams in the ground after levelling.

## 0.2.874
- Better caves, sinkholes, waterfalls and location placement.

## 0.2.873
- Updated for Valheim 1.0: sculpt terrain, caves, rivers, waterfalls and whole worlds from the hammer.

## 0.2.367
- First release.
