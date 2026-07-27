using System.Collections.Generic;
using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Data
{
    /// <summary>A space on a board, as authored in game-data/maps/*.json.</summary>
    public sealed class SpaceDef
    {
        public string Id { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Zone letter (e.g. "S"), or null for outdoor numbered spaces.</summary>
        public string? Zone { get; set; }
        /// <summary>Printed light level: Dim (solid ring) or Dark (dashed ring).</summary>
        public LightLevel PrintedLight { get; set; }
        public SpaceKind Kind { get; set; }
        /// <summary>True for Ferris Wheel / Zipper carriage spaces (Amusement Park).</summary>
        public bool Carriage { get; set; }
        /// <summary>True for spaces on the Tunnel of Love water channel (Amusement Park).</summary>
        public bool Water { get; set; }
    }

    public sealed class EdgeDef
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public EdgeType Type { get; set; }
        /// <summary>Mirror Maze door color (only for EdgeType.MirrorDoor).</summary>
        public MirrorDoorColor? Color { get; set; }
        /// <summary>True when the printed line is part of the blue wavy water channel.</summary>
        public bool Water { get; set; }
    }

    public sealed class RideDef
    {
        /// <summary>Carriages as space-id pairs, e.g. [["65","79"], ...].</summary>
        public List<List<string>> Carriages { get; set; } = new List<List<string>>();
        /// <summary>Forced rotation: occupant of key space is immediately moved to value space (if empty).</summary>
        public Dictionary<string, string> ForcedNext { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>The printed per-zone light square on the board art — where the physical
    /// lights-on / burnt-out / permanently-dim token is placed, covering the square.</summary>
    public sealed class ZoneLightSquareDef
    {
        /// <summary>Square center, in board pixels.</summary>
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>Side length, in board pixels.</summary>
        public double Size { get; set; }
    }

    public sealed class MapDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Center-to-center distance between adjacent spaces, in board pixels.</summary>
        public double SpacePitch { get; set; }
        /// <summary>Space circle radius, in board pixels.</summary>
        public double SpaceRadius { get; set; }
        /// <summary>Zone letter to display name.</summary>
        public Dictionary<string, string> Zones { get; set; } = new Dictionary<string, string>();
        /// <summary>Zone letter to the printed light square its state token covers.</summary>
        public Dictionary<string, ZoneLightSquareDef> ZoneLights { get; set; } = new Dictionary<string, ZoneLightSquareDef>();
        public List<SpaceDef> Spaces { get; set; } = new List<SpaceDef>();
        public List<EdgeDef> Edges { get; set; } = new List<EdgeDef>();
        /// <summary>Ride id ("zipper", "ferrisWheel") to rotation definition. Empty for the Sawmill.</summary>
        public Dictionary<string, RideDef> Rides { get; set; } = new Dictionary<string, RideDef>();
        /// <summary>Ordered clockwise water loop (Amusement Park); empty for the Sawmill.</summary>
        public List<string> WaterFlowLoop { get; set; } = new List<string>();
    }

    public sealed class TrackDef
    {
        public int Spaces { get; set; }
        public int Start { get; set; }
        public List<int> WoundIconSpaces { get; set; } = new List<int>();
    }

    public sealed class AbilityDef
    {
        public string? Name { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class InvestigatorDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Mp { get; set; }
        public AbilityDef MinorAbility { get; set; } = new AbilityDef();
        public AbilityDef MajorAbility { get; set; } = new AbilityDef();
        public TrackDef StaminaTrack { get; set; } = new TrackDef();
        public TrackDef ChargeTrack { get; set; } = new TrackDef();
        /// <summary>"base" or "promo". v1 uses base only.</summary>
        public string Set { get; set; } = "base";
    }

    /// <summary>A card in any deck. Deck-specific fields are null when not applicable.</summary>
    public sealed class CardDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Deck { get; set; } = "";
        public int Count { get; set; } = 1;
        public string Text { get; set; } = "";
        /// <summary>Number of Supply uses; null = single use; -1 = infinite.</summary>
        public int? Supply { get; set; }
        /// <summary>"base", "MI", or "NF".</summary>
        public string Set { get; set; } = "base";
        /// <summary>For MI cards: the base card id this replaces when the Mini-Expansion toggle is on.</summary>
        public string? Replaces { get; set; }
        /// <summary>Events: "sawmill" or "amusement-park". Objective/escape cards: owning scenario or adversary.</summary>
        public string? Owner { get; set; }
        /// <summary>Events only: "minor", "moderate", "major".</summary>
        public string? Severity { get; set; }
        /// <summary>Adversary cards: "attack", "ability", "condition", "reference", "revealed".</summary>
        public string? AdversaryCardType { get; set; }
        /// <summary>Adversary cards: cooldown slot 1 or 2; null when handled by card text.</summary>
        public int? Cooldown { get; set; }
    }

    /// <summary>The Small Flashlight template geometry, from game-data/flashlight.json.</summary>
    public sealed class FlashlightDef
    {
        /// <summary>Beam outline in template pixels, [x, y] pairs.</summary>
        public List<double[]> OutlinePolygon { get; set; } = new List<double[]>();
        /// <summary>Notch center (where the figure sits), template pixels.</summary>
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double ImageWidth { get; set; }
        public double ImageHeight { get; set; }
        /// <summary>Designer-confirmed: the template's full length spans this many space pitches.</summary>
        public double LengthInSpacePitches { get; set; }
    }

    public sealed class InvestigatorCountRules
    {
        public int EvidenceRequiredForObjective { get; set; }
        public int StartingPointsOfInterest { get; set; }
        public int MedicalItemsOnBoard { get; set; }
    }

    public sealed class GameConfig
    {
        public int Rounds { get; set; }
        public List<int> SprintDieFaces { get; set; } = new List<int>();
        public int ChargeMax { get; set; }
        public int WoundsToDie { get; set; }
        /// <summary>Keyed by Investigator count (2..4).</summary>
        public Dictionary<int, InvestigatorCountRules> ByInvestigatorCount { get; set; } = new Dictionary<int, InvestigatorCountRules>();
    }
}
