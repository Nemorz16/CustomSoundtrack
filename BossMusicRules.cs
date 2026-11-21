using System;
namespace CustomSoundtrack
{
    public enum BossDeathMode { ContinueMusic, StopMusic, Static, Random }
    public static class BossMusicRules
    {
        public static BossDeathMode DeathAction(BossDeathMode mode, double roll, double chance, double silence = 0)
        {
            if (mode != BossDeathMode.Random) return mode;
            chance = Math.Max(0, Math.Min(1, chance));
            silence = Math.Max(0, Math.Min(1-chance, silence));
            return roll < chance ? BossDeathMode.Static : roll < chance+silence ? BossDeathMode.StopMusic : BossDeathMode.ContinueMusic;
        }
        public static double ContinueChance(double chance, double silence) => Math.Max(0, 1-Math.Max(0,Math.Min(1,chance))-Math.Max(0,silence));
        public static double HelmetCutoff(double frontDot, double front, double rear)
        {
            double t = Math.Max(0, Math.Min(1, (frontDot+1)*.5));
            t=t*t*(3-2*t);
            return Math.Exp(Math.Log(Math.Max(10,rear))*(1-t)+Math.Log(Math.Max(10,front))*t);
        }
        public static readonly string[] Names = { "Killa", "Tagilla", "Reshala", "Shturman", "Glukhar", "Sanitar", "Kaban", "Kollontay", "Knight", "BigPipe", "Birdeye", "Zryachiy", "Partisan" };
        public static string NameForRole(string role)
        {
            switch (role)
            {
                case "bossKilla": case "bossKillaAgro": return "Killa";
                case "bossTagilla": case "bossTagillaAgro": return "Tagilla";
                case "bossBully": return "Reshala";
                case "bossKojaniy": return "Shturman";
                case "bossGluhar": return "Glukhar";
                case "bossSanitar": return "Sanitar";
                case "bossBoar": return "Kaban";
                case "bossKolontay": return "Kollontay";
                case "bossKnight": return "Knight";
                case "followerBigPipe": return "BigPipe";
                case "followerBirdEye": return "Birdeye";
                case "bossZryachiy": return "Zryachiy";
                case "bossPartisan": return "Partisan";
                default: return null;
            }
        }
        public static double Gain(double distance, double full, double radius)
        {
            if (double.IsNaN(distance) || radius <= 0 || distance >= radius) return 0;
            full = Math.Max(0, Math.Min(full, radius - .01));
            if (distance <= full) return 1;
            return Math.Max(0, Math.Min(1, (radius-distance)/(radius-full)));
        }
    }
}
