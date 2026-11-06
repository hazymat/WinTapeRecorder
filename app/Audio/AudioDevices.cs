using NAudio.CoreAudioApi;
namespace WinTapeRecorder.Audio;

// IsOutput devices are recorded as loopback (what the PC plays) and are where auditions play
public record AudioDevice(string Id, string Name, bool IsOutput, bool IsDefault, int SampleRate, int Channels)
{
    public string Label => (IsOutput ? "Output: " : "Input: ") + Name + (IsDefault ? "  (default)" : "");
}

public static class AudioDevices
{
    public static List<AudioDevice> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            var defaultId = "";
            try { defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; } catch { }
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (endpoint)
                {
                    int sampleRate = 0, channels = 0;
                    try { var mixFormat = endpoint.AudioClient.MixFormat; sampleRate = mixFormat.SampleRate; channels = mixFormat.Channels; } catch { }
                    devices.Add(new AudioDevice(endpoint.ID, endpoint.FriendlyName, flow == DataFlow.Render, endpoint.ID == defaultId, sampleRate, channels));
                }
            }
        }
        return devices.OrderBy(device => device.IsOutput ? 0 : 1).ThenByDescending(device => device.IsDefault).ThenBy(device => device.Name).ToList();
    }

    // By id first; a device that came back with a new id is found by name; empty id = the Windows default of that kind
    public static MMDevice? Find(string deviceId, string deviceName, bool output)
    {
        var enumerator = new MMDeviceEnumerator();
        var flow = output ? DataFlow.Render : DataFlow.Capture;
        if (deviceId == "") { try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); } catch { return null; } }
        try { var byId = enumerator.GetDevice(deviceId); if (byId.State == DeviceState.Active) return byId; } catch { }
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(endpoint => endpoint.FriendlyName == deviceName);
    }
}
