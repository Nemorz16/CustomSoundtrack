using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private readonly Dictionary<MusicKind, ConfigEntry<bool>> categoryEnabled = new Dictionary<MusicKind, ConfigEntry<bool>>();
        private readonly HashSet<MusicKind> pausedCategories = new HashSet<MusicKind>();
        private ConfigEntry<CombatOverrideMode> combatOverride;
        private ConfigEntry<KeyboardShortcut> personalKey, ambientKey, dismissOutcomeKey;
        private bool showCurrentPlaylist;
        private void ConfigurePlaybackOptions()
        {
            foreach(MusicKind kind in Enum.GetValues(typeof(MusicKind)))
                if(kind!=MusicKind.None) categoryEnabled[kind]=Config.Bind("Music Enabled",kind.ToString(),true,"Enable " + kind + " music.");
            combatOverride=Config.Bind("Combat","Override",CombatOverrideMode.Both,"Combat interrupts: None, AmbientOnly, PersonalOnly or Both.");
            personalKey=Config.Bind("Hotkeys","PlayPersonal",new KeyboardShortcut(KeyCode.Home),"Start/resume Personal music over raid or menu playlists.");
            ambientKey=Config.Bind("Hotkeys","PlayAmbient",new KeyboardShortcut(KeyCode.Insert),"Stop Personal and return to the relevant playlist.");
            dismissOutcomeKey=Config.Bind("Hotkeys","ReturnToMenu",new KeyboardShortcut(KeyCode.End),"Dismiss outcome music after returning to menus.");
        }
        private void UpdatePlaybackOptions()
        {
            foreach(var pair in categoryEnabled) state.SetEnabled(pair.Key,pair.Value.Value);
            state.CombatOverride=combatOverride.Value;
            state.CombatReturn=combatReturn.Value;
            if(state.Outcome!=MusicKind.None && !state.Enabled(state.Outcome)) state.FinishOutcome();
        }
        private bool IsPaused(MusicKind kind) => pausedCategories.Contains(kind) || (kind==MusicKind.MainMenu && menuPaused);
        private MusicKind PlayerControlKind => showCurrentPlaylist && state.Select(Now,menuAllowed,false)!=MusicKind.None ? state.Select(Now,menuAllowed,false) : MusicKind.Personal;
        private void ResumeCategory(MusicKind kind)
        {
            pausedCategories.Remove(kind); if(kind==MusicKind.MainMenu) menuPaused=false;
            var c=channels[kind];
            if(c.ResetWhenSilent) { c.Source.Stop(); c.SavedPosition=0; c.Started=false; }
            c.ResetWhenSilent=false; c.EndingTrack=false;
            if(active==kind) EnsureActive();
        }
        private void Transport(MusicKind kind,int command)
        {
            if(kind==MusicKind.None || !channels.ContainsKey(kind) || !state.Enabled(kind)) return;
            var c=channels[kind];
            if(command==2 || (command==0 && !IsPaused(kind) && (c.Source.isPlaying || c.Loading || c.PendingIndex>=0)))
            {
                // Freeze immediately: a paused outcome must not finish and fall back to the menu.
                if(Valid(c.Source.clip)) c.SavedPosition=c.Source.time;
                c.Source.Pause(); c.Gain.Set(0,0,Now); pausedCategories.Add(kind);
                if(command==2) { if(kind==MusicKind.Personal) { state.StopPersonal(); if(!state.InRaid && state.MenuSelection==MusicKind.Personal) state.MenuSelection=MusicKind.MainMenu; } c.Source.Stop(); c.SavedPosition=0; c.Started=false; c.PendingIndex=-1; c.ResetWhenSilent=false; c.EndingTrack=false; if(IsOutcome(kind)) outcome.Reset(); }
                return;
            }
            ResumeCategory(kind);
            if(command==1 || command==-1)
            {
                if(!c.Loading) QueueTrack(kind,command,false);
            }
        }
        private void StartPersonal()
        {
            if(!state.StartPersonal(Now,menuAllowed && !(Read(gui,"HideoutSoundActive") is bool h && h))) return;
            ResumeCategory(MusicKind.Personal);
        }
        private void HandleUnifiedHotkeys(MusicKind desired)
        {
            if(ambientKey.Value.IsDown()) { state.UseAmbient(); if(!state.InRaid) state.MenuSelection=MusicKind.MainMenu; var target=state.Select(Now,menuAllowed,false); if(target!=MusicKind.None) ResumeCategory(target); return; }
            if(personalKey.Value.IsDown()) { StartPersonal(); return; }
            if(stopKey.Value.IsDown()) Transport(desired,2);
            else if(playKey.Value.IsDown()) Transport(desired,0);
            else if(nextKey.Value.IsDown()) Transport(desired,1);
            else if(previousKey.Value.IsDown()) Transport(desired,-1);
        }
        private void SeekPersonal(float seconds)
        {
            var c=channels[MusicKind.Personal];
            if(c.Loading || c.PendingIndex>=0 || !Valid(c.Source.clip)) return;
            float position=(float)PlayerUiRules.SeekPosition(seconds,c.Source.clip.length);
            c.SavedPosition=position; c.Source.time=position; c.EndingTrack=false; c.ResetWhenSilent=false;
            if(active==MusicKind.Personal && !IsPaused(MusicKind.Personal)) c.Gain.Set(1,.1,Now);
        }
    }
}
