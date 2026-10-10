using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // FiresAdminTerrain's sections for the shared FiresCore help panel (registration only).
    internal static class FatHelpContent
    {
        private const string ModId = "Fires Admin Terrain";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "What Fires Admin Terrain Does", 0, BuildOverview, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Terrain", "Terrain Mode & New Worlds", 5, BuildTerrainMode, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Terrain", "Hammer V-Tools", 9, BuildVTools, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Terrain", "Terrain Brushes", 10, BuildBrushes, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Terrain", "Voxel Commands & Caves", 11, BuildVoxel, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Terrain", "Biome & Ground Paint", 12, BuildPainting, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Water", "Placing Water & Liquids", 20, BuildWater, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "Water", "Ice & Snow", 21, BuildIceSnow, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "World", "One Zone at a Time", 28, BuildZoneMode);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "World", "Zone Mode Admin", 29, BuildZoneModeAdmin, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "World", "FIRES Worlds & Sky Islands", 30, BuildWorldgen, adminOnly: true);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "World", "Key Config Levers", 31, BuildConfig, adminOnly: true);
        }

        private static void BuildOverview(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("What Fires Admin Terrain Does");
            w.Paragraph("The world-shaping toolset: sculpt terrain, dig real 3D tunnels and caves, place water, " +
                "lava, tar, ice and snow at any height, paint biomes, carve rivers and lakes, and generate whole " +
                "FIRES worlds, including one-zone-at-a-time worlds. Edits are saved with the world, so every " +
                "player sees the same ground. The server and every player need the same version of the mod.");
            w.Divider();

            w.SubHeader("Three Ways To Drive It");
            w.Bullet("Hammer V-tools - eight voxel and water tools on the Hammer's Misc tab, for admins. Nothing " +
                "else needed.");
            w.Bullet("Console commands (F5) - the va_*, place_*, ice_*, paint_* and fires_* commands below. Most " +
                "need devcommands; on a dedicated server an admin needs a mod like ServerDevcommands to run them.");
            w.Bullet("Infinity Hammer (optional) - its ruler sets where a brush lands, the mouse wheel sets the " +
                "radius and PgUp/PgDn the height, and holding the mouse paints continuously.");
            w.Divider();

            w.SubHeader("Before You Start");
            w.Bullet("[VoxelTerrain World] Terrain Mode decides what the mod does to the world. See " +
                "Terrain Mode & New Worlds.");
            w.Bullet("va_tile_undo / va_tile_redo undo terrain brushes (100 steps, on this machine). Other edits " +
                "have no undo, so back up the world before bulk commands.");
        }

        private static void BuildTerrainMode(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Terrain Mode & New Worlds");
            w.Paragraph("[VoxelTerrain World] Terrain Mode is the master switch, and the server's value applies " +
                "to everyone.");
            w.Bullet("Off (default) - vanilla terrain. Brushes, water, ice and snow still work; voxel tools " +
                "only add shapes.");
            w.Bullet("ZoneReplace - vanilla ground until you edit it: a zone becomes voxel terrain on its first " +
                "voxel brush or pickaxe dig.");
            w.Bullet("FullVoxel - every nearby zone is voxel terrain with generated caves, caverns and " +
                "sinkholes. Needed for FIRES worlds, sky islands and skylands.");
            w.Divider();

            w.SubHeader("Rivers & Lakes On Existing Worlds");
            w.Bullet("In ZoneReplace on vanilla-generated worlds, Hydrology Enabled and Hydrology " +
                "Place Water (both on) carve winding rivers and dug-out lakes with beaches, and remove the trees " +
                "and bushes standing in the new water. Turn both off if you only want tunnels.");
            w.Bullet("Shape them with River Threshold, River Width Min / Max, River Meander and Coarse Grid Bank " +
                "Relief.");
            w.Bullet("With voxel terrain Enabled, pickaxe hits on voxel ground dig 1 m tunnels. Wards and " +
                "no-build areas still apply.");
            w.Divider();

            w.SubHeader("New Worlds");
            w.Bullet("With Terrain Mode = FullVoxel, creating a world asks whether to generate it with FIRES " +
                "terrain.");
            w.Bullet("Creating a world also offers One Zone at a Time ([Zone Mode] Offer at world creation). A " +
                "dedicated server starts a never-saved world that way with the -fireszones launch flag.");
            w.Bullet("Both choices are permanent. They live in the FiresWorldGen folder next to the world save, " +
                "so copy that folder with the world.");
        }

        private static void BuildVTools(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Hammer V-Tools");
            w.Paragraph("On the Hammer's Misc tab, for admins only. Selecting a tool never places a piece. Hide " +
                "them with [VoxelTerrain (P0 prototype)] Show tools in build menu.");
            w.Divider();

            w.SubHeader("Tools");
            w.Bullet("Vcarve / Vfill - carve or fill a sphere; after PgUp/PgDn, a cylinder of that height");
            w.Bullet("Vdelete - remove voxel edits and bring back the original ground");
            w.Bullet("Vcube / Vsphere - stamp at the 3D gizmo; hold Left Alt to carve; drag spheres for smooth " +
                "tubes");
            w.Bullet("Vriver - drag to carve a river bed");
            w.Bullet("Vpour - drag to lay a ribbon of water");
            w.Bullet("Vpool - click to dig and fill a pool; click again downstream to join the two with a " +
                "channel. Switch tools to end the chain.");
            w.Divider();

            w.SubHeader("Controls");
            w.Bullet("Mouse wheel - brush size (0.5 to 32 m); the camera stops zooming while a V-tool is selected");
            w.Bullet("PgUp / PgDn - cylinder height (1 m steps, up to 24 m), or the water level for the water " +
                "tools (0.5 m steps)");
            w.Bullet("Hold left click and drag to apply continuously. [VoxelTerrain World] Brush Draw Speed sets " +
                "the pace.");
        }

        private static void BuildBrushes(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Terrain Brushes");
            w.Paragraph("Persistent sculpting on 64 m terrain tiles laid over vanilla ground. Aim, or place the " +
                "Infinity Hammer ruler, and run the command. The vanilla hoe and cultivator edit sculpted tiles " +
                "too.");
            w.Divider();

            w.SubHeader("Sculpt");
            w.Bullet("va_brush_flat [radius] [height] - flat platform, at the height you click by default");
            w.Bullet("va_brush_raise [radius] [delta] [falloff] / va_brush_lower [radius] [depth] [falloff]");
            w.Bullet("va_brush_flat_island / va_brush_raise_island / va_brush_lower_island - floating-island " +
                "versions; the underside rounds off 50 m or more above the ground");
            w.Bullet("va_brush_smooth [radius] [strength 0-1] - smooth");
            w.Bullet("va_brush_grab [radius] dy=N - drag terrain up or down with the cursor");
            w.Bullet("va_brush_noise [radius] [intensity] [scale] - Perlin bumps");
            w.Bullet("va_brush_hole_punch / va_brush_hole_patch [radius] - cut a hole in a tile, or fill it back");
            w.Bullet("va_stitch_seams [radius] - repair seams between tiles");
            w.Divider();

            w.SubHeader("Modes");
            w.Bullet("va_brush_toggle_heightlock - dragging follows the terrain, or holds the height you clicked");
            w.Bullet("va_brush_toggle_placement - the underside stretches to the ground, or floats");
            w.Bullet("va_brush_mode [heightlock=tracked|locked] [placement=connected|floating] - set both");
            w.Bullet("va_brush_drawspeed [0.05-1] - held-click speed for this session");
            w.Divider();

            w.SubHeader("Undo & Cleanup");
            w.Bullet("va_tile_undo / va_tile_redo - undo terrain brushes (ServerDevcommands' undo / redo also " +
                "reach them)");
            w.Bullet("va_brush_clear [radius] - reset sculpted heights, paint and biome overlays, and regrow grass");
            w.Bullet("va_brush_delete [radius] - scrub every terrain edit and overlay in the radius; buildings stay");
            w.Bullet("va_terrain_count - loaded and saved tile counts");
            w.Bullet("va_terrain_wipe_loaded / va_terrain_wipe_all - remove every tile edit in loaded zones, or " +
                "in the whole world (cannot be undone)");
            w.Bullet("va_terrain_recapture - refresh the tile look after a terrain shader mod loads");
        }

        private static void BuildVoxel(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Voxel Commands & Caves");
            w.Paragraph("Console versions of the V-tools, aimed with the crosshair or the Infinity Hammer ruler.");
            w.Divider();

            w.SubHeader("Commands");
            w.Bullet("va_voxel_carve / va_voxel_fill [radius] height=N - without height= a 2 m disc, with it a " +
                "cylinder (shaft, trench, pillar or wall)");
            w.Bullet("va_voxel_delete [radius] - bring back the original ground");
            w.Bullet("va_voxel_cube / va_voxel_sphere - stamp at the gizmo, sized by the V-tool mouse wheel");
            w.Bullet("va_voxel_wrapall - turn every loaded zone into voxel terrain now (Terrain Mode must not be Off)");
            w.Divider();

            w.SubHeader("Generated Caves (FullVoxel)");
            w.Paragraph("Tunnels, caverns and sinkhole entrances with pools and a dark cave environment. Each cave is " +
                "dressed for its biome by the server: pickables, ore nodes, wall and ceiling pieces, rubble, props, " +
                "creature spawners and, in some zones, a loot chest. Each client also draws pebbles, roots and " +
                "ice shards.");
            w.Bullet("Chests restock 3-10 in-game days after they are looted, adding a fresh roll to what is left. " +
                "Stones, bones, crystals and other pickables that never regrow in vanilla come back after their own " +
                "3-10 day roll, and so do slain cave creatures. Nothing restocks while a player is nearby.");
            w.Bullet("Shape: [VoxelTerrain World] Generate Caves and the Cave*, Cavern* and Sinkhole* keys.");
            w.Bullet("Content: [VoxelTerrain Cave Content] for chances, restock days and monsters, and " +
                "[VoxelTerrain Cave Content <Biome>] for each biome's prefab lists.");
            w.Divider();

            w.SubHeader("Diagnostics");
            w.Bullet("va_voxel_seamscan [heal] / va_voxel_seamaudit [radius|auto] - check the borders between chunks");
        }

        private static void BuildPainting(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Biome & Ground Paint");
            w.SubHeader("Biome Overlays (vanilla ground)");
            w.Bullet("paint_biome <name> [radius] - meadows, forest, swamp, snow (or mountain), plains, " +
                "mistlands, lava (or ash); any other word resets");
            w.Bullet("paint_snow / paint_lava [radius] - quick snow or lava and ash");
            w.Bullet("reset_biome_overlay [radius] - back to the original biome");
            w.Bullet("Players without the mod don't see overlays, and the vanilla hoe's dirt or paved paint " +
                "clears them.");
            w.Divider();

            w.SubHeader("Sculpted Tiles");
            w.Bullet("va_brush_biome <id> circle=R - one biome per 64 m tile: 1 Meadows, 2 Swamp, 4 Mountain, " +
                "8 Black Forest, 16 Plains, 32 Ashlands, 64 Deep North, 512 Mistlands, 0 clears");
            w.Divider();

            w.SubHeader("Ground Texture");
            w.Bullet("va_brush_paint [radius r g b a] - 255 0 0 dirt, 0 255 0 cultivated, 0 0 255 paved, " +
                "0 0 0 grass");
            w.Bullet("va_brush_toggle_clutter_clear - whether paint removes the grass under it");
        }

        private static void BuildWater(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Placing Water & Liquids");
            w.Paragraph("Liquid bodies at any height, each with its own waterline: swim in a mountain lake, sail " +
                "a river current, drown in a cave pool.");
            w.Divider();

            w.SubHeader("Liquid Disks");
            w.Bullet("va_brush_water / va_brush_lava / va_brush_water_tar [radius] depth=N");
            w.Bullet("va_brush_water_boiling / va_brush_water_freezing [radius] depth=N - a hot spring that " +
                "burns, or an icy lake that freezes");
            w.Bullet("va_brush_swimmable_lava - lava you can swim in (and die in)");
            w.Bullet("va_brush_waterfall [radius] - water that hugs the slope as you drag");
            w.Bullet("va_brush_water_volume [radius] - pour water that settles to its level, then sets");
            w.Bullet("va_brush_water_spring [radius] thickness=N - an endless source that flows downhill, then sets");
            w.Divider();

            w.SubHeader("Editing & Removal");
            w.Bullet("va_brush_water_delete [radius] - delete water in the radius; covering a spring's source " +
                "removes the whole spring");
            w.Bullet("va_brush_water_mop [radius] - precise removal; also dams spring flow");
            w.Bullet("va_brush_water_freeze [radius] - freeze or unfreeze spring flow");
            w.Bullet("set_liquid_type <water|tar|ice|boiling|freezing|lava> / cycle_liquid_type - change the " +
                "nearest body");
            w.Bullet("clear_water [radius] / clear_zone_water - remove custom water around you, or in your zone");
            w.Divider();

            w.SubHeader("Info");
            w.Bullet("water_info / list_water - custom water here, or everywhere");
            w.Bullet("va_water_census - every water surface at your position");
            w.Bullet("va_rivers / va_streams / va_lakes - ping vanilla water on the map");
            w.Bullet("va_water_dump - full report to config/FiresAdminTerrain/water_dump.txt");
            w.Divider();

            w.Paragraph("With FiresTossinShade installed, the water look is tunable under [FiresLiquid Look] and " +
                "[FiresInlandWater 1-9] (Advanced settings, applied live).");
        }

        private static void BuildIceSnow(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Ice & Snow");
            w.SubHeader("Ice");
            w.Bullet("place_ice [radius] - floating ice on water, an ice sheet on land; place_ice_land / " +
                "place_ice_water force one");
            w.Bullet("va_brush_water_ice [radius] depth=N - a solid, walkable ice disk");
            w.Bullet("break_standalone_ice circle=R - punch a hole through floating ice");
            w.Bullet("ice_land_raise / _lower / _level / _smooth and ice_water_raise / _lower / _smooth - shape " +
                "it (raise=H, lower=H, circle=R)");
            w.Bullet("delete_ice / delete_ice_land / delete_ice_water - remove it");
            w.Divider();

            w.SubHeader("Snow");
            w.Bullet("place_land_snow / delete_land_snow - a snow layer on the ground (looks only: no swimming, " +
                "no wet)");
            w.Bullet("snow_land_raise / _lower / _level / _smooth - shape its depth");
            w.Bullet("va_brush_snow - a snow disk at the ruler");
        }

        private static void BuildZoneMode(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("One Zone at a Time");
            w.Paragraph("In a one-zone-at-a-time world you start on a small patch of land in an endless sea, " +
                "and open the world one 64 m square at a time.");
            w.Divider();

            w.SubHeader("Opening Land");
            w.Bullet("A Meadows Chunk Generator stands next to the start and can't be destroyed. Feed it any two " +
                "different materials, 50 of each, for a Meadows Chunk Seed. It takes materials from every biome " +
                "and burns no fuel.");
            w.Bullet("Your first Chunk Hammer is in a chest in the start cave, inside the rocky hill beside the " +
                "StartTemple; the cave mouth faces the temple. A world without a start cave (one imported from another generator, e.g. a Gaea or Better Continents world) hides " +
                "the chest instead: in a cave under the start if it has one, otherwise above ground among the trees and " +
                "stones 24-72 m from the temple. Once you have held one, you can craft more with the " +
                "same recipe as the build hammer. Neither the chest nor the starter generator can be broken; once the " +
                "hammer is taken the chest disappears and is back the next day with another.");
            w.Bullet("Chunk Seeds are the Chunk Hammer's ammo. With the hammer in hand, look out over the closed " +
                "land beside your own within 48 m. A ghost of the square shows the land that will rise there, " +
                "red when it can't. Strike, confirm, and that square rises. A seed is only used up when the land rises.");
            w.Bullet("A seed raises its own biome. A square the world meant to be that biome rises as itself; " +
                "any other square, sea included, becomes that biome's land. Keep the planned biomes for a faithful " +
                "world, or mix them however you like.");
            w.Bullet("The Eye of the Beholder, a helmet from the workbench (Hard Antler, 2 Deer Trophies, Leather " +
                "Scraps, Resin), names the land a square was meant to be while your hammer is aimed.");
            w.Bullet("Progression can't be blocked: boss altars, and the places their summon items and key " +
                "materials come from, rise on their own the first time you come near them.");
            w.Divider();

            w.SubHeader("Later Biomes");
            w.Paragraph("Each boss unlocks the next biome's Chunk Generator on the hammer. A generator takes any " +
                "two different materials of its biome, 50 of each, or 100 each of the previous biome's pair:");
            w.Bullet("Black Forest (after Eikthyr) - Round Log, Copper Ore, Tin Ore, Greydwarf Eye, Troll Hide and more; or Stone and Wood");
            w.Bullet("Swamp (after The Elder) - Iron Scrap, Ancient Bark, Guck, Bloodbag, Ooze and more; or Round Log and Copper Ore");
            w.Bullet("Mountain (after Bonemass) - Silver Ore, Obsidian, Wolf Pelt, Freeze Gland, Crystal and more; or Iron Scrap and Ancient Bark");
            w.Bullet("Plains (after Moder) - Black Metal Scrap, Flax, Barley, Lox Pelt, Needle and more; or Silver Ore and Obsidian");
            w.Bullet("Mistlands (after Yagluth) - Yggdrasil Wood, Black Marble, Soft Tissue, Carapace, Sap and more; or Black Metal Scrap and Flax");
            w.Bullet("Ashlands (after The Queen) - Ashwood, Grausten, Flametal Ore, Charred Bone, Asksvin Hide and more; or Yggdrasil Wood and Black Marble");
            w.Bullet("Deep North (after Fader) - Timberwood, Petrified Tissue, Ice, Moose Hide, Seal Pelt and more; or Ashwood and Grausten");
            w.Paragraph("Hover a generator to see what it has counted toward the next seed.");
            w.Divider();

            w.SubHeader("Location Seeds");
            w.Bullet("The forge makes location seeds from two kinds of Chunk Seed. The pair picks the place: a " +
                "biome with Meadows seeds gives its dungeon, a biome with the next biome's seeds gives its boss " +
                "altar, and the other pairs give landmarks.");
            w.Bullet("A location seed raises its place on land copied from where the world planned one. It won't " +
                "plant over land kept for a boss altar or another place of its own.");
            w.Bullet("Unique seeds cost far more: 15 of two biome seeds plus 3 location seeds (10 + 10 + 2 for the " +
                "Rock Clearing). They raise one-of-a-kind places again: Haldor's and Hildir's camps, the Bog " +
                "Witch, the Forge of Potential, the three Places of Mystery and the Rock Clearing.");
            w.Divider();

            w.SubHeader("What You'll See");
            w.Bullet("Some points of interest rise from the sea as islands when you come near.");
            w.Bullet("Closed edges are cliff walls, with cave mouths, and waterfalls where rivers run out.");
            w.Bullet("Skylands float above the land around you.");
            w.Bullet("You can't build, dig or teleport over closed sea, and nothing spawns there.");
        }

        private static void BuildZoneModeAdmin(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Zone Mode Admin");
            w.Paragraph("The [Zone Mode] settings are read by the server: Offer at world creation, Coast width, " +
                "Sea floor depth, Start area radius, Location islands, Location island chance, Location " +
                "island reveal distance and Progression locations (boss altars and the places progression needs, " +
                "which rise on their own when a player first comes near).");
            w.AdminDivider();

            w.SubHeader("Commands");
            w.Bullet("fires_zones - the mode's status for this world and the zone you are in");
            w.Bullet("fires_zones_open [x y] [force] - open the square you look at, or x y; force skips the " +
                "must-touch-open-land rule. It opens a square as its own planned land.");
            w.Bullet("fires_skylands - the skylands nearest you; fires_skylands_goto [n] stands you on one " +
                "(devcommands)");
            w.Bullet("fires_zones_verify / fires_zones_dump plan|zones [radius] - diagnostics");
            w.AdminDivider();

            w.SubHeader("Keep These Files");
            w.Bullet("Copy the world's FiresWorldGen files (firesgen_<world>_zones.bin, _islands.bin, _skylands.txt, " +
                "_caves.txt, _hammer.txt, .mode, _baked.bin) along with the world save. The save also keeps a copy of " +
                "the open zones, so a world moved without them stays one zone at a time, but skylands, cave dressing " +
                "and the first-hammer chest keep their records in those files.");
            w.Bullet("A zone can never be closed again, and an existing world can't be switched to one zone at a time yet: only a new world can.");
        }

        private static void BuildWorldgen(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("FIRES Worlds & Sky Islands");
            w.Paragraph("FIRES worlds replace Valheim's terrain generation with their own continents, erosion, " +
                "climate-driven biomes, carved rivers, lakes, volcanoes and sky islands. They need Terrain Mode = " +
                "FullVoxel, and apply to worlds named Fires..., worlds you chose FIRES terrain for at creation, " +
                "and worlds with a baked cache.");
            w.AdminDivider();

            w.SubHeader("Custom Base Maps");
            w.Bullet("WorldGen Image Base - a greyscale PNG heightmap as the base world; WorldGen Image Min Y / " +
                "Max Y map black and white to heights");
            w.Bullet("WorldGen Biome Image - a biome colour PNG");
            w.Bullet("WorldGen VAHC Base - a .bin height cache as the base");
            w.Bullet("WorldGen Resolution (10501) - never change it on an existing world");
            w.AdminDivider();

            w.SubHeader("Sky Islands");
            w.Bullet("fires_islands - how many this world has, and where they are saved");
            w.Bullet("fires_islands_list [radius] / fires_islands_add [rolling|mountain|crater|grotto] [radius] " +
                "- add one with its base at your current height");
            w.Bullet("fires_islands_remove [radius] - builds on a removed island are left floating");
            w.Bullet("fires_islands_reset - re-roll islands in zones nobody has visited yet");
            w.Bullet("va_skyislands [pin|clear] - list them, or pin them on the map");
            w.AdminDivider();

            w.SubHeader("Water, Features & Far Terrain");
            w.Bullet("va_gen_water [radius] / va_gen_water clear - pin generated rivers, streams and lakes on the map");
            w.Bullet("va_water_scan radius=N step=N - survey generated water near you");
            w.Bullet("va_features [list|clear|all|<kind>] - pin generated features (river, lake, volcano, skyisland...)");
            w.Bullet("fires_bake_drift - how far a baked cache would move this world's ground");
            w.Bullet("Far Quilt Enabled and the Impostor settings - distant terrain and far trees");
        }

        private static void BuildConfig(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Key Config Levers");
            w.Paragraph("Everything is in the Fires config window (F8). These are the settings that matter most.");
            w.AdminDivider();

            w.SubHeader("Performance (VoxelTerrain Performance)");
            w.Bullet("Vegetation density scale (0.7) and Clutter density scale (0.55) - below vanilla by default: " +
                "fewer trees and bushes in newly generated zones, and less grass. Set both to 1 for vanilla.");
            w.Bullet("Grass render distance (0 = vanilla) and Terrain casts shadows - big frame-rate levers");
            w.Bullet("Chunk cache and Chunk cache size limit (MB) - keep generated chunks on disk so revisits " +
                "load instantly");
            w.Bullet("Voxel wrap radius (headless) - how far a dedicated server builds voxel terrain around each " +
                "player, the biggest server lever");
            w.Bullet("Max loaded chunks - a brush that hits the cap leaves holes");
            w.AdminDivider();

            w.SubHeader("Sync & Presets");
            w.Bullet("01 - General / 02 ServerAuthority - the server's settings win and are locked for players");
            w.Bullet("00 - Presets - save and load looks; 04 ServerPreset pushes one to everyone (each player needs " +
                "the preset file); 05 AdminOnlyShaderTuning locks the water-look sliders to admins");
            w.AdminDivider();

            w.SubHeader("Other");
            w.Bullet("03 - Water Engine / 10 OwnWavePhysics - FAT runs the wave clock (identical to vanilla); " +
                "turn it off to hand waves back to vanilla");
            w.Bullet("07 - Distant Paint / 01 Enabled - paint and river beds stay visible on distant terrain");
        }
    }
}
