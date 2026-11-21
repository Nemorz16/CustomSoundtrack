using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> playerUiEnabled;
        private ConfigEntry<float> playerUiScale;
        private ConfigEntry<PlayerSkin> playerSkin;
        private ConfigEntry<KeyboardShortcut> playerUiKey;
        private bool playerUiVisible, playerInputCaptured, playerUiFailed, uiPositioned;
        private MusicKind lastControlKind = MusicKind.None;
        private bool oldCursorVisible, oldIgnoreInput;
        private CursorLockMode oldCursorLock;
        private int releaseInputFrame, selectedPersonal = -1;
        private MethodInfo setIgnoreInput;
        private FieldInfo ignoreInputField;
        private Rect playerWindow = new Rect(20, 20, 483.78f, 665.28f);
        private Vector2 playlistScroll;
        private string playlistSearch = "";
        private GUIStyle uiText, uiDim, uiDisplay, uiIcon, uiTrack, uiSelectedTrack, uiSearch;

        private void ConfigurePlayerUi()
        {
            playerSkin = Config.Bind("Player UI", "Skin", PlayerSkin.Green, "Choose the player colour.");
            playerUiEnabled = Config.Bind("Player UI", "Enabled", true, "Show the raid player. The game keeps running.");
            playerUiScale = FloatSetting("Player UI", "Scale", 0.66f, 0.45f, 1f, "Resize the player; automatically fits the screen.");
            playerUiKey = Config.Bind("Hotkeys", "TogglePlayer", new KeyboardShortcut(KeyCode.F10), "Open/close the raid player. Close other menus first.");
            var owner = AccessTools.TypeByName("EFT.GamePlayerOwner");
            setIgnoreInput = owner == null ? null : AccessTools.Method(owner, "SetIgnoreInput", new[] { typeof(bool) });
            ignoreInputField = owner == null ? null : AccessTools.Field(owner, "bool_0");
            if (setIgnoreInput == null || !setIgnoreInput.IsStatic || ignoreInputField == null ||
                !ignoreInputField.IsStatic || ignoreInputField.FieldType != typeof(bool))
            {
                playerUiFailed = true;
                Logger.LogWarning("Player popup unavailable: expected native input-capture API not found. Audio/hotkeys remain available.");
            }
        }
        private void InstallPlayerUiInputHook()
        {
            if (playerUiFailed) return;
            try
            {
                var owner = AccessTools.TypeByName("EFT.GamePlayerOwner");
                var method = AccessTools.Method(owner, "CanTranslateCommand");
                if (method == null || method.ReturnType != typeof(bool)) throw new MissingMethodException("CanTranslateCommand");
                Patch(method, nameof(PlayerUiCommandGate), true);
            }
            catch (Exception ex)
            {
                playerUiFailed = true;
                Logger.LogWarning("Player popup unavailable: command input gate could not be installed. Audio remains available. " + ex.Message);
            }
        }
        private static bool PlayerUiCommandGate(ref bool __result)
        {
            // Native ignore-input allows a few commands through; consume those too while our popup owns input.
            if (instance == null || !instance.playerInputCaptured) return true;
            __result = false;
            return false;
        }
        private bool PlayerUiAvailable => ready && !faulted && enabledSetting.Value && playerUiEnabled.Value && state.InRaid;
        private void UpdatePlayerUi()
        {
            if (!PlayerUiAvailable || playerUiFailed) { ClosePlayerUi(true); return; }
            if (playerInputCaptured && !playerUiVisible && Time.frameCount > releaseInputFrame &&
                !Input.GetMouseButton(0) && !Input.GetMouseButton(1)) RestorePlayerInput();
            if (playerUiKey.Value.IsDown())
            {
                if (playerUiVisible) ClosePlayerUi(false);
                else OpenPlayerUi();
            }
            if (playerUiVisible && Input.GetKeyDown(KeyCode.Escape)) ClosePlayerUi(false);
        }
        private void OpenPlayerUi()
        {
            if (!PlayerUiAvailable || playerUiFailed || playerInputCaptured) return;
            // Do not steal a cursor/input lock from inventory, pause menus or another popup.
            if (Cursor.visible || (bool)ignoreInputField.GetValue(null) || Input.GetMouseButton(0) || Input.GetMouseButton(1))
            {
                Logger.LogInfo("Player popup: close other menus and release mouse buttons before opening.");
                return;
            }
            try
            {
                oldCursorVisible = Cursor.visible; oldCursorLock = Cursor.lockState;
                oldIgnoreInput = (bool)ignoreInputField.GetValue(null);
                playerInputCaptured = true;
                setIgnoreInput.Invoke(null, new object[] { true });
                playerUiVisible = true;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                if (!uiPositioned)
                {
                    float scale = (float)PlayerUiRules.SkinScale(playerUiScale.Value, Screen.width, Screen.height);
                    playerWindow.width = PlayerSkinWidth * scale;
                    playerWindow.height = PlayerSkinHeight * scale;
                    playerWindow.x = (Screen.width - playerWindow.width) / 2;
                    playerWindow.y = (Screen.height - playerWindow.height) / 2;
                    uiPositioned = true;
                }
                var c = channels[MusicKind.Personal];
                if (selectedPersonal < 0 || selectedPersonal >= c.Tracks.Count)
                    selectedPersonal = c.Index >= 0 ? c.Index : c.Tracks.Count > 0 ? 0 : -1;
                Logger.LogInfo("Personal player opened.");
            }
            catch (Exception ex) { DisablePlayerUi(ex); }
        }
        private void ClosePlayerUi(bool immediate)
        {
            playerUiVisible = false;
            if (!playerInputCaptured) return;
            if (immediate) RestorePlayerInput();
            else releaseInputFrame = Time.frameCount + 1; // Prevent closing mouse clicks leaking into weapon input.
        }
        private void RestorePlayerInput()
        {
            if (!playerInputCaptured) return;
            try { setIgnoreInput?.Invoke(null, new object[] { oldIgnoreInput }); }
            catch (Exception ex)
            {
                Logger.LogWarning("Native input restore failed; restoring the captured flag directly: " + ex.Message);
                try { ignoreInputField?.SetValue(null, oldIgnoreInput); }
                catch (Exception fallback) { Logger.LogError("Input flag restore failed: " + fallback); }
            }
            finally
            {
                Cursor.lockState = oldCursorLock; Cursor.visible = oldCursorVisible;
                playerInputCaptured = false;
            }
        }
        private void DisablePlayerUi(Exception ex)
        {
            playerUiFailed = true; ClosePlayerUi(true);
            Logger.LogWarning("Player popup disabled for this session; soundtrack remains active. " + ex);
        }
        private void LateUpdate()
        {
            if (playerUiVisible && !PlayerUiAvailable) ClosePlayerUi(true);
            if (playerInputCaptured) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }
        private void OnApplicationFocus(bool focused) { if (!focused) ClosePlayerUi(true); }
        private void OnDisable() { ClosePlayerUi(true); }

        private void OnGUI()
        {
            DrawSoundToast();
            if (!playerUiVisible || !PlayerUiAvailable || playerUiFailed) return;
            var oldColor = GUI.color; var oldBackground = GUI.backgroundColor; bool oldEnabled = GUI.enabled; int oldDepth = GUI.depth;
            try
            {
                PreparePlayerStyles();
                float scale = (float)PlayerUiRules.SkinScale(playerUiScale.Value, Screen.width, Screen.height);
                playerWindow.width = PlayerSkinWidth * scale;
                playerWindow.height = PlayerSkinHeight * scale;
                UpdateSkinFontSizes(scale);
                playerWindow.x = Mathf.Clamp(playerWindow.x, 0, Mathf.Max(0, Screen.width - playerWindow.width));
                playerWindow.y = Mathf.Clamp(playerWindow.y, 0, Mathf.Max(0, Screen.height - playerWindow.height));
                GUI.depth = -1000;
                GUI.color = Color.white; GUI.backgroundColor = Color.white;
                playerWindow = GUI.Window(GetInstanceID(), playerWindow, DrawPlayerWindow, "", GUIStyle.none);
            }
            catch (Exception ex) { DisablePlayerUi(ex); }
            finally { GUI.color = oldColor; GUI.backgroundColor = oldBackground; GUI.enabled = oldEnabled; GUI.depth = oldDepth; }
        }
        private void PreparePlayerStyles()
        {
            PreparePlayerArtwork();
            if (uiText != null) return;
            uiText = new GUIStyle(GUI.skin.label)
            {
                clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0)
            };
            uiText.normal.textColor = new Color(0.84f, 0.91f, 0.69f);
            uiDim = new GUIStyle(uiText);
            uiDim.normal.textColor = new Color(0.69f, 0.78f, 0.58f);
            uiDisplay = new GUIStyle(uiText) { fontStyle = FontStyle.Bold, wordWrap = true, alignment = TextAnchor.UpperLeft };
            uiDisplay.normal.textColor = new Color(0.86f, 0.96f, 0.66f);
            uiIcon = new GUIStyle(GUIStyle.none);
            uiIcon.hover.background = playerHover;
            uiIcon.active.background = playerPressed;
            uiIcon.focused.background = playerHover;
            uiTrack = new GUIStyle(GUIStyle.none) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            uiTrack.normal.textColor = uiText.normal.textColor;
            uiTrack.hover.textColor = Color.white;
            uiTrack.hover.background = playerHover;
            uiTrack.active.background = playerPressed;
            uiTrack.focused.textColor = Color.white;
            uiSelectedTrack = new GUIStyle(uiTrack);
            uiSelectedTrack.normal.background = playerSelection;
            uiSelectedTrack.normal.textColor = new Color(0.93f, 1f, 0.75f);
            uiSearch = new GUIStyle(GUI.skin.textField)
            {
                alignment = TextAnchor.MiddleLeft, fixedHeight = 0,
                margin = new RectOffset(0, 0, 0, 0)
            };
            uiSearch.normal.background = playerSearchBack;
            uiSearch.focused.background = playerSearchBack;
            uiSearch.hover.background = playerSearchBack;
            uiSearch.normal.textColor = uiText.normal.textColor;
            uiSearch.focused.textColor = Color.white;
            uiSearch.hover.textColor = Color.white;
        }
        private void UpdateSkinFontSizes(float scale)
        {
            uiText.fontSize = Mathf.Max(10, Mathf.RoundToInt(20 * scale));
            uiDim.fontSize = Mathf.Max(9, Mathf.RoundToInt(18 * scale));
            uiDisplay.fontSize = Mathf.Max(12, Mathf.RoundToInt(26 * scale));
            uiTrack.fontSize = uiSelectedTrack.fontSize = uiText.fontSize;
            uiSearch.fontSize = uiText.fontSize;
            int padding = Mathf.Max(3, Mathf.RoundToInt(8 * scale));
            uiTrack.padding = new RectOffset(padding, padding, 0, 0);
            uiSelectedTrack.padding = new RectOffset(padding, padding, 0, 0);
            // Native skin padding is in unscaled pixels and can consume the text height.
            uiSearch.padding = new RectOffset(padding, padding, 0, 0);
        }
        private float SkinScale => playerWindow.height / PlayerSkinHeight;
        private Rect SkinRect(float x, float y, float width, float height)
            => new Rect(x * playerWindow.width / 450f, y * playerWindow.height / 768f, width * playerWindow.width / 450f, height * playerWindow.height / 768f);
        private static void UiPanel(Rect rect, Color color)
        {
            var old = GUI.color; GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old;
        }
        private bool SkinButton(float x, float y, float w, float h, string tooltip)
        {
            var rect = SkinRect(x, y, w, h);
            if (!GUI.enabled) UiPanel(rect, new Color(0, 0, 0, 0.3f));
            return GUI.Button(rect, new GUIContent("", tooltip), uiIcon);
        }
        private void DrawPlayerWindow(int id)
        {
            var controlKind = PlayerControlKind;
            if(lastControlKind!=controlKind) { selectedPersonal=-1; playlistScroll=Vector2.zero; lastControlKind=controlKind; }
            var c = channels[controlKind];
            bool combat = controlKind == MusicKind.Personal && state.InCombat(Now) && state.Overrides(MusicKind.Personal);
            GUI.DrawTexture(new Rect(0, 0, playerWindow.width, playerWindow.height), playerShell);
            if (SkinButton(390, 17, 29, 35, "Close player")) ClosePlayerUi(false);
            // Three swatches form an icon, with no extra printed label on the casing.
            if (SkinButton(350, 21, 30, 27, "Change skin")) playerSkin.Value = (PlayerSkin)(((int)playerSkin.Value + 1) % Enum.GetValues(typeof(PlayerSkin)).Length);
            UiPanel(SkinRect(353, 27, 8, 13), new Color(.40f,.44f,.28f));
            UiPanel(SkinRect(362, 27, 8, 13), new Color(.12f,.13f,.12f));
            UiPanel(SkinRect(371, 27, 8, 13), new Color(.85f,.85f,.78f));
            var oldContent = GUI.contentColor;
            if(playerSkin.Value==PlayerSkin.White || playerSkin.Value==PlayerSkin.Yellow) GUI.contentColor=new Color(.12f,.15f,.1f);
            if (GUI.Button(SkinRect(32,207,180,27),showCurrentPlaylist ? "Current playlist" : "Personal playlist",uiTrack))
            { showCurrentPlaylist=!showCurrentPlaylist; selectedPersonal=-1; playlistScroll=Vector2.zero; }
            GUI.Label(SkinRect(220,207,190,27),controlKind.ToString(),uiDim);
            GUI.contentColor=oldContent;
            string status = combat ? "COMBAT / PERSONAL PLAYBACK ON HOLD" : c.Loading ? "LOADING TRACK" : !IsPaused(controlKind) && c.Source.isPlaying ? "PLAYING" : c.Index < 0 ? "READY" : !c.Started && c.SavedPosition == 0 ? "STOPPED" : "PAUSED";
            GUI.Label(SkinRect(32, 78, 385, 24), status, uiDim);
            int shown = c.PendingIndex >= 0 ? c.PendingIndex : c.Index;
            string title = shown >= 0 && shown < c.Tracks.Count ? Path.GetFileNameWithoutExtension(c.Tracks[shown].File) : "Select a track below";
            GUI.Label(SkinRect(32, 105, 385, 46), title, uiDisplay);
            double duration = Valid(c.Source.clip) ? c.Source.clip.length : 0;
            GUI.Label(SkinRect(32, 157, 150, 25), PlayerUiRules.TimeLabel(c.SavedPosition) + " / " + PlayerUiRules.TimeLabel(duration), uiText);
            if(controlKind == MusicKind.Personal)
            {
                GUI.enabled = !c.Loading && c.PendingIndex<0 && duration>0;
                float seek = GUI.HorizontalSlider(SkinRect(190,158,220,24),c.SavedPosition,0,Mathf.Max(.01f,(float)duration));
                if(Mathf.Abs(seek-c.SavedPosition)>.15f) { float requested=seek; events.Enqueue(()=>SeekPersonal(requested)); }
                GUI.enabled=true;
            }
            else
            {
            UiPanel(SkinRect(190, 169, 220, 4), new Color(.28f,.36f,.22f,.7f));
            UiPanel(SkinRect(190, 169, 220 * (float)PlayerUiRules.Progress(c.SavedPosition, duration), 4), new Color(.77f,.88f,.51f));
            }
            GUI.Label(SkinRect(32, 250, 170, 28), PlayerUiRules.PlaylistLabel(c.Tracks.Count, selectedPersonal, shown), uiDim);
            GUI.SetNextControlName("SoundtrackPlayerSearch");
            playlistSearch = GUI.TextField(SkinRect(205, 250, 205, 28), playlistSearch, 80, uiSearch);
            if (playlistSearch.Length == 0 && GUI.GetNameOfFocusedControl() != "SoundtrackPlayerSearch")
                GUI.Label(SkinRect(212, 250, 190, 28), "Search tracks...", uiDim);
            int matches = 0;
            for (int i = 0; i < c.Tracks.Count; i++) if (PlayerUiRules.Matches(c.Tracks[i].File, playlistSearch)) matches++;
            float unit = playerWindow.height / 768f;
            float listHeight = 215 * unit, rowHeight = 30 * unit;
            float maxScroll = Mathf.Max(0, matches * rowHeight + 4 * unit - listHeight);
            playlistScroll.y = Mathf.Clamp(playlistScroll.y, 0, maxScroll);
            DrawScrollArrow(384, 286, true, maxScroll, rowHeight);
            DrawScrollArrow(384, 470, false, maxScroll, rowHeight);
            var viewport = SkinRect(30, 286, 349, 215);
            playlistScroll = GUI.BeginScrollView(viewport, playlistScroll, new Rect(0,0,viewport.width - 20,Mathf.Max(listHeight,matches * rowHeight + 4 * unit)));
            float y = 2 * unit;
            for (int i = 0; i < c.Tracks.Count; i++)
            {
                if (!PlayerUiRules.Matches(c.Tracks[i].File, playlistSearch)) continue;
                string prefix = i == c.Index && c.Source.isPlaying ? "> " : "  ";
                if (GUI.Button(new Rect(0,y,viewport.width - 22,28 * unit), prefix + (i+1).ToString("00") + "  " + Path.GetFileNameWithoutExtension(c.Tracks[i].File), i == selectedPersonal ? uiSelectedTrack : uiTrack)) selectedPersonal = i;
                y += rowHeight;
            }
            if (matches == 0) GUI.Label(new Rect(3,3,viewport.width - 22,65 * unit), c.Tracks.Count == 0 ? "Add WAV/OGG to Music/" + controlKind + ",\nthen restart." : "No matching tracks.", uiText);
            GUI.EndScrollView();
            GUI.enabled = !combat && state.Enabled(controlKind) && !c.Loading && c.Tracks.Count > 0;
            if (SkinButton(263,606,39,45,"Previous track")) QueuePlayerCommand(-1);
            if (SkinButton(365,606,39,45,"Next track")) QueuePlayerCommand(1);
            if (SkinButton(304,591,59,76,"Play / pause selected track")) QueuePlayerCommand(0);
            GUI.enabled = !combat && state.Enabled(controlKind) && c.Tracks.Count > 0;
            if (SkinButton(317,550,33,34,"Stop")) QueuePlayerCommand(2);
            GUI.enabled = true;
            var oldText = uiDisplay.normal.textColor;
            if ((playerSkin.Value == PlayerSkin.White || playerSkin.Value == PlayerSkin.Yellow)) uiDisplay.normal.textColor = new Color(.12f,.15f,.10f);
            GUI.Label(SkinRect(27,573,195,32),Mathf.RoundToInt(c.Volume.Value * 100) + "%",uiDisplay);
            uiDisplay.normal.textColor = oldText;
            if (SkinButton(22,608,33,40,"Playlist volume down")) c.Volume.Value = Mathf.Clamp01(c.Volume.Value-volumeStep.Value);
            float value = GUI.HorizontalSlider(SkinRect(65,623,120,22),c.Volume.Value,0,1);
            if (Mathf.Abs(value-c.Volume.Value)>.001f) c.Volume.Value=value;
            if (SkinButton(194,608,34,40,"Playlist volume up")) c.Volume.Value = Mathf.Clamp01(c.Volume.Value+volumeStep.Value);
            GUI.DragWindow(SkinRect(27,8,315,52));
        }
        private void DrawScrollArrow(float x, float y, bool up, float maxScroll, float step)
        {
            bool available = up ? playlistScroll.y > 0 : playlistScroll.y < maxScroll;
            GUI.enabled = available;
            if (SkinButton(x,y,30,30,up ? "Previous playlist rows" : "More playlist rows"))
                playlistScroll.y = Mathf.Clamp(playlistScroll.y + (up ? -step * 5 : step * 5),0,maxScroll);
            // Pixel-built double chevrons avoid missing Unicode glyphs in the game's font.
            Color tint = new Color(.80f,.88f,.62f,available ? 1f : .25f);
            for (int chevron=0;chevron<2;chevron++)
                for (int i=0;i<7;i++)
                {
                    float yy = y + 6 + chevron*9 + (up ? 6-i : i);
                    UiPanel(SkinRect(x+5+i,yy,2,2),tint);
                    UiPanel(SkinRect(x+18-i,yy,2,2),tint);
                }
            GUI.enabled = true;
        }
        private void QueuePlayerCommand(int command)
        {
            int selection = selectedPersonal;
            MusicKind kind = PlayerControlKind;
            events.Enqueue(() =>
            {
                try { RunPlayerCommand(command, selection, kind); }
                catch (Exception ex) { DisablePlayerUi(ex); }
            });
        }
        private void RunPlayerCommand(int command, int selection, MusicKind kind)
        {
            if(!PlayerUiAvailable || !state.Enabled(kind)) return;
            if(kind==MusicKind.Personal && state.InCombat(Now) && state.Overrides(kind)) return;
            if(kind!=MusicKind.Personal && state.Select(Now,menuAllowed,false)!=kind) return;
            var c=channels[kind];
            if(command==2) { Transport(kind,2); return; }
            if(c.Loading || c.Tracks.Count==0) return;
            if(command==0 && active==kind && !IsPaused(kind) && c.Source.isPlaying && (selection<0 || selection==c.Index)) { Transport(kind,0); return; }
            int target=PlayerUiRules.TargetIndex(c.Tracks.Count,c.Index,selection,command);
            if(target<0) return;
            if(kind==MusicKind.Personal) { StartPersonal(); if(!state.PersonalPlaying) return; }
            ResumeCategory(kind);
            if(command!=0 || target!=c.Index || c.Failed) QueueTrackIndex(kind,target);
            selectedPersonal=target;
        }
    }
}
