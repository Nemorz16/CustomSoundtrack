using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;
namespace CustomSoundtrack
{
    // Configuration Manager reads these public fields by name; no plugin dependency is required.
    public sealed class SoundtrackMenuAttributes
    {
        public string Category, DispName, Description;
        public int? Order;
        public bool? Browsable, ReadOnly, IsAdvanced, ShowRangeAsPercent;
    }
    public sealed partial class Plugin
    {
        private readonly Dictionary<ConfigEntryBase,SoundtrackMenuAttributes> menuAttributes=new Dictionary<ConfigEntryBase,SoundtrackMenuAttributes>();
        private string menuVisibilitySnapshot;
        private bool menuLayoutFailed;
        private static string FriendlyName(string name) => Regex.Replace(Regex.Replace(name,"([a-z0-9])([A-Z])","$1 $2"),"([A-Z])([A-Z][a-z])","$1 $2");
        private static string MusicMenu(MusicKind kind)
        {
            switch(kind)
            {
                case MusicKind.Combat:return "04 Combat Music";
                case MusicKind.Death:return "05 Death Music";
                case MusicKind.Extraction:return "06 Extraction Music";
                case MusicKind.MainMenu:return "08 Main Menu Music";
                case MusicKind.Personal:return "09 Personal Player";
                case MusicKind.RaidAmbient:return "10 Raid Ambient";
                default:return "01 General";
            }
        }
        private void ConfigureMenuLayout()
        {
            if(menuLayoutFailed) return;
            try
            {
                foreach(var pair in Config.ToArray())
                {
                    var entry=pair.Value;
                    if(menuAttributes.ContainsKey(entry)) continue;
                    string section=pair.Key.Section,key=pair.Key.Key;
                    string category="01 General", name=FriendlyName(key);
                    bool diagnostic=section.Contains("Debug") || key=="DebugLogging" || key=="LogEachTrack" || section=="UI Sounds - UI" || section=="UI Sounds - Chat";
                    if(diagnostic) category=section.StartsWith("Hideout") || key=="LogEachTrack" ? "00 Debug - Hideout Radio" : section.StartsWith("Boss") ? "00 Debug - Boss" : section.StartsWith("UI") ? "00 Debug - UI" : "00 Debug - General";
                    else if(section=="Hotkeys") category="02 Hotkeys";
                    else if(section.StartsWith("Boss")) category="03 Boss Music";
                    else if(section=="Combat") category=MusicMenu(MusicKind.Combat);
                    else if(section.StartsWith("Hideout")) category="07 Hideout Radio";
                    else if(section=="Player UI" || section=="Personal Player") category=MusicMenu(MusicKind.Personal);
                    else if(section.StartsWith("UI Sounds")) category="11 UI Replacement";
                    else if(section=="Environment") category="12 Environment";
                    else if(section=="Injury Speech") category="13 Injury Speech";
                    else if(section=="Music Enabled" && Enum.TryParse(key,out MusicKind k)) { category=MusicMenu(k); name="Enabled"; }
                    else if(section=="Volumes" && Enum.TryParse(key,out MusicKind v)) { category=MusicMenu(v); name="Volume"; }
                    else if(section=="Outcomes") category=key.StartsWith("Death") ? MusicMenu(MusicKind.Death) : key.StartsWith("Extraction") ? MusicMenu(MusicKind.Extraction) : "01 General";
                    else if(section=="Fades")
                    {
                        category=key.StartsWith("Death") || key=="RaidToDeath" ? MusicMenu(MusicKind.Death) : key.StartsWith("Extraction") || key=="RaidToExtraction" ? MusicMenu(MusicKind.Extraction) : key.StartsWith("Combat") ? MusicMenu(MusicKind.Combat) : key.StartsWith("Personal") || key=="AmbientToPersonal" ? MusicMenu(MusicKind.Personal) : key.StartsWith("RaidAmbient") ? MusicMenu(MusicKind.RaidAmbient) : key.StartsWith("Menu") || key.StartsWith("MainMenu") ? MusicMenu(MusicKind.MainMenu) : "01 General";
                        name=name.Replace("Track Change","Next Track Fade");
                    }
                    switch(key)
                    {
                        case "Master":name="Master Volume";break;
                        case "AdvancedDebug":name="Advanced Debug";break;
                        case "UseMasterFade":name="Override Fade Times";break;
                        case "MenuDelaySeconds":name="Startup Delay (seconds)";break;
                        case "WholeHideoutPlayback":name="Whole Hideout Playback";break;
			case "DirectionalGain":name="Directional Volume";break;
			case "DirectionalReach":name="Directional Reach";break;
                        case "RandomStaticChance":name="Static Chance";break;
                        case "RandomSilenceChance":name="Silence Chance";break;
                        case "ContinueMusicChance":name="Continue Music Chance";break;
                        case "HearingRadiusMetres":name="Hearing Distance (metres)";break;
                        case "FullVolumeDistanceMetres":name="Full Volume Distance";break;
                        case "QuietSeconds":name="Combat End Delay (seconds)";break;
                        case "PlayAmbient":name="Play Other Music";break;
                        case "RunThroughCountsAsExtraction":name="Legacy Run Through";break;
                        case "DeathSeconds":case "ExtractionSeconds":name="Playback Limit (0 = full track)";break;
                    }
                    if(key=="DeathTrackChange" || key=="ExtractionTrackChange") name="Next Track Fade";
                    if(section=="Player UI" && key=="Enabled") name="Show Player Overlay";
                    if(section=="Advanced Debug" && key=="Enabled") name="Playback Debug Logging";
                    if(section=="Hideout Advanced Debug" && key=="Enabled") name="Record Radio Diagnostics";
                    if(entry.Description.Description.Contains("***") && !name.Contains("***")) name+=" ***";
                    var tag=new SoundtrackMenuAttributes { Category=category, DispName=name, Description=entry.Description.Description, Order=key=="AdvancedDebug" ? 1100 : key=="Enabled" || section=="Music Enabled" ? 1000 : key=="Volume" || section=="Volumes" ? 900 : 0, Browsable=true, IsAdvanced=false, ReadOnly=key=="ContinueMusicChance", ShowRangeAsPercent=entry.SettingType==typeof(float) && entry.Description.AcceptableValues is AcceptableValueRange<float> range && range.MinValue==0 && range.MaxValue==1 };
                    var description=new ConfigDescription(entry.Description.Description,entry.Description.AcceptableValues,entry.Description.Tags.Concat(new object[]{tag}).ToArray());
                    var setter=AccessTools.PropertySetter(typeof(ConfigEntryBase),"Description");
                    if(setter!=null) setter.Invoke(entry,new object[]{description});
                    else
                    {
                        var field=AccessTools.Field(typeof(ConfigEntryBase),"<Description>k__BackingField");
                        if(field==null) throw new MissingFieldException("BepInEx config description");
                        field.SetValue(entry,description);
                    }
                    menuAttributes.Add(entry,tag);
                }
                menuVisibilitySnapshot=null;
                UpdateMenuVisibility();
            }
            catch(Exception ex) { menuLayoutFailed=true; Logger.LogWarning("Menu presentation fallback: original settings remain available. "+ex.Message); }
        }
        private bool ToggleValue(string section,string key,bool fallback=true)
        {
            return Config.TryGetEntry<bool>(section,key,out var entry) ? entry.Value : fallback;
        }
        private void UpdateMenuVisibility()
        {
            if(advancedVisible==null || menuLayoutFailed) return;
            string snapshot=string.Join(";",Config.Where(p=>p.Value.SettingType==typeof(bool)).Select(p=>p.Value.BoxedValue.ToString()))+";"+bossDeathMode.Value;
            if(snapshot==menuVisibilitySnapshot) return;
            menuVisibilitySnapshot=snapshot;
            foreach(var pair in menuAttributes)
            {
                var entry=pair.Key; var tag=pair.Value; string section=entry.Definition.Section,key=entry.Definition.Key;
                bool visible=true;
                if(tag.Category.StartsWith("00")) visible=advancedVisible.Value;
                else if(tag.Category=="03 Boss Music" && key!="Enabled")
                {
                    visible=bossMusicEnabled.Value;
                    if(key=="RandomStaticChance" || key=="RandomSilenceChance" || key=="ContinueMusicChance") visible &= bossDeathMode.Value==BossDeathMode.Random;
                    if(key.Contains("Cutoff") || key=="MuffleSmoothingSeconds") visible &= bossMuffle.Value;
                }
                else if(tag.Category=="07 Hideout Radio" && !(section=="Hideout General" && key=="Enabled"))
                {
                    visible=ToggleValue("Hideout General","Enabled");
                    if(key.StartsWith("Directional")) visible &= !ToggleValue("Hideout Audio","WholeHideoutPlayback");
                }
                else if(tag.Category=="11 UI Replacement" && key!="Enabled") visible=customUiEnabled?.Value ?? true;
                else if(section=="Environment" && key!="Enabled") visible=environmentEnabled.Value;
                else if(section=="Injury Speech" && key!="Enabled") visible=injuryEnabled.Value;
                else if(key=="MasterFadeSeconds") visible=masterFadeEnabled.Value;
                else if(key=="RunThroughCountsAsExtraction") visible=false;
                else foreach(var music in categoryEnabled)
                    if(tag.Category==MusicMenu(music.Key) && section!="Music Enabled") visible=music.Value.Value;
                tag.Browsable=visible;
            }
            // Config Manager caches its list. Refresh only on a real visibility change.
            var manager=BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Select(p=>p.Instance).FirstOrDefault(p=>p!=null && p.GetType().Name=="ConfigurationManager");
            try { if(manager!=null) AccessTools.Method(manager.GetType(),"BuildSettingList")?.Invoke(manager,null); }
            catch { /* Reopening the settings window also rebuilds its list. */ }
        }
    }
}
