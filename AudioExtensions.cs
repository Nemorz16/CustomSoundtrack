using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
namespace CustomSoundtrack
{
    internal static class AudioFormats
    {
        public static bool Supported(string file) => UiPackRules.AudioFile(file);
        public static AudioType Type(string file)
        {
            switch(System.IO.Path.GetExtension(file).ToLowerInvariant())
            { case ".ogg": return AudioType.OGGVORBIS; case ".mp3": return AudioType.MPEG; default: return AudioType.WAV; }
        }
    }
    public sealed partial class Plugin
    {
        internal static float MasterGain => instance != null && instance.enabledSetting.Value ? instance.master.Value : 0;
        private ConfigEntry<bool> advancedVisible, masterFadeEnabled, bossMuffle, stopHelmetLoot, trackToast;
        private ConfigEntry<float> masterFadeSeconds, bossSilenceChance, bossContinueChance, killaCutoff, tagillaFront, tagillaRear, muffleSpeed, uiVolume;
        public enum PersonalPlaybackMode { Sequence, Shuffle }
        private ConfigEntry<PersonalPlaybackMode> personalPlayback;
        private string toastText;
        private double toastUntil;
        private void ConfigureAudioExtensions()
        {
            advancedVisible=Config.Bind("General","AdvancedDebug",false,"Show advanced diagnostics; does not enable logging.");
            masterFadeEnabled=Config.Bind("General","UseMasterFade",false,"Override fade times; combat entry remains instant.");
            masterFadeSeconds=FloatSetting("General","MasterFadeSeconds",2,0,15,"Fade duration when Master Fade is enabled.");
            uiVolume=FloatSetting("UI Sounds","Volume",1,0,1,"Replacement UI volume, multiplied by Master Volume.");
            bossSilenceChance=FloatSetting("Boss Music","RandomSilenceChance",0,0,1,"Random mode: chance of silence. Static takes priority if the total exceeds 100%.");
            bossContinueChance=FloatSetting("Boss Music","ContinueMusicChance",.5f,0,1,"Remaining probability after static and silence; calculated automatically.");
            bossMuffle=Config.Bind("Boss Music","HelmetMuffling",false,"Filter Killa and Tagilla through their helmets.");
            killaCutoff=FloatSetting("Boss Music","KillaCutoffHz",1800,200,22000,"Lower values make Killa more muffled in all directions.");
            tagillaFront=FloatSetting("Boss Music","TagillaFrontCutoffHz",2200,200,22000,"Tagilla's front-facing low-pass cutoff.");
            tagillaRear=FloatSetting("Boss Music","TagillaRearCutoffHz",12000,200,22000,"Tagilla's rear-facing low-pass cutoff.");
            muffleSpeed=FloatSetting("Boss Music","MuffleSmoothingSeconds",.2f,.02f,2,"Smooth filtering as the boss or listener turns.");
            stopHelmetLoot=Config.Bind("Boss Music","StopWhenHelmetLooted",true,"Stop music/static permanently when a dead boss's original headwear is removed.");
            personalPlayback=Config.Bind("Personal Player","Playback",PersonalPlaybackMode.Sequence,"Play personal tracks in sequence or shuffle.");
            trackToast=Config.Bind("Personal Player","TrackAnnouncement",false,"Briefly show the personal track name on screen.");
            ConfigureInjuryAnnouncements();
            ConfigureEnvironmentControls();
            ConfigureSessionChoices();
        }
        private float FadeValue(string key) => masterFadeEnabled.Value ? masterFadeSeconds.Value : fades[key].Value;
        private void UpdateAudioExtensions()
        {
            float silence=Mathf.Min(bossSilenceChance.Value,1-bossStaticChance.Value);
            if(!Mathf.Approximately(silence,bossSilenceChance.Value)) bossSilenceChance.Value=silence;
            float remaining=(float)BossMusicRules.ContinueChance(bossStaticChance.Value,silence);
            if(!Mathf.Approximately(remaining,bossContinueChance.Value)) bossContinueChance.Value=remaining;
            UpdateSessionChoices();
        }
        private void ShowToast(string text, float seconds=4) { toastText=text; toastUntil=Now+seconds; }
        private void DrawSoundToast()
        {
            if(Now>=toastUntil || string.IsNullOrEmpty(toastText)) return;
            var style=new GUIStyle(GUI.skin.box) { wordWrap=true, alignment=TextAnchor.MiddleCenter, fontSize=16 };
            GUI.Box(new Rect(20,Screen.height-105,Math.Min(900,Screen.width-40),80),toastText,style);
        }
        private void CaptureBossHelmet(BossEmitter b)
        {
            try
            {
                var head=Read(Read(b.Player,"PlayerBones"),"Head");
                b.Head=head as Transform ?? Read(head,"Original") as Transform;
                var inventory=Read(Read(b.Player,"Profile"),"Inventory");
                var equipment=Read(b.Player,"Equipment") ?? Read(inventory,"Equipment");
                if(equipment==null) return;
                var get=equipment.GetType().GetMethods().FirstOrDefault(m=>m.Name=="GetSlot" && m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType.IsEnum);
                if(get==null) return;
                // Prefer actual headwear; face cover supports bosses whose mask uses that slot.
                foreach(string name in new[]{"Headwear","FaceCover"})
                {
                    var slot=get.Invoke(equipment,new[]{Enum.Parse(get.GetParameters()[0].ParameterType,name)});
                    var item=Read(slot,"ContainedItem");
                    if(item==null) continue;
                    b.HelmetSlot=slot; b.HelmetItem=item; break;
                }
                if(bossDebug.Value) Logger.LogInfo("Boss headwear tracking: "+b.Kind+"; slot="+Read(b.HelmetSlot,"ID")+"; item="+Read(b.HelmetItem,"Id"));
            }
            catch(Exception ex) { if(bossDebug.Value) Logger.LogWarning("Boss headwear unavailable: "+ex.Message); }
        }
        private void UpdateBossHelmet(BossEmitter b)
        {
            if(!b.Dead || !stopHelmetLoot.Value || b.HelmetRemoved || b.HelmetItem==null || b.HelmetSlot==null) return;
            if(!ReferenceEquals(Read(b.HelmetSlot,"ContainedItem"),b.HelmetItem))
            {
                b.HelmetRemoved=true; b.Source.Stop();
                if(bossDebug.Value) Logger.LogInfo("Boss speaker stopped after headwear removal: "+b.Kind);
            }
        }
        private void UpdateBossMuffle(BossEmitter b, Component listener)
        {
            bool enabled=bossMuffle.Value && (b.Kind=="Killa" || b.Kind=="Tagilla") && b.HelmetItem!=null && !b.HelmetRemoved;
            if(!enabled) { if(b.Filter!=null) b.Filter.enabled=false; return; }
            if(b.Filter==null) { b.Filter=b.Source.gameObject.AddComponent<AudioLowPassFilter>(); b.Filter.lowpassResonanceQ=1; b.Filter.cutoffFrequency=22000; }
            b.Filter.enabled=true;
            float cutoff=killaCutoff.Value;
            if(b.Kind=="Tagilla")
            {
                var head=b.Head!=null ? b.Head : b.Target;
                float dot=head!=null && listener!=null ? Vector3.Dot(head.forward,(listener.transform.position-head.position).normalized) : 0;
                cutoff=(float)BossMusicRules.HelmetCutoff(dot,tagillaFront.Value,tagillaRear.Value);
            }
            b.Filter.cutoffFrequency=Mathf.Lerp(b.Filter.cutoffFrequency,cutoff,1-Mathf.Exp(-Time.unscaledDeltaTime/muffleSpeed.Value));
        }
    }
}
