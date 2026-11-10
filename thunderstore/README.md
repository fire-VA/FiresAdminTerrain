# Fires Admin Terrain

**Updated for Valheim 1.0.**

The world-shaping toolkit of the Fires mod family. Sculpt terrain, dig real 3D tunnels and caves, place water, lava, ice and snow at any height, paint biomes (including your own custom ones), and carve rivers and lakes into your world. Or generate whole new FIRES worlds, including "one zone at a time" worlds where you open the map square by square.

Every edit is saved with the world, so every player sees the same ground.

## What's new in 0.2.1430

Since 0.2.1327: custom biome painting on the map, one regeneration command for any world, each world now remembers
its own terrain type, rivers and placed water survive a relog, and winter is much smoother on normal worlds.

### Biggest changes

- **Paint your own custom biomes**, with the Chaos Hammer and on the large map, using Expand World's biome data (needs
  Expand World Data with biome data turned on). Admins can draw a closed outline on the map to paint a whole area at
  once. The map now shows the painted biomes.
- **Biome painting has its own map button and panel**, so it works without Fires RPG Maker. With RPG Maker it sits
  under the Territory button.
- **The biome brush can keep or wipe the builds** inside a painted area (a toggle on the brush).
- **One regeneration command, `fires_regen`, for any world**, not only imported ones. It can redo locations, plants
  and spawners for the whole world in one run. The old name still works. Options: ignore safe zones (builds are never
  removed; the reset happens around them), thin every type evenly by a percentage, keep ore from being re-rolled, and
  keep chosen dungeons where they are. Zones far past the world edge that are not a build area are only cleaned out,
  never regenerated, and anything players built or placed is never removed by that cleanup.
- **Environment boxes now really change the ground texture inside them.** They never did on Valheim 1.0.

### Fixed

- Rivers and placed water no longer break when you log out and back in.
- The water mop no longer stacks water levels when you redraw, and removed water stays removed for players who were
  away.
- River water and waterfalls fit the slope better and match the river's level. No more giant spikes along rivers on
  super flat worlds.
- Swimming under winter ice no longer drops you out of the water as soon as you go under, and sea waves no longer
  swell up through sea ice.
- Terrain loads from the ground nearest you outward, so the ground under you is ready first, and it no longer
  flickers between two versions while it loads.
- After a relog you are held in place until there is ground under you, instead of dropping onto terrain that isn't
  built yet.
- Far terrain edges now meet exactly, with no thin lines of sky across distant land.
- A long regeneration scan no longer drops the player from the server, and the Deep North ring at the far edge of the
  world is now included in regeneration.
- Location pieces no longer double up when a location is placed again.
- Custom biome painting works again with Expand World Data 1.79 and newer, and Expand World Data's plant placement
  works again alongside FAT.
- The shovel works on Vedr snow on normal ground the same as on voxel ground, and snow lowered by terrain tools fills
  back in.
- Seasonal flowers no longer show where no grass is drawn.
- A refused dungeon entrance no longer retries every frame.
- Stricter size checks on terrain and water data received from other players.

### Looks

- Lily pads grow in deeper water and reeds stay along the shallows.
- Fire rings in the snow blend in softly instead of making perfect circles.
- The ruler is drawn on top of the snow instead of under it.
- Frozen lake ice looks less like TV static up close.
- Player trails in the snow are narrower, and a companion, tame or mount walking with you no longer cuts a second wide
  trail.

### Smoother play

- Much smoother in winter on normal (heightmap) worlds: cave and deep-ground checks are skipped where there is no
  voxel ground.
- Ice near you is cheaper to refresh while you move.
- A river stroke re-grows grass once instead of on every dab, and terrain edits no longer re-roll grass on ground that
  did not change.
- Building pieces on islands check their ground far less often.
- Season snow is already in place when an area first builds, without a second terrain pass.
- Distant tree billboards draw only what the camera can see.

### Settings that moved or changed

- **Terrain Mode is no longer a setting.** Each world keeps its own choice (heightmap or voxel) from when it was
  created on the New World screen. Old worlds read your old setting once, automatically. **No world reset is needed.**
- **Settings that used to live in Fires Unified Core** (cave ore regrow, undo, location loot refresh, snow) are now
  in FAT's own config, with the same names and defaults, and the server keeps them in sync. Your saved values are
  copied over.
- **Ocean swim settings** come from Fires Advanced Player Movement when it is installed.
- **With Vedr installed**, Vedr draws the sky rainbow and owns the snow sparkle setting, so you only get one of each.
- **Distant tree billboards are off by default for now** (they could show through hills and look blocky up close).
  If you had changed this setting yourself, your choice is kept.

## What you get

- **Terrain sculpting**: flat, raise, lower, smooth, grab, noise and floating-island brushes, with ground paint and undo.
- **Voxel terrain**: real 3D overhangs, tunnels and caves. Full voxel worlds grow their own cave networks, stocked with the biome's ore, pickables and spawners, which restock over time.
- **Water and liquids**: water, lava, tar, boiling and freezing water, waterfalls and springs, at any height. Swim in them and sail rivers.
- **Ice and snow**: floating ice, ice sheets and snow layers, all shapeable.
- **Biome paint**: paint snow, lava, swamp, forest, plains, mistlands or meadows onto the ground, or your own custom biomes with Expand World Data. Paint with the Chaos Hammer or on the large map.
- **World regeneration**: `fires_regen` redoes locations, plants and spawners on any world, around your builds.
- **Rivers and lakes** carved into existing worlds, with beaches, reeds and lilies.
- **FIRES worlds**: continents, erosion, climate-driven biomes, rivers, volcanoes and sky islands, or start from your own heightmap image.
- **Super flat** admin build world with a build grid.
- **Faster joins**: players download the server's finished world once instead of building it.

## How to use

- **V-tools** (admins, Chaos Hammer, Voxel tab): Vcarve, Vfill, Vdelete, Vcube, Vsphere, Vlevel, Vriver, Vpour, Vpool, Vfall and Vmop.
  - **Mouse wheel** = size, **Shift + mouse wheel** = height (PgUp / PgDn also work).
  - **Vlevel** flattens to the height you first click. **Vpool** digs and fills a pool, or a river between two clicks (higher one first). **Vfall** hangs a waterfall from an edge. **Vmop** removes water; LeftAlt brings it back.
- **Biome painting on the map**: open the large map and use the biome button (under Territory if Fires RPG Maker is installed). Admins can draw a closed outline to paint a whole area. The brush has a toggle to keep or wipe the builds inside.
- **Undo / redo**: numpad 7 / 9, or ServerDevcommands' `undo` / `redo`.
- **Console commands**: every tool is also a command (`va_*`, `place_*`, `ice_*`, `paint_*`, `fires_*`). Most need devcommands. The full list is in the pause menu under **? Help > Fires Admin Terrain**.

## Creating and cloning worlds

- **New World**: under Seed, pick the generator (Stock Valheim or Fires), terrain (Heightmap, Voxel wrap or Full voxel), one zone at a time, sky islands, rivers and lakes, and caves. If a combination can't work, the reason shows in red. Each world remembers its choices, including its terrain type.
- **Clone**: right-click a world in Select World and pick Clone. The original is never touched.
- The Fires generator needs voxel terrain. FIRES worlds take a few minutes to generate the first time.

## One zone at a time

Start on a small patch of land in an endless sea and open the world one 64 m square at a time.

- **Chunk Generators make Chunk Seeds.** One stands next to the start. Feed it 50 each of two different materials of its biome for one seed.
- **The Chunk Hammer plants them.** Your first one is in a chest in the cave beside the start temple. Look at closed land next to yours, strike, confirm, and the square rises.
- **Each boss unlocks the next biome's generator:**

  | Biome | Unlocked by | Some of its materials |
  |---|---|---|
  | Black Forest | Eikthyr | Round Log, Copper Ore, Tin Ore, Greydwarf Eye, Troll Hide |
  | Swamp | The Elder | Iron Scrap, Ancient Bark, Guck, Bloodbag, Ooze |
  | Mountain | Bonemass | Silver Ore, Obsidian, Wolf Pelt, Freeze Gland, Crystal |
  | Plains | Moder | Black Metal Scrap, Flax, Barley, Lox Pelt, Needle |
  | Mistlands | Yagluth | Yggdrasil Wood, Black Marble, Soft Tissue, Carapace, Sap |
  | Ashlands | The Queen | Ashwood, Grausten, Flametal Ore, Charred Bone, Asksvin Hide |
  | Deep North | Fader | Timberwood, Petrified Tissue, Ice, Moose Hide, Seal Pelt |

- **Location seeds** from the forge raise dungeons, boss altars and landmarks; **unique seeds** raise the traders, the Bog Witch and other one-of-a-kind places.
- **The Eye of the Beholder** helmet shows what a square was meant to be.
- **Progression can't be blocked**: boss altars and the places their items come from rise on their own when you come near.
- Hugin explains it in game. Admins can open squares with `fires_zones_open`.
- Cloned worlds keep what you built: explored zones are parked and come back when reopened.

## Install

- **Server and every client, on the same version.** A player without it, or on another version, sees different ground.

## Requires

- **FiresUnifiedCore**
- Optional: **Infinity Hammer** (aims the brushes and paints while you hold the mouse), **ServerDevcommands** (commands on a dedicated server, plus undo / redo), **FiresTossinShade** (nicer water), **Expand World Data** with biome data on (custom biome painting), **Fires Advanced Player Movement** (ocean swim settings), **Fires RPG Maker** (biome painting under the Territory button).
- **Not compatible with Valheim Performance Optimizations**: Fires Admin Terrain won't load while it is installed.

## Good to know

- Each world keeps the terrain type it was created with (heightmap or voxel); there is no longer a global Terrain Mode setting. Brushes work on every world. Full voxel worlds get caves.
- Fewer trees, bushes and grass than vanilla by default. Set `Vegetation density scale` and `Clutter density scale` to 1 for vanilla.
- Distant tree billboards are off by default for now.
- Pickaxe hits on voxel ground dig tunnels. Wards still protect.
- **Back up** the `FiresWorldGen` folder next to your world save (every `firesgen_<world>*` file and `<world>.biomedata`) together with the world. Without them a FIRES world loads as a normal one.
- Performance: heightmap worlds skip the cave and deep-ground work. On a dedicated server, `Voxel wrap radius (headless)` matters most.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Screenshots

<!-- ![A voxel cavern](HOSTED_URL/FiresAdminTerrain/01_fat_cave.png) -->
