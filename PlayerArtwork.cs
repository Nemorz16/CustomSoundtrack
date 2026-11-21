using System;
using System.IO;
using UnityEngine;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        public enum PlayerSkin { Green, Black, White, Yellow, Blue, Red }
        private const float PlayerSkinHeight = 1008f;
        private const float PlayerSkinWidth = 450f / 768f * PlayerSkinHeight;
        private PlayerSkin loadedSkin;
        private Texture2D playerShell, playerHover, playerPressed, playerSelection, playerSearchBack;
        private void PreparePlayerArtwork()
        {
            if (playerShell != null && loadedSkin == playerSkin.Value) return;
            if (playerShell != null) Destroy(playerShell);
            playerShell = null;
            using (var stream = typeof(Plugin).Assembly.GetManifestResourceStream("CustomSoundtrack.FieldPlayer" + playerSkin.Value + ".png"))
            {
                if (stream == null) throw new InvalidDataException("Embedded FieldPlayer.png is missing.");
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    try
                    {
                        if (!ImageConversion.LoadImage(texture, memory.ToArray(), true))
                            throw new InvalidDataException("Cannot decode embedded FieldPlayer.png.");
                        if (texture.width < 440 || texture.width > 460 || texture.height != 768)
                            throw new InvalidDataException("Player artwork size does not match its control map.");
                        texture.hideFlags = HideFlags.HideAndDontSave;
                        texture.wrapMode = TextureWrapMode.Clamp;
                        texture.filterMode = FilterMode.Bilinear;
                        playerShell = texture; loadedSkin = playerSkin.Value;
                    }
                    catch { Destroy(texture); throw; }
                }
            }
            if (playerHover != null) return;
            playerHover = PlayerTint(new Color(0.75f, 0.85f, 0.53f, 0.13f));
            playerPressed = PlayerTint(new Color(0, 0, 0, 0.25f));
            playerSelection = PlayerTint(new Color(0.5f, 0.7f, 0.31f, 0.24f));
            playerSearchBack = PlayerTint(new Color(0.02f, 0.04f, 0.02f, 0.60f));
        }
        private static Texture2D PlayerTint(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color); texture.Apply(false, true);
            return texture;
        }
        private void DestroyPlayerArtwork()
        {
            foreach (var texture in new[] { playerShell, playerHover, playerPressed, playerSelection, playerSearchBack })
                if (texture != null) Destroy(texture);
        }
    }
}
