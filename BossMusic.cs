using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        public enum BossPlaylistMode { PreferIndividual, SharedOnly, IndividualOnly, MixAllBossFolders }
        private ConfigEntry<bool> bossMusicEnabled, bossDebug;
        private ConfigEntry<float> bossVolume, bossRadius, bossFullDistance;
        private ConfigEntry<BossPlaylistMode> bossPlaylistMode;
        private ConfigEntry<BossDeathMode> bossDeathMode;
        private ConfigEntry<float> bossStaticChance, bossStaticVolume;
        private AudioClip bossStaticClip;
        private readonly Dictionary<string, ConfigEntry<bool>> bossToggles = new Dictionary<string, ConfigEntry<bool>>();
        private readonly Dictionary<string, BossEmitter> bossEmitters = new Dictionary<string, BossEmitter>();
        private readonly Dictionary<string, string[]> bossFiles = new Dictionary<string, string[]>();
        private double nextBossScan;
        private bool bossLoading, bossFaulted;
        private object bossWorld;
        private int bossGeneration;
        private sealed class BossEmitter
        {
            public string Id, Kind, LastFile;
            public object Player;
            public Transform Target, Head;
            public object HelmetSlot, HelmetItem;
            public bool HelmetRemoved;
            public AudioLowPassFilter Filter;
            public AudioSource Source;
            public Asset Asset;
            public bool Dead, CorpseFound, FallbackLogged;
            public double RetryAt, DiedAt;
            public double DeathRoll = -1;
            public BossDeathMode DeathAction;
        }
        private void ConfigureBossMusic()
        {
            bossMusicEnabled = Config.Bind("Boss Music", "Enabled", false, "Enable local boss audio; choose bosses below.");
            bossVolume = FloatSetting("Boss Music", "Volume", .65f, 0, 1, "Boss level, multiplied by Master Volume.");
            bossRadius = FloatSetting("Boss Music", "HearingRadiusMetres", 40, 1, 200, "Maximum audible distance.");
            bossFullDistance = FloatSetting("Boss Music", "FullVolumeDistanceMetres", 3, 0, 50, "Full volume within this distance.");
            var oldDeath = Config.Bind("Boss Music", "ContinueAfterDeath", true, "Legacy migration.");
            bossDeathMode = Config.Bind("Boss Music", "OnBossDeath", oldDeath.Value ? BossDeathMode.ContinueMusic : BossDeathMode.StopMusic, "Choose what the speaker does when a boss dies.");
            Config.Remove(new ConfigDefinition("Boss Music","ContinueAfterDeath"));
            bossStaticChance = FloatSetting("Boss Music","RandomStaticChance",.5f,0,1,"Random mode: chance of static. Otherwise music continues.");
            bossStaticVolume = FloatSetting("Boss Music","StaticVolume",.35f,0,1,"Static level, multiplied by boss volume.");
            bossPlaylistMode = Config.Bind("Boss Music", "PlaylistMode", BossPlaylistMode.PreferIndividual, "Use individual boss folders or Shared only.");
            bossDebug = Config.Bind("Boss Music", "DebugLogging", false, "Log boss detection and corpse attachment.");
            foreach (var name in BossMusicRules.Names)
                bossToggles[name] = Config.Bind("Boss Music - Bosses", name, false, "Enable local music for " + name + ". Folder: Music/Bosses/" + name + ". Applies during a raid.");
            // No audio is decoded during startup. Only folder names are prepared here.
            foreach (var name in BossMusicRules.Names.Concat(new[] { "Shared" }))
                Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Music", "Bosses", name));
        }
        private string[] BossPlaylist(string name)
        {
            string key = bossPlaylistMode.Value + ":" + name;
            if (bossFiles.TryGetValue(key, out var cached)) return cached;
            string root = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Music", "Bosses");
            Func<string,string[]> scan = folder => Directory.GetFiles(Path.Combine(root,folder)).Where(AudioFormats.Supported).OrderBy(f=>f,StringComparer.OrdinalIgnoreCase).ToArray();
            string[] files;
            if (bossPlaylistMode.Value == BossPlaylistMode.MixAllBossFolders)
                files=BossMusicRules.Names.Concat(new[]{"Shared"}).SelectMany(scan).Distinct().ToArray();
            else if(bossPlaylistMode.Value == BossPlaylistMode.SharedOnly) files=scan("Shared");
            else { files=scan(name); if(files.Length==0 && bossPlaylistMode.Value==BossPlaylistMode.PreferIndividual) files=scan("Shared"); }
            bossFiles[key]=files;
            return files;
        }
        private void ClearBossMusic()
        {
            bossGeneration++;
            foreach (var b in bossEmitters.Values) ReleaseBoss(b);
            bossEmitters.Clear(); bossFiles.Clear(); bossWorld = null; nextBossScan = 0;
        }
        private static void ReleaseBoss(BossEmitter b)
        {
            if (b.Source != null) { b.Source.Stop(); Destroy(b.Source.gameObject); }
            b.Asset?.Dispose(); b.Asset = null;
        }
        private void UpdateBossMusic()
        {
            try
            {
                if (bossMusicEnabled == null || !bossMusicEnabled.Value || !enabledSetting.Value || faulted || bossFaulted || !state.InRaid || world == null)
                { if (bossWorld != null || bossEmitters.Count > 0) ClearBossMusic(); return; }
                if (!ReferenceEquals(bossWorld, world)) { ClearBossMusic(); bossWorld = world; }
                if (Now >= nextBossScan)
                {
                    nextBossScan = Now + 1;
                    ScanBosses();
                }
                foreach (var b in bossEmitters.Values)
                {
                    if (b.Source == null) continue;
                    if(b.Dead)
                    {
                        if(b.DeathRoll<0) b.DeathRoll=random.NextDouble();
                        b.DeathAction=BossMusicRules.DeathAction(bossDeathMode.Value,b.DeathRoll,bossStaticChance.Value,bossSilenceChance.Value);
                    }
                    UpdateBossHelmet(b);
                    bool enabled = !b.HelmetRemoved && bossToggles[b.Kind].Value && (!b.Dead || b.DeathAction!=BossDeathMode.StopMusic);
                    if (!enabled) { b.Source.Stop(); continue; }
                    if (b.Target != null) b.Source.transform.position = b.Target.position + Vector3.up * (b.Dead ? .2f : 1f);
                    // Position follows every frame. Reflection/role discovery runs once per second.
                    var listener = localPlayer as Component;
                    float distance = listener != null ? Vector3.Distance(listener.transform.position, b.Source.transform.position) : float.PositiveInfinity;
                    b.Source.volume = MasterGain * bossVolume.Value * (float)BossMusicRules.Gain(distance, bossFullDistance.Value, bossRadius.Value);
                    UpdateBossMuffle(b, listener);
                    if(b.Dead && b.DeathAction==BossDeathMode.Static)
                    {
                        b.Source.volume *= bossStaticVolume.Value;
                        if(bossStaticClip==null) CreateBossStatic();
                        if(b.Source.clip!=bossStaticClip) { b.Source.Stop(); b.Source.clip=bossStaticClip; b.Source.loop=true; }
                        if(!b.Source.isPlaying) b.Source.Play();
                        continue;
                    }
                    if(b.Source.loop) { b.Source.Stop(); b.Source.loop=false; b.Source.clip=null; }
                    if (!bossLoading && distance < bossRadius.Value && !b.Source.isPlaying && Now >= b.RetryAt)
                    {
                        bossLoading = true;
                        StartCoroutine(LoadBossTrack(b, bossGeneration));
                    }
                }
            }
            catch (Exception ex)
            {
                bossFaulted = true; ClearBossMusic();
                Logger.LogWarning("Boss music disabled for this session; other soundtrack features retained. " + ex);
            }
        }
        private void CreateBossStatic()
        {
            const int rate=22050;
            var samples=new float[rate*2];
            var noise=new System.Random(7421);
            float last=0;
            for(int i=0;i<samples.Length;i++) { last=.3f*last+.7f*((float)noise.NextDouble()*2-1); samples[i]=last*.25f; }
            bossStaticClip=AudioClip.Create("CustomSoundtrack broken speaker",samples.Length,1,rate,false);
            bossStaticClip.hideFlags=HideFlags.HideAndDontSave;
            bossStaticClip.SetData(samples,0);
        }
        private void RememberBossDeath(object player)
        {
            foreach (var b in bossEmitters.Values)
                if (ReferenceEquals(b.Player, player) && !b.Dead) { b.Dead = true; b.DiedAt = Now; }
        }
        private void ScanBosses()
        {
            var players = Read(world, "RegisteredPlayers") as IEnumerable;
            if (players == null) throw new MissingMemberException("GameWorld.RegisteredPlayers is unavailable.");
            foreach (var player in players)
            {
                var component = player as Component;
                if (component == null) continue;
                var profile = Read(player, "Profile");
                string role = Read(Read(Read(profile, "Info"), "Settings"), "Role")?.ToString();
                string kind = BossMusicRules.NameForRole(role);
                if (kind == null || !bossToggles[kind].Value) continue;
                string id = Read(player, "ProfileId")?.ToString();
                if (string.IsNullOrEmpty(id)) id = "instance:" + component.GetInstanceID();
                if (bossEmitters.ContainsKey(id)) continue;
                var go = new GameObject("CustomSoundtrack Boss " + kind);
                go.transform.SetParent(transform, false);
                go.transform.position = component.transform.position + Vector3.up;
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false; source.loop = false; source.spatialBlend = 1; source.dopplerLevel = 0;
                // Constant Unity attenuation; our gain supplies an exact, live-adjustable cutoff.
                source.rolloffMode = AudioRolloffMode.Custom;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Linear(0,1,1,1));
                source.minDistance = 1; source.maxDistance = 1000; source.volume = 0;
                bossEmitters[id] = new BossEmitter { Id=id, Kind=kind, Player=player, Target=component.transform, Source=source };
                CaptureBossHelmet(bossEmitters[id]);
                if (bossDebug.Value) Logger.LogInfo("Boss music detected: " + kind + "; role=" + role + "; profile=" + id);
            }
            foreach (var b in bossEmitters.Values)
            {
                if (b.CorpseFound) continue;
                // Player.Corpse is exposed by the supplied SPT game assembly.
                var component = b.Player as Component;
                object corpse = component != null ? Read(b.Player, "Corpse") : null;
                var body = corpse as Component;
                if (body != null)
                {
                    b.Dead = true; b.Target = body.transform; b.CorpseFound = true;
                    if (bossDebug.Value) Logger.LogInfo("Boss music attached to corpse: " + b.Kind);
                    continue;
                }
                var alive = component != null ? Read(Read(b.Player, "HealthController"), "IsAlive") : null;
                if (alive is bool isAlive && !isAlive && !b.Dead) { b.Dead = true; b.DiedAt = Now; }
                if (component == null && !b.Dead)
                {
                    // Unspawn is not proof of death. Do not leave ghost music at a despawn location.
                    b.Source.Stop(); b.RetryAt = double.PositiveInfinity;
                    continue;
                }
                if (b.Dead && Now - b.DiedAt > 15 && !b.FallbackLogged)
                {
                    b.FallbackLogged = true;
                    Logger.LogWarning("Boss music: corpse reference not available for " + b.Kind + "; retaining last known position. Include this log if its body moved away from the music.");
                }
            }
        }
        private IEnumerator LoadBossTrack(BossEmitter b, int generation)
        {
            var asset = new Asset();
            try
            {
                string[] files;
                try { files = BossPlaylist(b.Kind); }
                catch (Exception ex) { Logger.LogWarning("Boss playlist scan failed: " + ex.Message); b.RetryAt = Now+30; yield break; }
                if (files.Length == 0) { b.RetryAt=Now+30; yield break; }
                int start = random.Next(files.Length);
                if (files.Length > 1 && files[start] == b.LastFile) start = (start+1)%files.Length;
                // One candidate per request; failed files back off rather than blocking the frame.
                asset.File = files[start];
                yield return Decode(asset);
                if (generation != bossGeneration || b.Source == null || !bossMusicEnabled.Value || !bossToggles[b.Kind].Value || !state.InRaid || b.HelmetRemoved || (b.Dead && BossMusicRules.DeathAction(bossDeathMode.Value,b.DeathRoll<0 ? 0 : b.DeathRoll,bossStaticChance.Value,bossSilenceChance.Value)!=BossDeathMode.ContinueMusic)) yield break;
                if (!Valid(asset.Clip)) { b.RetryAt=Now+30; yield break; }
                b.Source.Stop(); b.Asset?.Dispose(); b.Asset=asset; asset=null;
                b.Source.clip=b.Asset.Clip; b.Source.Play(); b.LastFile=b.Asset.File;
                if (bossDebug.Value) Logger.LogInfo("Boss track: " + b.Kind + " / " + Path.GetFileName(b.LastFile));
            }
            finally { asset?.Dispose(); bossLoading=false; }
        }
    }
}
