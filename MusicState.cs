using System;
namespace CustomSoundtrack
{
    public enum MusicKind { None, MainMenu, RaidAmbient, Personal, Combat, Extraction, Death }
    public enum CombatReturnMode { Previous, Ambient, PersonalSaved, PersonalRandom, Silence }
    public enum CombatOverrideMode { None, AmbientOnly, PersonalOnly, Both }
    public static class OutcomePolicy
    {
        public static bool Finished(bool started, bool playing, double elapsed, double limit)
            => !started || !playing || (limit > 0 && elapsed >= limit);
    }
    public sealed class MusicState
    {
        public bool InRaid { get; private set; }
        public bool PersonalPlaying { get; private set; }
        public bool ResultHandled { get; private set; }
        public MusicKind Outcome { get; private set; }
        public CombatOverrideMode CombatOverride = CombatOverrideMode.Both;
        private readonly bool[] enabled = { false, true, true, true, true, true, true };
        public CombatReturnMode CombatReturn=CombatReturnMode.Ambient;
        public MusicKind MenuSelection=MusicKind.MainMenu;
        public bool RandomPersonalRequested;
        private bool combatPending, raidSilent;
        private double combatUntil;
        private MusicKind combatVictim;
        public bool Enabled(MusicKind kind) => enabled[(int)kind];
        public void SetEnabled(MusicKind kind, bool value) { enabled[(int)kind] = value; if (kind == MusicKind.Personal && !value) PersonalPlaying=false; }
        public bool Overrides(MusicKind kind) => Enabled(MusicKind.Combat) && (CombatOverride == CombatOverrideMode.Both || (kind == MusicKind.Personal ? CombatOverride == CombatOverrideMode.PersonalOnly : CombatOverride == CombatOverrideMode.AmbientOnly));
        public void BeginRaid() { InRaid=true; PersonalPlaying=false; ResultHandled=false; Outcome=MusicKind.None; combatUntil=0; combatVictim=MusicKind.None; combatPending=false; raidSilent=false; RandomPersonalRequested=false; }
        public void LeaveWorld() { InRaid=false; PersonalPlaying=false; combatUntil=0; combatVictim=MusicKind.None; combatPending=false; raidSilent=false; RandomPersonalRequested=false; }
        public bool InCombat(double now) => InRaid && now < combatUntil && Overrides(combatVictim);
        public void CombatEvent(double now, double quietSeconds)
        {
            if (!InRaid) return;
            AdvanceCombat(now);
            var baseKind = PersonalPlaying ? MusicKind.Personal : MusicKind.RaidAmbient;
            // Preserve the interrupted category while extending the same combat encounter.
            if (InCombat(now)) { combatUntil=now+Math.Max(1,quietSeconds); return; }
            if (!Overrides(baseKind)) return;
            combatVictim=baseKind; combatUntil=now+Math.Max(1,quietSeconds); combatPending=true; raidSilent=false;
            PersonalPlaying=false;
        }
        public void AdvanceCombat(double now)
        {
            if(!combatPending || InCombat(now)) return;
            combatPending=false;
            if(!InRaid) return;
            raidSilent=CombatReturn==CombatReturnMode.Silence;
            PersonalPlaying=Enabled(MusicKind.Personal) && (CombatReturn==CombatReturnMode.PersonalSaved || CombatReturn==CombatReturnMode.PersonalRandom || (CombatReturn==CombatReturnMode.Previous && combatVictim==MusicKind.Personal));
            RandomPersonalRequested=PersonalPlaying && CombatReturn==CombatReturnMode.PersonalRandom;
        }
        public bool StartPersonal(double now, bool menuAvailable)
        {
            if(!Enabled(MusicKind.Personal) || (!InRaid && !menuAvailable) || (InCombat(now) && Overrides(MusicKind.Personal))) return false;
            PersonalPlaying=true; raidSilent=false; combatPending=false; combatUntil=0; return true;
        }
        public bool TogglePersonal(double now)
        {
            if (!InRaid || !Enabled(MusicKind.Personal) || (InCombat(now) && Overrides(MusicKind.Personal))) return false;
            PersonalPlaying=!PersonalPlaying;
            if (PersonalPlaying) { combatUntil=0; combatPending=false; raidSilent=false; RandomPersonalRequested=false; } // Ambient-only combat may be replaced explicitly by Personal.
            return true;
        }
        public void UseAmbient() { PersonalPlaying=false; raidSilent=false; if(!Overrides(MusicKind.RaidAmbient)) combatUntil=0; }
        public void StopPersonal() { PersonalPlaying=false; raidSilent=false; }
        public bool EndRaid(MusicKind outcome)
        {
            if (ResultHandled) return false;
            ResultHandled=true; LeaveWorld(); Outcome=Enabled(outcome) ? outcome : MusicKind.None; return true;
        }
        public bool TryDismissOutcome(bool menuAvailable, bool hideout, bool worldPresent)
        {
            if (Outcome==MusicKind.None || InRaid || !menuAvailable || hideout || worldPresent) return false;
            Outcome=MusicKind.None; return true;
        }
        public void FinishOutcome() { Outcome=MusicKind.None; }
        public MusicKind Select(double now, bool menuAllowed, bool hideout)
        {
            AdvanceCombat(now);
            if(!InCombat(now) && PersonalPlaying && Enabled(MusicKind.Personal) && (InRaid || (!hideout && menuAllowed))) return MusicKind.Personal;
            if (Outcome!=MusicKind.None && Enabled(Outcome)) return Outcome;
            if (InCombat(now)) return MusicKind.Combat;
            if(InRaid && raidSilent) return MusicKind.None;
            if (InRaid) return PersonalPlaying && Enabled(MusicKind.Personal) ? MusicKind.Personal : Enabled(MusicKind.RaidAmbient) ? MusicKind.RaidAmbient : MusicKind.None;
            if (hideout) return MusicKind.None;
            return menuAllowed && Enabled(MenuSelection) ? MenuSelection : MusicKind.None;
        }
    }
}
