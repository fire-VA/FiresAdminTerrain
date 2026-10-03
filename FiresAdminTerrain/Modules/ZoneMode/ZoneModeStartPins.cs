using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // FAT's start objects on every player's map, and Hugin's word on the hidden Cultivator (Fire, 2026-09-27,
    // Tools\FAT_START_ITEMS.md). The server knows where the starter Chunk Generator and the two chests stand; a client
    // asks for them and is told again whenever one moves. The starter and the hammer chest are pinned at once. The
    // Cultivator chest is pinned only once its opener has found it, and Hugin gives its direction and rough distance
    // when the first Chunk Hammer is found (its chest opened, or the hammer first held).
    internal static class ZoneModeStartPins
    {
        private const string RpcAsk = "FiresZones_StartItemsAsk";
        private const string RpcTell = "FiresZones_StartItems";
        private const float AskSeconds = 30f;
        private const float MovedPin = 3f;
        private const float PaceMetres = 0.75f;
        private const int PaceRounding = 10;
        private const Minimap.PinType PinType = Minimap.PinType.Icon3;

        internal const string StarterPin = "Chunk Generator";
        internal const string HammerPin = "Hammer Chest";
        internal const string CultivatorPin = "Cultivator Chest";

        private enum Item : byte { Starter = 1, Hammer = 2, Cultivator = 3 }

        private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> PinsRef =
            AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

        private static ZRoutedRpc s_registeredOn;
        private static float s_nextAsk;
        private static bool s_haveCultivator;
        private static Vector3 s_cultivatorAt;
        private static ZoneModeCultivatorChest.Hiding s_cultivatorHiding;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_registeredOn, rpc)) return;
            try
            {
                rpc.Register(RpcAsk, RPC_Ask);
                rpc.Register<ZPackage>(RpcTell, RPC_Tell);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] start item RPC register failed: {ex.Message}"); }
        }

        internal static void Reset()
        {
            s_nextAsk = 0f;
            s_haveCultivator = false;
            s_cultivatorHiding = ZoneModeCultivatorChest.Hiding.None;
        }

        // ── Client ──────────────────────────────────────────────────────────
        internal static void ClientTick()
        {
            if (Time.time < s_nextAsk || Player.m_localPlayer == null || ZRoutedRpc.instance == null) return;
            s_nextAsk = Time.time + AskSeconds;
            var net = ZNet.instance;
            if (net == null) return;
            if (net.IsServer()) Take(Describe());
            else ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcAsk);
        }

        private static void RPC_Tell(long sender, ZPackage pkg) => Take(pkg);

        private static void Take(ZPackage pkg)
        {
            int count = pkg.ReadInt();
            s_haveCultivator = false;
            for (int i = 0; i < count; i++)
            {
                var item = (Item)pkg.ReadByte();
                var at = pkg.ReadVector3();
                var hiding = (ZoneModeCultivatorChest.Hiding)pkg.ReadByte();
                if (item == Item.Starter) Pin(StarterPin, at);
                else if (item == Item.Hammer) Pin(HammerPin, at);
                else if (item == Item.Cultivator)
                {
                    s_haveCultivator = true;
                    s_cultivatorAt = at;
                    s_cultivatorHiding = hiding;
                }
            }
        }

        // Pinned once for good; a pin of the same name that no longer marks the object (it was moved) goes first.
        private static void Pin(string name, Vector3 at)
        {
            var minimap = Minimap.instance;
            if (minimap == null || Player.m_localPlayer == null) return;
            var pins = PinsRef(minimap);
            if (pins != null)
                foreach (var stale in pins.FindAll(pin => pin.m_name == name && pin.m_save && Utils.DistanceXZ(pin.m_pos, at) > MovedPin))
                    minimap.RemovePin(stale);
            minimap.DiscoverLocation(at, PinType, name, false);
        }

        // Hugin's words on the Cultivator: which way and roughly how far from where the player stands, and whether it
        // lies in the dark under the land or among the trees.
        internal static void OfferCultivatorHint(Player player)
        {
            if (!s_haveCultivator || player == null || player != Player.m_localPlayer) return;
            var from = player.transform.position;
            int paces = Mathf.Max(PaceRounding, Mathf.RoundToInt(Mathf.Sqrt(ZoneModeStartSpots.FlatDistanceSquared(from, s_cultivatorAt)) / PaceMetres / PaceRounding) * PaceRounding);
            string where = s_cultivatorHiding == ZoneModeCultivatorChest.Hiding.Cave
                ? "down in the dark beneath the land, where a cave opens"
                : "hidden among the trees and stones";
            ZoneModeRavenHints.ShowCultivator(player,
                $"Something that tills the earth waits for you, little one: a <color=orange>Cultivator</color>, "
                + $"{where}, to the <color=orange>{ZoneModeStartSpots.Bearing(from, s_cultivatorAt)}</color>, "
                + $"some <color=orange>{paces} paces</color> from where you stand. Find it and your fields will follow.");
        }

        // ── Server ──────────────────────────────────────────────────────────
        private static void RPC_Ask(long sender)
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer()) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcTell, Describe());
        }

        // Tells every peer at once: something the pins mark has just been placed or moved.
        internal static void Changed()
        {
            var net = ZNet.instance;
            if (net == null || !net.IsServer() || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcTell, Describe());
        }

        private static ZPackage Describe()
        {
            var entries = new List<(Item Item, Vector3 At, ZoneModeCultivatorChest.Hiding Hiding)>();
            if (ZoneModeChunkMachines.TryGetStarter(out var starter, out _)) entries.Add((Item.Starter, starter, ZoneModeCultivatorChest.Hiding.None));
            if (ZoneModeHammerChest.TryGetSpot(out var hammer)) entries.Add((Item.Hammer, hammer, ZoneModeCultivatorChest.Hiding.None));
            if (ZoneModeCultivatorChest.TryGetSpot(out var cultivator, out var hiding)) entries.Add((Item.Cultivator, cultivator, hiding));
            var pkg = new ZPackage();
            pkg.Write(entries.Count);
            foreach (var entry in entries)
            {
                pkg.Write((byte)entry.Item);
                pkg.Write(entry.At);
                pkg.Write((byte)entry.Hiding);
            }
            pkg.SetPos(0);
            return pkg;
        }

        // Opening the hammer chest is finding the first hammer; opening the Cultivator chest pins it for its finder.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        private static class InventoryGui_Show_StartChests
        {
            private static void Postfix(Container container)
            {
                if (container == null || !ZoneModeRegistry.ModeActive) return;
                var view = container.GetComponent<ZNetView>();
                if (view == null) view = container.GetComponentInParent<ZNetView>();
                var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo == null) return;
                if (zdo.GetBool(ZoneModeHammerChest.HammerChestHash, false)) OfferCultivatorHint(Player.m_localPlayer);
                else if (zdo.GetBool(ZoneModeCultivatorChest.CultivatorChestHash, false)) Pin(CultivatorPin, container.transform.position);
            }
        }
    }
}
