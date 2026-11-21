using System;
using System.IO;
namespace CustomSoundtrack
{
    public static class PlayerUiRules
    {
        public static double SkinScale(double requested, double screenWidth, double screenHeight)
        {
            if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0) requested = 0.66;
            return Math.Max(0.01, Math.Min(requested, Math.Min((screenWidth - 20) / 733.0, (screenHeight - 20) / 1008.0)));
        }
        public static string PlaylistLabel(int count, int selected, int current)
        {
            int index = selected >= 0 && selected < count ? selected : current >= 0 && current < count ? current : count > 0 ? 0 : -1;
            return "Playlist " + (index + 1) + "/" + count;
        }
        public static int TargetIndex(int count, int current, int selected, int command)
        {
            if (count <= 0) return -1;
            int basis = selected >= 0 && selected < count ? selected : current >= 0 && current < count ? current : 0;
            return command == -1 ? (basis + count - 1) % count : command == 1 ? (basis + 1) % count : basis;
        }
        public static bool Matches(string file, string search)
            => Path.GetFileNameWithoutExtension(file ?? "").IndexOf((search ?? "").Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        public static double SeekPosition(double requested, double duration)
            => double.IsNaN(requested) || double.IsInfinity(requested) || double.IsNaN(duration) || double.IsInfinity(duration) || duration<=0 ? 0 : Math.Max(0,Math.Min(requested,Math.Max(0,duration-.05)));
        public static double Progress(double position, double duration)
            => duration <= 0 || double.IsNaN(position) || double.IsNaN(duration) || double.IsInfinity(duration) ? 0 : Math.Max(0, Math.Min(1, position / duration));
        public static string TimeLabel(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            int total = (int)Math.Min(seconds, int.MaxValue);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }
    }
}
