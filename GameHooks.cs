using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private static object Read(object value, string name)
        {
            if (value == null) return null;
            var type = value.GetType();
            var key = Tuple.Create(type, name);
            if (!readMembers.TryGetValue(key, out var member))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                for (var current = type; current != null; current = current.BaseType)
                {
                    member = (MemberInfo)current.GetField(name, flags) ?? current.GetProperty(name, flags);
                    if (member != null) break;
                }
                readMembers[key] = member; // Missing members are cached too, without per-frame warnings.
            }
            if (member is FieldInfo field) return field.GetValue(value);
            if (member is PropertyInfo property) return property.GetValue(value, null);
            return null;
        }
        private static readonly Dictionary<Tuple<Type, string>, MemberInfo> readMembers = new Dictionary<Tuple<Type, string>, MemberInfo>();

        private static IEnumerable<Type> Types(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch (NotSupportedException) { return Array.Empty<Type>(); }
        }
        private void Patch(MethodInfo method, string callback, bool prefix = false)
        {
            if (method == null || method.IsAbstract) throw new MissingMethodException("Required hook for " + callback + " is absent.");
            var hook = new HarmonyMethod(typeof(Plugin), callback);
            if (prefix) harmony.Patch(method, prefix: hook); else harmony.Patch(method, postfix: hook);
            Logger.LogInfo("Hook: " + method.DeclaringType.FullName + "." + method.Name);
        }
        private void InstallHooks()
        {
            var ui = AccessTools.TypeByName("EFT.UI.GUISounds") ?? throw new TypeLoadException("EFT.UI.GUISounds");
            var worldType = AccessTools.TypeByName("EFT.GameWorld") ?? throw new TypeLoadException("EFT.GameWorld");
            var playerType = AccessTools.TypeByName("EFT.Player") ?? throw new TypeLoadException("EFT.Player");
            var result = AccessTools.TypeByName("EFT.UI.SessionEnd.SessionResultExitStatus") ?? throw new TypeLoadException("Result screen");
            if (AccessTools.Field(ui, "audioSource_3") == null) throw new MissingFieldException("Menu music source changed.");
            var menu = AccessTools.TypeByName("EFT.UI.MenuScreen") ?? throw new TypeLoadException("EFT.UI.MenuScreen");
            var show = menu.GetMethods().Single(m => m.Name == "Show" && m.GetParameters().Any(p => p.Name == "profile"));
            Patch(show, nameof(MenuShown));
            Patch(AccessTools.Method(ui, "PlayMenuBackgroundMusic", Type.EmptyTypes), nameof(MenuSilence), true);
            try
            {
                var sessionEnd = ui.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SingleOrDefault(m => m.Name == "PlaySessionEnd" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "ExitStatus");
                Patch(sessionEnd, nameof(SessionEndAudio), true);
            }
            catch (Exception ex) { Logger.LogWarning("Native outcome jingle suppression unavailable: " + ex.Message); }
            Patch(AccessTools.Method(ui, "PlayMenuBackgroundMusic", Type.EmptyTypes), nameof(MenuPlay));
            Patch(AccessTools.PropertySetter(ui, "BackgroundMusicActive"), nameof(MenuActive));
            try
            {
                var fade = ui.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SingleOrDefault(m => m.Name == "FadeBackgroundMusicAsync" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(bool));
                Patch(fade, nameof(MenuFadeRequested), true);
            }
            catch (Exception ex) { Logger.LogWarning("Early menu fade hook unavailable: " + ex.Message); }
            try
            {
                var stop = ui.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SingleOrDefault(m => m.Name == "StopMenuBackgroundMusicWithDelay" && m.GetParameters().Length == 2);
                Patch(stop, nameof(MenuStopScheduled), true);
            }
            catch (Exception ex) { Logger.LogWarning("Scheduled menu-stop hook unavailable: " + ex.Message); }
            Patch(AccessTools.PropertySetter(ui, "HideoutSoundActive"), nameof(CaptureGui));
            Patch(AccessTools.Method(worldType, "OnGameStarted", Type.EmptyTypes), nameof(WorldStart));
            Patch(AccessTools.Method(worldType, "Dispose", Type.EmptyTypes), nameof(WorldDispose), true);
            Patch(AccessTools.Method(playerType, "OnMakingShot"), nameof(Shot));
            Patch(AccessTools.Method(playerType, "OnHealthApplyDamage"), nameof(Damage));
            Patch(AccessTools.Method(playerType, "OnDead"), nameof(Death), true);
            var resultMethod = result.GetMethods().Single(m => m.Name == "Show" && m.GetParameters().Any(p => p.Name == "exitStatus"));
            Patch(resultMethod, nameof(ResultScreen));
            // Include overrides in Fika when already loaded. Results-screen hook is a fallback.
            var gameType = AccessTools.TypeByName("EFT.AbstractGame") ?? throw new TypeLoadException("EFT.AbstractGame");
            var methods = AppDomain.CurrentDomain.GetAssemblies().SelectMany(Types)
                .Where(t => !t.ContainsGenericParameters && gameType.IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == "Stop" && !m.IsAbstract && !m.ContainsGenericParameters &&
                    m.GetParameters().Any(p => p.Name == "exitStatus" && p.ParameterType.Name == "ExitStatus") &&
                    m.GetParameters().Any(p => p.Name == "profileId" && p.ParameterType == typeof(string)))
                .Distinct().ToArray();
            foreach (var method in methods) Patch(method, nameof(GameStop));
            try
            {
                var grenade = AccessTools.TypeByName("EFT.Grenade");
                var explosion = grenade?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .SingleOrDefault(m => m.Name == "Explosion" && m.GetParameters().Any(p => p.Name == "grenadePosition" && p.ParameterType == typeof(UnityEngine.Vector3)));
                if (explosion != null) Patch(explosion, nameof(GrenadeExplosion), true);
                else Logger.LogWarning("Grenade proximity unavailable: expected Explosion signature not found.");
            }
            catch (Exception ex) { Logger.LogWarning("Grenade hook unavailable: " + ex.Message); }
            Logger.LogInfo("Custom Soundtrack hooks installed; result hooks=" + methods.Length + ". Waiting for menu audio initialization.");
        }
        private static bool SessionEndAudio(object __0)
        {
            var p = instance;
            if (p == null || p.faulted || !p.enabledSetting.Value || !p.ready) return true;
            string status = __0?.ToString();
            // This method only selects/plays the native outcome jingle; it does not build the results UI.
            Dispatch(plugin => plugin.FinishRaid(status));
            if (!p.suppressOutcomeJingles.Value || !p.ShouldSuppressResultJingle(status)) return true;
            p.Logger.LogInfo("Suppressed native outcome jingle: " + status);
            return false;
        }
        private static void MenuFadeRequested(object __instance, bool __0)
        { if (!__0) BeginEarlyMenuExit(__instance); }
        private static void MenuStopScheduled(object __instance) => BeginEarlyMenuExit(__instance);
        private static void BeginEarlyMenuExit(object soundController)
        {
            var p = instance;
            if (p == null || p.faulted || !p.enabledSetting.Value || !p.ready || p.state.InRaid ||
                p.state.Outcome != MusicKind.None || p.active != MusicKind.MainMenu) return;
            try
            {
                p.gui = soundController;
                p.menuAllowed = false;
                // Begin during the game's native fade/delayed-stop request, before initialization/unloading.
                p.SwitchTo(MusicKind.None);
                Dispatch(plugin => plugin.menuAllowed = false);
                p.Logger.LogInfo("Early menu-exit fade started; duration=" + p.fades["MenuToHideoutOrLoading"].Value + "s");
            }
            catch (Exception ex) { p.Logger.LogWarning("Early menu fade failed: " + ex.Message); }
        }
        private static void MenuSilence(object __instance)
        {
            var p = instance;
            if (p == null || p.faulted || !p.enabledSetting.Value || !p.suppressDefault.Value) return;
            // Before native Play(), so default music cannot escape for the first audio buffer.
            try { p.gui = __instance; p.MuteNative(p.NativeSource()); }
            catch (Exception ex) { p.Logger.LogWarning("Native menu mute failed: " + ex.Message); }
        }
        private static void GrenadeExplosion(MethodBase __originalMethod, object[] __args)
        {
            int index = Array.FindIndex(__originalMethod.GetParameters(), a => a.Name == "grenadePosition");
            if (index >= 0 && __args[index] is UnityEngine.Vector3 point) Dispatch(p => p.GrenadeSignal(point));
        }
        private static void MenuShown() => Dispatch(p =>
        {
            if (p.menuShownAt >= 0) return;
            p.menuShownAt = Now;
            p.Logger.LogInfo("Main-menu Show observed; custom audio waits " + p.startupDelay.Value + " seconds.");
        });
        private static void MenuPlay(object __instance) => Dispatch(p => { p.gui = __instance; p.menuAllowed = true; });
        private static void MenuActive(object __instance, bool __0) => Dispatch(p => { p.gui = __instance; p.menuAllowed = __0; });
        private static void CaptureGui(object __instance) => Dispatch(p => p.gui = __instance);
        private static void WorldStart(object __instance) => Dispatch(p => p.BeginWorld(__instance));
        private static void WorldDispose(object __instance) => Dispatch(p =>
        {
            if (!ReferenceEquals(p.world, __instance)) return;
            p.ClearBossMusic(); p.state.LeaveWorld(); p.world = null;
            p.RequestRecovery("raid world disposal");
            p.Logger.LogInfo("Raid world disposed; waiting for menu/result state.");
        });
        private static void Shot(object __instance) => Dispatch(p => p.CombatSignal(__instance, false));
        private static void Damage(object __instance, object[] __args) => Dispatch(p =>
        {
            // Ignore fall damage, bleeding, dehydration and other non-combat health ticks.
            var info = __args.Length > 2 ? __args[2] : null;
            string type = Read(info, "DamageType")?.ToString();
            if (type == "Bullet" || type == "Melee" || type == "Explosion" || type == "GrenadeFragment") p.CombatSignal(__instance, true);
        });
        private static void Death(object __instance) => Dispatch(p =>
        { p.RememberBossDeath(__instance); if (ReferenceEquals(p.localPlayer, __instance) && p.state.InRaid) p.FinishRaid("Killed"); });
        private static string Argument(MethodBase method, object[] args, string name)
        {
            int index = Array.FindIndex(method.GetParameters(), parameter => parameter.Name == name);
            return index >= 0 && index < args.Length ? args[index]?.ToString() : null;
        }
        private static void GameStop(MethodBase __originalMethod, object[] __args)
        {
            string id = Argument(__originalMethod, __args, "profileId");
            string status = Argument(__originalMethod, __args, "exitStatus");
            Dispatch(p =>
            {
                if (p.localPlayer == null || id != Read(p.localPlayer, "ProfileId")?.ToString()) return;
                p.FinishRaid(status);
            });
        }
        private static void ResultScreen(MethodBase __originalMethod, object[] __args)
        {
            string status = Argument(__originalMethod, __args, "exitStatus");
            Dispatch(p => { p.FinishRaid(status); p.menuAllowed = true; });
        }
    }
}
