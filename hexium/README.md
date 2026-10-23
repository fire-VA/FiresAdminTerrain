# Fires Admin Terrain

**Updated for Valheim 1.0.**

The world-shaping toolkit of the Fires mod family. Sculpt terrain, dig real 3D tunnels and caves, place water, lava, ice and snow at any height, paint biomes, and carve rivers and lakes into your world. Or generate whole new FIRES worlds, including "one zone at a time" worlds where you open the map square by square.

Every edit is saved with the world, so every player sees the same ground.

## What you get

- **Terrain sculpting**: flat, raise, lower, smooth, grab, noise and floating-island brushes, with ground paint and undo.
- **Voxel terrain**: real 3D overhangs, tunnels and caves. Full voxel worlds grow their own cave networks, stocked with the biome's ore, pickables and spawners, which restock over time.
- **Water and liquids**: water, lava, tar, boiling and freezing water, waterfalls and springs, at any height. Swim in them and sail rivers.
- **Ice and snow**: floating ice, ice sheets and snow layers, all shapeable.
- **Biome paint**: paint snow, lava, swamp, forest, plains, mistlands or meadows onto the ground.
- **Rivers and lakes** carved into existing worlds, with beaches, reeds and lilies.
- **FIRES worlds**: continents, erosion, climate-driven biomes, rivers, volcanoes and sky islands, or start from your own heightmap image.
- **Super flat** admin build world with a build grid.
- **Faster joins**: players download the server's finished world once instead of building it.

## How to use

- **V-tools** (admins, Chaos Hammer, Voxel tab): Vcarve, Vfill, Vdelete, Vcube, Vsphere, Vlevel, Vriver, Vpour, Vpool, Vfall and Vmop.
  - **Mouse wheel** = size, **Shift + mouse wheel** = height (PgUp / PgDn also work).
  - **Vlevel** flattens to the height you first click. **Vpool** digs and fills a pool, or a river between two clicks (higher one first). **Vfall** hangs a waterfall from an edge. **Vmop** removes water; LeftAlt brings it back.
- **Undo / redo**: numpad 7 / 9, or ServerDevcommands' `undo` / `redo`.
- **Console commands**: every tool is also a command (`va_*`, `place_*`, `ice_*`, `paint_*`, `fires_*`). Most need devcommands. The full list is in the pause menu under **? Help > Fires Admin Terrain**.

## Creating and cloning worlds

- **New World**: under Seed, pick the generator (Stock Valheim or Fires), terrain (Heightmap, Voxel wrap or Full voxel), one zone at a time, sky islands, rivers and lakes, and caves. If a combination can't work, the reason shows in red. Each world remembers its choices.
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
- Optional: **Infinity Hammer** (aims the brushes and paints while you hold the mouse), **ServerDevcommands** (commands on a dedicated server, plus undo / redo), **FiresTossinShade** (nicer water).
- **Not compatible with Valheim Performance Optimizations**: Fires Admin Terrain won't load while it is installed.

## Good to know

- `[Voxel Terrain - World] Terrain Mode` is `Off` by default (vanilla terrain, brushes still work). `FullVoxel` turns on voxel terrain everywhere with caves. Worlds made on the New World screen keep their own choice.
- Fewer trees, bushes and grass than vanilla by default. Set `Vegetation density scale` and `Clutter density scale` to 1 for vanilla.
- Pickaxe hits on voxel ground dig tunnels. Wards still protect.
- **Back up** the `FiresWorldGen` folder next to your world save (every `firesgen_<world>*` file and `<world>.biomedata`) together with the world. Without them a FIRES world loads as a normal one.
- Performance: `Terrain Mode Off` keeps the voxel systems asleep. On a dedicated server, `Voxel wrap radius (headless)` matters most.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Screenshots

<!-- ![A voxel cavern](HOSTED_URL/FiresAdminTerrain/01_fat_cave.png) -->
