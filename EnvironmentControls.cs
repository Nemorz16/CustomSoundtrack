using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> environmentEnabled;
        private ConfigEntry<float> rainVolume, windVolume, btrVolume, planeVolume, dropVolume;
        private readonly HashSet<object> rainBlenders=new HashSet<object>(), windBlenders=new HashSet<object>();
        private readonly Dictionary<object,EnvironmentBaseGain> environmentBases=new Dictionary<object,EnvironmentBaseGain>();
        private sealed class EnvironmentBaseGain
        {
            public float Native, Applied;
            public ConfigEntry<float> Setting;
            public FieldInfo Field;
        }
        private bool environmentHooked, environmentFailed;
        private string environmentSnapshot;
        private void ConfigureEnvironmentControls()
        {
            environmentEnabled=Config.Bind("Environment","Enabled",false,"Adjust native environment volumes.");
            rainVolume=FloatSetting("Environment","Rain",1,0,2,"Precipitation level. 1 = native, before Master Volume. Snow coverage needs testing.");
            windVolume=FloatSetting("Environment","Wind",1,0,2,"Wind level. 1 = native, before Master Volume.");
            btrVolume=FloatSetting("Environment","BTR",1,0,2,"BTR movement volume; does not change gunshots.");
            planeVolume=FloatSetting("Environment","Airplane",1,0,2,"Airdrop aircraft volume.");
            dropVolume=FloatSetting("Environment","Airdrop",1,0,2,"*** Airdrop container playback volume.");
        }
        private float EnvironmentGain(ConfigEntry<float> setting)
            => !environmentFailed && environmentEnabled.Value && enabledSetting.Value ? master.Value*setting.Value : 1;
        // Also used by other partial Plugin files: retain this helper unchanged.
        private void OptionalHook(string type,string method,string callback,bool prefix=false)
        {
            try { var t=AccessTools.TypeByName(type); var m=t==null ? null : AccessTools.Method(t,method); Patch(m,callback,prefix); }
            catch(Exception ex) { Logger.LogWarning("Optional feature unavailable: "+type+"."+method+": "+ex.Message); }
        }
        private void InstallEnvironmentControls()
        {
            if(BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Any(p=>p.Metadata.Name.Replace(" ","").IndexOf("EnvironmentVolumeControl",StringComparison.OrdinalIgnoreCase)>=0))
            { Logger.LogWarning("Environment controls skipped: separate EnvironmentVolumeControl is loaded. Move its DLL outside plugins and restart."); return; }
            OptionalHook("Audio.AmbientSubsystem.PrecipitationAmbientBlender","UpdateGlobalsVolumeMult",nameof(ScalePrecipitation));
            OptionalHook("Audio.AmbientSubsystem.WindAmbientBlender","GetWindVolume",nameof(ScaleWind));
            // Changing OverallVolume also affects callers that inline MaxAllowedVolume.
            OptionalHook("Audio.Vehicles.VehicleMovementSoundContext","Init",nameof(TrackBtrVolume));
            OptionalHook("Audio.Vehicles.VehicleMovementSoundContext","Update",nameof(TrackBtrVolume),true);
            OptionalHook("Audio.Vehicles.VehicleMovementSoundContext","Dispose",nameof(ReleaseEnvironmentBase),true);
            OptionalHook("Audio.Vehicles.VehicleMovementSoundContext","OnDestroy",nameof(ReleaseEnvironmentBase),true);
            // Register before native playback so its first base-volume assignment is scaled too.
            HookAircraft("EFT.Airdrop.ClientAirPlane",1,nameof(TrackPlaneAudio));
            HookAircraft("EFT.Airdrop.ClientAirDrop",3,nameof(TrackDropAudio));
            OptionalHook("BetterSource","SetBaseVolume",nameof(NativeEnvironmentBaseChanged));
            // Audio sources are pooled. Never leave a gain attached to a reused source.
            OptionalHook("BetterSource","Release",nameof(ReleaseEnvironmentBase),true);
            environmentHooked=true;
            Logger.LogInfo("Environment patch E1 installed: native base gains and rain crossfade refresh.");
        }
        private void HookAircraft(string type,int arguments,string callback)
        {
            try
            {
                var t=AccessTools.TypeByName(type);
                var method=t?.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)
                    .SingleOrDefault(m=>m.Name=="PlaySound" && m.GetParameters().Length==arguments && m.GetParameters()[0].ParameterType.Name=="TaggedClip");
                Patch(method,callback,true);
            }
            catch(Exception ex) { Logger.LogWarning("Optional aircraft control unavailable: "+type+": "+ex.Message); }
        }
        private void EnvironmentAttempt(Action action)
        {
            if(environmentFailed) return;
            try { action(); }
            catch(Exception ex)
            {
                environmentFailed=true;
                Logger.LogWarning("Environmental adjustment failed; restoring native gains. Other soundtrack features remain active. "+ex);
                RestoreEnvironmentControls();
            }
        }
        private static void ScalePrecipitation(object __instance)
        {
            var p=instance; if(p==null) return;
            p.EnvironmentAttempt(()=>
            {
                p.rainBlenders.Add(__instance);
                var field=AccessTools.Field(__instance.GetType(),"_globalsVolumeMult");
                if(field==null) throw new MissingFieldException("Precipitation _globalsVolumeMult");
                field.SetValue(__instance,(float)field.GetValue(__instance)*p.EnvironmentGain(p.rainVolume));
            });
        }
        private static void ScaleWind(object __instance,ref float __result)
        { var p=instance; if(p==null) return; p.windBlenders.Add(__instance); __result*=p.EnvironmentGain(p.windVolume); }
        private static void TrackBtrVolume(object __instance)
        { var p=instance; if(p!=null) p.EnvironmentAttempt(()=>p.TrackEnvironmentBase(__instance,"OverallVolume",p.btrVolume)); }
        private static void TrackPlaneAudio(object __instance)
        { var p=instance; if(p!=null) p.EnvironmentAttempt(()=>p.TrackEnvironmentBase(Read(__instance,"SoundSource"),"<BaseVolume>k__BackingField",p.planeVolume)); }
        private static void TrackDropAudio(object __instance)
        { var p=instance; if(p!=null) p.EnvironmentAttempt(()=>p.TrackEnvironmentBase(Read(__instance,"LoopSoundSource"),"<BaseVolume>k__BackingField",p.dropVolume)); }
        private void TrackEnvironmentBase(object target,string fieldName,ConfigEntry<float> setting)
        {
            if(target==null || (target is UnityEngine.Object o && o==null)) return;
            if(!environmentBases.TryGetValue(target,out var saved))
            {
                var field=AccessTools.Field(target.GetType(),fieldName);
                if(field==null || field.FieldType!=typeof(float)) throw new MissingFieldException(target.GetType().FullName,fieldName);
                float native=(float)field.GetValue(target);
                saved=new EnvironmentBaseGain { Field=field, Native=native, Applied=native, Setting=setting };
                environmentBases.Add(target,saved);
                if(debug.Value) Logger.LogInfo("Environment gain attached: "+setting.Definition.Key+" / "+target.GetType().Name);
            }
            ApplyEnvironmentBase(target,saved);
        }
        private void ApplyEnvironmentBase(object target,EnvironmentBaseGain saved)
        {
            float current=(float)saved.Field.GetValue(target);
            if(!Mathf.Approximately(current,saved.Applied)) saved.Native=current;
            saved.Applied=saved.Native*EnvironmentGain(saved.Setting);
            saved.Field.SetValue(target,saved.Applied);
        }
        private static void NativeEnvironmentBaseChanged(object __instance)
        {
            var p=instance; if(p==null) return;
            p.EnvironmentAttempt(()=>
            {
                if(!p.environmentBases.TryGetValue(__instance,out var saved)) return;
                // SetBaseVolume has just written a fresh native value, even if it
                // happens to equal the last scaled value. Do not infer it by equality.
                saved.Native=(float)saved.Field.GetValue(__instance);
                saved.Applied=saved.Native*p.EnvironmentGain(saved.Setting);
                saved.Field.SetValue(__instance,saved.Applied);
            });
        }
        private static void ReleaseEnvironmentBase(object __instance)
        {
            var p=instance; if(p==null || !p.environmentBases.TryGetValue(__instance,out var saved)) return;
            try { saved.Field.SetValue(__instance,saved.Native); }
            catch(Exception ex) { if(p.debug.Value) p.Logger.LogWarning("Environment source release: "+ex.Message); }
            finally { p.environmentBases.Remove(__instance); }
        }
        private void UpdateEnvironmentControls()
        {
            if(!environmentHooked || environmentFailed) return;
            EnvironmentAttempt(()=>
            {
                foreach(var pair in environmentBases.ToArray())
                {
                    if(pair.Key is UnityEngine.Object o && o==null) { environmentBases.Remove(pair.Key); continue; }
                    ApplyEnvironmentBase(pair.Key,pair.Value);
                }
                string snapshot=environmentEnabled.Value+":"+enabledSetting.Value+":"+master.Value+":"+rainVolume.Value+":"+windVolume.Value;
                if(snapshot==environmentSnapshot) return;
                environmentSnapshot=snapshot;
                // Find already-running blenders as well as those captured by hooks.
                DiscoverEnvironmentBlenders(rainBlenders,"Audio.AmbientSubsystem.PrecipitationAmbientBlender");
                DiscoverEnvironmentBlenders(windBlenders,"Audio.AmbientSubsystem.WindAmbientBlender");
                RefreshBlenders(rainBlenders,"StartCrossfader",new object[]{1f,1f});
                RefreshBlenders(windBlenders,"UpdateWindSound",new object[]{true});
            });
        }
        private void DiscoverEnvironmentBlenders(HashSet<object> blenders,string name)
        {
            var type=AccessTools.TypeByName(name);
            if(type!=null && typeof(UnityEngine.Object).IsAssignableFrom(type))
                foreach(var blender in UnityEngine.Object.FindObjectsOfType(type)) blenders.Add(blender);
        }
        private void RefreshBlenders(HashSet<object> blenders,string method,object[] args)
        {
            foreach(var blender in blenders.ToArray())
            {
                if(blender is UnityEngine.Object o && o==null) { blenders.Remove(blender); continue; }
                try
                {
                    var call=AccessTools.Method(blender.GetType(),method) ?? throw new MissingMethodException(method);
                    call.Invoke(blender,args);
                }
                catch(Exception ex) { blenders.Remove(blender); Logger.LogWarning("Environment refresh unavailable: "+method+": "+ex.Message); }
            }
        }
        private void RestoreEnvironmentControls()
        {
            foreach(var pair in environmentBases.ToArray())
            {
                try { if(!(pair.Key is UnityEngine.Object o) || o!=null) pair.Value.Field.SetValue(pair.Key,pair.Value.Native); }
                catch { }
            }
            environmentBases.Clear();
            RefreshBlenders(rainBlenders,"StartCrossfader",new object[]{1f,1f});
            RefreshBlenders(windBlenders,"UpdateWindSound",new object[]{true});
            rainBlenders.Clear(); windBlenders.Clear();
        }
    }
}
