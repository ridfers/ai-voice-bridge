using NAudio.CoreAudioApi;
namespace AiVoiceBridge.Core;

public interface IAudioActivityMonitor : IDisposable
{
    bool Available { get; }
    void Select(string deviceId);
    float? ReadPeak();
}
// Uses Windows' existing endpoint meter. No capture stream or audio buffers.
public sealed class WindowsAudioActivityMonitor : IAudioActivityMonitor
{
    private MMDevice? _device;
    public bool Available=>_device!=null;
    public void Select(string deviceId)
    {
        Dispose();
        try
        {
            using var devices=new MMDeviceEnumerator();
            var device=devices.GetDevice(deviceId);
            if(device.State!=DeviceState.Active) { device.Dispose(); return; }
            _device=device;
            _=_device.AudioMeterInformation.MasterPeakValue;
        }
        catch { Dispose(); }
    }
    public float? ReadPeak()
    {
        if(_device==null)return null;
        try { return _device.AudioMeterInformation.MasterPeakValue; }
        catch { Dispose(); return null; }
    }
    public void Dispose() { _device?.Dispose(); _device=null; }
}
public sealed class SpeechActivityIndicator
{
    private long _lastSignal=long.MinValue;
    public bool Available { get; private set; }
    public bool Speaking { get; private set; }
    public bool Update(float? peak,long nowMilliseconds)
    {
        bool oldAvailable=Available,oldSpeaking=Speaking;
        Available=peak.HasValue && float.IsFinite(peak.Value);
        if(!Available) { _lastSignal=long.MinValue; Speaking=false; }
        else
        {
            if(peak!.Value>=0.005f)_lastSignal=nowMilliseconds;
            Speaking=_lastSignal!=long.MinValue && nowMilliseconds-_lastSignal<=250;
        }
        return oldAvailable!=Available || oldSpeaking!=Speaking;
    }
    public void Reset() { _lastSignal=long.MinValue; Available=false; Speaking=false; }
}
