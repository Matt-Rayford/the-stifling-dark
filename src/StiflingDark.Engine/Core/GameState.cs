using System.Collections.Generic;

namespace StiflingDark.Engine.Core
{
    public enum GamePhase
    {
        /// <summary>Adversary player is placing hidden Evidence, POI tokens, and their standee.</summary>
        AdversarySetup,
        InvestigatorTurns,
        AdversaryTurn,
        GameOver,
    }

    public enum FinalActionKind
    {
        None,
        Charge,
        PlaceFlashlight,
        InvolvedAction,
    }

    public enum GameResult
    {
        Undecided,
        InvestigatorsWin,
        AdversaryWins,
        Draw,
    }

    public sealed class WoundInstance
    {
        public string CardId { get; set; } = "";
        public bool FaceUp { get; set; }
    }

    public sealed class InvestigatorState
    {
        public string DefId { get; set; } = "";
        public string Space { get; set; } = "";
        public int Stamina { get; set; }
        public int Charge { get; set; }
        public int MajorAbilityTokens { get; set; } = 1;
        public List<WoundInstance> Wounds { get; set; } = new List<WoundInstance>();
        public List<string> Items { get; set; } = new List<string>();
        /// <summary>Zones whose Evidence token this Investigator is carrying.</summary>
        public List<string> EvidenceCarried { get; set; } = new List<string>();
        public bool Dead { get; set; }
        public bool Escaped { get; set; }

        // Per-turn bookkeeping (reset when the turn begins).
        public int MpRemaining { get; set; }
        public bool TurnTakenThisRound { get; set; }
        public bool SprintedOrRested { get; set; }
        public bool Rested { get; set; }
        public FinalActionKind FinalAction { get; set; }
        public bool MovementLocked { get; set; }
        public bool WaterFloatUsedThisTurn { get; set; }
        public bool CarriageRotationUsedThisRound { get; set; }
    }

    public sealed class HiddenTokenState
    {
        /// <summary>Space the token occupies (on the mini-map while hidden, main board once revealed).</summary>
        public string Space { get; set; } = "";
        public bool Revealed { get; set; }
    }

    public sealed class PoiTokenState
    {
        /// <summary>The printed POI space this token belongs to.</summary>
        public string PoiSpace { get; set; } = "";
        public string TokenSpace { get; set; } = "";
        /// <summary>True: purple Cursed Item front; false: gray General Item front.</summary>
        public bool CursedFront { get; set; }
        public bool Revealed { get; set; }
        public bool Collected { get; set; }
    }

    public sealed class AdversaryState
    {
        public string DefId { get; set; } = "";
        public string Space { get; set; } = "";
        public bool Revealed { get; set; }
        /// <summary>Space id -> shadow token marker (single-token adversaries use key "main").</summary>
        public Dictionary<string, string> ShadowTokens { get; set; } = new Dictionary<string, string>();
        public List<string> NoiseTokens { get; set; } = new List<string>();
        public bool CarriageRotationUsedThisRound { get; set; }
        /// <summary>Kills needed for the Adversary to win (Butcher 1, Horror 2, Cult = all).</summary>
        public int KillsToWin { get; set; }
        public int Kills { get; set; }
    }

    public sealed class FlashlightPlacement
    {
        public string InvestigatorId { get; set; } = "";
        public string Space { get; set; } = "";
        public double AngleRadians { get; set; }
        public List<string> BrightSpaces { get; set; } = new List<string>();
    }

    public sealed class GameEvent
    {
        public int Round { get; set; }
        public string Type { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// The complete authoritative state of one game. Pure data — Newtonsoft-serializable
    /// for saves, replays, and network sync. All mutation goes through <see cref="Game"/>.
    /// </summary>
    public sealed class GameState
    {
        public string ScenarioId { get; set; } = "";
        public ulong RngState { get; set; }
        public GamePhase Phase { get; set; }
        public GameResult Result { get; set; }
        public int Round { get; set; }
        /// <summary>DefId of the Investigator whose turn is in progress, or null between turns.</summary>
        public string? ActiveInvestigator { get; set; }

        public List<InvestigatorState> Investigators { get; set; } = new List<InvestigatorState>();
        public AdversaryState Adversary { get; set; } = new AdversaryState();

        /// <summary>Zone letter -> hidden Evidence token.</summary>
        public Dictionary<string, HiddenTokenState> Evidence { get; set; } = new Dictionary<string, HiddenTokenState>();
        public List<PoiTokenState> PoiTokens { get; set; } = new List<PoiTokenState>();
        /// <summary>Spaces currently holding a Medical Item token.</summary>
        public List<string> MedicalItemSpaces { get; set; } = new List<string>();

        public BoardOverlay Overlay { get; set; } = new BoardOverlay();
        public List<FlashlightPlacement> Flashlights { get; set; } = new List<FlashlightPlacement>();
        /// <summary>Zones whose Light Switch has already burned out (Faltering Lights).</summary>
        public HashSet<string> FalteringZones { get; set; } = new HashSet<string>();

        public List<string> EventDeck { get; set; } = new List<string>();
        public string? CurrentEvent { get; set; }
        public List<string> GeneralItemDeck { get; set; } = new List<string>();
        public List<string> CursedItemDeck { get; set; } = new List<string>();
        public List<string> WoundDeck { get; set; } = new List<string>();

        /// <summary>Set while an Investigator must resolve a Window crossing before doing anything else.</summary>
        public bool PendingWindowChoice { get; set; }

        public List<GameEvent> Log { get; set; } = new List<GameEvent>();
    }
}
