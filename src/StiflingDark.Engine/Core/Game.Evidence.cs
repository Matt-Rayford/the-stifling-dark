using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Evidence turn-in: the Involved Action that spends carried Evidence tokens for rewards,
    /// plus the small placement actions ("free interacts") that later cash in the map tokens
    /// those rewards can grant. See game-data/scenarios.json "shared" for the reward lists.
    /// </summary>
    public sealed partial class Game
    {
        // ---------- Evidence turn-in ----------

        public void TurnInEvidence(List<(string zone, string reward, string? arg, string? arg2)> turnIns)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireNoFinalAction(inv);

            var requiredKind = State.ScenarioId switch
            {
                "sawmill" => SpaceKind.Computer,
                "amusement-park" => SpaceKind.TicketBooth,
                _ => throw new InvalidOperationException($"No Evidence turn-in feature is defined for scenario '{State.ScenarioId}'."),
            };
            if (Graph.Space(inv.Space).Kind != requiredKind)
            {
                throw new InvalidOperationException($"Turning in Evidence requires standing on a {requiredKind} space.");
            }

            // Every zone requested must actually be carried; a zone may not be spent twice
            // within the same Action even though the loop below mutates as it goes.
            var stillCarried = new HashSet<string>(inv.EvidenceCarried);
            foreach (var turnIn in turnIns)
            {
                if (!stillCarried.Remove(turnIn.zone))
                {
                    throw new InvalidOperationException($"{inv.DefId} is not carrying {turnIn.zone} Evidence.");
                }
            }

            foreach (var turnIn in turnIns)
            {
                inv.EvidenceCarried.Remove(turnIn.zone);
                State.Objective.EvidenceTurnedIn += 1;
                GrantReward(inv, turnIn.reward, turnIn.arg, turnIn.arg2);
                Log("evidence", $"{inv.DefId} turned in {turnIn.zone} Evidence for '{turnIn.reward}'");
            }

            inv.FinalAction = FinalActionKind.InvolvedAction;
            EndTurn(inv);
        }

        private void GrantReward(InvestigatorState inv, string reward, string? arg, string? arg2)
        {
            switch (reward)
            {
                case "reveal-poi":
                {
                    var poi = State.PoiTokens.FirstOrDefault(p => p.PoiSpace == arg)
                        ?? throw new InvalidOperationException($"No Point of Interest token for '{arg}'.");
                    poi.Revealed = true;
                    break;
                }
                case "open-window-token":
                    inv.MapTokens.Add("open-window");
                    break;
                case "general-item":
                    inv.Items.Add(Draw(State.GeneralItemDeck, "general item"));
                    break;
                case "rearrange-mirror-doors":
                    if (State.ScenarioId != "amusement-park")
                    {
                        throw new InvalidOperationException("Rearranging Mirror Maze doors only applies at the Amusement Park.");
                    }
                    State.Overlay.OpenMirrorColor = ParseMirrorColor(arg);
                    break;
                case "cursed-item":
                    if (State.CursedItemDeck.Count == 0)
                    {
                        throw new InvalidOperationException("The cursed item deck is empty.");
                    }
                    UseOncePerGame(reward);
                    inv.Items.Add(Draw(State.CursedItemDeck, "cursed item"));
                    break;
                case "dim-token":
                    UseOncePerGame(reward);
                    inv.MapTokens.Add("dim");
                    break;
                case "secret-passage-token":
                    UseOncePerGame(reward);
                    inv.MapTokens.Add("secret-passage");
                    break;
                case "medical-item":
                    UseOncePerGame(reward);
                    inv.Items.Add(DrawMedicalItem());
                    break;
                case "major-ability-token":
                {
                    var target = Investigator(arg
                        ?? throw new InvalidOperationException("'major-ability-token' requires a target Investigator id."));
                    UseOncePerGame(reward);
                    // Major Ability tokens cannot be given away or Traded; max 1 held at a time.
                    target.MajorAbilityTokens = Math.Min(1, target.MajorAbilityTokens + 1);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown Evidence reward '{reward}'.");
            }
        }

        private void UseOncePerGame(string reward)
        {
            if (State.Objective.OncePerGameRewardsUsed.Contains(reward))
            {
                throw new InvalidOperationException($"'{reward}' has already been claimed this game.");
            }
            State.Objective.OncePerGameRewardsUsed.Add(reward);
        }

        private static MirrorDoorColor ParseMirrorColor(string? arg) => (arg ?? "").ToLowerInvariant() switch
        {
            "red" => MirrorDoorColor.Red,
            "green" => MirrorDoorColor.Green,
            "blue" => MirrorDoorColor.Blue,
            _ => throw new InvalidOperationException($"Unknown Mirror Maze color '{arg}'."),
        };

        // ---------- Map token placement (free interacts; do not end the turn) ----------

        public void PlaceOpenWindowToken(string a, string b)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (!inv.MapTokens.Contains("open-window"))
            {
                throw new InvalidOperationException($"{inv.DefId} does not carry an Open Window token.");
            }
            var edge = Graph.Edge(a, b);
            if (edge == null || edge.Type != EdgeType.Window)
            {
                throw new InvalidOperationException($"There is no Window between '{a}' and '{b}'.");
            }
            if (!AdjacentOrSame(inv.Space, a) && !AdjacentOrSame(inv.Space, b))
            {
                throw new InvalidOperationException("The Window must be adjacent to you.");
            }
            inv.MapTokens.Remove("open-window");
            State.Overlay.OpenWindows.Add(BoardOverlay.EdgeKey(a, b));
            Log("token", $"{inv.DefId} placed an Open Window token on {a}-{b}");
        }

        public void PlaceDimToken(string zone)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (!inv.MapTokens.Contains("dim"))
            {
                throw new InvalidOperationException($"{inv.DefId} does not carry a Dim token.");
            }
            if (!Graph.Def.Zones.ContainsKey(zone))
            {
                throw new InvalidOperationException($"Unknown zone '{zone}'.");
            }
            bool inZone = Graph.Space(inv.Space).Zone == zone;
            bool adjacentToZone = Graph.DistancesFrom(inv.Space, 1, State.Overlay).Keys
                .Any(spaceId => Graph.Space(spaceId).Zone == zone);
            if (!inZone && !adjacentToZone)
            {
                throw new InvalidOperationException($"You must be in or adjacent to zone {zone}.");
            }
            inv.MapTokens.Remove("dim");
            State.Overlay.DimZones.Add(zone);
            Log("token", $"{inv.DefId} placed a Dim token on zone {zone}");
        }

        public void PlaceSecretPassage(string a, string b)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (!inv.MapTokens.Contains("secret-passage"))
            {
                throw new InvalidOperationException($"{inv.DefId} does not carry a Secret Passage token.");
            }
            Graph.Space(a); // validates existence; throws if unknown
            Graph.Space(b);
            if (!AdjacentOrSame(inv.Space, a))
            {
                throw new InvalidOperationException("One end of the Secret Passage must be adjacent to you (or be your space).");
            }
            inv.MapTokens.Remove("secret-passage");
            State.Overlay.SecretPassages.Add(BoardOverlay.EdgeKey(a, b));
            Log("token", $"{inv.DefId} placed a Secret Passage between {a} and {b}");
        }

        private bool AdjacentOrSame(string from, string to) =>
            from == to || Graph.Edge(from, to) != null || Graph.DistancesFrom(from, 1, State.Overlay).ContainsKey(to);
    }
}
