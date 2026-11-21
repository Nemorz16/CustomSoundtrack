using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> customUiEnabled, customUiDebug;
        private readonly Dictionary<string, ConfigEntry<bool>> uiSoundToggles = new Dictionary<string, ConfigEntry<bool>>();
        private readonly Dictionary<string, List<Asset>> uiSoundAssets = new Dictionary<string, List<Asset>>();
        private readonly Queue<string> uiSoundReloads = new Queue<string>();
        private readonly Dictionary<string, double> uiSoundRetry = new Dictionary<string, double>();
        private bool uiSoundsLoading, uiSoundsStarted, uiSoundsHooked;
        private string UiSoundRoot => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Sounds");
        private void InstallNotificationSounds()
        {
            customUiEnabled = Config.Bind("UI Sounds", "Enabled", true, "Enable custom UI sounds; empty folders keep vanilla.");
            customUiDebug = Config.Bind("UI Sounds", "DebugLogging", false, "Log which custom UI sound plays.");
            try { ConfigureUiPacks(); }
            catch(Exception ex) { Logger.LogWarning("UI packs unavailable; vanilla retained: " + ex.Message); return; }
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.hj.customuisounds"))
            {
                Logger.LogWarning("CustomUISounds is also loaded. Integrated UI replacements skipped to avoid competing patches. Remove the separate DLL and restart to use this feature.");
                return;
            }
            try
            {
                var wrapper = AccessTools.TypeByName("UISoundsWrapper") ?? throw new TypeLoadException("UISoundsWrapper");
                InstallSoundClipHook(wrapper, "GetUIClip", "UI", nameof(ReplaceUiClip));
                InstallSoundClipHook(wrapper, "GetSocialNetworkClip", "Chat", nameof(ReplaceChatClip));
                uiSoundToggles["Notifications/GeneralNotification"] = Config.Bind("UI Sounds - Notifications", "GeneralNotification", true, "Replace the shared notification cue. This may be used by several types of notification, not only messages.");

                Patch(AccessTools.Method(AccessTools.TypeByName("EFT.UI.GUISounds"), "PlayNotificationSound", Type.EmptyTypes), nameof(ReplaceGeneralNotification), true);
                Patch(AccessTools.Method(AccessTools.TypeByName("EFT.UI.GUISounds"), "PlaySound",new[]{typeof(AudioClip),typeof(bool),typeof(bool),typeof(float)}),nameof(ScaleCustomUiSound),true);
                OptionalHook("EFT.UI.GUISounds","PlayChatSound",nameof(PlayCustomChat),true);
                uiSoundsHooked = true;
                Logger.LogInfo("Custom UI sound hooks installed. Empty folders retain native sounds.");
            }
            catch (Exception ex) { Logger.LogWarning("Some UI sound hooks unavailable; soundtrack remains active. " + ex.Message); uiSoundsHooked = uiSoundToggles.Count > 0; }
        }
        private void InstallSoundClipHook(Type wrapper, string methodName, string group, string callback)
        {
            var method = wrapper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .SingleOrDefault(m => m.Name == methodName && m.ReturnType == typeof(AudioClip) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
            if (method == null) throw new MissingMethodException(wrapper.FullName,methodName);
            foreach (string name in Enum.GetNames(method.GetParameters()[0].ParameterType))
            {
                string key = group + "/" + name;
                uiSoundToggles[key] = Config.Bind("UI Sounds - " + group, name, true, "Replace " + name + " when included in the selected pack.");

            }
            Patch(method,callback);
        }
        private static bool ReplaceGeneralNotification(object __instance)
        {
            var p = instance;
            if (p == null) return true;
            AudioClip replacement = null;
            p.ReplaceNotification("Notifications/GeneralNotification", ref replacement);
            if (replacement == null) return true;
            try
            {
                var source = Read(__instance,"audioSource_0") as AudioSource;
                if (source == null) return true;
                source.outputAudioMixerGroup = Read(__instance,"audioMixerGroup_0") as UnityEngine.Audio.AudioMixerGroup;
                source.PlayOneShot(replacement,MasterGain*p.uiVolume.Value);
                return false;
            }
            catch (Exception ex) { p.Logger.LogWarning("Notification retained vanilla: " + ex.Message); return true; }
        }
        private static bool ScaleCustomUiSound(object __instance,AudioClip __0,bool __1,bool __2,float __3)
        {
            var p=instance; if(p==null || __0==null) return true;
            if(!p.uiSoundAssets.Values.Any(list=>list.Any(a=>a.Clip==__0)) && !p.retiredUiAssets.Any(pair=>pair.Key.Clip==__0)) return true;
            try
            {
                // The native non-common path ignores its volume argument, so route only
                // our replacements through the same source with an explicit one-shot gain.
                var source=Read(__instance,__1 ? "audioSource_4" : __2 ? "audioSource_1" : "audioSource_0") as AudioSource;
                if(source==null) return true;
                var mixer=Read(__instance,"audioMixerGroup_0") as UnityEngine.Audio.AudioMixerGroup;
                float gain=__3;
                if(__2)
                {
                    mixer=AccessTools.Method(__instance.GetType(),"GetCommonSoundsMixerGroup")?.Invoke(__instance,null) as UnityEngine.Audio.AudioMixerGroup ?? mixer;
                    var nativeGain=AccessTools.Method(__instance.GetType(),"GetCommonSoundsVolume")?.Invoke(__instance,null);
                    if(nativeGain is float level) gain=level;
                }
                if(__1) source.Stop();
                source.outputAudioMixerGroup=mixer;
                source.PlayOneShot(__0,gain*MasterGain*p.uiVolume.Value);
                return false;
            }
            catch(Exception ex) { if(p.customUiDebug.Value) p.Logger.LogWarning("UI gain fallback: "+ex.Message); return true; }
        }
        private static bool PlayCustomChat(object __instance,object __0)
        {
            var p=instance; if(p==null) return true;
            AudioClip clip=null; p.ReplaceNotification("Chat/"+__0,ref clip);
            if(clip==null) return true;
            var source=Read(__instance,"audioSource_2") as AudioSource;
            if(source==null) return true;
            source.PlayOneShot(clip,MasterGain*p.uiVolume.Value); return false;
        }
        private static void ReplaceUiClip(object __0, ref AudioClip __result) { instance?.ReplaceNotification("UI/" + __0, ref __result); }
        private static void ReplaceChatClip(object __0, ref AudioClip __result) { instance?.ReplaceNotification("Chat/" + __0, ref __result); }
        private void ReplaceNotification(string key, ref AudioClip result)
        {
            if (uiPack == null || uiPack.Value != activeUiPack || uiPack.Value == UiPackRules.Vanilla || !enabledSetting.Value || customUiEnabled == null || !customUiEnabled.Value || !uiSoundToggles.TryGetValue(key,out var toggle) || !toggle.Value) return;
            try
            {
                if (!uiSoundAssets.TryGetValue(key,out var assets)) return;
                var valid = assets.Where(a => Valid(a.Clip)).ToArray();
                if (valid.Length > 0)
                {
                    var selected=valid[random.Next(valid.Length)]; result=selected.Clip;
                    if (customUiDebug.Value) { Logger.LogInfo("UI sound replacement: " + key+" -> "+selected.File); ShowToast(key+"\n"+selected.File,5); }
                }
                // Restore lost audio asynchronously. The original result remains when none is valid.
                if (assets.Any(a => !Valid(a.Clip)) && (!uiSoundRetry.TryGetValue(key,out double retry) || Now >= retry))
                { uiSoundRetry[key]=Now+30; uiSoundReloads.Enqueue(key); }
            }
            catch (Exception ex) { Logger.LogWarning("UI sound replacement retained vanilla: " + key + "; " + ex.Message); }
        }
        private void UpdateNotificationSounds()
        {
            UpdateUiPackSelection();
            if (faulted || !uiSoundsHooked || !ready || !enabledSetting.Value || !customUiEnabled.Value || uiSoundsLoading) return;
            if (!uiSoundsStarted)
            {
                uiSoundsStarted=true;
                foreach (string key in uiSoundToggles.Keys) uiSoundReloads.Enqueue(key);
            }
            if (uiSoundReloads.Count > 0) { uiSoundsLoading=true; StartCoroutine(LoadNotificationSounds()); }
        }
        private IEnumerator LoadNotificationSounds()
        {
            int generation=uiPackGeneration;
            try
            {
                while (uiSoundReloads.Count > 0)
                {
                    if(generation!=uiPackGeneration) yield break;
                    string key=uiSoundReloads.Dequeue();
                    if (!uiSoundAssets.TryGetValue(key,out var assets))
                    {
                        string[] files=null;
                        try { files=UiPackFiles(key); }
                        catch(Exception ex) { Logger.LogWarning("UI sound scan: " + key + "; " + ex.Message); }
                        assets = files == null ? new List<Asset>() : files.Select(f=>new Asset { File=f }).ToList();
                        uiSoundAssets[key]=assets;
                    }
                    foreach(var asset in assets)
                        if (!Valid(asset.Clip)) { yield return Decode(asset); if(generation!=uiPackGeneration) yield break; yield return null; }
                    uiSoundRetry[key]=Now+30;
                    yield return null;
                }
            }
            finally { uiSoundsLoading=false; }
        }
        private void DestroyNotificationSounds()
        {
            foreach(var assets in uiSoundAssets.Values) foreach(var asset in assets) asset.Dispose();
            foreach(var retired in retiredUiAssets) retired.Key.Dispose(); retiredUiAssets.Clear();
            uiSoundAssets.Clear(); uiSoundReloads.Clear();
        }
    }
}
