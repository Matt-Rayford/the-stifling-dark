using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The client's local, read-only mirror of one board: the map graph, the flashlight
    /// template, the raster line-of-sight mask, and the pixel scale between the map JSON's
    /// coordinate space and whatever board texture we actually loaded.
    ///
    /// This exists so the beam preview can run at mouse speed without a round trip. It never
    /// decides anything: the server owns the rules, and every placement is still a command.
    /// No UnityEngine reference — compile-checked by tools/ClientCheck.
    /// </summary>
    public sealed class BoardModel
    {
        public GameDatabase Db { get; }
        public MapDef Map { get; }
        public MapGraph Graph { get; }

        /// <summary>
        /// Width/height of the render the map JSON's coordinates were measured in
        /// (<c>source.imageSize</c>: 7092 for the Sawmill, 6621 for the Amusement Park, both
        /// at 285 dpi). Space x/y are pixels in THAT image, so a texture of any other size
        /// needs <see cref="TextureScale"/>.
        /// </summary>
        public double SourceWidth { get; }
        public double SourceHeight { get; }

        private readonly FlashlightBeam _beam;
        private readonly ILineOfSightBlocker _blocker;

        public BoardModel(GameDatabase db, string mapId, string gameDataDir)
        {
            Db = db;
            Map = db.Map(mapId);
            Graph = new MapGraph(Map);
            _beam = new FlashlightBeam(db.Flashlight);
            // Same mask the server's Game uses; without it the preview would light through walls.
            _blocker = (ILineOfSightBlocker)db.LosMask(mapId) ?? NoLineOfSightBlocker.None;

            double width = 0, height = 0;
            try
            {
                var json = JObject.Parse(File.ReadAllText(
                    Path.Combine(gameDataDir, "maps", mapId + ".json")));
                width = (double?)json["source"]?["imageSize"]?["w"] ?? 0;
                height = (double?)json["source"]?["imageSize"]?["h"] ?? 0;
            }
            catch (Exception)
            {
                // Fall through to the bounding-box estimate below.
            }
            if (width <= 0 || height <= 0)
            {
                // Last resort: the spaces' own extent plus a pitch of margin. Slightly wrong
                // beats not drawing a board at all.
                width = Map.Spaces.Max(s => s.X) + Map.SpacePitch;
                height = Map.Spaces.Max(s => s.Y) + Map.SpacePitch;
            }
            SourceWidth = width;
            SourceHeight = height;
        }

        /// <summary>True when the raster line-of-sight mask was found; the preview needs it.</summary>
        public bool HasLosMask => !(_blocker is NoLineOfSightBlocker);

        /// <summary>
        /// Map-JSON pixels -> texture pixels. The two boards were rendered at different full
        /// resolutions from square pages, so this is per-map: 4096/7092 = 0.5776 (Sawmill),
        /// 4096/6621 = 0.6186 (Amusement Park).
        /// </summary>
        public double TextureScale(double textureWidth) => textureWidth / SourceWidth;

        /// <summary>
        /// The Bright set a flashlight placed here at this angle would produce, computed with
        /// the engine's own <see cref="FlashlightBeam"/> over the engine's own mask — so it
        /// matches what the server will do. Card effects that shrink the beam
        /// (Misty, Hazy, Downpour, Tunnel Vision) are applied server-side afterwards, so a
        /// preview can be generous in those rounds.
        /// </summary>
        public HashSet<string> PreviewBright(string atSpace, double angleRadians) =>
            _beam.ComputeBright(Graph, atSpace, angleRadians, _blocker);

        /// <summary>
        /// Turns a mouse offset from the aiming Investigator's figure, in WORLD units (client
        /// is y-up), into the engine's own board-space angle convention (board is y-down,
        /// 0 rad = +x/east — see <see cref="Core.FlashlightBeam.ComputeBright"/>). BoardView
        /// draws a board point (x, y) at world (x, -y) (see BoardView's class comment), so
        /// world-down (-y) is board-down (+y) and vice versa: negate the y offset, leave x
        /// alone. Pulled out as a pure function, with no UnityEngine reference, so the one line
        /// a mirrored beam would get wrong has a deterministic check in tools/ClientCheck
        /// without needing a Unity runtime.
        /// </summary>
        public static double AngleFromWorldOffset(double worldDx, double worldDy) =>
            Math.Atan2(-worldDy, worldDx);

        /// <summary>
        /// Rebuild the engine's <see cref="BoardOverlay"/> from the redacted view, so the
        /// client can ask MapGraph the same light and adjacency questions the server does.
        /// </summary>
        public static BoardOverlay OverlayFrom(PlayerView view)
        {
            var overlay = new BoardOverlay();
            if (view == null)
            {
                return overlay;
            }
            var info = view.Overlay;
            foreach (var pair in info.DoorStates)
            {
                overlay.DoorStates[pair.Key] = pair.Value;
            }
            overlay.BrightZones.UnionWith(info.BrightZones);
            overlay.DimZones.UnionWith(info.DimZones);
            overlay.BrightSpaces.UnionWith(info.BrightSpaces);
            overlay.OpenMirrorColor = info.OpenMirrorColor;
            overlay.OpenWindows.UnionWith(info.OpenWindows);
            overlay.FalseWindows.UnionWith(info.FalseWindows);
            overlay.SecretPassages.UnionWith(info.SecretPassages);
            overlay.AdversaryBarriers.UnionWith(info.AdversaryBarriers);
            return overlay;
        }

        public LightLevel LightOf(string spaceId, BoardOverlay overlay) =>
            Graph.HasSpace(spaceId) ? Graph.EffectiveLight(spaceId, overlay) : LightLevel.Dark;

        /// <summary>
        /// Steps this figure could take out of <paramref name="from"/> right now, with the MP
        /// each would cost, as MapGraph sees it. Purely advisory highlighting — the engine
        /// re-decides, and per-card adjustments (Dylan's Dark discount, water, carriages) can
        /// move the real cost.
        /// </summary>
        public Dictionary<string, MoveStep> StepsFrom(string from, FigureKind figure,
            BoardOverlay overlay)
        {
            var steps = new Dictionary<string, MoveStep>(StringComparer.Ordinal);
            if (!Graph.HasSpace(from))
            {
                return steps;
            }
            foreach (var space in Neighbors(from, overlay))
            {
                var step = Graph.TryStep(figure, from, space, overlay);
                if (step != null)
                {
                    steps[space] = step;
                }
            }
            return steps;
        }

        /// <summary>Every space joined to this one by a printed edge or a Secret Passage.</summary>
        public IEnumerable<string> Neighbors(string spaceId, BoardOverlay overlay)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in Map.Edges)
            {
                if (edge.A == spaceId)
                {
                    seen.Add(edge.B);
                }
                else if (edge.B == spaceId)
                {
                    seen.Add(edge.A);
                }
            }
            foreach (string key in overlay.SecretPassages)
            {
                int sep = key.IndexOf('|');
                if (sep < 0)
                {
                    continue;
                }
                string a = key.Substring(0, sep);
                string b = key.Substring(sep + 1);
                if (a == spaceId)
                {
                    seen.Add(b);
                }
                else if (b == spaceId)
                {
                    seen.Add(a);
                }
            }
            return seen;
        }

        /// <summary>
        /// Spaces an Investigator may trade or interact across: normal adjacency, excluding
        /// the yellow adversary-only links, plus their own space.
        /// </summary>
        public List<string> InteractRange(string from, BoardOverlay overlay)
        {
            var list = new List<string> { from };
            foreach (string space in Neighbors(from, overlay))
            {
                var edge = Graph.Edge(from, space);
                if (edge != null && edge.Type == EdgeType.AdversaryLink)
                {
                    continue;
                }
                list.Add(space);
            }
            return list;
        }

        public SpaceDef SpaceOrNull(string id) => Graph.HasSpace(id) ? Graph.Space(id) : null;

        /// <summary>Zone display name ("Lumber Shed"), or the bare letter if unmapped.</summary>
        public string ZoneName(string zone)
        {
            if (string.IsNullOrEmpty(zone))
            {
                return "outdoors";
            }
            return Map.Zones.TryGetValue(zone, out string name) ? name : zone;
        }
    }
}
