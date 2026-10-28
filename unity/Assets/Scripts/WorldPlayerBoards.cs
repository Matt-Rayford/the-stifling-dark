using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using TMPro;
using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Investigator boards laid out in the map's own world space, so they pan and zoom with
    /// it: this seat's board centred below the map, every other Investigator's stacked in
    /// one column down the map's right edge. Markers ride the printed Stamina and Charge
    /// tracks, the Major Ability token sits on its printed gear while unspent, and Wound
    /// chips fill the printed slots (hover a chip for what the viewer may know of the card).
    ///
    /// Printed-geometry constants are fractions of the board image measured from its
    /// top-left; world y runs downward from 0 at the map's top, hence the sign flips.
    /// </summary>
    public sealed class WorldPlayerBoards
    {
        // ---- layout, as shares of the map's width
        private const float BoardWidthShare = 0.42f;
        private const float GapShare = 0.025f;

        /// <summary>Aspect of the rendered board art (1400x934); a loaded sprite overrides it.</summary>
        private const float FallbackAspect = 1400f / 934f;

        // ---- printed track geometry, as fractions of the board image (x, y from its top-left)
        private static readonly float[] StaminaCircleX =
            { 0.4909f, 0.5733f, 0.6581f, 0.7416f, 0.8246f, 0.9083f };
        private const float StaminaCircleY = 0.655f;
        private static readonly float[] ChargeCircleX = { 0.6161f, 0.6999f, 0.7834f, 0.8677f };
        private const float ChargeCircleY = 0.776f;
        /// <summary>Printed track circle radius, as a fraction of the board's WIDTH.</summary>
        private const float CircleRadius = 0.042f;
        private const float MarkerDiameter = CircleRadius * 2f * 0.7f;

        // ---- printed Major Ability box (the gear ring the physical token covers)
        private const float MajorAbilityX = 0.1416f;
        private const float MajorAbilityY = 0.778f;
        private const float MajorAbilityDiameter = 0.06f;

        // ---- printed Wound slots along the bottom band
        private const int WoundSlots = 4;
        private const float WoundBandLeft = 0.032f;
        private const float WoundBandRight = 0.971f;
        private const float WoundBandGap = 0.014f;
        private const float WoundBandTop = 0.918f;
        private const float WoundBandBottom = 0.996f;

        // ---- Wound cards sit IN the printed slots and hang below the board, as on the table
        /// <summary>Card width as a share of the slot's; the rest is breathing room.</summary>
        private const float WoundCardSlotShare = 0.92f;
        /// <summary>Card aspect (width / height) of the rendered deck (536x800).</summary>
        private const float WoundCardAspect = 536f / 800f;
        /// <summary>Wounds past the fourth stack down the last slot by this much of a card.</summary>
        private const float WoundStackStep = 0.16f;

        // Above the map sprite and the space graph, below figures; no overlap either way.
        private const int BoardOrder = 6;
        private const int MarkerOrder = 7;
        private const int TextOrder = 8;

        private readonly TokenArt _art;
        private readonly Describe _describe;
        private readonly Transform _root;
        private readonly List<(Rect area, string note)> _hoverNotes = new List<(Rect, string)>();

        /// <summary>World extents the boards add past the map's own rect, for the camera
        /// clamp: the right edge of the column, and the (negative) bottom of this seat's
        /// board. Zero until the first Render.</summary>
        public float RightExtent { get; private set; }
        public float BottomExtent { get; private set; }

        public WorldPlayerBoards(Transform parent, TokenArt art, Describe describe)
        {
            _art = art;
            _describe = describe;
            var go = new GameObject("PlayerBoards");
            _root = go.transform;
            _root.SetParent(parent, false);
        }

        public void Render(PlayerView view, string myInvestigatorId, float mapWidth, float mapHeight)
        {
            UiKit.Clear(_root);
            _hoverNotes.Clear();
            RightExtent = mapWidth;
            BottomExtent = -mapHeight;
            if (view == null)
            {
                return;
            }

            // Same-sized boards: this seat's centred under the map, everyone else's stacked
            // down a single column off the map's right edge, the column's foot level with
            // the map's bottom.
            float gap = GapShare * mapWidth;
            float width = BoardWidthShare * mapWidth;
            float bottom = -mapHeight;
            var mine = view.Investigators.FirstOrDefault(i => i.DefId == myInvestigatorId);
            if (mine != null)
            {
                float myTop = -(mapHeight + gap);
                bottom = Mathf.Min(bottom, myTop - DrawBoard(mine, (mapWidth - width) / 2f, myTop, width));
            }

            var others = view.Investigators.Where(i => i.DefId != myInvestigatorId).ToList();
            float columnGap = gap * 0.6f;
            float columnHeight = others.Sum(inv => BoardHeight(inv, width)) +
                                 columnGap * Mathf.Max(0, others.Count - 1);
            float columnLeft = mapWidth + gap;
            float columnTop = -mapHeight + columnHeight;
            foreach (var inv in others)
            {
                columnTop -= DrawBoard(inv, columnLeft, columnTop, width) + columnGap;
            }
            RightExtent = others.Count > 0 ? columnLeft + width : mapWidth;
            BottomExtent = bottom;
        }

        /// <summary>The wound-chip note under this world point, if any.</summary>
        public string NoteAt(Vector3 world)
        {
            foreach (var (area, note) in _hoverNotes)
            {
                if (area.Contains((Vector2)world))
                {
                    return note;
                }
            }
            return null;
        }

        // ------------------------------------------------------------- one board

        /// <summary>World height of an Investigator's board at this width — the art's own
        /// aspect, or the fallback card's when no art was synced — plus the room its Wound
        /// cards hang below it, reserved whether or not any are dealt so nothing shifts
        /// when one arrives.</summary>
        private float BoardHeight(PlayerView.InvestigatorPanel inv, float width)
        {
            var sprite = _art.PlayerBoardFront(inv.DefId);
            float aspect = sprite != null && sprite.bounds.size.y > 0f
                ? sprite.bounds.size.x / sprite.bounds.size.y
                : FallbackAspect;
            float art = width / aspect;
            float hang = WoundBandTop * art + WoundCardHeight(width) - art;
            return art + Mathf.Max(0f, hang);
        }

        private static float WoundSlotWidth(float boardWidth) =>
            (WoundBandRight - WoundBandLeft - WoundBandGap * (WoundSlots - 1)) / WoundSlots * boardWidth;

        private static float WoundCardHeight(float boardWidth) =>
            WoundSlotWidth(boardWidth) * WoundCardSlotShare / WoundCardAspect;

        /// <summary>Draw one Investigator's board with its top-left at (left, top); returns
        /// the board's world height so the caller can stack the next one.</summary>
        private float DrawBoard(PlayerView.InvestigatorPanel inv, float left, float top, float width)
        {
            var sprite = _art.PlayerBoardFront(inv.DefId);
            float total = BoardHeight(inv, width);
            float height = sprite != null && sprite.bounds.size.y > 0f
                ? width / (sprite.bounds.size.x / sprite.bounds.size.y)
                : width / FallbackAspect;

            if (sprite == null)
            {
                DrawFallback(inv, left, top, width, height);
                DrawWounds(inv, left, top, width, height);
                return total;
            }

            var board = NewSprite("Board-" + inv.DefId, sprite, Color.white, BoardOrder);
            board.transform.position = new Vector3(left + width / 2f, top - height / 2f, 0f);
            float scale = width / Mathf.Max(0.0001f, sprite.bounds.size.x);
            board.transform.localScale = new Vector3(scale, scale, 1f);

            TrackMarker("Stamina.png", left, top, width, height,
                StaminaCircleX[Mathf.Clamp(inv.Stamina, 0, StaminaCircleX.Length - 1)],
                StaminaCircleY, MarkerDiameter * width);
            TrackMarker("Charge.png", left, top, width, height,
                ChargeCircleX[Mathf.Clamp(inv.Charge, 0, ChargeCircleX.Length - 1)],
                ChargeCircleY, MarkerDiameter * width);
            if (inv.MajorAbilityTokens > 0)
            {
                // The physical token sits on the printed gear while unspent and leaves when used.
                TrackMarker("Major-Ability.png", left, top, width, height,
                    MajorAbilityX, MajorAbilityY, MajorAbilityDiameter * width);
            }
            DrawWounds(inv, left, top, width, height);
            return total;
        }

        /// <summary>The physical marker token (tokens/investigator/player-board); when that
        /// art was never synced, a plain white disc with a dark ring.</summary>
        private void TrackMarker(string tokenFile, float left, float top, float width, float height,
            float fractionX, float fractionY, float diameter)
        {
            var world = new Vector3(left + fractionX * width, top - fractionY * height, 0f);
            // The token art is a white square scan; the circle mask turns it back into the
            // physical punch-out disc.
            var art = _art.CircularToken("investigator/player-board/" + tokenFile);
            var go = NewSprite("Marker", art ?? UiSprites.Circle,
                art != null ? Color.white : new Color(0.97f, 0.97f, 0.95f), MarkerOrder);
            go.transform.position = world;
            Scale(go, diameter);
            if (art == null)
            {
                var ring = NewSprite("MarkerRing", UiSprites.Ring,
                    new Color(0.04f, 0.04f, 0.05f), MarkerOrder + 1);
                ring.transform.position = world;
                Scale(ring, diameter);
            }
        }

        // ----------------------------------------------------------------- wounds

        /// <summary>
        /// One card per Wound in the printed slots, hanging below the board the way the
        /// physical cards do: a face-up Wound shows its face (hover for the text), a face-down
        /// one shows the deck's back. Wounds past the fourth slot — the unslotted ones included
        /// — stack down the last slot.
        /// </summary>
        private void DrawWounds(PlayerView.InvestigatorPanel inv, float left, float top,
            float width, float height)
        {
            var wounds = inv.Wounds.Concat(inv.NonSlotWounds).ToList();
            for (int i = 0; i < wounds.Count; i++)
            {
                var wound = wounds[i];
                int slot = Mathf.Min(i, WoundSlots - 1);
                float drop = Mathf.Max(0, i - (WoundSlots - 1)) * WoundStackStep * WoundCardHeight(width);
                bool named = IsNamed(wound);
                var face = named ? _art.WoundCard(wound.CardId) : _art.WoundCardBack();
                WoundCard(slot, left, top, width, height, drop, face, WoundLabel(wound),
                    named ? _describe.CardText(wound.CardId) : null, i);
            }
        }

        /// <summary>A face-up Wound whose card the viewer is allowed to see.</summary>
        private static bool IsNamed(PlayerView.WoundSlot wound) =>
            wound.FaceUp && !string.IsNullOrEmpty(wound.CardId);

        private string WoundLabel(PlayerView.WoundSlot wound) =>
            IsNamed(wound) ? _describe.Card(wound.CardId) : wound.FaceUp ? "Wound" : "Face-down";

        private void WoundCard(int slot, float left, float top, float width, float height,
            float drop, Sprite face, string label, string note, int order)
        {
            float slotWidth = WoundSlotWidth(width);
            float cardWidth = slotWidth * WoundCardSlotShare;
            float cardHeight = WoundCardHeight(width);
            float cardLeft = left + (WoundBandLeft + slot * (slotWidth / width + WoundBandGap)) * width +
                             (slotWidth - cardWidth) / 2f;
            float cardTop = top - WoundBandTop * height - drop;
            var centre = new Vector3(cardLeft + cardWidth / 2f, cardTop - cardHeight / 2f, 0f);

            if (face != null)
            {
                var card = NewSprite("Wound" + slot, face, Color.white, MarkerOrder + order);
                card.transform.position = centre;
                float scale = cardWidth / Mathf.Max(0.0001f, face.bounds.size.x);
                card.transform.localScale = new Vector3(scale, scale, 1f);
            }
            else
            {
                // No synced card art: a dark card with the name on it.
                var card = NewSprite("Wound" + slot, UiSprites.RoundedRect,
                    new Color(0.03f, 0.03f, 0.04f, 0.92f), MarkerOrder + order);
                var renderer = card.GetComponent<SpriteRenderer>();
                renderer.drawMode = SpriteDrawMode.Sliced;
                renderer.size = new Vector2(cardWidth, cardHeight);
                card.transform.position = centre;
                var text = new GameObject("Label", typeof(TextMeshPro));
                text.transform.SetParent(_root, false);
                var tmp = text.GetComponent<TextMeshPro>();
                tmp.font = UiKit.Font;
                tmp.text = label;
                tmp.fontSize = cardWidth * 0.14f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = new Color(0.90f, 0.45f, 0.42f);
                tmp.GetComponent<MeshRenderer>().sortingOrder = TextOrder + order;
                text.transform.position = centre;
            }

            if (!string.IsNullOrEmpty(note))
            {
                _hoverNotes.Add((new Rect(cardLeft, cardTop - cardHeight, cardWidth, cardHeight), note));
            }
        }

        // --------------------------------------------------------------- fallback

        /// <summary>No synced art (the TokenArt contract: every lookup may miss) — a dark
        /// card with the name and the numbers the printed tracks would have shown.</summary>
        private void DrawFallback(PlayerView.InvestigatorPanel inv, float left, float top,
            float width, float height)
        {
            var card = NewSprite("Fallback-" + inv.DefId, UiSprites.RoundedRect,
                new Color(0.055f, 0.06f, 0.075f, 0.96f), BoardOrder);
            var renderer = card.GetComponent<SpriteRenderer>();
            renderer.drawMode = SpriteDrawMode.Sliced;
            renderer.size = new Vector2(width, height);
            card.transform.position = new Vector3(left + width / 2f, top - height / 2f, 0f);

            var text = new GameObject("Stats", typeof(TextMeshPro));
            text.transform.SetParent(_root, false);
            var tmp = text.GetComponent<TextMeshPro>();
            tmp.font = UiKit.Font;
            tmp.text = _describe.Investigator(inv.DefId) + "\nStamina " + inv.Stamina +
                "   Charge " + inv.Charge + "   Wounds " +
                (inv.Wounds.Count + inv.NonSlotWounds.Count);
            tmp.fontSize = height * 0.13f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.88f, 0.87f, 0.84f);
            tmp.GetComponent<MeshRenderer>().sortingOrder = TextOrder;
            text.transform.position = card.transform.position;
        }

        // ---------------------------------------------------------------- helpers

        private GameObject NewSprite(string name, Sprite sprite, Color color, int sortingOrder)
        {
            var go = new GameObject(name, typeof(SpriteRenderer));
            go.transform.SetParent(_root, false);
            var renderer = go.GetComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;
            return go;
        }

        private static void Scale(GameObject go, float diameter)
        {
            var renderer = go.GetComponent<SpriteRenderer>();
            float native = renderer.sprite != null ? renderer.sprite.bounds.size.x : 1f;
            float scale = diameter / Mathf.Max(0.0001f, native);
            go.transform.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
