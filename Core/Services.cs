using System.IO;
using System.Text.Json;
namespace AiVoiceBridge.Core;

public enum BridgeState { Stopped, Ready, Transmitting, ConnectionError }
public sealed record AudioDevice(string Id, string Name) { public override string ToString() => Name; }
public sealed class BridgeSettings : System.ComponentModel.INotifyPropertyChanged
{
    private string _physicalMicrophone="";
    public string PhysicalMicrophone { get => _physicalMicrophone; set { if(_physicalMicrophone==value)return; _physicalMicrophone=value; PropertyChanged?.Invoke(this,new(nameof(PhysicalMicrophone))); } }
    private CaptureSource _captureSource;
    public CaptureSource CaptureSource { get => _captureSource; set { if(_captureSource==value)return; _captureSource=value; PropertyChanged?.Invoke(this,new(nameof(CaptureSource))); } }
    private bool _routeVoice; public bool RouteVoice { get => _routeVoice; set { _routeVoice=value; PropertyChanged?.Invoke(this,new(nameof(RouteVoice))); } }
    public bool MonitorVoice { get; set; }
    public bool MonitorGame { get; set; }
    public string GameMicMode { get; set; } = "MOUSE4 — вручную";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private string _hotkey = "MOUSE5";
    public string Hotkey { get => _hotkey; set { if (_hotkey == value) return; _hotkey = value; PropertyChanged?.Invoke(this, new(nameof(Hotkey))); } }
    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set { if (_startWithWindows == value) return; _startWithWindows = value; PropertyChanged?.Invoke(this, new(nameof(StartWithWindows))); } }
    private bool _minimizeToTray;
    public bool MinimizeToTray { get => _minimizeToTray; set { if (_minimizeToTray == value) return; _minimizeToTray = value; PropertyChanged?.Invoke(this, new(nameof(MinimizeToTray))); } }
    private bool _startMinimized;
    public bool StartMinimized { get => _startMinimized; set { if (_startMinimized == value) return; _startMinimized = value; PropertyChanged?.Invoke(this, new(nameof(StartMinimized))); } }
    private bool _saveSettings = true;
    public bool SaveSettings { get => _saveSettings; set { if (_saveSettings == value) return; _saveSettings = value; PropertyChanged?.Invoke(this, new(nameof(SaveSettings))); } }
    private double _gameVolume = 75;
    public double GameVolume { get => _gameVolume; set { if (_gameVolume == value) return; _gameVolume = value; PropertyChanged?.Invoke(this, new(nameof(GameVolume))); } }
    private double _voiceVolume = 80;
    public double VoiceVolume { get => _voiceVolume; set { if (_voiceVolume == value) return; _voiceVolume = value; PropertyChanged?.Invoke(this, new(nameof(VoiceVolume))); } }
    private string _gameSource = "game";
    public string GameSource { get => _gameSource; set { if (_gameSource == value) return; _gameSource = value; PropertyChanged?.Invoke(this, new(nameof(GameSource))); } }
    private string _browserMicrophone = "cable-a";
    public string BrowserMicrophone { get => _browserMicrophone; set { if (_browserMicrophone == value) return; _browserMicrophone = value; PropertyChanged?.Invoke(this, new(nameof(BrowserMicrophone))); } }
    private string _browserSource = "browser";
    public string BrowserSource { get => _browserSource; set { if (_browserSource == value) return; _browserSource = value; PropertyChanged?.Invoke(this, new(nameof(BrowserSource))); } }
    private string _gameMicrophone = "cable-b";
    public string GameMicrophone { get => _gameMicrophone; set { if (_gameMicrophone == value) return; _gameMicrophone = value; PropertyChanged?.Invoke(this, new(nameof(GameMicrophone))); } }
    private string _headphones = "headphones";
    public string Headphones { get => _headphones; set { if (_headphones == value) return; _headphones = value; PropertyChanged?.Invoke(this, new(nameof(Headphones))); } }
}
public interface IAudioDeviceService { IReadOnlyList<AudioDevice> GetDevices(); }
public interface ISettingsStore { BridgeSettings Load(); void Save(BridgeSettings settings); void Delete(); }
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public BridgeSettings Load()
    {
        if (!File.Exists(path)) return new();
        var s = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path)) ?? new();
        if (s.Hotkey != "F8") s.Hotkey = "MOUSE5";
        s.GameVolume = double.IsFinite(s.GameVolume) ? Math.Clamp(s.GameVolume, 0, 300) : 75;
        s.VoiceVolume = double.IsFinite(s.VoiceVolume) ? Math.Clamp(s.VoiceVolume, 0, 100) : 80;
        return s;
    }
    public void Save(BridgeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public void Delete() { if (File.Exists(path)) File.Delete(path); }
}








