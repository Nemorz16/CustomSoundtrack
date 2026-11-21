using BepInEx.Configuration;
using UnityEngine;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private void HandleHideoutHotkeys()
        {
            if (stopKey.Value.IsDown()) hideoutRadio.HandleTransport(2);
            else if (playKey.Value.IsDown()) hideoutRadio.HandleTransport(0);
            else if (nextKey.Value.IsDown()) hideoutRadio.HandleTransport(1);
            else if (previousKey.Value.IsDown()) hideoutRadio.HandleTransport(-1);
        }
        private bool menuPaused;
        private ConfigEntry<KeyboardShortcut> volumeUpKey, volumeDownKey;
        private ConfigEntry<float> volumeStep;
        private void ConfigureVolumeHotkeys()
        {
            volumeUpKey = Config.Bind("Hotkeys", "VolumeUp", new KeyboardShortcut(KeyCode.PageUp), "Raise current playlist volume.");
            volumeDownKey = Config.Bind("Hotkeys", "VolumeDown", new KeyboardShortcut(KeyCode.PageDown), "Lower current playlist volume.");
            volumeStep = FloatSetting("Hotkeys", "VolumeStep", 0.05f, 0.01f, 0.25f, "Volume change per press. 0.05 = 5 percent.");
        }
        private void HandleVolumeHotkeys(MusicKind desired, bool hideout)
        {
            int direction = (volumeUpKey.Value.IsDown() ? 1 : 0) - (volumeDownKey.Value.IsDown() ? 1 : 0);
            if (direction == 0) return;
            ConfigEntry<float> target = null;
            string label = desired.ToString();
            if (desired != MusicKind.None) target = channels[desired].Volume;
            else if (hideout && !state.InRaid && hideoutRadio != null)
            {
                target = hideoutRadio.VolumeSetting;
                label = "Hideout";
            }
            if (target == null) return;
            target.Value = Mathf.Clamp01(target.Value + direction * volumeStep.Value);
            Logger.LogInfo(label + " volume: " + Mathf.RoundToInt(target.Value * 100) + "%");
        }
        private bool HandleOutcomeOverride(bool hideout)
        {
            if (!dismissOutcomeKey.Value.IsDown() || !state.TryDismissOutcome(menuAllowed, hideout, world != null)) return false;
            menuPaused = false; pausedCategories.Remove(MusicKind.MainMenu);
            var menu = channels[MusicKind.MainMenu];
            if (menu.ResetWhenSilent) { menu.Source.Stop(); menu.SavedPosition = 0; menu.Started = false; }
            menu.ResetWhenSilent = false;
            Logger.LogInfo("Outcome overridden by Play/Pause; fading into main-menu music.");
            // Update performs the normal outcome-to-menu transition. Consume this press so it cannot pause the menu too.
            return true;
        }
        private void HandleMenuHotkeys()
        {
            var c = channels[MusicKind.MainMenu];
            if (playKey.Value.IsDown())
            {
                menuPaused = !menuPaused;
                if (menuPaused) c.Gain.Set(0, fades["MainMenuTrackChange"].Value, Now);
                else
                {
                    // Play during a stop fade must restart, rather than revive the old position.
                    if (c.ResetWhenSilent) { c.Source.Stop(); c.SavedPosition = 0; c.Started = false; }
                    c.ResetWhenSilent = false;
                    c.FadeInSeconds = fades["MenuFadeIn"].Value;
                    if (active == MusicKind.MainMenu) EnsureActive();
                }
            }
            if (stopKey.Value.IsDown())
            {
                menuPaused = true; c.PendingIndex = -1; c.EndingTrack = false;
                c.ResetWhenSilent = true;
                c.Gain.Set(0, fades["MainMenuTrackChange"].Value, Now);
            }
            if (!c.Loading && (nextKey.Value.IsDown() || previousKey.Value.IsDown()))
            {
                menuPaused = false; c.ResetWhenSilent = false;
                QueueTrack(MusicKind.MainMenu, nextKey.Value.IsDown() ? 1 : -1, false);
            }
        }
    }
}
