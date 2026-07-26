using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Shared adversary-turn framework: lazy turn start with the MP budget and sprint
    /// roll, the card-play dispatch with the cooldown engine, and the partial hooks each
    /// adversary implementation (Game.Butcher / Game.Horror / Game.Cult) fills in.
    /// </summary>
    public sealed partial class Game
    {
        private bool _banishSetupDone;

        /// <summary>Adversary Counters key: the round in which the Adversary may play no Ability
        /// cards at all, only Attacks and Core Actions (the Cross Item card).</summary>
        public const string AbilitiesBlockedRoundKey = "abilities-blocked-round";

        private static readonly Dictionary<string, int> AdversaryBaseMp = new Dictionary<string, int>
        {
            ["butcher"] = 5,
            ["insatiable-horror"] = 4,
            ["cult-of-hunlow"] = 3, // per Cultist and Mor'gonnod, sharing one sprint roll
        };

        // Per-adversary hooks; an adversary's partial implements its own and leaves the rest.
        partial void BeginButcherTurn();
        partial void BeginHorrorTurn();
        partial void BeginCultTurn();
        partial void ApplyButcherCard(string cardId, List<string> targets);
        partial void ApplyHorrorCard(string cardId, List<string> targets);
        partial void ApplyCultCard(string cardId, List<string> targets);
        partial void SetupGraveBanish();
        partial void SetupEggsBanish();
        partial void SetupAltarBanish();

        /// <summary>Choose the Attack and Ability cards during setup (counts vary by adversary and investigator count).</summary>
        public void SetupAdversaryCards(string attackCardId, List<string> abilityCardIds)
        {
            RequirePhase(GamePhase.AdversarySetup);
            var adv = State.Adversary;
            var owned = Db.Deck("adversary")
                .Where(c => c.Owner == OwnerKey(adv.DefId))
                .ToDictionary(c => c.Id);
            if (!owned.TryGetValue(attackCardId, out var attack) || attack.AdversaryCardType != "attack")
            {
                throw new InvalidOperationException($"'{attackCardId}' is not one of this adversary's Attack cards.");
            }
            int investigators = State.Investigators.Count;
            int allowed = AllowedAbilityCount(adv.DefId, investigators);
            if (abilityCardIds.Count != allowed)
            {
                throw new InvalidOperationException($"{adv.DefId} takes {allowed} Ability card(s) with {investigators} investigators.");
            }
            foreach (string id in abilityCardIds)
            {
                if (!owned.TryGetValue(id, out var card) || card.AdversaryCardType != "ability")
                {
                    throw new InvalidOperationException($"'{id}' is not one of this adversary's Ability cards.");
                }
            }
            adv.AttackCard = attackCardId;
            if (adv.DefId == "cult-of-hunlow")
            {
                // The Cult starts with exactly 1 Ability face-up; Bloodletting flips the rest.
                adv.ActiveAbilities.Add(abilityCardIds[0]);
                adv.FaceDownAbilities.AddRange(abilityCardIds.Skip(1));
            }
            else
            {
                adv.ActiveAbilities.AddRange(abilityCardIds);
            }
        }

        private static string OwnerKey(string defId) => defId switch
        {
            "butcher" => "butcher",
            "insatiable-horror" => "horror",
            "cult-of-hunlow" => "cult",
            _ => throw new InvalidOperationException($"Unknown adversary '{defId}'."),
        };

        private static int AllowedAbilityCount(string defId, int investigators) => defId switch
        {
            "butcher" => investigators <= 2 ? 1 : 2,
            "insatiable-horror" => investigators <= 3 ? 1 : 2,
            "cult-of-hunlow" => investigators <= 2 ? 1 : investigators == 3 ? 2 : 3,
            _ => throw new InvalidOperationException($"Unknown adversary '{defId}'."),
        };

        /// <summary>Called lazily by every adversary action: rolls the sprint die, sets the
        /// MP budget, applies the began-turn-revealed attack lock, and runs per-adversary hooks.</summary>
        private void EnsureAdversaryTurnStarted()
        {
            RequirePhase(GamePhase.AdversaryTurn);
            var adv = State.Adversary;
            if (adv.TurnStarted)
            {
                return;
            }
            adv.TurnStarted = true;
            adv.ActionsUsed.Clear();
            adv.AttackUsedThisTurn = false;
            // Designer-confirmed: an Adversary that BEGINS its turn Revealed cannot Attack that turn.
            adv.AttackLockedThisTurn = adv.Revealed;
            OnAdversaryTurnStart();
            int flatMp = FlatAdversaryMp();
            if (flatMp > 0)
            {
                // The Enraged Horror (4 MP) and the Corporeal Mor'gonnod (10 MP) replace the
                // printed budget with a flat one and roll no Sprint die at all.
                adv.SprintRolled = 0;
                adv.MpRemaining = flatMp;
            }
            else
            {
                int sprint = SkipAdversarySprintDie() ? 0 : _rng.RollSprintDie(Db.Config.SprintDieFaces);
                SaveRng();
                adv.SprintRolled = sprint;
                adv.MpRemaining = AdversaryBaseMp[adv.DefId] + sprint;
            }
            switch (adv.DefId)
            {
                case "butcher": BeginButcherTurn(); break;
                case "insatiable-horror": BeginHorrorTurn(); break;
                case "cult-of-hunlow": BeginCultTurn(); break;
            }
        }

        /// <summary>The flat, Sprint-die-free MP budget of the two "final form" states, or 0
        /// when the printed base + Sprint die applies.</summary>
        private int FlatAdversaryMp()
        {
            if (State.Adversary.DefId == "insatiable-horror" && AdversaryCounter("enraged") == 1)
            {
                return 4;
            }
            if (State.Adversary.DefId == "cult-of-hunlow" && AdversaryCounter("corporeal") == 1)
            {
                return CorporealMoveMp;
            }
            return 0;
        }

        /// <summary>Witch Bells: the Investigators may force the Adversary to forgo their
        /// Sprint die for the turn they are about to take.</summary>
        private bool SkipAdversarySprintDie()
        {
            if (AdversaryCounter("skip-sprint-die-round") != State.Round)
            {
                return false;
            }
            State.Adversary.Counters.Remove("skip-sprint-die-round");
            Log("adversary", "Witch Bells: no Sprint die this turn");
            return true;
        }

        /// <summary>Resolve an Attack or active Ability card. Targets are investigator def
        /// ids (or space ids, card-dependent). The per-adversary partial applies the effect;
        /// this framework enforces availability, once-per-turn, the attack lock, and cooldowns.</summary>
        public void PlayAdversaryCard(string cardId, List<string>? targets = null)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            var card = Db.Deck("adversary").FirstOrDefault(c => c.Id == cardId)
                ?? throw new InvalidOperationException($"Unknown adversary card '{cardId}'.");
            bool isAttack = card.AdversaryCardType == "attack";
            if (isAttack)
            {
                if (adv.AttackCard != cardId)
                {
                    throw new InvalidOperationException($"'{cardId}' is not the equipped Attack card.");
                }
                if (adv.AttackLockedThisTurn)
                {
                    throw new InvalidOperationException("The Adversary cannot Attack this turn (Revealed).");
                }
                if (adv.AttackUsedThisTurn)
                {
                    throw new InvalidOperationException("The Attack card was already used this turn.");
                }
            }
            else
            {
                if (!adv.ActiveAbilities.Contains(cardId))
                {
                    throw new InvalidOperationException($"'{cardId}' is not an active Ability (on cooldown, face-down, or not chosen).");
                }
                if (AdversaryCounter(AbilitiesBlockedRoundKey) == State.Round)
                {
                    throw new InvalidOperationException(
                        "A Cross is raised: no Ability cards this turn (Attacks and Core Actions are still available).");
                }
            }
            if (adv.ActionsUsed.Contains("card:" + cardId))
            {
                throw new InvalidOperationException($"'{cardId}' was already used this turn.");
            }

            var targetList = targets ?? new List<string>();
            switch (adv.DefId)
            {
                case "butcher": ApplyButcherCard(cardId, targetList); break;
                case "insatiable-horror": ApplyHorrorCard(cardId, targetList); break;
                case "cult-of-hunlow": ApplyCultCard(cardId, targetList); break;
            }

            adv.ActionsUsed.Add("card:" + cardId);
            if (isAttack)
            {
                adv.AttackUsedThisTurn = true;
            }
            else if (card.Cooldown is int cooldown)
            {
                adv.ActiveAbilities.Remove(cardId);
                var slot = cooldown == 1 ? adv.Cooldown1 : adv.Cooldown2;
                slot.Add(new CooldownCard { CardId = cardId, FaceUp = false });
            }
            Log("adversary", $"played {cardId}");
        }

        /// <summary>End-of-adversary-turn cooldown advancement, per the adversary boards:
        /// face-up Cooldown 1 cards return to the Active slots, face-up Cooldown 2 cards
        /// move to Cooldown 1, then all face-down cooldown cards flip face-up.</summary>
        private void AdvanceCooldowns()
        {
            var adv = State.Adversary;
            foreach (var card in adv.Cooldown1.Where(c => c.FaceUp).ToList())
            {
                adv.Cooldown1.Remove(card);
                adv.ActiveAbilities.Add(card.CardId);
            }
            foreach (var card in adv.Cooldown2.Where(c => c.FaceUp).ToList())
            {
                adv.Cooldown2.Remove(card);
                adv.Cooldown1.Add(card);
            }
            foreach (var card in adv.Cooldown1.Concat(adv.Cooldown2))
            {
                card.FaceUp = true;
            }
        }

        /// <summary>An Attack deals its Wounds to one investigator (helper for the adversary
        /// partials). Tagged as Adversary-inflicted, so Mauled adds its extra Wound.</summary>
        private void DealAttackWounds(string invId, int count, bool faceUp)
        {
            var target = Investigator(invId);
            for (int i = 0; i < count && !target.Dead; i++)
            {
                GainWound(target, faceUp, WoundFromAdversary);
            }
        }

        /// <summary>Adjacency for Attacks/Abilities includes the yellow-dashed carriage links.</summary>
        private bool AdversaryAdjacentTo(string fromSpace, string invSpace) =>
            Graph.AdjacentForAdversaryAbilities(fromSpace, State.Overlay).Contains(invSpace);
    }
}
