using System;
namespace CustomSoundtrack
{
    public sealed class FadeEnvelope
    {
        private double began, seconds;
        private float from, target;
        public float Target => target;
        public float Value(double now)
        {
            if (seconds <= 0) return target;
            double t = Math.Max(0, Math.Min(1, (now - began) / seconds));
            return (float)(from + (target - from) * t);
        }
        public void Set(float value, double duration, double now)
        { from = Value(now); target = value; seconds = Math.Max(0, duration); began = now; }
        public bool Finished(double now) => seconds <= 0 || now - began >= seconds;
    }
    public sealed class OutcomeClock
    {
        public double Position { get; private set; }
        public double Duration { get; private set; }
        public void Reset() { Position = 0; Duration = 0; }
        public void Observe(double position, double duration)
        {
            if (duration <= 0 || double.IsNaN(duration) || double.IsInfinity(duration)) return;
            Duration = duration;
            if (position >= 0 && position <= duration) Position = Math.Max(Position, position);
        }
        public double Remaining(double limit)
        {
            if (Duration <= 0) return double.PositiveInfinity;
            return Math.Max(0, (limit > 0 ? Math.Min(limit, Duration) : Duration) - Position);
        }
        // A stopped source during scene unloading is not proof that the song finished.
        public bool Complete(bool playing, double limit)
            => Duration > 0 && (Remaining(limit) <= 0 || (!playing && Remaining(limit) <= 0.3));
    }
    public static class PlaybackRouting
    {
        public static MusicKind RaidTrackTarget(bool personalPlaying)
            => personalPlaying ? MusicKind.Personal : MusicKind.RaidAmbient;
    }
    public static class AudioRetryRules
    {
        // A cooldown belongs to a failed decode, never to a successful clip later lost during unloading.
        public static double AfterDecode(bool valid, bool permanentlyRejected, double now)
            => valid ? 0 : permanentlyRejected ? double.PositiveInfinity : now + 30;
    }
    public static class OutcomeAudioRules
    {
        public static MusicKind FromStatus(string status, bool runThrough)
            => status == "Killed" ? MusicKind.Death : status == "Survived" || (status == "Runner" && runThrough) ? MusicKind.Extraction : MusicKind.None;
        public static bool SuppressJingle(string status, bool runThrough, int extractionTracks, int deathTracks)
        {
            var kind = FromStatus(status, runThrough);
            return kind == MusicKind.Extraction ? extractionTracks > 0 : kind == MusicKind.Death && deathTracks > 0;
        }
    }
    public static class CombatRules
    {
        public static bool Shot(bool own, bool ownEnabled, bool nearbyEnabled, double distance, double radius)
            => own ? ownEnabled : nearbyEnabled && distance >= 0 && distance <= radius;
        public static bool Grenade(bool enabled, double distance, double radius)
            => enabled && distance >= 0 && distance <= radius;
    }
}
