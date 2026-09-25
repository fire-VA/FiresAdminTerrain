using UnityEngine;
using UnityEngine.Rendering;

namespace VerdantsAscent.Terrain.Voxel
{
    // Vanilla stops drawing the overworld while the player is inside an
    // interior (dungeon or crypt, y > 3000). Both renderers of the zone
    // prefab carry a RenderGroupSubscriber for RenderGroup.Overworld - the
    // Terrain and the WaterSurface - and ClutterSystem skips grass on the same
    // group. Everything we draw IN PLACE of that ground and those water sheets
    // has to follow, or a dungeon renders the whole surface world under its
    // floor. Colliders stay live, exactly as vanilla leaves them.
    //
    // This gate is the single owner of that state: it subscribes once and every
    // voxel renderer composes OverworldVisible into its own visibility, so the
    // flip lands in the same LateUpdate vanilla's own subscribers see.
    //
    // Deliberately NOT gated, because vanilla does not gate their counterparts:
    // placed water pours (TarLiquid clones), far vegetation impostors, the
    // terrain tiles and the brush preview.
    internal static class VoxelRenderGroup
    {
        private static bool s_registered;

        internal static bool OverworldVisible { get; private set; } = true;

        internal static void Arm()
        {
            if (s_registered || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            s_registered = RenderGroupSystem.Register(RenderGroup.Overworld, OnGroupChanged);
        }

        internal static void Disarm()
        {
            if (s_registered) RenderGroupSystem.Unregister(RenderGroup.Overworld, OnGroupChanged);
            s_registered = false;
            OverworldVisible = true;
        }

        internal static void Apply(Renderer renderer)
        {
            if (renderer != null) renderer.enabled = OverworldVisible;
        }

        private static void OnGroupChanged(bool shouldRender)
        {
            if (OverworldVisible == shouldRender) return;
            OverworldVisible = shouldRender;
            VoxelWorld.RefreshAllViewVisibility();
            FarQuilt.RefreshVisibility();
            Zones.ZoneModeBorderWalls.RefreshVisibility();
            Zones.ZoneModeSkylands.RefreshVisibility();
            FiresFarWater.RefreshVisibility();
            VoxelGenWaterMesh.RefreshVisibility();
            FiresLogger.LogInfo(shouldRender
                ? "[FiresVoxel] player left the interior - voxel terrain and water drawing again."
                : "[FiresVoxel] player entered an interior - voxel terrain and water hidden (colliders stay).");
        }
    }
}
