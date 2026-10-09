using System.Globalization;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

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
    /// The user's own volume of the session that plays (or of the app's first session), so the
    /// delayed copy can be played as loud as the app was; 1 before any session was seen.
    /// </summary>
    public float OriginalVolume { get; private set; } = 1;

    /// <summary>
    /// The output the app is playing on now: the device of one of its active sessions (the default
    /// output when several are). An app keeps idle sessions on every output it ever used (Chrome
    /// does), so only an active one says where it is heard. Null while none is active.
    /// </summary>
    public string? PlayingDeviceId { get; private set; }

    public string? PlayingDeviceName { get; private set; }

    /// <summary>The default output at the last <see cref="Refresh"/>.</summary>
    public string? DefaultDeviceId { get; private set; }

    /// <summary>
    /// The loudest this process's own sound (the delayed copy) was in Windows' meter since the
    /// last <see cref="TakeOwnPeak"/>, and its session volume and output: proof of what Windows
    /// actually received from the player.
    /// </summary>
    private float ownPeak, ownVolume = float.NaN;
    private string? ownDevice;

    public (float Peak, float Volume, string? Device) TakeOwnPeak()
    {
        var result = (ownPeak, ownVolume, ownDevice);
        ownPeak = 0;
        return result;
    }

    /// <summary>
    /// Turns down any session of the processes not yet turned down (e.g. a new tab's), and notes
    /// where the app plays.
    /// </summary>
    public void Refresh()
    {
        bool changed = false;
        string? defaultId = DefaultDevice();
        string? playingId = null, playingName = null;
        ForEachSession((device, session) =>
        {
            uint pid = session.GetProcessID;
            if (pid == Environment.ProcessId && session.State == AudioSessionState.AudioSessionStateActive)
            {
                ownPeak = Math.Max(ownPeak, session.AudioMeterInformation.MasterPeakValue);
                ownVolume = session.SimpleAudioVolume.Volume;
                ownDevice = device.FriendlyName;
                return;
            }
            if (!processes.Contains(pid)) return;
            string id = session.GetSessionInstanceIdentifier;
            SimpleAudioVolume volume = session.SimpleAudioVolume;
            if (!originals.ContainsKey(id))
            {
                // Exactly our level: left turned down by an earlier run that could not restore it
                // (its output was gone at the time). Its real volume is unknown; full is the default.
                float original = Math.Abs(volume.Volume - Volume) < 1e-4 ? 1 : volume.Volume;
                if (originals.Count == 0) OriginalVolume = original;
                originals[id] = original;
                changed = true;
                Log.Write($"Audio: turned down the live sound of process {session.GetProcessID} on {device.FriendlyName} (was {originals[id]:0.00}).");
            }
            // Also when the user moved the app's slider meanwhile: keep the live sound silent.
            if (Math.Abs(volume.Volume - Volume) > 1e-4) volume.Volume = Volume;

            if (session.State == AudioSessionState.AudioSessionStateActive && (playingId == null || device.ID == defaultId))
            {
                playingId = device.ID;
                playingName = device.FriendlyName;
                OriginalVolume = originals[id];
            }
        });
        DefaultDeviceId = defaultId;
        PlayingDeviceId = playingId;
        PlayingDeviceName = playingName;
        if (changed) WriteMarker();
    }

    private static string? DefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? device) || device == null) return null;
            using (device) return device.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Puts every session this attenuator changed back to its own volume. Sessions that cannot be
    /// reached now (their output is gone) stay in the marker file for the next start.
    /// </summary>
    public void Dispose()
    {
        if (originals.Count == 0) return;
        Restore(originals);
        if (originals.Count == 0)
        {
            try { File.Delete(MarkerPath); } catch { }
        }
        else
        {
            Log.Write($"Audio: {originals.Count} session(s) could not be restored now; kept for the next start.");
            WriteMarker();
            originals.Clear();
        }
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
            Log.Write($"Audio: restored the volume of {count} session(s) a previous run had turned down" +
                      (saved.Count > 0 ? $"; {saved.Count} no longer exist." : "."));
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

    /// <summary>Restores the sessions found and removes them from <paramref name="volumes"/>.</summary>
    private static int Restore(Dictionary<string, float> volumes)
    {
        int count = 0;
        ForEachSession((_, session) =>
        {
            string id = session.GetSessionInstanceIdentifier;
            if (!volumes.TryGetValue(id, out float v)) return;
            session.SimpleAudioVolume.Volume = v;
            volumes.Remove(id);
            count++;
        });
        return count;
    }

    /// <summary>Every audio session on every active output, so a device change cannot strand one.</summary>
    private static void ForEachSession(Action<MMDevice, AudioSessionControl> action)
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
                        action(device, session);
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
