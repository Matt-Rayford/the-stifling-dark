using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Playing an Item card from the hand or the side panel: the per-card pickers (a space,
    /// a Wound, a teammate) and the free-form argument sheet the open-ended cards fall back to.
    /// </summary>
    public sealed partial class GameUi
    {
        /// <summary>
        /// Play a card from the hand (or the side panel). Most Items take no choices at all
        /// and are simply used; the ones that name a space, a Wound, or a teammate get the
        /// matching picker. Only a handful of genuinely open-ended cards still fall back to
        /// the free-form argument sheet (designer note 2026-08-31: "I would just use it").
        /// </summary>
        private void UseItemFromHand(string cardId)
        {
            var me = Me;
            if (me == null || !MyTurnActive)
            {
                return;
            }
            void Use(params string[] args) =>
                Send(new UseItemCommand { CardId = cardId, Args = args.ToList() });
            void PickOne(string prompt) => PickSpaces(1, prompt, s => Use(s[0]));
            void PickTwo(string prompt) => PickSpaces(2, prompt, s => Use(s[0], s[1]));
            string name = _describe.Card(cardId);

            switch (cardId)
            {
                case "glowstick":
                    PickOne("Click a space within 4 for the Glowstick");
                    break;
                case "firecrackers":
                    PickOne("Click a space within 3 — every Adversary figure moves 2 toward it");
                    break;
                case "stray-mutt":
                    PickOne("Click a space within 3 for the Stray Mutt token");
                    break;
                case "lantern":
                case "lantern-mi":
                    PickOne("Click an adjacent space for the Lantern");
                    break;
                case "security-bar":
                    PickOne("Click an adjacent Door space for the Security Bar");
                    break;
                case "blueprints":
                    PickOne("Click a Point of Interest space — hidden tokens within 2 are revealed");
                    break;
                case "kerosene":
                    PickTwo("Click 2 spaces adjacent to you and to each other");
                    break;
                case "phantom-amulet":
                    PickTwo("Click the 2 spaces the Secret Passage joins");
                    break;
                case "medkit":
                    ChoosePatientAndWound(name, me, (patient, wound) =>
                    {
                        if (wound == null) Use(patient); else Use(patient, wound);
                    });
                    break;
                case "tourniquet":
                    ChooseOwnWound(name, me, required: true, wound => Use(wound));
                    break;
                case "leather-jacket":
                    ChooseOwnWound(name, me, required: false, wound =>
                    {
                        if (wound == null) Use(); else Use(wound);
                    });
                    break;
                case "makeshift-iv":
                    ChooseTeammate(name, me, adjacentOnly: true, other =>
                        _prompt.Show("iv:" + other, name,
                            "Take a face-up Wound from " + _describe.Investigator(other) +
                            ", or give them one of yours?",
                            new List<PromptOption>
                            {
                                new PromptOption("Take theirs", () => Use(other, "take")),
                                new PromptOption("Give mine", () => Use(other, "give")),
                            }, () => { }));
                    break;
                case "cursed-poppet":
                case "foul-spell-bag":
                case "hexed-mirror":
                case "lorgnette":
                case "whistle":
                    ChooseTeammate(name, me, adjacentOnly: false, other => Use(other));
                    break;
                case "whiskey":
                    _prompt.Show("whiskey", name,
                        "On a 3 or 4 the drink restores one point — which track?",
                        new List<PromptOption>
                        {
                            new PromptOption("Stamina", () => Use()),
                            new PromptOption("Charge", () => Use("charge")),
                        }, () => { });
                    break;
                case "journal":
                {
                    var options = new List<PromptOption>();
                    foreach (var reward in Describe.EvidenceRewards)
                    {
                        var captured = reward;
                        options.Add(new PromptOption(captured.Label, () =>
                            ChooseRewardArg(captured, arg =>
                            {
                                if (arg == null) Use(captured.Reward); else Use(captured.Reward, arg);
                            })));
                    }
                    _prompt.Show("journal", name, "Gain one Evidence reward.", options, () => { });
                    break;
                }
                case "emergency-flare":
                case "tripod":
                case "two-way-radio":
                case "crystal-amulet":
                case "summoning-stones":
                    // Open-ended choices (an aim angle, a borrowed Ability's own arguments, a
                    // reordered deck, two cards): the free-form sheet is still the honest tool.
                    AskArgs("Use " + name, _describe.CardText(cardId),
                        args => Send(new UseItemCommand { CardId = cardId, Args = args }));
                    break;
                default:
                    Use();
                    break;
            }
        }

        /// <summary>Medkit: yourself or an adjacent teammate, then which face-up Wound if
        /// there is more than one to choose from.</summary>
        private void ChoosePatientAndWound(string title, PlayerView.InvestigatorPanel me,
            Action<string, string> done)
        {
            var overlay = BoardModel.OverlayFrom(View);
            var reach = new HashSet<string>(_board.InteractRange(me.Space, overlay));
            var patients = View.Investigators
                .Where(i => reach.Contains(i.Space) && i.Wounds.Any(w => w.FaceUp && w.CardId != null))
                .ToList();
            if (patients.Count == 0)
            {
                _prompt.Show("medkit-none", title, "Nobody in reach has a face-up Wound to treat.",
                    new List<PromptOption> { new PromptOption("OK", () => { }) }, () => { });
                return;
            }
            void PickWound(PlayerView.InvestigatorPanel patient)
            {
                var wounds = patient.Wounds.Where(w => w.FaceUp && w.CardId != null).ToList();
                if (wounds.Count == 1)
                {
                    done(patient.DefId, wounds[0].CardId);
                    return;
                }
                var options = wounds.Select(w => new PromptOption(_describe.Card(w.CardId),
                    () => done(patient.DefId, w.CardId), _describe.CardText(w.CardId))).ToList();
                _prompt.Show("medkit-wound:" + patient.DefId, title,
                    "Which of " + _describe.Investigator(patient.DefId) + "'s Wounds?", options, () => { });
            }
            if (patients.Count == 1)
            {
                PickWound(patients[0]);
                return;
            }
            var who = patients.Select(p => new PromptOption(
                _describe.Investigator(p.DefId) + (p.DefId == me.DefId ? " (you)" : ""),
                () => PickWound(p))).ToList();
            _prompt.Show("medkit-who", title, "Treat whom?", who, () => { });
        }

        /// <summary>One of your own face-up Wounds; optional cards offer "none".</summary>
        private void ChooseOwnWound(string title, PlayerView.InvestigatorPanel me, bool required,
            Action<string> done)
        {
            var wounds = me.Wounds.Where(w => w.FaceUp && w.CardId != null).ToList();
            if (wounds.Count == 0)
            {
                if (required)
                {
                    _prompt.Show("wound-none", title, "You have no face-up Wound for this.",
                        new List<PromptOption> { new PromptOption("OK", () => { }) }, () => { });
                }
                else
                {
                    done(null);
                }
                return;
            }
            var options = wounds.Select(w => new PromptOption(_describe.Card(w.CardId),
                () => done(w.CardId), _describe.CardText(w.CardId))).ToList();
            if (!required)
            {
                options.Add(new PromptOption("No Wound", () => done(null)));
            }
            _prompt.Show("wound-pick:" + title, title, "Which Wound?", options, () => { });
        }

        /// <summary>Another living Investigator — adjacent only when the card says so.</summary>
        private void ChooseTeammate(string title, PlayerView.InvestigatorPanel me, bool adjacentOnly,
            Action<string> done)
        {
            var overlay = BoardModel.OverlayFrom(View);
            var reach = adjacentOnly
                ? new HashSet<string>(_board.InteractRange(me.Space, overlay))
                : null;
            var others = View.Investigators
                .Where(i => i.DefId != me.DefId && !i.Dead && !i.Escaped &&
                            (reach == null || reach.Contains(i.Space)))
                .ToList();
            if (others.Count == 0)
            {
                _prompt.Show("teammate-none", title,
                    adjacentOnly ? "No teammate is adjacent." : "No other Investigator is in play.",
                    new List<PromptOption> { new PromptOption("OK", () => { }) }, () => { });
                return;
            }
            var options = others.Select(o => new PromptOption(_describe.Investigator(o.DefId),
                () => done(o.DefId))).ToList();
            _prompt.Show("teammate:" + title, title, "Choose an Investigator", options, () => { });
        }

        private void AskArgs(string title, string body, Action<List<string>> submit)
        {
            var quickFill = new List<KeyValuePair<string, string>>();
            var view = View;
            var me = Me;
            if (view != null)
            {
                foreach (var panel in view.Investigators)
                {
                    quickFill.Add(new KeyValuePair<string, string>(panel.DefId,
                        "Investigator " + _describe.Investigator(panel.DefId)));
                }
                if (me != null)
                {
                    var overlay = BoardModel.OverlayFrom(view);
                    foreach (string space in _board.InteractRange(me.Space, overlay))
                    {
                        quickFill.Add(new KeyValuePair<string, string>(space, "space " + space));
                    }
                    if (me.Items != null)
                    {
                        foreach (string item in me.Items)
                        {
                            quickFill.Add(new KeyValuePair<string, string>(item,
                                "item " + _describe.Card(item)));
                        }
                    }
                }
                foreach (var zone in _board.Map.Zones)
                {
                    quickFill.Add(new KeyValuePair<string, string>(zone.Key, "zone " + zone.Value));
                }
            }
            _prompt.ShowArgs("args", title,
                (body ?? "") + "\n\nArguments are engine ids, comma separated. If the engine wants " +
                "something else it says so in the log.",
                quickFill, submit, () => { });
        }
    }
}
