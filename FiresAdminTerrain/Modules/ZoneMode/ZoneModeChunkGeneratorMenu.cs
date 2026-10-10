using System;
using System.Collections.Generic;
using FiresCore.Bridge;
using FiresCore.UI;
using UnityEngine;
using UnityEngine.UI;

namespace VerdantsAscent.Terrain.Zones
{
    // The Chunk Generator's two windows (Tools\CHUNK_GENERATOR.md): the ore hopper lists the recipes this player has
    // learned (hidden until learned) and switches the machine to one; the door shows the fuels the current recipe burns
    // that the player carries, and a click opens an amount step, so fuels can be mixed. Both are vanilla's own panels,
    // borrowed at runtime (ZoneModeChunkGeneratorWindows); if one cannot be borrowed the player gets a centre message
    // and the log says what was missing. This file holds what both share, the way Tools\FIRES_CLICKABLE_UI_RECIPE.md
    // sets out: a canvas of its own at the game's GUI scale, input blocked, registered with ModUiRegistry, the cursor
    // forced in OnGUI and the crosshair kept out of the way.
    internal static class ZoneModeChunkGeneratorMenu
    {
        private const string RegistryKey = "FiresChunkGenerator";
        private const string CanvasName = "FiresChunkGeneratorMenu";
        private const string CrosshairName = "crosshair";
        private const int SortingOrder = 220;
        private const float ReachMeters = 6f;
        private const string Unavailable = "The Chunk Generator's window could not open; the log says why.";

        private static GameObject s_canvas;
        private static Smelter s_machine;
        private static Player s_player;
        private static Action s_enter;
        private static Action s_escape;
        private static bool s_ownsBlock;
        private static GameObject s_crosshair;
        private static readonly List<Graphic> s_crosshairRaycasts = new List<Graphic>();

        internal static bool IsOpen => s_canvas != null;

        // ── The hopper: recipes ──────────────────────────────────────────────────────────────────────────────────
        internal static bool OpenRecipes(Smelter smelter, Player player)
        {
            if (!Usable(smelter, player)) return false;
            var current = ZoneModeChunkGenerator.CurrentRecipe(smelter.GetComponent<ZNetView>().GetZDO());
            if (ZoneModeChunkGeneratorWindows.TryOpenRecipes(smelter, player, current)) return true;
            player.Message(MessageHud.MessageType.Center, Unavailable);
            return false;
        }

        internal static string BurnsLine(ChunkTier recipe)
        {
            if (recipe.RestoresOnly) return "It burns any material from any biome.";
            var later = ZoneModeChunkTiers.ByLevel(recipe.Level + 1);
            return later == null ? $"It burns {recipe.Title} materials." : $"It burns {recipe.Title} materials and those of every later land.";
        }

        // ── The door: fuel. Nothing to add (full, or no fuel the recipe burns) is a centre message, never a window ──
        internal static bool OpenFuel(Smelter smelter, Player player, ItemDrop.ItemData item)
        {
            if (!Usable(smelter, player)) return false;
            var zdo = smelter.GetComponent<ZNetView>().GetZDO();
            string refusal = ZoneModeChunkGeneratorWindows.FuelRefusal(ZoneModeChunkGenerator.CurrentRecipe(zdo), player, ZoneModeChunkGenerator.Loaded(zdo));
            if (refusal != null)
            {
                player.Message(MessageHud.MessageType.Center, refusal);
                return false;
            }
            ItemDrop preselect = null;
            if (item?.m_dropPrefab != null) ZoneModeChunkGenerator.TryGetFuel(item.m_dropPrefab.name, out preselect);
            if (ZoneModeChunkGeneratorWindows.TryOpenFuel(smelter, player, preselect)) return true;
            player.Message(MessageHud.MessageType.Center, Unavailable);
            return false;
        }

        // ── The window's canvas ──────────────────────────────────────────────────────────────────────────────────
        private static bool Usable(Smelter smelter, Player player)
            => smelter != null && player != null && player == Player.m_localPlayer
               && ZoneModeChunkGenerator.IsOurs(smelter.GetComponent<ZNetView>()) && !MenuDialog.IsOpen;

        // A canvas at the game's GUI scale (so borrowed panels draw at vanilla size), with a clear full-screen catcher
        // behind them, input capture and the pump.
        internal static Transform OpenCanvas(Smelter smelter, Player player, float vanillaScale)
        {
            Close();
            s_machine = smelter;
            s_player = player;
            s_canvas = new GameObject(CanvasName);
            s_canvas.SetActive(false);
            var canvas = s_canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = s_canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = vanillaScale;
            s_canvas.AddComponent<GraphicRaycaster>();
            s_canvas.AddComponent<Pump>();
            s_canvas.SetActive(true);
            UIBuilderHelper.CreateImage(s_canvas.transform, "Blocker", Vector2.zero, Vector2.one, Color.clear);

            ModUiRegistry.Register(RegistryKey, () => IsOpen, Close, () => s_canvas);
            s_ownsBlock = !InputBlock.IsBlocked;
            if (s_ownsBlock) InputBlock.Block(true);
            CrosshairRaycast.Suppress(s_crosshairRaycasts);
            return s_canvas.transform;
        }

        internal static void SetKeys(Action enter, Action escape)
        {
            s_enter = enter;
            s_escape = escape;
        }

        internal static void Close()
        {
            var canvas = s_canvas;
            s_canvas = null;
            s_machine = null;
            s_player = null;
            s_enter = null;
            s_escape = null;
            ZoneModeChunkGeneratorWindows.Clear();
            if (canvas == null) return;
            UnityEngine.Object.Destroy(canvas);
            ModUiRegistry.Unregister(RegistryKey);
            if (s_ownsBlock) InputBlock.Block(false);
            s_ownsBlock = false;
            CrosshairRaycast.Restore(s_crosshairRaycasts);
            SetCrosshairHidden(false);
        }

        private static void SetCrosshairHidden(bool hidden)
        {
            if (s_crosshair == null && Hud.instance != null)
            {
                var found = UIBuilderHelper.FindChildRecursive(Hud.instance.transform, CrosshairName);
                s_crosshair = found != null ? found.gameObject : null;
            }
            if (s_crosshair != null && s_crosshair.activeSelf == hidden) s_crosshair.SetActive(!hidden);
        }

        // Keeps the window honest every frame: it closes when the player walks off, dies or the machine goes; Escape
        // steps back or closes, Enter confirms. The cursor is forced in OnGUI, which runs after the game re-locks it.
        private sealed class Pump : MonoBehaviour
        {
            private void Update()
            {
                if (s_machine == null || s_player == null || s_player.IsDead()
                    || Vector3.Distance(s_player.transform.position, s_machine.transform.position) > ReachMeters)
                {
                    Close();
                    return;
                }
                CrosshairRaycast.Reassert(s_crosshairRaycasts);
                SetCrosshairHidden(true);
                ZoneModeChunkGeneratorWindows.Tick();
                if (VerdantsAscent.Terrain.FatInput.KeyDown(KeyCode.Escape)) s_escape?.Invoke();
                else if ((VerdantsAscent.Terrain.FatInput.KeyDown(KeyCode.Return) || VerdantsAscent.Terrain.FatInput.KeyDown(KeyCode.KeypadEnter)) && s_enter != null) s_enter();
            }

            private void OnGUI()
            {
                if (!IsOpen) return;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            private void OnDestroy()
            {
                if (s_canvas == gameObject) Close();
            }
        }
    }
}
