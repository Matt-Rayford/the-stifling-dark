using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The two promo Investigators' printed Abilities (Kya Prosser, Winston Pitts), dispatched
    /// from the same Minor/Major switches as the base ten (Game.InvestigatorAbilities.cs).
    /// Both reach into the light model: Kya's Major flips Dim and Dark for the round
    /// (<see cref="BoardOverlay.DimDarkInverted"/>), Winston's Campfire is the one source of
    /// per-space Dim (<see cref="BoardOverlay.DimSpaces"/>), and his Night Vision is a
    /// Flashlight placement with a narrower template and no Charge spent.
    /// </summary>
    public sealed partial class Game
    {
        /// <summary>Kya's swap has been used this round (once per round).</summary>
        public const string KyaTransferUsedPrefix = "kya-transfer-used:";

        /// <summary>Kya swapped this turn, so neither Rest nor the automatic Charge recovers anything.</summary>
        public const string KyaNoRecoveryPrefix = "kya-no-recovery:";

        /// <summary>Kya's Minor: what she GAINS, the other resource being spent.</summary>
        public const string KyaGainCharge = "charge";
        public const string KyaGainStamina = "stamina";

        /// <summary>"The single center line" of a Flashlight is the centre section of the fan
        /// (designer ruling 2026-08-31 for Hazy; Winston's Night Vision reads the same way).</summary>
        public const int CenterLineSightLines = 3;

        public const string CampfireTokenPrefix = "campfire:";

        /// <summary>"All spaces within 2 of the token are Dim" and the Stamina radius.</summary>
        public const int CampfireRadius = 2;

        /// <summary>Round number at whose end the Campfire leaves: the round after placement.</summary>
        private const string CampfireExpiresRoundPrefix = "campfire-expires-round:";

        // ---------- Kya Prosser ----------

        /// <summary>
        /// Minor: "Lose a Stamina to gain a Charge, or lose a Charge to gain a Stamina. You may
        /// only use this Ability once per round, and you may not recover Stamina or Charge at
        /// the end of your turn if you use this Ability." args: [<see cref="KyaGainCharge"/> |
        /// <see cref="KyaGainStamina"/>]. Losing Stamina walks her track like any other loss,
        /// Wound icons included.
        /// </summary>
        private void KyaTransfer(AbilityUse use)
        {
            var inv = use.Actor;
            RequireOwnTurn(inv, "Kya's Minor Ability forfeits her own end-of-turn recovery");
            if (HasRoundModifier(KyaTransferUsedPrefix + inv.DefId))
            {
                throw new InvalidOperationException($"{inv.DefId} has already swapped Stamina and Charge this round.");
            }
            string gain = use.Args.Count > 0 ? use.Args[0] : "";
            var def = Db.Investigator(inv.DefId);
            switch (gain)
            {
                case KyaGainCharge:
                    if (inv.Stamina < 1)
                    {
                        throw new InvalidOperationException($"{inv.DefId} has no Stamina to lose.");
                    }
                    if (inv.Charge >= def.ChargeTrack.Spaces - 1)
                    {
                        throw new InvalidOperationException($"{inv.DefId}'s Charge is already full.");
                    }
                    LoseStamina(inv, 1);
                    inv.Charge += 1;
                    break;
                case KyaGainStamina:
                    if (inv.Charge < 1)
                    {
                        throw new InvalidOperationException($"{inv.DefId} has no Charge to lose.");
                    }
                    if (inv.Stamina >= def.StaminaTrack.Spaces - 1)
                    {
                        throw new InvalidOperationException($"{inv.DefId}'s Stamina is already full.");
                    }
                    inv.Charge -= 1;
                    GainStamina(inv, 1);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Transference: say what to gain, '{KyaGainCharge}' or '{KyaGainStamina}'.");
            }
            SetRoundModifier(KyaTransferUsedPrefix + inv.DefId, 1);
            SetRoundModifier(KyaNoRecoveryPrefix + inv.DefId, 1);
            Log("ability", $"{inv.DefId} swapped a {(gain == KyaGainCharge ? "Stamina for a Charge" : "Charge for a Stamina")} (no recovery this turn)");
        }

        /// <summary>
        /// Major: "For the remainder of the round, all Dim spaces are treated as Dark spaces and
        /// all Dark spaces are treated as Dim spaces. This applies to all players, including the
        /// Adversary." Flips the overlay flag every light reading goes through; cleared at round end.
        /// </summary>
        private void KyaInvertLight(AbilityUse use)
        {
            if (State.Overlay.DimDarkInverted)
            {
                throw new InvalidOperationException("Dim and Dark are already inverted this round.");
            }
            State.Overlay.DimDarkInverted = true;
            Log("ability", $"{use.Actor.DefId} inverted the light: Dim is Dark and Dark is Dim until the end of the round");
        }

        /// <summary>Kya's swap forfeits the turn's automatic Rest and Charge.</summary>
        private bool RecoveryForfeited(InvestigatorState inv) =>
            HasRoundModifier(KyaNoRecoveryPrefix + inv.DefId);

        // ---------- Winston Pitts ----------

        /// <summary>
        /// Minor, Night Vision: "When you place a Flashlight as your Final Action, you may choose
        /// to spend 0 Charge. If you do, you can only use the single center line of your
        /// Flashlight for line of sight. Cards and Abilities that modify Flashlights still
        /// impact this Ability, and you may not use this Ability if you have 0 Charge."
        /// args: [angle in radians]. The placement itself — Final Action, Reveal, Mitchell-free
        /// end of turn — is the ordinary one with the template narrowed and the Charge waived.
        /// </summary>
        private void WinstonNightVision(AbilityUse use)
        {
            var inv = use.Actor;
            RequireOwnTurn(inv, "Night Vision is a Flashlight placement");
            if (use.Args.Count < 1 ||
                !double.TryParse(use.Args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
            {
                throw new InvalidOperationException("Night Vision: give the Flashlight's angle in radians.");
            }
            if (inv.Charge < 1)
            {
                throw new InvalidOperationException("Night Vision may not be used with 0 Charge.");
            }
            Log("ability", $"{inv.DefId} uses Night Vision: the centre line only, no Charge spent");
            PlaceFlashlightCore(inv, angle, nightVision: true);
        }

        /// <summary>
        /// Major: "Place a Campfire token on an adjacent space on the main board. That space
        /// counts as Bright and all spaces within 2 of the token are Dim. Investigators gain 1
        /// Stamina when ending their turn within 2 spaces of the token. Remove the token at the
        /// end of the next round." args: [adjacent space].
        /// </summary>
        private void WinstonPlaceCampfire(AbilityUse use)
        {
            var inv = use.Actor;
            string tokenId = CampfireTokenPrefix + inv.DefId;
            if (BoardTokenSpace(tokenId) != null)
            {
                throw new InvalidOperationException($"{inv.DefId}'s Campfire is already on the board.");
            }
            if (use.Args.Count < 1 || use.Args[0].Length == 0)
            {
                throw new InvalidOperationException("Campfire: name the adjacent space for the token.");
            }
            string space = use.Args[0];
            Graph.Space(space);
            if (space == inv.Space || !Graph.DistancesFrom(inv.Space, 1, State.Overlay).ContainsKey(space))
            {
                throw new InvalidOperationException($"Campfire: '{space}' is not adjacent to {inv.Space}.");
            }
            PlaceBoardToken(tokenId, space);
            State.Adversary.Counters[CampfireExpiresRoundPrefix + inv.DefId] = State.Round + 1;
            RebuildCampfireLight();
            Log("ability", $"{inv.DefId} lit a Campfire on {space}: Bright there, Dim within {CampfireRadius}, until the end of round {State.Round + 1}");
            RevealOnBright(new[] { space });
        }

        /// <summary>The Campfires' light is the only per-space Dim there is, so the set is
        /// simply rebuilt from the tokens on the board; their Bright spaces ride along with
        /// the Flashlights in <see cref="RecomputeBrightSpaces"/>.</summary>
        private void RebuildCampfireLight()
        {
            State.Overlay.DimSpaces.Clear();
            foreach (string space in BoardTokenSpaces(CampfireTokenPrefix))
            {
                State.Overlay.DimSpaces.UnionWith(Graph.DistancesFrom(space, CampfireRadius, State.Overlay).Keys);
                State.Overlay.BrightSpaces.Add(space);
            }
        }

        private IEnumerable<string> CampfireSpacesWithin(string space) =>
            BoardTokenSpaces(CampfireTokenPrefix)
                .Where(fire => Graph.DistancesFrom(fire, CampfireRadius, State.Overlay).ContainsKey(space));

        /// <summary>Called from AbilitiesOnTurnEnd (Game.InvestigatorAbilities.cs).</summary>
        private void CampfireOnTurnEnd(InvestigatorState inv)
        {
            if (inv.Dead || inv.Escaped || IsSpirit(inv))
            {
                return;
            }
            if (CampfireSpacesWithin(inv.Space).Any())
            {
                GainStamina(inv, 1);
                Log("ability", $"{inv.DefId} ends the turn by the Campfire and gains 1 Stamina");
            }
        }

        partial void AbilitiesOnRoundEnd()
        {
            if (State.Overlay.DimDarkInverted)
            {
                State.Overlay.DimDarkInverted = false;
                Log("ability", "the light is no longer inverted");
            }
            foreach (string tokenId in BoardTokenIds(CampfireTokenPrefix))
            {
                string counter = CampfireExpiresRoundPrefix + tokenId.Substring(CampfireTokenPrefix.Length);
                if (State.Adversary.Counters.TryGetValue(counter, out int expires) && State.Round >= expires)
                {
                    string space = State.BoardTokens[tokenId];
                    RemoveBoardToken(tokenId);
                    State.Adversary.Counters.Remove(counter);
                    State.Overlay.BrightSpaces.Remove(space);
                    Log("ability", $"the Campfire on {space} burns out");
                }
            }
            RebuildCampfireLight();
        }
    }
}
