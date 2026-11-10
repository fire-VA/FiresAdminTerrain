using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── The V-tool stroke framework: terrain sculpting tools in the spirit of Blender's brushes,
    // at a manageable draw speed ───────────────────────────────────────────────────────────────────────────────────────────
    // VoxelBrushDrag owns the input: the click gate, the crosshair (HitAny = today's ray, water included; HitGround = water
    // and triggers ignored), the radius on scroll, a per-tool OFFSET on PgUp/PgDn (Shift fine, held = accelerating), and the
    // stroke: Begin on the press, Dab at the tool's DrawSpeed (dabs per second) each time the crosshair has moved sideways
    // by the spacing, End on the release or a tool change. A driver per tool family says what the tool draws (its Ghost,
    // the real shape) and what each of those calls does. Drivers are found by the hammer piece's name. Water drivers are
    // in WaterToolDriver.cs; the terrain ones are below.
    internal enum GhostShape { None, Disc, Cylinder, Sphere, Cube }
    internal enum GhostMaterial { Terrain, Water, Lines }

    internal struct Ghost
    {
        public GhostShape Shape;
        public Vector3 Centre;       // the shape's middle
        public float Radius, Height;  // Height: a disc's or cylinder's full height
        public GhostMaterial Material;
    }

    internal struct StrokeInput
    {
        public string Piece;          // the hammer piece's name ("Vcarve", "Vlevel", ...)
        public Vector3 HitAny, HitGround, Normal, View;
        public bool HasGround;
        public float Radius, Offset;
        public bool Alt, First;
        public float Time;
    }

    internal interface IVToolDriver
    {
        float DrawSpeed { get; }       // dabs per second while held
        bool ClickOnly { get; }        // true: Begin only, one action per click
        float DefaultOffset { get; }
        float MinOffset { get; }       // the lowest offset PgDn reaches (the world bounds clamp too)
        string OffsetLabel { get; }    // the readout: "depth", "height", ...
        Ghost Preview(in StrokeInput s);
        void Begin(in StrokeInput s);
        void Dab(in StrokeInput s);
        void End(in StrokeInput s);
        void DrawExtras(in StrokeInput s);
    }

    internal static class VToolDrivers
    {
        private static readonly Dictionary<string, IVToolDriver> s_byPiece = new Dictionary<string, IVToolDriver>();

        internal static void Register(string piece, IVToolDriver driver) { if (!string.IsNullOrEmpty(piece) && driver != null) s_byPiece[piece] = driver; }

        internal static bool TryGet(string piece, out IVToolDriver driver)
        {
            driver = null;
            return piece != null && s_byPiece.TryGetValue(piece, out driver);
        }
    }

    public sealed partial class VoxelBrushDrag
    {
        // ── The stroke loop (from Update, for every tool whose piece has a driver) ─────────────────────────────────────
        private IVToolDriver _activeDriver;
        private StrokeInput _lastInput;

        // Undo: FatUndo keeps a chunk's state only inside a player-edit scope, which it opens on DispatchNow. The
        // driver tools run from here, outside it, so their strokes would make no undo step: every driver call (and the
        // level / skin queues, PumpLevel / PumpSkin) now runs inside BeginPlayerEdit / EndPlayerEdit. A drag stays one step
        // (FatUndo closes a stroke 0.4 s after its last dab).
        private static string UndoName(string piece) => $"a {piece ?? "V-tool"} stroke";

        private void EndStroke()
        {
            if (_activeDriver != null)
            {
                FatUndo.BeginPlayerEdit(UndoName(_lastInput.Piece)); VoxelWorld.VToolScope++;   // inside the undo scope (see DriveStroke)
                try { _activeDriver.End(_lastInput); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] stroke end threw: {ex.Message}"); }
                finally { VoxelWorld.VToolScope--; FatUndo.EndPlayerEdit(); }
            }
            _activeDriver = null;
            _strokeActive = false;
        }

        // A stroke in progress ends when its tool changes or goes away.
        private void EndStrokeIfToolChanged(string piece)
        {
            if (_activeDriver == null) return;
            if (VToolDrivers.TryGet(piece, out var now) && ReferenceEquals(now, _activeDriver)) return;
            EndStroke();
        }

        private void DriveStroke(Tool tool, string piece, IVToolDriver driver, Player player, Vector3 cursor, bool alt, bool virtualPress)
        {
            PondTool.Hide();
            Vector3 ground = cursor;
            bool hasGround = virtualPress || PondTool.GroundCursor(out ground);
            if (!hasGround) ground = cursor;
            if (!virtualPress) ApplyFreeze(piece, player, ref cursor, ref ground, ref hasGround);
            Vector3 view = GameCamera.instance != null ? GameCamera.instance.transform.forward : Vector3.forward;
            float radius = EffectiveGizmoRadius();
            float offset = NudgeOffset(piece, driver, ground.y, radius, player);
            var s = new StrokeInput
            {
                Piece = piece, HitAny = cursor, HitGround = ground, HasGround = hasGround, Normal = Vector3.up, View = view,
                Radius = radius, Offset = offset, Alt = alt, First = !_strokeActive, Time = Time.time,
            };
            DrawGhost(driver.Preview(s));
            driver.DrawExtras(s);
            UpdateHintRay(tool, ground);

            if (virtualPress && !VerdantsAscent.Terrain.FatInput.PrimaryHeld) _armAfterRelease = false;
            if (!VerdantsAscent.Terrain.FatInput.PrimaryHeld && !virtualPress)
            {
                EndStroke();
                _armAfterRelease = false;   // selecting-click released: a fresh press now acts
                return;
            }
            if (_armAfterRelease) { Blocked(tool, "the click that picked the tool is still held (release, then press again)"); return; }
            if (!ReadyToAct(out string gate)) { Blocked(tool, gate); return; }

            float halfH = Mathf.Max(radius, Mathf.Abs(offset) + radius);
            if (!_strokeActive)
            {
                if (SkipForIsland(tool, ground, radius, halfH)) return;   // retried next frame, once the island streams in
                _activeDriver = driver;
                _strokeActive = true;
                _lastDabPos = ground;
                _dabBudget = 0f;
                _lastInput = s;
                FatUndo.BeginPlayerEdit(UndoName(piece)); VoxelWorld.VToolScope++;
                try { driver.Begin(s); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] stroke begin threw: {ex.Message}"); }
                finally { VoxelWorld.VToolScope--; FatUndo.EndPlayerEdit(); }
                return;
            }
            _lastInput = s;
            if (driver.ClickOnly) return;

            // The draw rate: DrawSpeed dabs a second at most, and a dab only once the crosshair has moved SIDEWAYS (across the
            // view ray) by the spacing, so holding still never tunnels or towers.
            float spacing = Mathf.Max(radius * DAB_SPACING_RATIO, 0.25f);
            _dabBudget = Mathf.Min(_dabBudget + driver.DrawSpeed * Time.deltaTime, 1.5f);
            while (_dabBudget >= 1f)
            {
                Vector3 delta = ground - _lastDabPos;
                Vector3 sideways = delta - Vector3.Dot(delta, view) * view;
                float len = sideways.magnitude;
                if (len < spacing) break;
                Vector3 at = _lastDabPos + delta * Mathf.Min(1f, spacing / len);
                _lastDabPos = at;
                _dabBudget -= 1f;
                if (SkipForIsland(tool, at, radius, halfH)) continue;
                var d = s;
                d.HitGround = at;
                d.HitAny = at;
                d.First = false;
                FatUndo.BeginPlayerEdit(UndoName(piece)); VoxelWorld.VToolScope++;
                try { driver.Dab(d); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] stroke dab threw: {ex.Message}"); }
                finally { VoxelWorld.VToolScope--; FatUndo.EndPlayerEdit(); }
            }
        }

        // PgUp/PgDn move the tool's offset (Shift: 0.1 m steps). A press steps once; held, it repeats faster the longer it
        // is held. One offset per tool, kept while the session lasts. The readout names it.
        private static readonly Dictionary<string, float> s_offsets = new Dictionary<string, float>();
        private static float s_offsetHeldSince;

        // The ghost keys (FiresVoxelConfig, defaulting to Infinity Hammer's binds).
        internal static KeyCode GhostUp => FiresVoxelConfig.GhostUpKey != null ? FiresVoxelConfig.GhostUpKey.Value : KeyCode.PageUp;
        internal static KeyCode GhostDown => FiresVoxelConfig.GhostDownKey != null ? FiresVoxelConfig.GhostDownKey.Value : KeyCode.PageDown;
        internal static KeyCode GhostLargeKey => FiresVoxelConfig.GhostLargeStepKey != null ? FiresVoxelConfig.GhostLargeStepKey.Value : KeyCode.LeftAlt;
        internal static KeyCode GhostFreeze => FiresVoxelConfig.GhostFreezeKey != null ? FiresVoxelConfig.GhostFreezeKey.Value : KeyCode.Keypad0;
        private const float GhostLargeFactor = 5f;

        // FREEZE (Infinity Hammer's Freeze selection): the ghost holds the crosshair point it had when the key was pressed; the
        // tool keeps drawing and acting there until the key is pressed again or the tool changes.
        private static bool s_ghostFrozen;
        private static string s_frozenPiece;
        private static Vector3 s_frozenCursor, s_frozenGround;
        private static bool s_frozenHasGround;

        private static void ApplyFreeze(string piece, Player player, ref Vector3 cursor, ref Vector3 ground, ref bool hasGround)
        {
            if (s_ghostFrozen && s_frozenPiece != piece) s_ghostFrozen = false;
            if (VerdantsAscent.Terrain.FatInput.KeyDown(GhostFreeze))
            {
                s_ghostFrozen = !s_ghostFrozen;
                s_frozenPiece = piece; s_frozenCursor = cursor; s_frozenGround = ground; s_frozenHasGround = hasGround;
                string line = s_ghostFrozen ? $"{piece} ghost frozen at ({ground.x:F1},{ground.y:F1},{ground.z:F1}) - {GhostFreeze} releases" : $"{piece} ghost follows the crosshair again";
                FiresLogger.LogDebug("[FiresVoxel] " + line);
                if (player != null) player.Message(MessageHud.MessageType.TopLeft, line, 0, null, false);
            }
            if (!s_ghostFrozen) return;
            cursor = s_frozenCursor; ground = s_frozenGround; hasGround = s_frozenHasGround;
        }

        private static float NudgeOffset(string piece, IVToolDriver driver, float groundY, float radius, Player player)
        {
            if (!s_offsets.TryGetValue(piece, out float o)) o = driver.DefaultOffset;
            float before = o;
            float step = VerdantsAscent.Terrain.FatInput.ShiftHeld ? LevelFineStep
                : VerdantsAscent.Terrain.FatInput.Key(GhostLargeKey) ? LEVEL_OFFSET_STEP * GhostLargeFactor : LEVEL_OFFSET_STEP;
            int dir = VerdantsAscent.Terrain.FatInput.Key(GhostUp) ? 1 : VerdantsAscent.Terrain.FatInput.Key(GhostDown) ? -1 : 0;
            if (VerdantsAscent.Terrain.FatInput.KeyDown(GhostUp) || VerdantsAscent.Terrain.FatInput.KeyDown(GhostDown))
            {
                o += dir * step;
                s_offsetHeldSince = Time.unscaledTime;
            }
            else if (dir != 0 && Time.unscaledTime - s_offsetHeldSince > LevelRepeatDelay)
            {
                float held = Time.unscaledTime - s_offsetHeldSince - LevelRepeatDelay;
                o += dir * step * LevelRepeatRate * (1f + held * held) * Time.unscaledDeltaTime;
            }
            o += s_heightWheelNotches * LEVEL_OFFSET_STEP;   // Shift+wheel moves the same one height value
            o = Mathf.Clamp(o, Mathf.Max(driver.MinOffset, LevelWorldBottom - groundY), LevelWorldTop - groundY);
            s_offsets[piece] = o;
            if (!Mathf.Approximately(before, o) && player != null)
                player.Message(MessageHud.MessageType.TopLeft, $"{piece} {driver.OffsetLabel} {o:+0.0;-0.0;0} m, radius {radius:0.0} m", 0, null, false);
            return o;
        }

        // ── Drawing a Ghost: the real shape ───────────────────────────────────────────────────────────────────────────
        private GameObject _gizmoCylinder;

        private void DrawGhost(Ghost g)
        {
            switch (g.Shape)
            {
                case GhostShape.Sphere:
                    HideCylinderGizmo();
                    SetGizmo(Tool.Sphere, g.Centre, g.Material == GhostMaterial.Lines);
                    break;
                case GhostShape.Cube:
                    HideCylinderGizmo();
                    SetGizmo(Tool.Cube, g.Centre, g.Material == GhostMaterial.Lines);
                    break;
                case GhostShape.Disc:
                case GhostShape.Cylinder:
                    SetGizmo(Tool.None, Vector3.zero, false);
                    ShowCylinderGizmo(g);
                    break;
                default:
                    SetGizmo(Tool.None, Vector3.zero, false);
                    HideCylinderGizmo();
                    break;
            }
        }

        private void HideCylinderGizmo()
        {
            if (_gizmoCylinder != null && _gizmoCylinder.activeSelf) _gizmoCylinder.SetActive(false);
        }

        // A true cylinder (a disc when thin): Unity's cylinder primitive is 1 wide and 2 tall, so it scales by (2r, h/2, 2r);
        // two dotted rings mark its top and bottom edge, four lines its side. Lines only for a carve, the terrain shader for
        // a fill or a level.
        private void ShowCylinderGizmo(Ghost g)
        {
            if (_gizmoCylinder == null)
            {
                _gizmoCylinder = new GameObject("FiresGizmo_Cylinder");
                _gizmoCylinder.transform.SetParent(transform, false);
                var cage = new GameObject("cage");
                cage.transform.SetParent(_gizmoCylinder.transform, false);
                const int seg = 48;
                foreach (float y in new[] { -1f, 1f })
                {
                    var ring = NewLine(cage, seg + 1);
                    for (int i = 0; i <= seg; i++)
                    {
                        float a = i * Mathf.PI * 2f / seg;
                        ring.SetPosition(i, new Vector3(Mathf.Cos(a) * 0.5f * CAGE_INFLATE, y, Mathf.Sin(a) * 0.5f * CAGE_INFLATE));
                    }
                }
                for (int k = 0; k < 4; k++)
                {
                    float a = k * Mathf.PI * 0.5f;
                    var side = NewLine(cage, 2);
                    var p = new Vector3(Mathf.Cos(a) * 0.5f * CAGE_INFLATE, 0f, Mathf.Sin(a) * 0.5f * CAGE_INFLATE);
                    side.SetPosition(0, p + Vector3.down);
                    side.SetPosition(1, p + Vector3.up);
                }
                AddSolidChild(_gizmoCylinder, PrimitiveType.Cylinder);
            }
            if (!_gizmoCylinder.activeSelf) _gizmoCylinder.SetActive(true);
            _gizmoCylinder.transform.position = g.Centre;
            _gizmoCylinder.transform.localScale = new Vector3(g.Radius * 2f, Mathf.Max(0.05f, g.Height) * 0.5f, g.Radius * 2f);
            foreach (var lr in _gizmoCylinder.GetComponentsInChildren<LineRenderer>(true))
            {
                lr.startColor = GIZMO_GREEN;
                lr.endColor = GIZMO_GREEN;
            }
            var solid = _gizmoCylinder.transform.Find("solid");
            if (solid == null) return;
            bool showSolid = g.Material != GhostMaterial.Lines;
            if (solid.gameObject.activeSelf != showSolid) solid.gameObject.SetActive(showSolid);
            if (!showSolid) return;
            var mr = solid.GetComponent<MeshRenderer>();
            var mat = FiresCore.Materials.TerrainMaterialCache.GetCurrentMaterial();
            if (mr != null && mat != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
        }

        private static bool s_driversRegistered;

        internal static void RegisterTerrainDrivers()
        {
            if (s_driversRegistered) return;
            s_driversRegistered = true;
            VToolDrivers.Register("Vcarve", new CarveSphereDriver());   // Fire 10-23: a sphere at the aim point, any direction (was a ground-riding trench)
            VToolDrivers.Register("Vfill", new CylinderDriver(VoxelBrushMode.Fill));
            VToolDrivers.Register("Vdelete", new CylinderDriver(VoxelBrushMode.Restore));
            VToolDrivers.Register("Vlevel", new LevelDriver());
            VToolDrivers.Register("Vcube", new ShapeDriver(VoxelBrushShape.Cube));
            VToolDrivers.Register("Vsphere", new ShapeDriver(VoxelBrushShape.Sphere));
            VToolDrivers.Register("Vskin", new SkinDriver());
            VToolDrivers.Register("Vramp", new RampDriver());   // 0.2.1057 (Fire: Voxheim pick 6)
        }

        // ── Vskin: smooths over the top of lattice meshes broken into the caves, following the existing terrain
        // height exactly at the surface and just filling the holes from cave openings (some open on roadways and
        // the like) ──────────────────────────────────────────────────────────────────────────────────────────────────────────
        // Each dab fits the surface across the brush from the INTACT ground just outside it (rings at 1.1 and 1.35 radii; a
        // rim point counts only when it is solid for the shell's thickness under its surface), as a plane, so a road stays
        // flat across the patch. Then, per 1 m cell inside the brush: where the voxel surface already sits at the fitted
        // height with solid under it, nothing; where ground stands ABOVE it, nothing; elsewhere (the opening) a shell from the
        // fitted surface DOWN by the thickness. Never anything above the fitted surface, and nothing under the shell, so the
        // cave stays hollow. The cell stamps are queued, SkinStampsPerFrame a frame.
        private sealed class SkinDriver : IVToolDriver
        {
            public float DrawSpeed => ConfigSpeed(FiresVoxelConfig.FillDrawSpeed, 3f);
            public bool ClickOnly => false;
            public float DefaultOffset => 0.75f;
            public float MinOffset => 0.2f;
            public string OffsetLabel => "thickness";

            // 0.2.1066 (Fire: "z fighting when shown over the existing terrain"): the solid Terrain-material shell was drawn
            // with its top face exactly on the fitted ground, so over intact ground it z-fought the real surface. Now the
            // solid shell shows only when the brush covers a hole, lifted SkinGhostLift above the surface; over intact ground
            // it is the line ring alone. The hole count is cached while the aim stays within half a metre.
            private Vector3 _previewAt = new Vector3(float.NaN, 0f, 0f);
            private float _previewRadius = -1f;
            private int _previewHoles;
            private bool _previewLogged;

            public Ghost Preview(in StrokeInput s)
            {
                if (!FitRim(s.HitGround, s.Radius, s.Offset, out var fit))
                    return new Ghost { Shape = GhostShape.Disc, Centre = s.HitGround, Radius = s.Radius, Height = LevelGhostThickness, Material = GhostMaterial.Lines };
                float top = fit.At(s.HitGround.x, s.HitGround.z);
                if (float.IsNaN(_previewAt.x) || (s.HitGround - _previewAt).sqrMagnitude > 0.25f || !Mathf.Approximately(_previewRadius, s.Radius))
                {
                    _previewAt = s.HitGround; _previewRadius = s.Radius;
                    _previewHoles = Classify(s, fit, null, out _, out _, out _);
                    if (!_previewLogged)
                    {
                        _previewLogged = true;
                        FiresLogger.LogDebug($"[FiresVoxel] skin preview: {_previewHoles} hole cell(s) under the brush at ({F(s.HitGround.x)},{F(top)},{F(s.HitGround.z)}); "
                            + (_previewHoles > 0 ? $"shell drawn {F(SkinGhostLift)} m above the ground" : "intact ground, ring only (no shell to z-fight)"));
                    }
                }
                if (_previewHoles == 0)
                    return new Ghost { Shape = GhostShape.Disc, Centre = new Vector3(s.HitGround.x, top, s.HitGround.z), Radius = s.Radius, Height = LevelGhostThickness, Material = GhostMaterial.Lines };
                return new Ghost
                {
                    Shape = GhostShape.Disc, Centre = new Vector3(s.HitGround.x, top - Mathf.Max(s.Offset, SkinMinShell) * 0.5f + SkinGhostLift, s.HitGround.z),
                    Radius = s.Radius, Height = Mathf.Max(s.Offset, SkinMinShell), Material = GhostMaterial.Terrain,
                };
            }

            private bool _toldNothing;
            private int _skinnedCells;

            public void Begin(in StrokeInput s) { _toldNothing = false; _skinnedCells = 0; Dab(s); }

            public void Dab(in StrokeInput s)
            {
                if (!FitRim(s.HitGround, s.Radius, s.Offset, out var fit))
                {
                    FiresLogger.LogDebug($"[FiresVoxel] stroke Vskin at ({F(s.HitGround.x)},{F(s.HitGround.z)}) r={F(s.Radius)}: too little intact ground round the brush to fit the surface ({fit.Used} point(s)), nothing laid");
                    return;
                }
                int holes = Classify(s, fit, s_skinJobs, out int intact, out int higher, out int unloaded);
                // 0.2.1080: say so instead of doing nothing silently: once per stroke when no hole is under the ring, and the
                // skinned area + height when it lays a shell.
                var me = Player.m_localPlayer;
                if (holes == 0)
                {
                    if (!_toldNothing && me != null) me.Message(MessageHud.MessageType.Center, "Nothing to skin here: no hole under the ring");
                    _toldNothing = true;
                }
                else
                {
                    _skinnedCells += holes;
                    if (me != null) me.Message(MessageHud.MessageType.TopLeft, $"Skinned {_skinnedCells} m² at y {F(fit.At(s.HitGround.x, s.HitGround.z))}");
                }
                FiresLogger.LogDebug($"[FiresVoxel] skin: holes {holes}, intact {intact}, higher {higher}, not loaded {unloaded}, wrote {holes} cell(s) at y "
                    + $"{F(fit.At(s.HitGround.x, s.HitGround.z))} (fitted from {fit.Used} rim points; grid-snap ignored: voxel probe, not physics)");
                FiresLogger.LogDebug($"[FiresVoxel] stroke Vskin at ({F(s.HitGround.x)},{F(s.HitGround.z)}) r={F(s.Radius)} surface {F(fit.At(s.HitGround.x, s.HitGround.z))} "
                    + $"(fitted from {fit.Used} rim point(s)), thickness {F(s.Offset)}: {holes} hole cell(s) queued, {intact} intact, {higher} higher ground, {unloaded} not loaded");
                if (holes > 0)
                {
                    float shellT = Mathf.Max(s.Offset, SkinMinShell), topC = fit.At(s.HitGround.x, s.HitGround.z);
                    FiresLogger.LogDebug($"[FiresVoxel] skin fill: {holes} cell(s) filled, shell {F(shellT)} m thick (y {F(topC - shellT)}..{F(topC)} at the centre, "
                        + $"stamp r {F(SkinStampRadius)} per 1 m cell), skipped: {intact} intact, {higher} higher ground (left alone), {unloaded} not loaded");
                }
                int before = s_skinWrote;
                PumpSkin();
                if (Time.unscaledTime >= s_skinNextLine)
                {
                    s_skinNextLine = Time.unscaledTime + 1f;
                    FiresLogger.LogDebug($"[FiresVoxel] skin: drag at ({F(s.HitGround.x)},{F(fit.At(s.HitGround.x, s.HitGround.z))},{F(s.HitGround.z)}) found {holes} hole cell(s), "
                        + $"wrote {s_skinWrote - before} voxel stamp(s) this frame ({s_skinJobs.Count} queued)");
                }
            }

            // Per 1 m cell inside the brush: hole (queued when jobs != null), intact, higher ground, or not loaded.
            // 0.2.1066 (Fire: "vskin doesnt work"; his log: "293 hole cell(s) queued, 0 intact" on flat intact ground at y 50,
            // then "fill changed nothing ... density@center=20"): the intact test sampled the surface in a +-0.3 m window,
            // narrower than one voxel cell, so the column sampler (solid-to-air crossings between whole cells) never found the
            // surface and every cell of flat ground read as a hole. The window is now SkinSurfaceWindow (over a cell) and the
            // found surface must sit within SkinTolerance of the fitted height.
            private static int Classify(in StrokeInput s, SurfaceFit fit, Queue<LevelJob> jobs, out int intact, out int higher, out int unloaded)
            {
                int holes = 0; intact = 0; higher = 0; unloaded = 0;
                int r = Mathf.CeilToInt(s.Radius);
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dz * dz > s.Radius * s.Radius) continue;
                        float x = Mathf.Floor(s.HitGround.x) + dx + 0.5f, z = Mathf.Floor(s.HitGround.z) + dz + 0.5f;
                        float top = fit.At(x, z);
                        // 0.2.1080 (Fire on SuperFlatSkyCaves2: "v skin still does not work"; his log: "surface 50.00 (fitted from
                        // 32 rim point(s)) ... 357 hole cell(s) queued, 0 intact" on intact flat ground): the narrow +-1.5 m column
                        // probe still never found the flat y 50 surface that the rim fit's +-LevelSurfaceReach probe found at the
                        // same spots. The cell test now reads the surface exactly as the rim fit does (the same wide probe, the
                        // same solid-under check), so a cell counts as intact whenever it would have counted as a rim point.
                        if (!VoxelWorld.TrySampleSurfaceY(x, z, top + LevelSurfaceReach, top - LevelSurfaceReach, out float sy, out _))
                        {
                            if (!VoxelWorld.TryIsSolid(x, top - s.Offset * 0.5f, z, out _)) { unloaded++; continue; }
                            sy = float.MinValue;   // loaded, no surface in reach: an open shaft, a hole
                        }
                        // Ground standing above the fitted surface (a bank, a wall) is left alone.
                        if (sy > top + SkinSurfaceWindow) { higher++; continue; }
                        bool solidBelow = sy > float.MinValue
                            && VoxelWorld.TryIsSolid(x, sy - Mathf.Max(0.5f, s.Offset), z, out bool sb) && sb;
                        if (Mathf.Abs(sy - top) <= SkinTolerance && solidBelow) { intact++; continue; }
                        // 0.2.1086 (Fire: "v skin is fucking useless"; his log: "holes 10 ... wrote 10 cell(s) at y 50.01", the openings
                        // stayed open, the next stroke "changed nothing"): the density grid samples whole metres, and the old stamp
                        // (r 0.8 from the cell CENTRE, 0.75 m tall, top at 50.01) reached grid points only at y 50, where its inside
                        // distance was min(0.8 - 0.707, 0.375 - 0.365) = 0.01 m = round(0.01 x QUANT 20) = 0: density 0, never solid
                        // (> 0), and y 49 lay outside it. Ten stamps wrote zeros; the mesher drew nothing; Fill never lowers, so a
                        // repeat "changed nothing". Now each hole cell is a SkinStampRadius 1.5 m cylinder (covers the cell's four
                        // grid columns at >= 0.79 m inside) and at least SkinMinShell 2 m thick (>= 2 grid layers solid), top on the
                        // fitted surface: flush, never above it, the cave under the shell still hollow.
                        float shell = Mathf.Max(s.Offset, SkinMinShell);
                        if (jobs != null)
                            jobs.Enqueue(new LevelJob
                            {
                                Centre = new Vector3(x, top - shell * 0.5f, z), Radius = SkinStampRadius, Height = shell, Mode = VoxelBrushMode.Fill,
                            });
                        holes++;
                    }
                return holes;
            }

            public void End(in StrokeInput s) { }
            public void DrawExtras(in StrokeInput s) { }
        }

        private const float SkinTolerance = 0.5f, SkinSurfaceWindow = 1.5f, SkinAboveCheck = 3f, SkinStampRadius = 1.5f, SkinGhostLift = 0.08f, SkinMinShell = 2f;
        private const int SkinStampsPerFrame = 32;
        private static readonly Queue<LevelJob> s_skinJobs = new Queue<LevelJob>();
        private static int s_skinWrote;
        private static float s_skinNextLine;

        // From Update every frame (beside PumpLevel).
        private static void PumpSkin()
        {
            if (s_skinJobs.Count == 0) return;
            FatUndo.BeginPlayerEdit("a Vskin stroke"); VoxelWorld.VToolScope++;   // queued stamps belong to the stroke's undo step
            try
            {
                for (int i = 0; i < SkinStampsPerFrame && s_skinJobs.Count > 0; i++)
                {
                    var job = s_skinJobs.Dequeue();
                    LevelStamp(job.Centre, job.Radius, job.Height, VoxelBrushShape.Cylinder, job.Mode);
                    s_skinWrote++;
                }
            }
            finally { VoxelWorld.VToolScope--; FatUndo.EndPlayerEdit(); }
        }

        // The surface across a brush, fitted from the intact ground round it: y = A + B (x - X) + C (z - Z).
        private struct SurfaceFit
        {
            public float X, Z, A, B, C;
            public int Used;
            public float At(float x, float z) => A + B * (x - X) + C * (z - Z);
        }

        private static readonly float[] SkinRimRings = { 1.1f, 1.35f };
        private const int SkinRimPoints = 16, SkinMinRimPoints = 5;

        private static bool FitRim(Vector3 at, float radius, float thickness, out SurfaceFit fit)
        {
            fit = new SurfaceFit { X = at.x, Z = at.z };
            float sx = 0f, sz = 0f, sy = 0f, sxx = 0f, szz = 0f, sxz = 0f, sxy = 0f, szy = 0f;
            int n = 0;
            foreach (float ring in SkinRimRings)
                for (int i = 0; i < SkinRimPoints; i++)
                {
                    float a = i * Mathf.PI * 2f / SkinRimPoints;
                    float dx = Mathf.Cos(a) * radius * ring, dz = Mathf.Sin(a) * radius * ring;
                    if (!VoxelWorld.TrySampleSurfaceY(at.x + dx, at.z + dz, at.y + LevelSurfaceReach, at.y - LevelSurfaceReach, out float y, out _)) continue;
                    if (!VoxelWorld.TryIsSolid(at.x + dx, y - Mathf.Max(0.5f, thickness), at.z + dz, out bool solid) || !solid) continue;   // a rim point over a hole is no rim
                    sx += dx; sz += dz; sy += y; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; sxy += dx * y; szy += dz * y;
                    n++;
                }
            fit.Used = n;
            if (n < SkinMinRimPoints) return false;
            // Least squares for y = A + B dx + C dz (normal equations, 3x3, solved by Cramer's rule).
            float m00 = n, m01 = sx, m02 = sz, m11 = sxx, m12 = sxz, m22 = szz;
            float det = m00 * (m11 * m22 - m12 * m12) - m01 * (m01 * m22 - m12 * m02) + m02 * (m01 * m12 - m11 * m02);
            if (Mathf.Abs(det) < 1e-4f) { fit.A = sy / n; return true; }
            fit.A = (sy * (m11 * m22 - m12 * m12) - m01 * (sxy * m22 - m12 * szy) + m02 * (sxy * m12 - m11 * szy)) / det;
            fit.B = (m00 * (sxy * m22 - szy * m12) - sy * (m01 * m22 - m12 * m02) + m02 * (m01 * szy - sxy * m02)) / det;
            fit.C = (m00 * (m11 * szy - m12 * sxy) - m01 * (m01 * szy - sxy * m02) + sy * (m01 * m12 - m11 * m02)) / det;
            return true;
        }

        private static float ConfigSpeed(BepInEx.Configuration.ConfigEntry<float> entry, float fallback) => entry != null ? entry.Value : fallback;

        // ── The surface a stroke started from ─────────────────────────────────────────────────────────────────────────
        // A carve dragged along the terrain must keep its depth from the ground as it WAS, not from the trench the stroke
        // has just dug (the crosshair would ride the trench floor and every dab would go deeper). Every cell a dab covers is
        // read once, before that dab edits it; a dab's surface is its centre cell's first reading.
        private static readonly Dictionary<long, float> s_strokeSurface = new Dictionary<long, float>();
        private const float StrokeCell = 1f, StrokeCellWide = 2f, StrokeReach = 12f;

        private static void ClearStrokeSurface() => s_strokeSurface.Clear();

        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static float StrokeSurface(Vector3 at, float radius)
        {
            float cell = radius > 8f ? StrokeCellWide : StrokeCell;
            int r = Mathf.CeilToInt(radius / cell);
            int cx = Mathf.FloorToInt(at.x / cell), cz = Mathf.FloorToInt(at.z / cell);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    long key = CellKey(cx + dx, cz + dz);
                    if (s_strokeSurface.ContainsKey(key)) continue;
                    float x = (cx + dx + 0.5f) * cell, z = (cz + dz + 0.5f) * cell;
                    s_strokeSurface[key] = VoxelWorld.TryFindSurfaceY(x, z, at.y, StrokeReach, out float y) ? y : at.y;
                }
            return s_strokeSurface.TryGetValue(CellKey(cx, cz), out float centre) ? centre : at.y;
        }

        private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        // Levelling within a cave must not affect the ground above the roof. The roof over a
        // point in a cave: the first solid voxel stepping up from it, below the column's ground top (so open sky, a cave mouth
        // under an overhang or a skyland overhead is not a roof). A tool aimed inside a cave stays RoofKeep under it.
        // The one roof test lives in FiresTerrainLayers (the shared layer resolver); these stay for older callers.
        internal const float RoofKeep = FiresTerrainLayers.RoofKeep;

        internal static bool TryCaveRoofAbove(Vector3 p, out float roof) => FiresTerrainLayers.TryRoofAbove(p, out roof);

        // ── Vcarve / Vfill / Vdelete: a cylinder at a constant depth (or height) from the surface the stroke started on ──
        private sealed class CylinderDriver : IVToolDriver
        {
            private readonly VoxelBrushMode _mode;
            internal CylinderDriver(VoxelBrushMode mode) { _mode = mode; }

            public float DrawSpeed => _mode == VoxelBrushMode.Fill ? ConfigSpeed(FiresVoxelConfig.FillDrawSpeed, 3f) : ConfigSpeed(FiresVoxelConfig.CarveDrawSpeed, 3f);
            public bool ClickOnly => false;
            public float DefaultOffset => 1f;
            public float MinOffset => 0.1f;
            public string OffsetLabel => _mode == VoxelBrushMode.Fill ? "height" : "depth";

            // Carve: from Offset under the surface to 1 m over it. Fill: from 0.5 m under it to Offset over it.
            // Delete: Offset either side of it (back to the generated ground).
            private void Span(float surface, float offset, out float bottom, out float top)
            {
                float d = Mathf.Max(0.1f, offset);
                switch (_mode)
                {
                    case VoxelBrushMode.Fill: bottom = surface - 0.5f; top = surface + d; break;
                    case VoxelBrushMode.Restore: bottom = surface - d; top = surface + d; break;
                    default: bottom = surface - d; top = surface + 1f; break;
                }
            }

            public Ghost Preview(in StrokeInput s)
            {
                Span(s.HitGround.y, s.Offset, out float bottom, out float top);
                return new Ghost
                {
                    Shape = GhostShape.Cylinder, Centre = new Vector3(s.HitGround.x, (bottom + top) * 0.5f, s.HitGround.z),
                    Radius = s.Radius, Height = top - bottom, Material = _mode == VoxelBrushMode.Fill ? GhostMaterial.Terrain : GhostMaterial.Lines,
                };
            }

            // The layer the stroke began on (FiresTerrainLayers). A dab whose own column is another layer (the stroke
            // dragged from the ground over a cave mouth, or off an island's edge) is left out; every dab is clipped to its
            // column's band, so a fill never reaches into a cave below or a cave's roof, and a carve never cuts up into it.
            private TerrainLayer _layer;

            public void Begin(in StrokeInput s)
            {
                ClearStrokeSurface();
                _layer = FiresTerrainLayers.Resolve(s.HitGround);
                Dab(s);
            }

            public void Dab(in StrokeInput s)
            {
                float surface = StrokeSurface(s.HitGround, s.Radius);
                var here = FiresTerrainLayers.Resolve(new Vector3(s.HitGround.x, surface, s.HitGround.z));
                if (!FiresTerrainLayers.SameLayer(in here, in _layer))
                {
                    FiresLogger.LogDebug($"[FiresVoxel] stroke {s.Piece} at ({F(s.HitGround.x)},{F(s.HitGround.z)}) skipped: {here.Describe()}, the stroke is on {_layer.Describe()}");
                    return;
                }
                Span(surface, s.Offset, out float bottom, out float top);
                if (!here.ClampSpan(ref bottom, ref top) || top <= bottom + 0.05f) return;
                var c = new Vector3(s.HitGround.x, (bottom + top) * 0.5f, s.HitGround.z);
                string result = VoxelWorld.ApplyBrush(c, s.Radius, top - bottom, VoxelBrushShape.Cylinder, _mode);
                FiresLogger.LogDebug($"[FiresVoxel] stroke {s.Piece} {_mode} at ({F(c.x)},{F(c.z)}) r={F(s.Radius)} surface {F(surface)} {F(bottom)}-{F(top)} on {here.Describe()} -> {result ?? "no result"}");
            }

            public void End(in StrokeInput s) => ClearStrokeSurface();
            public void DrawExtras(in StrokeInput s) { }
        }

        // ── Vlevel: drag to flatten (to the height the stroke started at + Offset) or, with LeftAlt, to smooth ───────────
        private sealed class LevelDriver : IVToolDriver
        {
            private float _target;

            public float DrawSpeed => ConfigSpeed(FiresVoxelConfig.LevelDrawSpeed, 3f);
            public bool ClickOnly => false;
            public float DefaultOffset => 0f;
            public float MinOffset => float.MinValue;
            public string OffsetLabel => "height";

            public Ghost Preview(in StrokeInput s)
            {
                float y = s_strokeActiveLevel ? _target : ClampLevelHeight(s.HitGround.y + s.Offset);
                return new Ghost { Shape = GhostShape.Disc, Centre = new Vector3(s.HitGround.x, y, s.HitGround.z), Radius = s.Radius, Height = LevelGhostThickness, Material = GhostMaterial.Terrain };
            }

            private TerrainLayer _layer;   // the layer the stroke began on; dabs over another layer are left out

            public void Begin(in StrokeInput s)
            {
                _layer = FiresTerrainLayers.Resolve(s.HitGround);
                _target = _layer.Clamp(ClampLevelHeight(s.HitGround.y + s.Offset));
                s_strokeActiveLevel = true;
                Dab(s);
            }

            public void Dab(in StrokeInput s)
            {
                var here = FiresTerrainLayers.Resolve(s.HitGround);
                if (!FiresTerrainLayers.SameLayer(in here, in _layer))
                {
                    FiresLogger.LogDebug($"[FiresVoxel] stroke {s.Piece} at ({F(s.HitGround.x)},{F(s.HitGround.z)}) skipped: {here.Describe()}, the stroke is on {_layer.Describe()}");
                    return;
                }
                if (s.Alt)
                {
                    // Smooth: halfway from the centre's ground to the mean ground under the disc, each dab.
                    if (!SurveyMean(s.HitGround, s.Radius, out float mean)) return;
                    FiresLogger.LogDebug(LevelDab(s.HitGround, s.Radius, Mathf.Lerp(s.HitGround.y, mean, 0.5f)) + " (smooth)");
                    return;
                }
                FiresLogger.LogDebug(LevelDab(s.HitGround, s.Radius, _target));
            }

            public void End(in StrokeInput s) { s_strokeActiveLevel = false; }
            public void DrawExtras(in StrokeInput s) { }
        }

        private static bool s_strokeActiveLevel;

        // The mean top surface under a disc (centre + rings), in a window round the hit.
        private static bool SurveyMean(Vector3 at, float radius, out float mean)
        {
            float sum = 0f;
            int n = 0;
            for (int ring = 0; ring <= 3; ring++)
            {
                float rr = radius * ring / 3f;
                int around = ring == 0 ? 1 : 12;
                for (int i = 0; i < around; i++)
                {
                    float a = i * Mathf.PI * 2f / around;
                    if (!VoxelWorld.TrySampleSurfaceY(at.x + Mathf.Cos(a) * rr, at.z + Mathf.Sin(a) * rr, at.y + LevelSurfaceReach, at.y - LevelSurfaceReach, out float y, out _)) continue;
                    sum += y;
                    n++;
                }
            }
            mean = n > 0 ? sum / n : at.y;
            return n > 0;
        }

        // ── Vcube / Vsphere: drag a solid shape (LeftAlt carves it), its centre Offset over the crosshair ─────────────────
        // ── Vcarve: a SPHERE carved at the aim point, in any direction ─────────────────────────────────────────────────────
        // Fire 10-23: "this was meant to be something that could carve in any direction" and "display as a sphere we can
        // scale". The aim point is the crosshair's hit on the terrain (floor, wall or ceiling; water is ignored); held, each
        // dab carves another sphere where the crosshair now hits, so a drag cuts a tunnel along the aim. Scroll = radius
        // (team rule). The offset pushes the sphere's centre past the hit along the view (0 = centred on the hit).
        // No layer / cave-roof rule: the old trench driver refused dabs whose column read as "cave", which is every dab once
        // the carve is under the ground.
        private sealed class CarveSphereDriver : IVToolDriver
        {
            public float DrawSpeed => ConfigSpeed(FiresVoxelConfig.CarveDrawSpeed, 3f);
            public bool ClickOnly => false;
            public float DefaultOffset => 0f;
            public float MinOffset => 0f;
            public string OffsetLabel => "push in";

            private static Vector3 Centre(in StrokeInput s)
            {
                var dir = s.View.sqrMagnitude > 1e-6f ? s.View.normalized : Vector3.forward;
                return s.HitGround + dir * Mathf.Max(0f, s.Offset);
            }

            public Ghost Preview(in StrokeInput s) => new Ghost
            {
                Shape = s.HasGround ? GhostShape.Sphere : GhostShape.None, Centre = Centre(s), Radius = s.Radius, Height = s.Radius * 2f,
                Material = GhostMaterial.Lines,
            };

            public void Begin(in StrokeInput s) => Dab(s);

            public void Dab(in StrokeInput s)
            {
                if (!s.HasGround) return;
                var c = Centre(s);
                string result = VoxelWorld.ApplyBrush(c, s.Radius, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve);
                FiresLogger.LogDebug($"[FiresVoxel] stroke {s.Piece} sphere carve at ({F(c.x)},{F(c.y)},{F(c.z)}) r={F(s.Radius)} -> {result ?? "no result"}");
            }

            public void End(in StrokeInput s) { }
            public void DrawExtras(in StrokeInput s) { }
        }

        private sealed class ShapeDriver : IVToolDriver
        {
            private readonly VoxelBrushShape _shape;
            internal ShapeDriver(VoxelBrushShape shape) { _shape = shape; }

            public float DrawSpeed => ConfigSpeed(FiresVoxelConfig.ShapeDrawSpeed, 3f);
            public bool ClickOnly => false;
            public float DefaultOffset => 0f;
            public float MinOffset => float.MinValue;
            public string OffsetLabel => "height";

            public Ghost Preview(in StrokeInput s) => new Ghost
            {
                Shape = _shape == VoxelBrushShape.Cube ? GhostShape.Cube : GhostShape.Sphere,
                Centre = s.HitAny + Vector3.up * s.Offset, Radius = s.Radius, Height = s.Radius * 2f,
                Material = s.Alt ? GhostMaterial.Lines : GhostMaterial.Terrain,
            };

            private TerrainLayer _layer;   // the layer the stroke began on; dabs over another layer are left out

            public void Begin(in StrokeInput s)
            {
                _layer = FiresTerrainLayers.Resolve(s.HitAny);
                Dab(s);
            }

            public void Dab(in StrokeInput s)
            {
                var here = FiresTerrainLayers.Resolve(s.HitAny);
                if (!FiresTerrainLayers.SameLayer(in here, in _layer)) return;
                var c = s.HitAny + Vector3.up * s.Offset;
                var mode = s.Alt ? VoxelBrushMode.Carve : VoxelBrushMode.Fill;
                string result = VoxelWorld.ApplyBrush(c, s.Radius, 0f, _shape, mode);
                FiresLogger.LogDebug($"[FiresVoxel] stroke {s.Piece} {mode} at ({F(c.x)},{F(c.y)},{F(c.z)}) r={F(s.Radius)} -> {result ?? "no result"}");
            }

            public void End(in StrokeInput s) { }
            public void DrawExtras(in StrokeInput s) { }
        }
    }
}
