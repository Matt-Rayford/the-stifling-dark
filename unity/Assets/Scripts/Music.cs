using UnityEngine;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Menu music and the master volume, Lemonade Wars style: volume moves 0-100 in steps
    /// of five on <see cref="AudioListener.volume"/> and persists in PlayerPrefs; the menu
    /// track loops from app start until a game begins and resumes when the table is left.
    /// The clip comes from Assets/Resources/Music, synced there from game-assets/music.
    /// </summary>
    public static class Music
    {
        public const int VolumeStep = 5;

        private const string VolumePref = "sd_master_volume";

        private static AudioSource _source;

        /// <summary>Master volume, 0-100 in steps of 5. Persisted and applied globally.</summary>
        public static int Volume
        {
            get
            {
                int stored = PlayerPrefs.GetInt(VolumePref, 100);
                return Mathf.Clamp(
                    Mathf.RoundToInt(stored / (float)VolumeStep) * VolumeStep, 0, 100);
            }
            set
            {
                int level = Mathf.Clamp(
                    Mathf.RoundToInt(value / (float)VolumeStep) * VolumeStep, 0, 100);
                PlayerPrefs.SetInt(VolumePref, level);
                PlayerPrefs.Save();
                ApplySavedVolume();
            }
        }

        /// <summary>Push the saved level onto the listener; call once at boot.</summary>
        public static void ApplySavedVolume()
        {
            AudioListener.volume = Volume / 100f;
        }

        /// <summary>Loop the menu track. Idempotent: safe on every stage change.</summary>
        public static void PlayMenuTrack()
        {
            EnsureSource();
            if (_source.clip == null)
            {
                _source.clip = Resources.Load<AudioClip>("Music/main-menu-music");
            }
            if (_source.clip != null && !_source.isPlaying)
            {
                _source.Play();
            }
        }

        public static void StopMenuTrack()
        {
            if (_source != null && _source.isPlaying)
            {
                _source.Stop();
            }
        }

        private static void EnsureSource()
        {
            if (_source != null)
            {
                return;
            }
            var go = new GameObject("Music", typeof(AudioSource));
            Object.DontDestroyOnLoad(go);
            _source = go.GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            if (Object.FindFirstObjectByType<AudioListener>() == null)
            {
                go.AddComponent<AudioListener>(); // the code-built scene ships without one
            }
        }
    }
}
