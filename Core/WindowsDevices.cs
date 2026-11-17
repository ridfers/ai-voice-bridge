using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace AiVoiceBridge.Core;
public sealed class WindowsDevices : IAudioDeviceService, IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    public event Action? Changed;
    public WindowsDevices() => _enumerator.RegisterEndpointNotificationCallback(this);
    public IReadOnlyList<AudioDevice> GetDevices()
    {
        var result = new List<AudioDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) result.Add(new(device.ID, device.FriendlyName));
        return result;
    }
    public IReadOnlyList<AudioDevice> GetMicrophones()
    {
        var result=new List<AudioDevice>();
        foreach(var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))
            using(device)result.Add(new(device.ID,device.FriendlyName));
        return result;
    }
    public void OnDeviceStateChanged(string id, DeviceState state) => Changed?.Invoke();
    public void OnDeviceAdded(string id) => Changed?.Invoke();
    public void OnDeviceRemoved(string id) => Changed?.Invoke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
    public void Dispose() { _enumerator.UnregisterEndpointNotificationCallback(this); _enumerator.Dispose(); }
}
public static class RouteValidation
{
    public static string? Validate(BridgeSettings s, IReadOnlyList<AudioDevice> devices)
    {
        if(!Enum.IsDefined(s.CaptureSource))return "Выберите источник звука для ИИ.";
        bool wholeChannel=s.CaptureSource==CaptureSource.All;
        var ids = new List<string> { s.BrowserMicrophone };
        if(wholeChannel)ids.Add(s.GameSource);
        if (s.RouteVoice) { ids.Add(s.BrowserSource); ids.Add(s.GameMicrophone); }
        if (s.MonitorVoice) { if (!s.RouteVoice) return "Сначала включите маршрут голоса браузера."; ids.Add(s.Headphones); }
        if (s.MonitorGame) ids.Add(s.Headphones);
        if (ids.Any(id => !devices.Any(d => d.Id == id))) return "Выберите все необходимые активные устройства. Автоподмена отключённого устройства запрещена.";
        if (wholeChannel && s.GameSource == s.BrowserMicrophone) return "Источник игры совпадает с выходом ChatGPT: петля.";
        if (s.RouteVoice && ((wholeChannel && s.BrowserSource == s.GameSource) || s.BrowserSource == s.BrowserMicrophone || (wholeChannel && s.GameMicrophone == s.GameSource) || s.GameMicrophone == s.BrowserMicrophone || s.GameMicrophone == s.BrowserSource)) return "Входящий и исходящий маршруты должны использовать разные устройства.";
        if ((s.MonitorVoice || s.MonitorGame) && ((wholeChannel && s.Headphones == s.GameSource) || s.Headphones == s.BrowserMicrophone || (s.RouteVoice && (s.Headphones == s.BrowserSource || s.Headphones == s.GameMicrophone)))) return "Выход мониторинга совпадает с маршрутом: отключите мониторинг или выберите отдельные наушники.";
        return null;
    }
}


