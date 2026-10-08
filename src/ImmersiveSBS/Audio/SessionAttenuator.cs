using System.Globalization;
using System.IO;
using NAudio.CoreAudioApi;

namespace ImmersiveSBS.Audio;

/// <summary>
/// Silences an app's live sound while a delayed copy plays, without silencing what loopback
/// captures. Muting does not work for that: process loopback is taken after the session mute
/// (see <c>AudioLoopbackTests</c>). It is also taken after the session volume, which acts as
/// amplitude v², so the sessions are turned down to <see cref="Volume"/> (−80 dB, inaudible) and
/// the capture is boosted by <see cref="CaptureGain"/>; the mixer works in float, so nothing is
/// lost. Each session's own volume is restored afterwards. The changed sessions and their
/// volumes are written to a marker file, so a crash or a kill is repaired at the next start
/// (<see cref="RecoverAfterCrash"/>).
/// </summary>
public sealed class SessionAttenuator : IDisposable
{
    /// <summary>The session volume the live sound is turned down to.</summary>
    public const float Volume = 0.01f;

    /// <summary>Amplitude factor that undoes <see cref="Volume"/> on the captured sound: (1/v)².</summary>
    public const float CaptureGain = 1 / (Volume * Volume);

    private readonly HashSet<uint> processes;
    private readonly Dictionary<string, float> originals = new(StringComparer.Ordinal);

    public SessionAttenuator(HashSet<uint> processes)
    {
        this.processes = processes;
    }

    public static string MarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImmersiveSBS", "attenuated-sessions.txt");

    /// <summary>
    /// The user's own volume of the app's first session, so the delayed copy can be played as loud
    /// as the app was; 1 before any session was seen.
    /// </summary>
    public float OriginalVolume { get; private set; } = 1;

    /// <summary>Turns down any session of the processes not yet turned down (e.g. a new tab's).</summary>
    public void Refresh()
    {
        bool changed = false;
        ForEachSession(session =>
        {
            if (!processes.Contains(session.GetProcessID)) return;
            string id = session.GetSessionInstanceIdentifier;
            SimpleAudioVolume volume = session.SimpleAudioVolume;
            if (originals.ContainsKey(id))
            {
                // The user moved the app's slider meanwhile: keep the live sound silent.
                if (Math.Abs(volume.Volume - Volume) > 1e-4) volume.Volume = Volume;
                return;
            }

            if (originals.Count == 0) OriginalVolume = volume.Volume;
            originals[id] = volume.Volume;
            volume.Volume = Volume;
            changed = true;
            Log.Write($"Audio: turned down the live sound of process {session.GetProcessID} (was {originals[id]:0.00}).");
        });
        if (changed) WriteMarker();
    }

    /// <summary>Puts every session this attenuator changed back to its own volume.</summary>
    public void Dispose()
    {
        if (originals.Count == 0) return;
        Restore(originals);
        originals.Clear();
        try { File.Delete(MarkerPath); } catch { }
    }

    /// <summary>Restores what a previous run turned down and could not restore (crash, kill, power loss).</summary>
    public static void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            var saved = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(MarkerPath))
            {
                int tab = line.LastIndexOf('\t');
                if (tab > 0 && float.TryParse(line[(tab + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    saved[line[..tab]] = v;
            }
            int count = Restore(saved);
            File.Delete(MarkerPath);
            Log.Write($"Audio: restored the volume of {count} session(s) a previous run had turned down.");
        }
        catch (Exception e)
        {
            Log.Write("Audio: could not restore turned-down sessions: " + e.Message);
        }
    }

    private void WriteMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllLines(MarkerPath, originals.Select(p => p.Key + "\t" + p.Value.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception e)
        {
            Log.Write("Audio: could not write the session marker: " + e.Message);
        }
    }

    private static int Restore(Dictionary<string, float> volumes)
    {
        int count = 0;
        ForEachSession(session =>
        {
            if (!volumes.TryGetValue(session.GetSessionInstanceIdentifier, out float v)) return;
            session.SimpleAudioVolume.Volume = v;
            count++;
        });
        return count;
    }

    /// <summary>Every audio session on every active output, so a device change cannot strand one.</summary>
    private static void ForEachSession(Action<AudioSessionControl> action)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    AudioSessionManager manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    SessionCollection sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using AudioSessionControl session = sessions[i];
                        action(session);
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"Audio: sessions of {device.FriendlyName}: {e.Message}");
                }
            }
        }
    }
}
