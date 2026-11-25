using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1455 (Fire: "re target our custom water to use the ebm water or vedr water when those mods are installed"; contract
    // Tools\DESIGN_fat_water_handoff_20261117.md 3.1b, frozen v1 with [visual] + [seasons]). FAT HOSTS the water look hand-off: look
    // mods (EBM 300, Vedr 200) register through FatApi.RegisterWaterLook, and FAT swaps ITS OWN water renderers to the top live
    // provider's material, then puts its own back when that provider withdraws or goes inactive. The look mods never walk FAT's objects.
    // Renderers come from FAT's own build / destroy sites only (Adopt / Forget), never a scene search:
    //   generated sheets (VoxelChunkView.ApplyGenWater), placed sheets (PlacedWaterSheets.FinishBuild), combiner bodies (SetupMaterial).
    // Never offered: lava / tar / ice / snow materials, anything under a ZNetView, waterfalls, non-water combiners, and a sheet whose
    // WaterStateField state holds any ice, lava or crust column (only FAT's shader draws that state).
    // Main thread only. With no provider registered (or the key off) nothing is ever swapped: FAT draws exactly as 0.2.1454.
    internal static class WaterLookHost
    {
        // ---- config ----
        private const string Section = "Custom Water";
        internal static ConfigEntry<bool> HandOffCfg;
        private static bool Enabled => HandOffCfg == null || HandOffCfg.Value;

        internal static void BindConfig(ConfigFile cfg)
        {
            NoteMainThread();
            if (cfg == null || HandOffCfg != null) return;
            HandOffCfg = cfg.Bind(Section, "Hand Off To Look Mods", true,
                "When EBM's or Vedr's water look is on, FAT's own rivers, lakes and placed water wear that look (FAT keeps lava, ice, crust and waterfalls). Off = FAT's own water shader always. Live.");
            HandOffCfg.SettingChanged += (_, __) => s_reeval = true;   // applied on the next main-thread Tick
        }

        // ---- providers (highest priority first; equal priority: first registered first) ----
        private sealed class Provider
        {
            public string Owner;
            public int Priority;
            public long Seq;
            public Func<bool> Active;
            public Func<FatWaterLookRequest, Material> Build;
            public Action<Renderer, FatWaterLookRequest> ApplyBody;
            public Action<Material> Release;
            public bool Dropped;
            public bool Internal;   // 0.2.1466: FAT's own vanilla-material provider (InlandVanillaMatch): not gated by the hand-off key
        }

        private static readonly List<Provider> s_providers = new List<Provider>(4);
        private static Provider s_top;   // the provider whose materials FAT's renderers wear now (null = FAT's own shader)
        private static long s_seq;
        private static bool s_busy, s_reeval;
        private static readonly List<Action> s_pending = new List<Action>();   // API calls made from inside a provider callback

        // ---- tracked renderers ----
        private sealed class Entry
        {
            public Renderer R;
            public int Id, Index;
            public FatWaterKind Kind;
            public bool Fresh = true;            // combiner: plain fresh water (other liquids are never offered)
            public WaterMeshCombiner Combiner;   // Body only
            public Material Original;            // the material FAT put there
            public Material Provided;            // the provider's material on it now (null = FAT's own)
            public FatWaterLookRequest Req;
            public bool ReqValid;
            public Bounds LastBounds;
            public bool HasBounds, NetViewChecked, UnderNetView;
        }

        private static readonly Dictionary<int, Entry> s_entries = new Dictionary<int, Entry>(1024);
        private static readonly List<Entry> s_list = new List<Entry>(1024);

        // The top provider's answers, cleared on every provider change: FAT original -> provider material (build may return a SHARED
        // one), originals it answered null for ("not yet": asked again on Refresh), and each distinct material it returned (released once).
        private static readonly Dictionary<Material, Material> s_cache = new Dictionary<Material, Material>();
        private static readonly HashSet<Material> s_notYet = new HashSet<Material>();
        private static readonly List<Material> s_returned = new List<Material>();
        private static readonly Dictionary<Material, bool> s_matOk = new Dictionary<Material, bool>();   // name / slot test, once per material

        // ---- surface revision + event ----
        private static int s_revision;
        private static bool s_eventPending;
        private static Action[] s_listeners = new Action[0];
        private static int s_listenerThrows;
        internal static int Revision => s_revision;

        private static int s_mainThread = -1;
        private static float s_nextCheck, s_nextCensus;
        private static bool s_adoptWarned, s_tickWarned, s_forgetWarned;

        private static void NoteMainThread()
        {
            if (s_mainThread < 0) s_mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        private static bool OffMainThread => s_mainThread >= 0 && System.Threading.Thread.CurrentThread.ManagedThreadId != s_mainThread;

        // ================================================================== provider API (FatApi)

        // 0.2.1466: FAT's own provider (vanilla's water material, InlandVanillaMatch). Same rules as a look mod, but [Custom Water] Hand Off To
        // Look Mods does not gate it: that key hands FAT's water to OTHER mods; with it off this provider is the only one asked.
        internal static void RegisterInternal(string owner, int priority, Func<bool> active, Func<FatWaterLookRequest, Material> build,
            Action<Renderer, FatWaterLookRequest> applyBody, Action<Material> release)
            => Register(owner, priority, active, build, applyBody, release, true);

        internal static bool TopIsInternal => s_top != null && s_top.Internal && !s_top.Dropped;

        internal static void Register(string owner, int priority, Func<bool> active, Func<FatWaterLookRequest, Material> build,
            Action<Renderer, FatWaterLookRequest> applyBody, Action<Material> release)
            => Register(owner, priority, active, build, applyBody, release, false);

        private static void Register(string owner, int priority, Func<bool> active, Func<FatWaterLookRequest, Material> build,
            Action<Renderer, FatWaterLookRequest> applyBody, Action<Material> release, bool isInternal)
        {
            if (string.IsNullOrEmpty(owner) || active == null || build == null)
            {
                FiresLogger.LogWarning($"[FiresWater] FAT water look: RegisterWaterLook refused for '{owner ?? "null"}' (owner, active and build are required) (0.2.1455)");
                return;
            }
            if (OffMainThread) { FiresLogger.LogWarning($"[FiresWater] FAT water look: RegisterWaterLook from '{owner}' off the main thread refused (0.2.1455)"); return; }
            if (s_busy) { s_pending.Add(() => Register(owner, priority, active, build, applyBody, release, isInternal)); return; }
            int at = Find(owner);
            if (at >= 0)
            {
                var old = s_providers[at];
                if (old == s_top) Switch(null, false);   // its renderers back to FAT's, its materials released
                s_providers.RemoveAt(at);
            }
            var p = new Provider { Owner = owner, Priority = priority, Seq = ++s_seq, Active = active, Build = build, ApplyBody = applyBody, Release = release, Internal = isInternal };
            int i = 0;
            while (i < s_providers.Count && s_providers[i].Priority >= priority) i++;
            s_providers.Insert(i, p);
            FiresLogger.LogInfo($"[FiresWater] FAT water look: provider '{owner}' registered (priority {priority}; {s_providers.Count} provider(s)); key Hand Off To Look Mods {(Enabled ? "on" : "off")} (0.2.1455)");
            Evaluate();
        }

        internal static void Unregister(string owner)
        {
            if (string.IsNullOrEmpty(owner) || OffMainThread) return;
            if (s_busy) { s_pending.Add(() => Unregister(owner)); return; }
            int at = Find(owner);
            if (at < 0) return;
            var p = s_providers[at];
            if (p == s_top) Switch(null, false);
            s_providers.RemoveAt(at);
            FiresLogger.LogInfo($"[FiresWater] FAT water look: provider '{owner}' withdrew ({s_providers.Count} provider(s) left) (0.2.1455)");
            Evaluate();
        }

        // The provider's active() or look template changed: FAT re-asks (the top one's materials are released and built again).
        internal static void Refresh(string owner)
        {
            if (string.IsNullOrEmpty(owner) || OffMainThread) return;
            if (s_busy) { s_pending.Add(() => Refresh(owner)); return; }
            int at = Find(owner);
            if (at < 0) return;
            var want = Want();
            if (want == s_top && want != null && want == s_providers[at]) Switch(want, true);
            else if (want != s_top) Switch(want, false);
        }

        internal static void AddListener(Action a)
        {
            if (a == null) return;
            var n = new Action[s_listeners.Length + 1];
            Array.Copy(s_listeners, n, s_listeners.Length);
            n[n.Length - 1] = a;
            s_listeners = n;   // allocation only on subscribe
        }

        internal static void RemoveListener(Action a)
        {
            if (a == null) return;
            int at = Array.IndexOf(s_listeners, a);
            if (at < 0) return;
            var n = new Action[s_listeners.Length - 1];
            if (at > 0) Array.Copy(s_listeners, 0, n, 0, at);
            if (at < s_listeners.Length - 1) Array.Copy(s_listeners, at + 1, n, at, s_listeners.Length - at - 1);
            s_listeners = n;
        }

        private static int Find(string owner)
        {
            for (int i = 0; i < s_providers.Count; i++) if (s_providers[i].Owner == owner) return i;
            return -1;
        }

        // ================================================================== FAT's renderers

        // FAT's build sites, right after the renderer has FAT's material (and, for sheets, its bound state). Re-adopting is how FAT says
        // "this renderer was rebuilt": a new FAT material becomes the original, moved bounds re-ask the level / size.
        internal static void Adopt(Renderer r, FatWaterKind kind, bool freshWater = true)
        {
            if (ReferenceEquals(r, null) || r == null) return;
            try
            {
                int id = r.GetInstanceID();
                bool added = false;
                if (!s_entries.TryGetValue(id, out var e))
                {
                    e = new Entry { R = r, Id = id, Index = s_list.Count };
                    s_entries[id] = e;
                    s_list.Add(e);
                    added = true;
                }
                else if (!ReferenceEquals(e.R, r)) { e.R = r; e.Provided = null; e.NetViewChecked = false; e.Combiner = null; e.ReqValid = false; e.HasBounds = false; }
                e.Kind = kind;
                e.Fresh = freshWater;
                var cur = r.sharedMaterial;
                if (e.Provided == null || cur != e.Provided) { e.Original = cur; e.Provided = null; }   // FAT (re)assigned its own
                if (kind == FatWaterKind.Body && e.Combiner == null) e.Combiner = r.GetComponent<WaterMeshCombiner>();
                Bounds b = e.Combiner != null ? e.Combiner.GetCombinedBounds() : r.bounds;   // a body's own extent (its renderer is hidden under placed sheets)
                bool moved = e.HasBounds && (b.center != e.LastBounds.center || b.size != e.LastBounds.size);
                e.LastBounds = b; e.HasBounds = true;
                if (added || moved) Bump();
                if (s_top == null || s_busy || !(Enabled || s_top.Internal)) { e.ReqValid = false; return; }   // nothing to hand off: no request work at all
                bool bodyChanged = false;
                if (moved || !e.ReqValid)
                {
                    bool had = e.ReqValid;
                    float oldLevel = e.Req.Level, oldSize = e.Req.BodySize;
                    e.Req = BuildRequest(e);
                    e.ReqValid = true;
                    if (had && (Mathf.Abs(e.Req.Level - oldLevel) > 0.01f || Mathf.Abs(e.Req.BodySize - oldSize) > 0.01f)) { bodyChanged = true; Bump(); }
                }
                e.Req.Original = e.Original;
                ApplyOne(e, added || bodyChanged);
                if (e.Provided != null && s_top != null && s_top.Internal) InlandVanillaMatch.EnsureTangents(r);   // 0.2.1466: a rebuilt mesh has no tangents
            }
            catch (Exception ex)
            {
                if (!s_adoptWarned) { s_adoptWarned = true; FiresLogger.LogWarning($"[FiresWater] FAT water look: adopt threw ({ex.GetType().Name}: {ex.Message}); that renderer keeps FAT's material (0.2.1455)"); }
            }
        }

        // FAT's destroy sites. Puts FAT's material back if the provider's is still on it.
        internal static void Forget(Renderer r)
        {
            if (ReferenceEquals(r, null)) return;
            try
            {
                if (!s_entries.TryGetValue(r.GetInstanceID(), out var e) || !ReferenceEquals(e.R, r)) return;
                if (e.Provided != null && r != null && r.sharedMaterial == e.Provided) r.sharedMaterial = e.Original;
                Remove(e);
                Bump();
            }
            catch (Exception ex)
            {
                if (!s_forgetWarned) { s_forgetWarned = true; FiresLogger.LogWarning($"[FiresWater] FAT water look: forget threw ({ex.GetType().Name}: {ex.Message}) (0.2.1455)"); }
            }
        }

        // FAT's own material re-assignments ask this first: true = the host holds this renderer on a provider material whose original is
        // exactly fatMat, so the write is skipped (the look stays). Any other material = FAT's write goes ahead (and the next Adopt records it).
        internal static bool Keeps(Renderer r, Material fatMat)
        {
            if (r == null || fatMat == null || s_entries.Count == 0) return false;
            return s_entries.TryGetValue(r.GetInstanceID(), out var e) && ReferenceEquals(e.R, r) && e.Provided != null
                && r.sharedMaterial == e.Provided && e.Original == fatMat;
        }

        // The material FAT put on r (its original while a provider's is on it; else r's own). WaterStateField.Bind tests this, so the
        // state texture keeps binding (and its ice / lava / crust test keeps working) while a look mod draws the sheet.
        internal static Material FatMaterialOf(Renderer r)
        {
            if (r == null) return null;
            if (s_entries.Count > 0 && s_entries.TryGetValue(r.GetInstanceID(), out var e) && ReferenceEquals(e.R, r)
                && e.Provided != null && r.sharedMaterial == e.Provided) return e.Original;
            return r.sharedMaterial;
        }

        // WaterStateField.Fill: this renderer's state now holds (or no longer holds) ice, lava or crust.
        internal static void StateChanged(Renderer r)
        {
            if (r == null || s_top == null || s_busy || s_entries.Count == 0) return;
            try
            {
                if (!s_entries.TryGetValue(r.GetInstanceID(), out var e) || !ReferenceEquals(e.R, r)) return;
                ApplyOne(e, false);
            }
            catch (Exception) { }
        }

        private static void Remove(Entry e)
        {
            int i = e.Index, last = s_list.Count - 1;
            if (i >= 0 && i <= last && s_list[i] == e)
            {
                var tail = s_list[last];
                s_list[i] = tail;
                tail.Index = i;
                s_list.RemoveAt(last);
            }
            s_entries.Remove(e.Id);
        }

        private static void Bump()
        {
            s_revision++;
            s_eventPending = true;   // raised once on the next Tick (never inside FAT's mesh apply)
        }

        // ================================================================== switching

        private static Provider Want()
        {
            bool handOff = Enabled;
            for (int i = 0; i < s_providers.Count; i++)
            {
                var p = s_providers[i];
                if (p.Dropped || (!handOff && !p.Internal)) continue;   // 0.2.1466: the hand-off key gates look mods, not FAT's own provider
                bool on;
                try { on = p.Active(); }
                catch (Exception ex) { Drop(p, "active()", ex); continue; }
                if (on) return p;
            }
            return null;
        }

        private static void Evaluate()
        {
            if (s_busy) { s_reeval = true; return; }
            var want = Want();
            if (want != s_top) Switch(want, false);
        }

        // Every renderer back to FAT's material, the old provider's materials released, then the new one asked.
        private static void Switch(Provider next, bool force)
        {
            var old = s_top;
            s_busy = true;
            try
            {
                if (old != null) { RestoreAll(); ReleaseAll(old); }
                s_cache.Clear(); s_notYet.Clear(); s_returned.Clear();
                s_top = next;
                if (next != null)
                {
                    for (int i = 0; i < s_list.Count; i++)
                    {
                        var e = s_list[i];
                        e.ReqValid = false;
                        ApplyOne(e, true);
                        if (next.Dropped) break;
                    }
                    if (next.Dropped)
                    {
                        // it threw while FAT asked it: its water goes back to FAT's shader now; the next one is asked on the next Tick
                        RestoreAll();
                        s_cache.Clear(); s_notYet.Clear(); s_returned.Clear();
                        s_top = null;
                        s_reeval = true;
                    }
                }
            }
            finally { s_busy = false; }
            if (old != s_top) SayChange();   // Info once per change of top provider
            else if (force && s_top != null && FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[FiresWater] FAT water look: '{s_top.Owner}' refreshed, FAT re-asked it ({s_returned.Count} material(s)) (0.2.1455)");
        }

        private static void ApplyOne(Entry e, bool fresh)
        {
            var top = s_top;
            if (top == null || top.Dropped || e.R == null) return;
            if (!Eligible(e)) { RestoreOne(e); return; }
            if (!e.ReqValid) { e.Req = BuildRequest(e); e.ReqValid = true; }
            var mat = MaterialFor(top, e);
            if (mat == null) { RestoreOne(e); return; }   // "not yet": FAT's own stays until a Refresh
            bool assigned = false;
            if (e.R.sharedMaterial != mat) { e.R.sharedMaterial = mat; assigned = true; }
            e.Provided = mat;
            if ((assigned || fresh) && top.ApplyBody != null)
            {
                try { top.ApplyBody(e.R, e.Req); }
                catch (Exception ex) { Drop(top, "applyBody()", ex); }
            }
        }

        private static Material MaterialFor(Provider top, Entry e)
        {
            var o = e.Original;
            if (o == null) return null;
            if (s_cache.TryGetValue(o, out var m))
            {
                if (m != null) return m;
                s_cache.Remove(o);   // the provider destroyed it: ask again
            }
            if (s_notYet.Contains(o)) return null;
            Material built;
            try { built = top.Build(e.Req); }
            catch (Exception ex) { Drop(top, "build()", ex); return null; }
            if (built == null) { s_notYet.Add(o); return null; }
            s_cache[o] = built;
            if (!s_returned.Contains(built)) s_returned.Add(built);
            return built;
        }

        private static void RestoreOne(Entry e)
        {
            if (e.Provided == null) return;
            if (e.R != null && e.R.sharedMaterial == e.Provided) e.R.sharedMaterial = e.Original;
            e.Provided = null;
        }

        private static void RestoreAll()
        {
            for (int i = 0; i < s_list.Count; i++) RestoreOne(s_list[i]);
        }

        // Once per distinct material the provider returned, after no renderer of FAT's wears it. A dropped provider is not called again.
        private static void ReleaseAll(Provider p)
        {
            if (p.Release == null || p.Dropped) return;
            for (int i = 0; i < s_returned.Count; i++)
            {
                try { p.Release(s_returned[i]); }
                catch (Exception ex) { Drop(p, "release()", ex); return; }
            }
        }

        private static void Drop(Provider p, string what, Exception ex)
        {
            if (p.Dropped) return;
            p.Dropped = true;
            s_reeval = true;
            FiresLogger.LogWarning($"[FiresWater] FAT water look: provider '{p.Owner}' threw in {what} ({ex.GetType().Name}: {ex.Message}); dropped for this session, FAT's own shader takes its water back (0.2.1455)");
        }

        // ================================================================== eligibility

        private static readonly List<ZNetView> s_netViews = new List<ZNetView>(2);

        private static bool Eligible(Entry e)
        {
            var r = e.R;
            if (r == null || e.Original == null) return false;
            if (e.Kind == FatWaterKind.Body && !e.Fresh) return false;   // lava / tar / ice / snow / boiling / freezing bodies stay FAT's
            if (!e.NetViewChecked)
            {
                e.NetViewChecked = true;
                s_netViews.Clear();
                r.GetComponentsInParent(true, s_netViews);
                e.UnderNetView = s_netViews.Count > 0;   // a built piece
                s_netViews.Clear();
            }
            if (e.UnderNetView) return false;
            if (!MaterialOk(e.Original)) return false;
            if (e.Kind != FatWaterKind.Body && WaterStateField.HoldsIceLavaCrust(r)) return false;   // only FAT's shader draws the state
            return true;
        }

        private static bool MaterialOk(Material m)
        {
            if (s_matOk.TryGetValue(m, out bool ok)) return ok;
            ok = !WaterMaterialManager.IsNonWaterLiquid(m);
            if (ok)
            {
                string n = m.name ?? string.Empty;
                string sh = m.shader != null ? (m.shader.name ?? string.Empty) : string.Empty;
                if (Has(n, "lava") || Has(n, "magma") || Has(n, "tar") || Has(n, "ice") || Has(n, "snow")
                    || Has(sh, "lava") || sh.StartsWith("Custom/FiresWaterfall", StringComparison.Ordinal)) ok = false;
            }
            if (s_matOk.Count > 256) s_matOk.Clear();
            s_matOk[m] = ok;
            return ok;
        }

        private static bool Has(string s, string token) => s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        // ================================================================== the request

        private const float PondMaxM = 32f;   // a combiner body under this (max horizontal extent) is a Pond, else a Lake

        private static FatWaterLookRequest BuildRequest(Entry e)
        {
            Bounds b = e.R.bounds;
            var q = new FatWaterLookRequest
            {
                Original = e.Original, Kind = e.Kind, Shape = FatWaterShape.Unknown, RendererBounds = b,
                BodySize = 0f, Level = b.max.y, Flow = Vector2.zero,
            };
            if (e.Kind == FatWaterKind.Body)
            {
                var c = e.Combiner;
                if (c != null)
                {
                    Bounds cb = c.GetCombinedBounds();
                    if (b.size == Vector3.zero) q.RendererBounds = cb;   // a disabled renderer reports empty bounds: the body's own
                    q.BodySize = Mathf.Max(cb.size.x, cb.size.z);
                    q.Level = BodyLevel(c, cb);
                    q.Shape = q.BodySize < PondMaxM ? FatWaterShape.Pond : FatWaterShape.Lake;
                }
                return q;
            }
            Vector3 centre = b.center;
            if (TryWaterBodyAt(centre, out float y, out float size, out _)) { q.Level = y; q.BodySize = size; }
            if (e.Kind == FatWaterKind.InlandSheet) q.Shape = ShapeOf(GenWaterField.WaterClassAt(centre.x, centre.z));
            return q;
        }

        private static FatWaterShape ShapeOf(byte cls)
        {
            switch (cls)
            {
                case FiresWorldGenBase.WC_RIVER:
                case FiresWorldGenBase.WC_STREAM: return FatWaterShape.River;
                case FiresWorldGenBase.WC_LAKE: return FatWaterShape.Lake;
                case FiresWorldGenBase.WC_POND: return FatWaterShape.Pond;
                default: return FatWaterShape.Unknown;
            }
        }

        // A body's surface: the settled level of an (older, restored) pond-mode body; else its highest disk (the ruler height each disk
        // is placed at); else the bounds top less the 2 m CalculateCombinedBounds adds over the highest disk.
        private static float BodyLevel(WaterMeshCombiner c, Bounds cb)
        {
            if (c.IsPondMode && c.SettledWaterLevel > -1f) return c.SettledWaterLevel;
            var disks = c.WaterDisks;
            float top = float.MinValue;
            for (int i = 0; i < disks.Count; i++) { var d = disks[i]; if (d != null && d.Position.y > top) top = d.Position.y; }
            return top > float.MinValue ? top : cb.max.y - 2f;
        }

        // The surface at p: the highest disk that covers p, else the body's level.
        private static float BodyLevelAt(WaterMeshCombiner c, Vector3 p, Bounds cb)
        {
            if (c.IsPondMode && c.SettledWaterLevel > -1f) return c.SettledWaterLevel;
            var disks = c.WaterDisks;
            float top = float.MinValue;
            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i];
                if (d != null && d.Position.y > top && d.ContainsPositionWithBlend(p, WaterMeshCombiner.BlendDistance)) top = d.Position.y;
            }
            return top > float.MinValue ? top : BodyLevel(c, cb);
        }

        // ================================================================== FatApi.TryWaterBodyAt (FAT's DATA, not its renderers)

        private const float SeaGate = 30.3f;   // FAT never claims the sea level (GenWaterField's SeaSheetGate)
        private static readonly float[] s_probeR = { 8f, 16f, 32f, 64f, 128f, 256f };
        private static readonly float[] s_dirX = { 1f, 0.70710678f, 0f, -0.70710678f, -1f, -0.70710678f, 0f, 0.70710678f };
        private static readonly float[] s_dirZ = { 0f, 0.70710678f, 1f, 0.70710678f, 0f, -0.70710678f, -1f, -0.70710678f };
        private static readonly Dictionary<long, float> s_extent = new Dictionary<long, float>(1024);
        private static object s_extentWorld;
        private static bool s_extentReady;

        // Tool water first (the smallest combiner body whose bounds and disks cover p.xz), else generated water above the sea. Main thread;
        // allocation-free once the 32 m cell's extent is cached. Works on a dedicated server (no renderer is read).
        internal static bool TryWaterBodyAt(Vector3 p, out float surfaceY, out float size, out int kind)
        {
            surfaceY = 0f; size = 0f; kind = -1;
            if (OffMainThread) return false;
            var all = WaterMeshCombiner.AllCombiners;
            WaterMeshCombiner best = null;
            float bestSize = float.MaxValue;
            Bounds bestB = default;
            if (all != null)
                for (int i = 0; i < all.Count; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    var t = c.CustomLiquidType;
                    if (t == CustomLiquidType.Lava || t == CustomLiquidType.Tar || t.IsSolid()) continue;   // not water
                    Bounds b = c.GetCombinedBounds();
                    if (p.x < b.min.x || p.x > b.max.x || p.z < b.min.z || p.z > b.max.z) continue;
                    if (!c.IsWithinAnyDiskRadius(p)) continue;
                    float s = Mathf.Max(b.size.x, b.size.z);
                    if (s < bestSize) { bestSize = s; best = c; bestB = b; }
                }
            if (best != null)
            {
                surfaceY = BodyLevelAt(best, p, bestB);
                size = bestSize;
                kind = (int)FatWaterKind.Body;
                return true;
            }
            // 0.2.1471: a dedicated server holds no combiners where the players are; their bodies are read from the anchor records instead
            if (WaterMeshCombinerAnchor.DediActive && WaterMeshCombinerAnchor.DediTryWaterBodyAt(p, out float dy, out float ds))
            {
                surfaceY = dy;
                size = ds;
                kind = (int)FatWaterKind.Body;
                return true;
            }
            if (GenWaterField.TryWaterSurfaceSmooth(p.x, p.z, out float level, out _) && level > SeaGate)
            {
                surfaceY = level;
                size = GenExtent(p.x, p.z);
                kind = (int)FatWaterKind.InlandSheet;
                return true;
            }
            return false;
        }

        private static bool GenWet(float x, float z) => GenWaterField.TryWaterSurfaceSmooth(x, z, out float l, out _) && l > SeaGate;

        // Extent estimate of generated water: 8 directions, 8 .. 256 m out until dry; the widest dry radius x 2. Cached per 32 m cell;
        // dropped past 4096 cells or when the generated field itself changes (another world, or the field becoming queryable).
        private static float GenExtent(float x, float z)
        {
            object world = ZNet.World;
            bool ready = GenWaterField.SurfaceReady;
            if (!ReferenceEquals(world, s_extentWorld) || ready != s_extentReady) { s_extent.Clear(); s_extentWorld = world; s_extentReady = ready; }
            long key = ((long)Mathf.FloorToInt(x / 32f) << 32) ^ (uint)Mathf.FloorToInt(z / 32f);
            if (s_extent.TryGetValue(key, out float cached)) return cached;
            float widest = 0f;
            for (int d = 0; d < 8; d++)
            {
                float dry = s_probeR[s_probeR.Length - 1];   // still wet at 256 m: counted as 256
                for (int k = 0; k < s_probeR.Length; k++)
                {
                    float r = s_probeR[k];
                    if (!GenWet(x + s_dirX[d] * r, z + s_dirZ[d] * r)) { dry = r; break; }
                }
                if (dry > widest) widest = dry;
            }
            float size = widest * 2f;
            if (s_extent.Count >= 4096) s_extent.Clear();
            s_extent[key] = size;
            return size;
        }

        // ================================================================== tick, proof lines

        // Game.Update (CustomWaterPatches): API calls queued from inside a callback, the coalesced WaterSurfacesChanged, the 1 s
        // provider check (active() only, no scene walk) and the 30 s Debug census.
        internal static void Tick()
        {
            try
            {
                NoteMainThread();
                if (WaterMeshCombinerAnchor.DediActive && WaterMeshCombinerAnchor.DediSweep()) Bump();   // 0.2.1472: dedi placed-water changes
                if (s_pending.Count > 0 && !s_busy)
                {
                    var n = s_pending.Count;
                    for (int i = 0; i < n && i < s_pending.Count; i++) s_pending[i]();
                    s_pending.RemoveRange(0, Mathf.Min(n, s_pending.Count));
                }
                if (s_eventPending)
                {
                    s_eventPending = false;
                    var ls = s_listeners;
                    for (int i = 0; i < ls.Length; i++)
                    {
                        try { ls[i](); }
                        catch (Exception ex) { if (++s_listenerThrows <= 3) FiresLogger.LogWarning($"[FiresWater] FAT water look: a WaterSurfacesChanged listener threw ({ex.GetType().Name}: {ex.Message}) (0.2.1455)"); }
                    }
                }
                float now = Time.realtimeSinceStartup;
                if (s_reeval || ((s_providers.Count > 0 || s_top != null) && now >= s_nextCheck))
                {
                    s_reeval = false;
                    s_nextCheck = now + 1f;
                    Evaluate();
                }
                if (now >= s_nextCensus)
                {
                    s_nextCensus = now + 30f;
                    Prune();
                    if (FiresLogger.DebugEnabled && (s_providers.Count > 0 || s_list.Count > 0)) Census();
                }
            }
            catch (Exception ex)
            {
                if (!s_tickWarned) { s_tickWarned = true; FiresLogger.LogWarning($"[FiresWater] FAT water look: tick threw ({ex.GetType().Name}: {ex.Message}) (0.2.1455)"); }
            }
        }

        // Entries whose renderer died without a Forget (a world unload).
        private static void Prune()
        {
            for (int i = s_list.Count - 1; i >= 0; i--)
                if (s_list[i].R == null) Remove(s_list[i]);
        }

        private static void Count(out int inland, out int bodies, out int placed, out int fat, out int kept)
        {
            inland = bodies = placed = fat = kept = 0;
            for (int i = 0; i < s_list.Count; i++)
            {
                var e = s_list[i];
                if (e.R == null) continue;
                if (e.Provided != null)
                {
                    if (e.Kind == FatWaterKind.InlandSheet) inland++;
                    else if (e.Kind == FatWaterKind.Body) bodies++;
                    else placed++;
                }
                else
                {
                    fat++;
                    if (e.Kind != FatWaterKind.Body && WaterStateField.HoldsIceLavaCrust(e.R)) kept++;
                }
            }
        }

        private static void SayChange()
        {
            if (s_top == null) { FiresLogger.LogInfo("[FiresWater] FAT water look: FAT's own shader (no live provider)"); return; }
            Count(out int inland, out int bodies, out int placed, out int fat, out int kept);
            FiresLogger.LogInfo($"[FiresWater] FAT water look: {s_top.Owner} (priority {s_top.Priority}) draws {inland} inland sheet(s), {bodies} body(ies), {placed} placed sheet(s); FAT's own shader on {fat} ({kept} kept: ice/lava/crust state)");
        }

        private static void Census()
        {
            Count(out int inland, out int bodies, out int placed, out int fat, out int kept);
            int dropped = 0;
            for (int i = 0; i < s_providers.Count; i++) if (s_providers[i].Dropped) dropped++;
            FiresLogger.LogDebug($"[FiresWater] FAT water look census: {(s_top != null ? $"{s_top.Owner} (priority {s_top.Priority})" : "FAT's own shader")}, {s_providers.Count} provider(s) ({dropped} dropped), key {(Enabled ? "on" : "off")}; "
                + $"{s_list.Count} tracked: provider draws {inland} inland sheet(s), {bodies} body(ies), {placed} placed sheet(s); FAT's own shader on {fat} ({kept} kept: ice/lava/crust state); "
                + $"{s_returned.Count} provider material(s), {s_notYet.Count} not yet; surfaces revision {s_revision} (0.2.1455)");
        }
    }
}
