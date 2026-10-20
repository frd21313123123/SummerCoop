using System;
using UnityEngine;

namespace MWCoop
{
    // One file decoder per receiver. A streaming WWW clip cannot be shared by
    // two AudioSources; retaining the request keeps its backing stream alive.
    public sealed class MscAudioStream : IDisposable
    {
        readonly WWW request;
        public readonly string Key;
        AudioClip clip;
        bool failed;
        public AudioClip Clip { get { return clip; } }
        public MscAudioStream(string path, string key) { Key = key; request = new WWW(new Uri(path).AbsoluteUri); }
        public void Apply(AudioSource source, bool playing, float time)
        {
            if (source == null || failed) return;
            if (clip == null && request.isDone)
            {
                if (!string.IsNullOrEmpty(request.error)) { failed = true; Log.Warn("MSC audio: " + request.error); return; }
                clip = request.GetAudioClip(false, true, AudioType.OGGVORBIS);
                if (clip != null) MscMusic.BindClip(clip, Key);
            }
            if (clip == null || clip.loadState != AudioDataLoadState.Loaded) return;
            bool swap = source.clip != clip;
            if (swap) source.clip = clip;
            bool start = playing && !source.isPlaying && source.gameObject.activeInHierarchy;
            if (start) source.Play(); else if (!playing && source.isPlaying) source.Stop();
            if (swap || start || Mathf.Abs(Time(source) - time) > .25f) Seek(source, time);
        }
        public static float Time(AudioSource source)
        { return source == null || source.clip == null || source.clip.frequency <= 0 ? 0 : (float)source.timeSamples / source.clip.frequency; }
        public static void Seek(AudioSource source, float time)
        {
            AudioClip c = source == null ? null : source.clip;
            if (c != null && c.samples > 0 && c.frequency > 0)
                source.timeSamples = (int)Mathf.Clamp(time * c.frequency, 0, c.samples - 1);
        }
        public void Dispose()
        {
            if (clip != null) { MscMusic.UnbindClip(clip); UnityEngine.Object.Destroy(clip); clip = null; }
            request.Dispose();
        }
    }
}
