namespace StiflingDark.Engine.Core
{
    /// <summary>Printed or effective light level of a space. Precedence: Bright > Dim > Dark.</summary>
    public enum LightLevel
    {
        Dark,
        Dim,
        Bright,
    }

    /// <summary>What is printed on a space (its interaction role).</summary>
    public enum SpaceKind
    {
        Normal,
        Door,
        LightSwitch,
        Computer,
        TicketBooth,
        GameBooth,
        PointOfInterest,
        MedicalItem,
        Start,
    }

    /// <summary>Connection types between spaces.</summary>
    public enum EdgeType
    {
        /// <summary>Normal printed movement line.</summary>
        Move,

        /// <summary>Window Map Hazard: passable; Investigators risk a Wound, the Adversary pays +1 MP and places Noise.</summary>
        Window,

        /// <summary>Mirror Maze door: passable only while its color is the Open color this round.</summary>
        MirrorDoor,

        /// <summary>Yellow dashed line: adjacent ONLY for Adversary Attacks/Abilities. Never passable, no trading.</summary>
        AdversaryLink,
    }

    /// <summary>State of a Door space. Open = no token.</summary>
    public enum DoorState
    {
        Open,
        Locked,
        Damaged,
        Destroyed,
        /// <summary>False Door token: permanent Obstacle (blocks movement and line of sight).</summary>
        False,
    }

    /// <summary>Who is moving — movement costs and hazard effects differ per figure kind.</summary>
    public enum FigureKind
    {
        Investigator,
        /// <summary>Adversary figures: Dark costs 1 MP; windows cost +1 MP and place Noise.</summary>
        Adversary,
        /// <summary>Dead Investigator's Spirit: flat 1 MP everywhere, ignores Map Hazards, Water, and Mirror Maze doors.</summary>
        Spirit,
    }

    /// <summary>Mirror Maze door colors (Amusement Park).</summary>
    public enum MirrorDoorColor
    {
        Red,
        Green,
        Blue,
    }
}
