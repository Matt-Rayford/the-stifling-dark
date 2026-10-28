using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// What takes the screen between actions: the server-driven modals (Window choice, Escape
    /// card, Spirit adoption, the Adversary's Event answers), the between-turns YOUR TURN
    /// banner, and the round's Event card reveal with its hover-to-enlarge.
    /// </summary>
    public sealed partial class GameUi
    {
        private readonly CardPreview _eventPreview = new CardPreview();

        // The round-start Event reveal, which slides its full-size card to the EVENT
        // section's resting card.
        private string _eventShownKey;
        private bool _eventBaselineSet;
        private RectTransform _eventCardRect;

        private void MaybeShowModal(PlayerView view)
        {
            if (_pickCount > 0 || _boardView.Aiming)
            {
                return;
            }
            var me = Me;

            if (view.PendingWindowChoice && MyTurnActive)
            {
                _prompt.Show("window:" + view.Round, "You are halfway through a Window",
                    "Stop here and lose Stamina, or push on and take a Wound.",
                    new List<PromptOption>
                    {
                        new PromptOption("Stop — lose Stamina",
                            () => Send(new ResolveWindowCommand { StopAndLoseStamina = true })),
                        new PromptOption("Push on — take a Wound",
                            () => Send(new ResolveWindowCommand { StopAndLoseStamina = false })),
                    });
                return;
            }

            if (view.EscapeChoices != null && view.EscapeChoices.Count > 0 &&
                view.Objective.SelectedEscapeCard == null && !AmAdversary)
            {
                var options = view.EscapeChoices
                    .Select(id => new PromptOption(_describe.Card(id),
                        () => Send(new SelectEscapeCardCommand { CardId = id }),
                        _describe.CardText(id)))
                    .ToList();
                _prompt.Show("escape:" + string.Join(",", view.EscapeChoices),
                    "Choose the team's Escape card",
                    "Enough Evidence is in. This choice belongs to the whole team.", options);
                return;
            }

            if (me != null && me.Dead && me.SpiritId == null && view.AvailableSpiritIds.Count > 0)
            {
                var options = view.AvailableSpiritIds
                    .Select(id => new PromptOption(_describe.Card(id),
                        () => Send(new AdoptSpiritCommand
                        {
                            DeadInvestigatorId = me.DefId,
                            SpiritId = id,
                        }), _describe.CardText(id)))
                    .ToList();
                _prompt.Show("spirit:" + me.DefId, "Adopt a Spirit",
                    _describe.Investigator(me.DefId) + " is dead. Take a Spirit card to keep playing.",
                    options);
                return;
            }

            // These are the ADVERSARY's choices (Fallen Tree, Flare-Up, Roll Vortex, Fire
            // Tornado): only a human holding that seat is ever asked. A bot Adversary answers
            // them itself, and an Investigator seat must never see this modal.
            if (view.PendingEventChoices.Count > 0 && AmAdversary)
            {
                var options = view.PendingEventChoices
                    .Select(id => new PromptOption("Answer " + _describe.Card(id),
                        () => AskArgs("Answer " + _describe.Card(id), _describe.CardText(id),
                            args => Send(new ResolveEventChoiceCommand { EventId = id, Args = args })),
                        _describe.CardText(id)))
                    .ToList();
                _prompt.Show("event:" + string.Join(",", view.PendingEventChoices),
                    "An Event is waiting on a choice", null, options);
                return;
            }

            // Take down only the modals THIS method raises: a picker the player opened from a
            // button must survive the update that a bot's turn pushes in behind it.
            string signature = _prompt.Signature;
            if (signature.StartsWith("window:") || signature.StartsWith("escape:") ||
                signature.StartsWith("spirit:") || signature.StartsWith("event:"))
            {
                _prompt.Hide();
            }
        }

        /// <summary>
        /// Between Investigator turns the old BEGIN TURN bar button is a full-screen banner
        /// instead. It appears only when pressing it would actually work: the phase is open,
        /// nobody is mid-turn, this seat's Investigator still has a turn, and the server says
        /// the table is waiting on this seat. Runs after <see cref="MaybeShowModal"/> so a
        /// between-turns Prompt (Spirit adoption, Escape selection) gets the screen first.
        /// </summary>
        private void MaybeShowTurnBanner(PlayerView view)
        {
            var me = AmAdversary ? null : Me;
            bool waiting = me != null
                && view.Phase == GamePhase.InvestigatorTurns
                && !view.OpenTurns.Contains(me.DefId)
                && (view.SimultaneousTurns || view.OpenTurns.Count == 0)
                && !me.TurnTakenThisRound
                && !me.Escaped
                && _session.YourTurn
                && !view.PendingWindowChoice;
            if (!waiting || _prompt.Open)
            {
                _turnBanner.Hide();
                return;
            }
            string investigatorId = me.DefId;
            _turnBanner.Show("turn:" + view.Round + ":" + investigatorId,
                () => Send(new BeginInvestigatorTurnCommand { InvestigatorId = investigatorId }));
        }

        /// <summary>
        /// A freshly drawn Event gets dealt to the table: full card over a darkened beat,
        /// then slid to its resting spot beside the board (see <see cref="EventReveal"/>).
        /// The first view after joining sets the baseline silently — replaying the reveal
        /// of a round already in progress would be noise.
        /// </summary>
        private void MaybeRevealEvent(PlayerView view)
        {
            string key = view.Round + ":" + view.CurrentEvent;
            if (!_eventBaselineSet)
            {
                _eventBaselineSet = true;
                _eventShownKey = key;
                return;
            }
            if (string.IsNullOrEmpty(view.CurrentEvent) || key == _eventShownKey)
            {
                return;
            }
            _eventShownKey = key;
            EventReveal.Play(_art.EventCard(view.CurrentEvent), EventCardScreenSpot);
        }

        private const float EventCardWidth = 225f;

        /// <summary>
        /// The round's Event as the card itself, resting in the side panel (designer request
        /// 2026-10-21 — replaces the ITEMS and ABILITIES text sections: Items are played from
        /// the hand, Abilities from the figure menu). Hover enlarges it like a hand card, and
        /// the round-start reveal slides its full-size card down to land here.
        /// </summary>
        private void RenderEventSection(PlayerView view)
        {
            Header("EVENT");
            _eventCardRect = null;
            if (string.IsNullOrEmpty(view.CurrentEvent))
            {
                Note("(no Event this round)");
            }
            else
            {
                var sprite = _art.EventCard(view.CurrentEvent);
                if (sprite == null)
                {
                    Note(_describe.Card(view.CurrentEvent) + ": " + _describe.CardText(view.CurrentEvent));
                }
                else
                {
                    float height = EventCardWidth * sprite.bounds.size.y /
                        Mathf.Max(0.0001f, sprite.bounds.size.x);
                    var cardGo = new GameObject("EventCard", typeof(RectTransform), typeof(Image),
                        typeof(LayoutElement));
                    cardGo.transform.SetParent(_actionsBody, false);
                    _eventCardRect = (RectTransform)cardGo.transform;
                    _eventCardRect.sizeDelta = new Vector2(EventCardWidth, height);
                    var layout = cardGo.GetComponent<LayoutElement>();
                    layout.preferredWidth = EventCardWidth;
                    layout.preferredHeight = height;
                    var image = cardGo.GetComponent<Image>();
                    image.sprite = sprite;
                    image.preserveAspect = true;
                    image.material = UiKit.RoundedImageMaterial(EventCardWidth, height);
                    _eventPreview.Attach(cardGo, sprite);
                }
            }
            // Major Events that persist past their round keep a text line under the card,
            // which only ever shows the round's own draw.
            foreach (string persistent in view.PersistentMajorEvents
                .Where(id => id != view.CurrentEvent))
            {
                Line(_actionsBody, _describe.Card(persistent) + "  (persists)", 15,
                    UiKit.AccentColor);
                Line(_actionsBody, _describe.CardText(persistent), 14, UiKit.MutedColor);
            }
        }

        /// <summary>The resting Event card's on-screen centre and height, or null while
        /// absent — where the round-start reveal slides its full-size card to.</summary>
        private (Vector2 Center, float Height)? EventCardScreenSpot()
        {
            if (_eventCardRect == null)
            {
                return null;
            }
            var corners = new Vector3[4];
            _eventCardRect.GetWorldCorners(corners); // overlay canvas: world == screen pixels
            var bottomLeft = (Vector2)corners[0];
            var topRight = (Vector2)corners[2];
            return ((bottomLeft + topRight) / 2f, topRight.y - bottomLeft.y);
        }

    }
}
