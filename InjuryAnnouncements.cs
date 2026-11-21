using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> injuryEnabled, injuryLight, injuryHeavy, injuryFracture, injuryBlackout, injuryDebug, ownGrenade, ownMelee;
        private ConfigEntry<float> injuryCooldown;
        private object injuryPlayer;
        private HashSet<string> previousInjuries=new HashSet<string>();
        private readonly Dictionary<string,string> pendingInjuries=new Dictionary<string,string>();
        private MethodInfo injuryEffects, injuryDestroyed, injurySay;
        private double nextInjuryPoll, nextAnnouncement;
        private bool injuryFaulted;
        private void ConfigureInjuryAnnouncements()
        {
            injuryEnabled=Config.Bind("Injury Speech","Enabled",false,"Announce new injuries with your selected character voice. Native speech is shared by Fika.");
            injuryLight=Config.Bind("Injury Speech","LightBleed",true,"Uses the native Bleeding phrase.");
            injuryHeavy=Config.Bind("Injury Speech","HeavyBleed",true,"Uses the native Bleeding phrase; native packs do not distinguish bleed severity.");
            injuryFracture=Config.Bind("Injury Speech","Fracture",true,"Uses HandBroken or LegBroken for the affected limb.");
            injuryBlackout=Config.Bind("Injury Speech","BlackedLimb",true,"Uses HurtHeavy; no dedicated native blacked-limb phrase exists.");
            injuryCooldown=FloatSetting("Injury Speech","CooldownSeconds",8,2,60,"Minimum gap between announcements. Resolved injuries are dropped from the queue.");
            injuryDebug=Config.Bind("Injury Speech","DebugLogging",false,"Log injury detection and native speech requests.");
            ownGrenade=Config.Bind("Combat","YourGrenadeThrow",true,"Enter combat when your grenade is released, before explosion.");
            ownMelee=Config.Bind("Combat","YourMelee",true,"Enter combat on your successful melee swing action.");
        }
        private void UpdateInjuryAnnouncements()
        {
            if(injuryFaulted) return;
            if(!enabledSetting.Value || !injuryEnabled.Value || !state.InRaid || localPlayer==null)
            { injuryPlayer=null; previousInjuries.Clear(); pendingInjuries.Clear(); return; }
            if(Now<nextInjuryPoll) return;
            nextInjuryPoll=Now+.25;
            try
            {
                if(!(Read(localPlayer,"IsYourPlayer") is bool owner) || !owner) return;
                var health=Read(localPlayer,"HealthController");
                if(health==null || !(Read(health,"IsAlive") is bool alive) || !alive) return;
                bool first=!ReferenceEquals(injuryPlayer,localPlayer);
                if(first)
                {
                    injuryPlayer=localPlayer; previousInjuries.Clear(); pendingInjuries.Clear(); nextAnnouncement=Now+2;
                    injuryEffects=AccessTools.Method(health.GetType(),"GetAllEffects",Type.EmptyTypes);
                    injuryDestroyed=health.GetType().GetMethods().FirstOrDefault(m=>m.Name=="IsBodyPartDestroyed" && m.GetParameters().Length==1);
                    injurySay=localPlayer.GetType().GetMethods().FirstOrDefault(m=>m.Name=="Say" && m.GetParameters().Length==6 && m.GetParameters()[0].ParameterType.IsEnum);
                    if(injuryEffects==null || injuryDestroyed==null || injurySay==null) throw new MissingMethodException("Native health/speech API was not found.");
                    Logger.LogInfo("Injury speech ready: "+localPlayer.GetType().FullName+"; native selected voice; Fika uses OnPhraseTold replication.");
                }
                var current=new Dictionary<string,string>();
                foreach(var effect in (IEnumerable)injuryEffects.Invoke(health,null))
                {
                    string type=effect.GetType().Name;
                    // Residual/healing effects must not trigger new injury calls.
                    if(Read(effect,"IsResidual") is bool residual && residual) continue;
                    string body=Read(effect,"BodyPart")?.ToString() ?? "Unknown";
                    if(type=="HeavyBleeding" && injuryHeavy.Value) current["0:Heavy:"+body]="Bleeding";
                    else if(type=="LightBleeding" && injuryLight.Value) current["2:Light:"+body]="Bleeding";
                    else if(type=="Fracture" && injuryFracture.Value) current["3:Fracture:"+body]=body.IndexOf("Leg",StringComparison.OrdinalIgnoreCase)>=0 ? "LegBroken" : "HandBroken";
                }
                if(injuryBlackout.Value)
                    foreach(string body in new[]{"LeftArm","RightArm","LeftLeg","RightLeg","Stomach"})
                    {
                        var part=Enum.Parse(injuryDestroyed.GetParameters()[0].ParameterType,body);
                        if(injuryDestroyed.Invoke(health,new[]{part}) is bool destroyed && destroyed) current["1:Black:"+body]="HurtHeavy";
                    }
                if(!first) foreach(var pair in current) if(!previousInjuries.Contains(pair.Key)) pendingInjuries[pair.Key]=pair.Value;
                previousInjuries=new HashSet<string>(current.Keys);
                foreach(string key in pendingInjuries.Keys.ToArray()) if(!current.ContainsKey(key)) pendingInjuries.Remove(key);
                if(Now<nextAnnouncement || pendingInjuries.Count==0 || MasterGain<=0) return;
                // Respect existing speech, preventing a new automatic call from cutting it off.
                var speaker=Read(localPlayer,"Speaker");
                if(Read(speaker,"Speaking") is bool speaking && speaking) return;
                var selected=pendingInjuries.OrderBy(p=>p.Key,StringComparer.Ordinal).First();
                string phrase=selected.Value;
                var pars=injurySay.GetParameters();
                object[] args={Enum.Parse(pars[0].ParameterType,phrase),true,0f,Enum.ToObject(pars[3].ParameterType,0),100,false};
                injurySay.Invoke(localPlayer,args);
                // Collapse simultaneous injuries that share the exact same voice line.
                foreach(var key in pendingInjuries.Where(p=>p.Value==phrase).Select(p=>p.Key).ToArray()) pendingInjuries.Remove(key);
                nextAnnouncement=Now+injuryCooldown.Value;
                if(injuryDebug.Value) Logger.LogInfo("Injury announcement: "+selected.Key+" -> "+phrase+" (native Say; voice-pack availability applies)");
            }
            catch(Exception ex) { injuryFaulted=true; Logger.LogWarning("Injury announcements disabled for this session; music unaffected: "+ex); }
        }
        private void InstallActionTriggers()
        {
            // Patch concrete game controller implementations; Fika may override and call these bases.
            var assembly=AccessTools.TypeByName("EFT.Player")?.Assembly;
            if(assembly==null) return;
            foreach(var t in Types(assembly).Where(t=>t.Name=="BaseGrenadeHandsController" || t.Name=="KnifeController"))
            foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            {
                if(m.ContainsGenericParameters || m.IsAbstract) continue;
                try
                {
                    if(m.Name=="ThrowGrenade") Patch(m,nameof(OwnGrenadeReleased));
                    if((m.Name=="MakeKnifeKick" || m.Name=="MakeAlternativeKick") && m.ReturnType==typeof(bool)) Patch(m,nameof(OwnMeleeAction));
                }
                catch(Exception ex) { Logger.LogWarning("Optional combat action hook: "+ex.Message); }
            }
        }
        private static bool OwnedController(object controller)
        {
            var p=instance;
            if(p==null || !p.enabledSetting.Value || !p.state.InRaid) return false;
            if(ReferenceEquals(Read(p.localPlayer,"HandsController"),controller)) return true;
            return ReferenceEquals(Read(controller,"_player") ?? Read(controller,"Player"),p.localPlayer);
        }
        private static void OwnGrenadeReleased(object __instance)
        { if(OwnedController(__instance) && instance.ownGrenade.Value) Dispatch(p=>p.state.CombatEvent(Now,p.combatQuiet.Value)); }
        private static void OwnMeleeAction(object __instance,bool __result)
        { if(__result && OwnedController(__instance) && instance.ownMelee.Value) Dispatch(p=>p.state.CombatEvent(Now,p.combatQuiet.Value)); }
    }
}
