using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Lazy texture cache over the art tools/sync_unity.sh drops into StreamingAssets —
    /// game-assets/ is gitignored, so every lookup may miss and every caller must cope. The
    /// board falls back to colored discs with initials, which is a perfectly playable v1.
    ///
    /// Loading is raw bytes -> Texture2D.LoadImage, so no import settings exist to be wrong:
    /// nothing here depends on a Unity editor having been run.
    /// </summary>
    public sealed class TokenArt
    {
        private readonly string _streamingRoot;
        private readonly string _editorAssetsRoot;
        private readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        public TokenArt(string streamingAssetsPath, string dataPath)
        {
            _streamingRoot = streamingAssetsPath;
            // Assets/Textures is only reachable while running in the editor; a built player
            // uses the StreamingAssets copy the sync script made.
            _editorAssetsRoot = dataPath;
        }

        /// <summary>
        /// A token sprite by its path under game-assets/tokens, e.g.
        /// "investigator/faces/Aira.png". Null when the art was never synced.
        /// </summary>
        public Sprite Token(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                return null;
            }
            if (_cache.TryGetValue(relativePath, out var cached))
            {
                return cached;
            }
            var sprite = LoadSprite(Path.Combine(_streamingRoot, "tokens", relativePath), 118f);
            _cache[relativePath] = sprite;
            return sprite;
        }

        /// <summary>
        /// The 4096x4096 board render for a map id. Tries StreamingAssets/textures first (what
        /// a built player has), then Assets/Textures (what the editor has straight after the
        /// sync script ran).
        /// </summary>
        public Sprite Board(string mapId)
        {
            string key = "board:" + mapId;
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            string file = "board-" + mapId + ".png";
            var sprite =
                LoadBoard(Path.Combine(_streamingRoot, "textures", file)) ??
                LoadBoard(Path.Combine(_editorAssetsRoot, "Textures", file));
            _cache[key] = sprite;
            return sprite;
        }

        /// <summary>Board sprites pivot at the TOP-LEFT so map (x, y) maps to world (x, -y).</summary>
        private static Sprite LoadBoard(string path)
        {
            var texture = LoadTexture(path);
            if (texture == null)
            {
                return null;
            }
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
        }

        private static Sprite LoadSprite(string path, float pixelsPerUnit)
        {
            var texture = LoadTexture(path);
            if (texture == null)
            {
                return null;
            }
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
        }

        private static Texture2D LoadTexture(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    return null;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Could not load " + path + ": " + e.Message);
                return null;
            }
        }

        // ---------------------------------------------------------- art paths

        /// <summary>
        /// Face token for an Investigator id. There is no standee art anywhere in
        /// game-assets — the 118px round faces in tokens/investigator/faces are the only
        /// figure art that exists — and two of the ten filenames do not match their id.
        /// </summary>
        public static string InvestigatorFace(string id)
        {
            switch (id)
            {
                case "lucy-belle": return "investigator/faces/Lucy.png";
                case "aira": return "investigator/faces/Aira.png";
                case "asher": return "investigator/faces/Asher.png";
                case "brielle": return "investigator/faces/Brielle.png";
                case "dylan": return "investigator/faces/Dylan.png";
                case "ibraheem": return "investigator/faces/Ibraheem.png";
                case "mada": return "investigator/faces/Mada.png";
                case "marci": return "investigator/faces/Marci.png";
                case "mitchell": return "investigator/faces/Mitchell.png";
                case "vincent": return "investigator/faces/Vincent.png";
                case "kya": return "investigator/faces/Kya.png";
                case "winston": return "investigator/faces/Winston.png";
                default: return null;
            }
        }

        public static string AdversaryFace(string adversaryId)
        {
            switch (adversaryId)
            {
                case "butcher": return "investigator/faces/Butcher.png";
                case "cult-of-hunlow": return "investigator/faces/Morgonnod.png";
                case "insatiable-horror": return "investigator/faces/Cone-Snail.png";
                default: return null;
            }
        }

        /// <summary>Cult figures are numbered 1..4; Mor'gonnod is the named one.</summary>
        public static string CultistFace(string figureId)
        {
            if (string.IsNullOrEmpty(figureId))
            {
                return null;
            }
            if (figureId.IndexOf("morgonnod", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "investigator/faces/Morgonnod.png";
            }
            for (int i = 1; i <= 4; i++)
            {
                if (figureId.EndsWith(i.ToString(), System.StringComparison.Ordinal))
                {
                    return "investigator/faces/Cultist-" + i + ".png";
                }
            }
            return "investigator/faces/Cultist-1.png";
        }

        public static string ShadowToken(string adversaryId, bool faceUp)
        {
            switch (adversaryId)
            {
                case "butcher":
                    return faceUp
                        ? "adversary/butcher/Butcher-Face-Up-Shadow.png"
                        : "adversary/butcher/Butcher-Face-Down-Shadow.png";
                case "insatiable-horror":
                    return faceUp
                        ? "adversary/horror/Horror-Face-Up-Shadow.png"
                        : "adversary/horror/Horror-Face-Down-Shadow.png";
                case "cult-of-hunlow":
                    return faceUp
                        ? "adversary/cultists/Cult-Face-Up-Shadow-X.png"
                        : "adversary/cultists/Cult-Face-Down-Shadow.png";
                default:
                    return null;
            }
        }

        public static string NoiseToken(string adversaryId)
        {
            switch (adversaryId)
            {
                case "butcher": return "adversary/butcher/Butcher-Noise.png";
                case "insatiable-horror": return "adversary/horror/Horror-Noise.png";
                case "cult-of-hunlow": return "adversary/cultists/Cult-Noise.png";
                default: return "investigator/abilities/Noise.png";
            }
        }

        public static string DoorToken(Engine.Core.DoorState state)
        {
            switch (state)
            {
                case Engine.Core.DoorState.Locked: return "door/Locked-Door.png";
                case Engine.Core.DoorState.Damaged: return "door/Damaged-Door.png";
                case Engine.Core.DoorState.Destroyed: return "door/Destroyed-Door.png";
                case Engine.Core.DoorState.False: return "door/False-Door.png";
                default: return null;
            }
        }

        public static string EvidenceToken(string scenarioId) =>
            scenarioId == "amusement-park" ? "amusement-park/Evidence.png" : "sawmill/Evidence.png";

        public const string PoiBack = "general-items/Point-of-Interest-Back.png";
        public const string ItemFront = "general-items/Generic-Item-Front.png";
        public const string CursedFront = "cursed-items/Generic-Cursed-Item-Front.png";
        public const string MedicalBack = "general-items/Medical-Item-Back.png";
        public const string BrightMarker = "other/Bright.png";
        public const string DimMarker = "other/Dim.png";
        public const string FalteringMarker = "other/Faltering-Lights.png";
        public const string OpenWindowMarker = "other/Open-Window.png";
        public const string FalseWindowMarker = "other/False-Window.png";
        public const string SecretPassageMarker = "other/Secret-Passage.png";
        public const string SupplyMarker = "other/Supply.png";
        public const string EscapeMarker = "other/Escape.png";
        public const string LockedEscapeMarker = "other/Locked-Escape.png";
        public const string AltarMarker = "adversary/cultists/Altar.png";
        public const string EggSacMarker = "adversary/horror/Egg-Sac.png";
        public const string GraveMarker = "adversary/butcher/Face-Up-Grave.png";
        public const string BurningGraveMarker = "adversary/butcher/Face-Up-Burning-Grave.png";
        public const string SpineChillMarker = "adversary/butcher/Spine-Chill.png";
        public const string BarricadeMarker = "investigator/abilities/Barricade.png";

        /// <summary>
        /// Objective-token art by the engine's token name. Several names in scenarios.json
        /// (Lockbox, Battery, RepairKit, SparkPlug) have no art at all, so those fall back to
        /// a labelled disc.
        /// </summary>
        public static string ObjectiveToken(string tokenName)
        {
            if (string.IsNullOrEmpty(tokenName))
            {
                return null;
            }
            string name = tokenName.ToLowerInvariant();
            if (name.Contains("altar"))
            {
                return AltarMarker;
            }
            if (name.Contains("grave"))
            {
                return name.Contains("burning") ? BurningGraveMarker : GraveMarker;
            }
            if (name.Contains("egg"))
            {
                return EggSacMarker;
            }
            if (name.Contains("supply") || name.Contains("supplies"))
            {
                return SupplyMarker;
            }
            if (name.Contains("truck"))
            {
                return "sawmill/Truck.png";
            }
            if (name.Contains("saw"))
            {
                return "sawmill/Saw.png";
            }
            if (name.Contains("duck"))
            {
                return "amusement-park/Duck.png";
            }
            if (name.Contains("tunnel"))
            {
                return "adversary/horror/Tunnel.png";
            }
            if (name.Contains("escape") || name.Contains("gate"))
            {
                return EscapeMarker;
            }
            return null;
        }

        // -------------------------------------------------------------- color

        private static readonly Color[] SeatColors =
        {
            new Color(0.36f, 0.68f, 0.96f), // blue
            new Color(0.98f, 0.72f, 0.28f), // amber
            new Color(0.52f, 0.86f, 0.50f), // green
            new Color(0.86f, 0.52f, 0.92f), // violet
        };

        /// <summary>
        /// Stable per-Investigator color, by index in the base roster. game-data has no color
        /// field for Investigators, so this palette is the client's own.
        /// </summary>
        public static Color InvestigatorColor(int index) =>
            SeatColors[((index % SeatColors.Length) + SeatColors.Length) % SeatColors.Length];

        /// <summary>The adversaries' own tokenColor from game-data/adversaries.json.</summary>
        public static Color AdversaryColor(string adversaryId)
        {
            switch (adversaryId)
            {
                case "butcher": return new Color(0.85f, 0.25f, 0.22f);
                case "cult-of-hunlow": return new Color(0.62f, 0.36f, 0.86f);
                case "insatiable-horror": return new Color(0.34f, 0.74f, 0.40f);
                default: return new Color(0.75f, 0.75f, 0.78f);
            }
        }
    }
}
