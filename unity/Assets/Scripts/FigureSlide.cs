using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Glides a freshly drawn figure element from where it last stood to where it is now:
    /// on creation the transform is pushed back by <see cref="Delta"/> and eased home with
    /// a soft landing. Lives on render-owned objects, so a rebuild simply replaces it.
    /// </summary>
    public sealed class FigureSlide : MonoBehaviour
    {
        /// <summary>Starting offset from the resting position (old space minus new).</summary>
        public Vector3 Delta;
        /// <summary>Matches GameUi's walk pace, so a pathed walk reads as one motion.</summary>
        public float Duration = 0.25f;

        private Vector3 _target;
        private float _elapsed;

        private void Start()
        {
            _target = transform.position;
            transform.position = _target + Delta;
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, Duration));
            t = t * t * (3f - 2f * t); // smoothstep: fast middle, soft landing
            transform.position = _target + Delta * (1f - t);
        }
    }
}
