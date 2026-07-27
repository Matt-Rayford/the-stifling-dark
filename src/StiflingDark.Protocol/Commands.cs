using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StiflingDark.Engine.Core;

namespace StiflingDark.Protocol
{
    /// <summary>Which seat is entitled to send a command.</summary>
    public enum CommandSide
    {
        Investigator,
        Adversary,
    }

    /// <summary>
    /// One player action on the wire. There is exactly one command per public
    /// <see cref="Game"/> method a seat may call, so the protocol is a mirror of the rules
    /// API rather than a second, drifting model of it: <see cref="Apply"/> does nothing but
    /// forward its fields, and the engine remains the only place that validates anything.
    /// An illegal command surfaces as the engine's own InvalidOperationException.
    /// </summary>
    public abstract class GameCommand
    {
        /// <summary>Which side of the table may send this. Never serialized — the server
        /// derives it from the command type, so a client cannot claim to be the other side.</summary>
        [JsonIgnore]
        public abstract CommandSide Side { get; }

        public abstract void Apply(Game game);
    }

    // =================================================================== setup

    /// <summary>Adversary setup: hide one Evidence token in a zone.</summary>
    public sealed class PlaceHiddenEvidenceCommand : GameCommand
    {
        public string Zone { get; set; } = "";
        public string SpaceId { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlaceHiddenEvidence(Zone, SpaceId);
    }

    /// <summary>Adversary setup: place one Point of Interest token, front chosen in secret.</summary>
    public sealed class PlacePoiTokenCommand : GameCommand
    {
        public string PoiSpace { get; set; } = "";
        public string TokenSpace { get; set; } = "";
        public bool CursedFront { get; set; }
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlacePoiToken(PoiSpace, TokenSpace, CursedFront);
    }

    /// <summary>Adversary setup: place the main standee.</summary>
    public sealed class PlaceAdversaryCommand : GameCommand
    {
        public string SpaceId { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlaceAdversary(SpaceId);
    }

    /// <summary>Adversary setup, Cult only: the Cultist group and the Altar.</summary>
    public sealed class SetupCultistsCommand : GameCommand
    {
        public List<string> Spaces { get; set; } = new List<string>();
        public string AltarSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.SetupCultists(Spaces, AltarSpace);
    }

    /// <summary>Adversary setup: choose the Attack card and the Ability loadout.</summary>
    public sealed class SetupAdversaryCardsCommand : GameCommand
    {
        public string AttackCardId { get; set; } = "";
        public List<string> AbilityCardIds { get; set; } = new List<string>();
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.SetupAdversaryCards(AttackCardId, AbilityCardIds);
    }

    /// <summary>Adversary setup: everything is placed; begin round 1.</summary>
    public sealed class FinishAdversarySetupCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.FinishAdversarySetup();
    }

    // ==================================================== investigator turn

    public sealed class BeginInvestigatorTurnCommand : GameCommand
    {
        public string InvestigatorId { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.BeginInvestigatorTurn(InvestigatorId);
    }

    public sealed class SprintCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.Sprint();
    }

    public sealed class RestCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.Rest();
    }

    public sealed class MoveStepCommand : GameCommand
    {
        public string To { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.MoveStep(To);
    }

    /// <summary>Answer a pending Window crossing: stop and lose Stamina, or push on and Wound.</summary>
    public sealed class ResolveWindowCommand : GameCommand
    {
        public bool StopAndLoseStamina { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.ResolveWindow(StopAndLoseStamina);
    }

    public sealed class PickUpEvidenceCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PickUpEvidence();
    }

    public sealed class ActivateLightSwitchCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.ActivateLightSwitch();
    }

    public sealed class LockDoorCommand : GameCommand
    {
        public string DoorSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.LockDoor(DoorSpace);
    }

    public sealed class OpenDoorCommand : GameCommand
    {
        public string DoorSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.OpenDoor(DoorSpace);
    }

    public sealed class PickUpMedicalItemCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PickUpMedicalItem();
    }

    public sealed class PickUpPoiTokenCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PickUpPoiToken();
    }

    public sealed class TradeItemCommand : GameCommand
    {
        public string ToInvestigatorId { get; set; } = "";
        public string ItemCardId { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.TradeItem(ToInvestigatorId, ItemCardId);
    }

    public sealed class TradeEvidenceCommand : GameCommand
    {
        public string ToInvestigatorId { get; set; } = "";
        public string Zone { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.TradeEvidence(ToInvestigatorId, Zone);
    }

    public sealed class ChargeFlashlightCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.ChargeFlashlight();
    }

    public sealed class PlaceFlashlightCommand : GameCommand
    {
        public double AngleRadians { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PlaceFlashlight(AngleRadians);
    }

    public sealed class TakeInvolvedActionCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.TakeInvolvedAction();
    }

    public sealed class EndTurnCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.EndTurnWithoutFinalAction();
    }

    public sealed class UseItemCommand : GameCommand
    {
        public string CardId { get; set; } = "";
        public List<string>? Args { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseItem(CardId, Args);
    }

    public sealed class UseMinorAbilityCommand : GameCommand
    {
        /// <summary>Null means the active Investigator's own ability.</summary>
        public string? InvestigatorId { get; set; }
        public List<string>? Args { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseMinorAbility(InvestigatorId, Args);
    }

    public sealed class UseMajorAbilityCommand : GameCommand
    {
        public string? InvestigatorId { get; set; }
        public List<string>? Args { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseMajorAbility(InvestigatorId, Args);
    }

    public sealed class ResolvePainkillersCommand : GameCommand
    {
        public string? ExistingWoundCardId { get; set; }
        public string? ChosenDrawnCardId { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) =>
            game.ResolvePainkillers(ExistingWoundCardId, ChosenDrawnCardId);
    }

    /// <summary>Answer an Event card that is waiting on a choice.</summary>
    public sealed class ResolveEventChoiceCommand : GameCommand
    {
        /// <summary>Null answers the single pending choice.</summary>
        public string? EventId { get; set; }
        public List<string>? Args { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game)
        {
            if (EventId == null)
            {
                game.ResolveEventChoice(Args);
            }
            else
            {
                game.ResolveEventChoice(EventId, Args);
            }
        }
    }

    public sealed class AdoptSpiritCommand : GameCommand
    {
        public string DeadInvestigatorId { get; set; } = "";
        public string SpiritId { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.AdoptSpirit(DeadInvestigatorId, SpiritId);
    }

    public sealed class UseSpiritAbilityCommand : GameCommand
    {
        public string AbilityName { get; set; } = "";
        public List<string>? Args { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseSpiritAbility(AbilityName, Args);
    }

    /// <summary>One Evidence token cashed in for one reward.</summary>
    public sealed class EvidenceTurnIn
    {
        public string Zone { get; set; } = "";
        public string Reward { get; set; } = "";
        public string? Arg { get; set; }
        public string? Arg2 { get; set; }
    }

    public sealed class TurnInEvidenceCommand : GameCommand
    {
        public List<EvidenceTurnIn> TurnIns { get; set; } = new List<EvidenceTurnIn>();
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.TurnInEvidence(
            TurnIns.Select(t => (t.Zone, t.Reward, t.Arg, t.Arg2)).ToList());
    }

    public sealed class PlaceOpenWindowTokenCommand : GameCommand
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PlaceOpenWindowToken(A, B);
    }

    public sealed class PlaceDimTokenCommand : GameCommand
    {
        public string Zone { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PlaceDimToken(Zone);
    }

    public sealed class PlaceSecretPassageCommand : GameCommand
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PlaceSecretPassage(A, B);
    }

    // ================================================== objective / escape

    /// <summary>
    /// Draw the three-card Escape shortlist. This consumes engine RNG, so it happens exactly
    /// once: the host caches <see cref="Choices"/> on the room and feeds it to every
    /// Investigator view until one of them is selected.
    /// </summary>
    public sealed class DrawEscapeChoicesCommand : GameCommand
    {
        /// <summary>The shortlist this draw produced; read by the host, never sent by a client.</summary>
        [JsonIgnore]
        public List<string> Choices { get; private set; } = new List<string>();

        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => Choices = game.DrawEscapeChoices().ToList();
    }

    public sealed class SelectEscapeCardCommand : GameCommand
    {
        public string CardId { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.SelectEscapeCard(CardId);
    }

    public sealed class PickUpObjectiveTokenCommand : GameCommand
    {
        public string TokenName { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PickUpObjectiveToken(TokenName);
    }

    public sealed class DropObjectiveTokenCommand : GameCommand
    {
        public string TokenName { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.DropObjectiveToken(TokenName);
    }

    public sealed class OpenLockboxCommand : GameCommand
    {
        public bool PushYourLuck { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.OpenLockbox(PushYourLuck);
    }

    public sealed class PowerTheGateCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PowerTheGate();
    }

    public sealed class EscapeThroughGateCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.EscapeThroughGate();
    }

    public sealed class InstallPartCommand : GameCommand
    {
        public string PartToken { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.InstallPart(PartToken);
    }

    public sealed class StartTruckCommand : GameCommand
    {
        public string EscapeSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.StartTruck(EscapeSpace);
    }

    public sealed class EscapeAtTruckExitCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.EscapeAtTruckExit();
    }

    public sealed class FireFlareGunCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.FireFlareGun();
    }

    public sealed class EscapeByHelicopterCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.EscapeByHelicopter();
    }

    public sealed class PickUpRidePartsCommand : GameCommand
    {
        public string Token { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PickUpRideParts(Token);
    }

    public sealed class OpenServiceTunnelCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.OpenServiceTunnel();
    }

    public sealed class EscapeThroughTunnelCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.EscapeThroughTunnel();
    }

    // ----- Banish objectives, Investigator side -----

    public sealed class DigUpGraveCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.DigUpGrave();
    }

    public sealed class UseTheHookCommand : GameCommand
    {
        public string ChosenSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseTheHook(ChosenSpace);
    }

    public sealed class UseFrayedRopesCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseFrayedRopes();
    }

    public sealed class DestroyEggSacCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.DestroyEggSac();
    }

    public sealed class BanishTheHorrorCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.BanishTheHorror();
    }

    public sealed class PickUpBanishTokenCommand : GameCommand
    {
        public string TokenName { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.PickUpBanishToken(TokenName);
    }

    public sealed class UseRitualKnifeCommand : GameCommand
    {
        public bool FlipFaceDownWound { get; set; }
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.UseRitualKnife(FlipFaceDownWound);
    }

    public sealed class CutRopeCircleCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.CutRopeCircle();
    }

    /// <summary>Two Investigators share a Wound (the Commiserate Wound card's own text).</summary>
    public sealed class CommiserateCommand : GameCommand
    {
        public string InvestigatorId { get; set; } = "";
        public string OtherInvestigatorId { get; set; } = "";
        public override CommandSide Side => CommandSide.Investigator;
        public override void Apply(Game game) => game.Commiserate(
            game.State.Investigators.First(i => i.DefId == InvestigatorId),
            game.State.Investigators.First(i => i.DefId == OtherInvestigatorId));
    }

    // ======================================================= adversary turn

    public sealed class AdversaryMoveStepCommand : GameCommand
    {
        public string To { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.AdversaryMoveStep(To);
    }

    public sealed class AdversaryDisappearCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.AdversaryDisappear();
    }

    public sealed class AdversaryBreakDoorCommand : GameCommand
    {
        public string DoorSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.AdversaryBreakDoor(DoorSpace);
    }

    public sealed class AdversaryEndTurnCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.AdversaryEndTurn();
    }

    public sealed class PlayAdversaryCardCommand : GameCommand
    {
        public string CardId { get; set; } = "";
        public List<string>? Targets { get; set; }
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlayAdversaryCard(CardId, Targets);
    }

    // ----- Butcher -----

    public sealed class ButcherStalkCommand : GameCommand
    {
        public List<string> TargetInvestigatorIds { get; set; } = new List<string>();
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.ButcherStalk(TargetInvestigatorIds);
    }

    public sealed class PlaceGraveCommand : GameCommand
    {
        public string ActualSpace { get; set; } = "";
        public string DecoySpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlaceGrave(ActualSpace, DecoySpace);
    }

    /// <summary>The Adversary's reply to an Investigator's Frayed Ropes.</summary>
    public sealed class AnswerFrayedRopesCommand : GameCommand
    {
        public string Space { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.AnswerFrayedRopes(Space);
    }

    // ----- Insatiable Horror -----

    public sealed class HorrorAmbushCommand : GameCommand
    {
        public Dictionary<string, string> InvestigatorToSpace { get; set; } =
            new Dictionary<string, string>();
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.HorrorAmbush(InvestigatorToSpace);
    }

    public sealed class EnragedGatherCommand : GameCommand
    {
        public Dictionary<string, string> InvestigatorToSpace { get; set; } =
            new Dictionary<string, string>();
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.EnragedGather(InvestigatorToSpace);
    }

    public sealed class PlaceEggSacCommand : GameCommand
    {
        public string Space { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlaceEggSac(Space);
    }

    // ----- Cult of Hunlow -----

    public sealed class CultistMoveStepCommand : GameCommand
    {
        public string CultistId { get; set; } = "";
        public string To { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.CultistMoveStep(CultistId, To);
    }

    public sealed class CultistDisappearCommand : GameCommand
    {
        public string CultistId { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.CultistDisappear(CultistId);
    }

    public sealed class CultistBreakDoorCommand : GameCommand
    {
        public string CultistId { get; set; } = "";
        public string DoorSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.CultistBreakDoor(CultistId, DoorSpace);
    }

    public sealed class BloodlettingCommand : GameCommand
    {
        public string CultistId { get; set; } = "";
        public string InvestigatorId { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.Bloodletting(CultistId, InvestigatorId);
    }

    public sealed class TheFinalSacrificeCommand : GameCommand
    {
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.TheFinalSacrifice();
    }

    public sealed class MorgonnodCorporealMoveStepCommand : GameCommand
    {
        public string To { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.MorgonnodCorporealMoveStep(To);
    }

    public sealed class PlaceRitualTokensCommand : GameCommand
    {
        public string KnifeSpace { get; set; } = "";
        public string RopeSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.PlaceRitualTokens(KnifeSpace, RopeSpace);
    }

    public sealed class UsePossessedCommand : GameCommand
    {
        public string CultistId { get; set; } = "";
        public string InvestigatorId { get; set; } = "";
        public string DestinationSpace { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) =>
            game.UsePossessed(CultistId, InvestigatorId, DestinationSpace);
    }

    /// <summary>The Adversary reveals a Bufotoxin Condition they dealt face-down.</summary>
    public sealed class FlipBufotoxinFaceUpCommand : GameCommand
    {
        public string InvestigatorId { get; set; } = "";
        public override CommandSide Side => CommandSide.Adversary;
        public override void Apply(Game game) => game.FlipBufotoxinFaceUp(InvestigatorId);
    }
}
