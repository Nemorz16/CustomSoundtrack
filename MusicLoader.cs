using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;
namespace CustomSoundtrack
{
    public sealed partial class Plugin
    {
        private sealed class Asset : IDisposable
        {
            public string File;
            public AudioClip Clip;
            public DownloadHandler Handler;
            public bool Warned;
            public double NextRetry;
            public void Dispose()
            {
                if (Clip != null) UnityEngine.Object.Destroy(Clip);
                Clip = null; Handler?.Dispose(); Handler = null;
            }
        }
        private sealed class Channel
        {
            public readonly List<Asset> Tracks = new List<Asset>();
            public AudioSource Source;
            public ConfigEntry<float> Volume;
            public int Index = -1, PendingIndex = -1;
            public bool Started, Loading, Failed, ResetWhenSilent, RestorePosition, EndingTrack;
            public float SavedPosition, FadeInSeconds = 2;
            public readonly FadeEnvelope Gain = new FadeEnvelope();
        }
        private static bool Valid(AudioClip clip) => clip != null && clip.loadState == AudioDataLoadState.Loaded &&
            clip.samples > 0 && clip.frequency > 0 && clip.length >= 0.1f && clip.length <= 36000;
        private IEnumerator LoadAll()
        {
            string root = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Music");
            foreach (var pair in channels)
            {
                string folder = Path.Combine(root, pair.Key.ToString());
                string[] files;
                try
                {
                    Directory.CreateDirectory(folder);
                    var allFiles = Directory.GetFiles(folder);
                    files = allFiles.Where(f => CustomSoundtrack.AudioFormats.Supported(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    Logger.LogInfo("Folder scan " + pair.Key + ": " + allFiles.Length + " file(s), " + files.Length + " WAV/OGG/MP3; path=" + folder);
                    foreach (var ignored in allFiles.Except(files).Where(f => !string.Equals(Path.GetExtension(f), ".txt", StringComparison.OrdinalIgnoreCase)))
                        Logger.LogWarning("Unsupported music file (WAV/OGG/MP3 required): " + ignored);
                    if (Directory.GetDirectories(folder).Length > 0)
                        Logger.LogWarning("Music subfolders are not scanned; put tracks directly inside " + folder);
                }
                catch (Exception ex) { Logger.LogWarning("Cannot read " + folder + ": " + ex.Message); continue; }
                foreach (string file in files)
                {
                    var asset = new Asset { File = file };
                    if (pair.Key == MusicKind.MainMenu)
                    {
                        yield return Decode(asset);
                        if (Valid(asset.Clip)) pair.Value.Tracks.Add(asset); else asset.Dispose();
                        yield return new WaitForSecondsRealtime(0.1f);
                    }
                    else pair.Value.Tracks.Add(asset); // File catalog only; decode when this category is requested.
                }
            }
            // Same bounded startup recovery that restored all 8 hideout tracks in testing.
            yield return null;
            foreach (var pair in channels.Where(p => p.Key == MusicKind.MainMenu))
                foreach (var asset in pair.Value.Tracks)
                    if (!Valid(asset.Clip))
                    {
                        Logger.LogWarning("Startup recovery: " + asset.File);
                        yield return Decode(asset);
                    }
            yield return null;
            foreach (var pair in channels)
            {
                for (int i = pair.Value.Tracks.Count - 1; pair.Key == MusicKind.MainMenu && i >= 0; i--)
                {
                    var asset = pair.Value.Tracks[i];
                    if (Valid(asset.Clip)) continue;
                    Logger.LogWarning("Excluded for this session: " + asset.File);
                    asset.Dispose(); pair.Value.Tracks.RemoveAt(i);
                }
                Logger.LogInfo((pair.Key == MusicKind.MainMenu ? "Ready " : "Catalogued for on-demand loading ") + pair.Key + ": " + pair.Value.Tracks.Count + " track(s).");
                if (pair.Value.Tracks.Count == 0)
                    Logger.LogWarning("Empty playlist: " + Path.Combine(root, pair.Key.ToString()) + "; add WAV/OGG/MP3 files here and restart.");
            }
            ready = true;
            Logger.LogInfo("Custom Soundtrack ready. Native menu music suppression follows General/SuppressDefaultMenuMusic.");
        }
        private IEnumerator Decode(Asset asset)
        {
            string headerError = null;
            try { if (string.Equals(Path.GetExtension(asset.File), ".wav", StringComparison.OrdinalIgnoreCase)) headerError = WavHeader.Check(asset.File); }
            catch (Exception ex) { headerError = ex.Message; }
            if (headerError != null)
            {
                asset.NextRetry = double.PositiveInfinity;
                Logger.LogWarning("WAV rejected before Unity decoding: " + asset.File + "; " + headerError + ". Repair or re-export this file, then restart.");
                yield break;
            }
            Logger.LogInfo("Decode starting: " + asset.File);
            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.GetFullPath(asset.File)).AbsoluteUri,
                    AudioFormats.Type(asset.File));
            }
            catch (Exception ex) { Logger.LogWarning("Cannot open " + asset.File + ": " + ex.Message); }
            if (request == null) yield break;
            using (request)
            {
                request.timeout = 30;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { Logger.LogWarning("Decode failed: " + asset.File + ": " + request.error); yield break; }
                AudioClip clip = null;
                bool adopted = false;
                try
                {
                    clip = DownloadHandlerAudioClip.GetContent(request);
                    if (!Valid(clip)) throw new InvalidDataException("Decoder returned invalid audio metadata");
                    clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    clip.name = "CustomSoundtrack: " + Path.GetFileName(asset.File);
                    asset.Dispose(); asset.Clip = clip; asset.Handler = request.downloadHandler; asset.Warned = false;
                    request.disposeDownloadHandlerOnDispose = false; adopted = true;
                    asset.NextRetry = 0; // Successful audio can be restored immediately after scene unloading.
                    Logger.LogInfo("Loaded " + asset.File + "; duration=" + clip.length + "; samples=" + clip.samples);
                }
                catch (Exception ex) { Logger.LogWarning("Decode failed: " + asset.File + ": " + ex.Message); }
                finally { if (!adopted && clip != null) UnityEngine.Object.Destroy(clip); }
            }
        }
    }
}
