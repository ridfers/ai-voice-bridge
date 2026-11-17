using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using AiVoiceBridge.Core;
namespace AiVoiceBridge.ViewModels;
public sealed class RelayCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter)=>true;
    public void Execute(object? parameter)=>execute();
    public event EventHandler? CanExecuteChanged { add{} remove{} }
}
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly WindowsDevices _devices;
    private readonly IAudioEngine _engine;
    private readonly RawHotkey _hotkey;
    private readonly ISettingsStore _store;
    private readonly Dispatcher _ui=Application.Current.Dispatcher;
    private readonly DispatcherTimer _meter;
    private readonly IAudioActivityMonitor _activity;
    private readonly IMicrophoneControl _microphone;
    private readonly IAudioActivityMonitor _microphoneMeter;
    public LevelIndicator MicLevel { get; }=new();
    public LevelIndicator ToAiLevel { get; }=new();
    public LevelIndicator AiLevel { get; }=new();
    private string? _validationError;
    public string ConnectionDetail=>_validationError??(_engine.State==BridgeState.ConnectionError?_engine.Error:null)??"";
    public System.Windows.Visibility ConnectionDetailVisibility=>string.IsNullOrEmpty(ConnectionDetail)?System.Windows.Visibility.Collapsed:System.Windows.Visibility.Visible;
    private bool? _microphoneMuted;
    public IReadOnlyList<AudioDevice> Microphones { get; private set; }=[];
    public bool MicrophoneAvailable=>_microphoneMuted.HasValue;
    public string MicrophoneLabel=>!MicrophoneAvailable?"Маршрут игры недоступен":_microphoneMuted==true?"Мой голос в игре: ВЫКЛ":"Мой голос в игре: ВКЛ";
    public string MicrophoneColor=>!MicrophoneAvailable?"#55535F":_microphoneMuted==true?"#A33E4B":"#28764C";
    public string MicrophoneName=>Microphones.FirstOrDefault(d=>d.Id==Settings.PhysicalMicrophone)?.Name??"Выберите настоящий микрофон во вкладке «Аудио»";
    public string MicrophoneHint=>!MicrophoneAvailable?MicrophoneName:_microphoneMuted==true?"Включить ваш голос только в игре (B2). Микрофон не отправляется в ChatGPT (B1). Discord использует MCHOSE напрямую.":"Отключить только ваш голос в игровом B2. Голос ИИ продолжает идти в игру.";
    public ICommand MicrophoneCommand { get; }
    private readonly SpeechActivityIndicator _speech=new();
    private CancellationTokenSource? _start;
    private readonly TransmissionControl _transmission=new();
    public bool Busy { get; private set; }
    public bool Visible { get; set; }=true;
    public BridgeSettings Settings { get; }
    public IReadOnlyList<AudioDevice> Devices { get; private set; }=[];
    public IReadOnlyList<CaptureChoice> CaptureChoices=>ProcessTargets.Choices;
    public bool WholeChannel=>Settings.CaptureSource==CaptureSource.All;
    public bool CanChooseSource=>!Busy;
    public string CaptureHint=>WholeChannel?"Все звуки выбранного выхода во вкладке «Аудио». Браузер держите на отдельном выходе.":"Только выбранное приложение. Откройте его, затем нажмите «Запустить».";
    public string[] Hotkeys { get; }=["MOUSE5","F8"];
    public string[] MicModes { get; }=["MOUSE4 — вручную","Открытый микрофон игры"];
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    public string Status=>_validationError!=null?ConnectionExplanation.Title(_validationError):Busy?"Подключение / остановка…":_engine.State switch { BridgeState.Ready=>"Готово",BridgeState.Transmitting=>"Передача звука",BridgeState.ConnectionError=>ConnectionExplanation.Title(_engine.Error),_=>"Остановлено" };
    public string StatusColor=>_engine.State==BridgeState.ConnectionError?"#EC9B9B":IsRunning?"#BEA0EA":"#A3A0B3";
    public bool IsRunning=>!Busy && _engine.State is BridgeState.Ready or BridgeState.Transmitting;
    public string ManualTransmissionLabel=>_transmission.Latched?"Выключить передачу":"Включить передачу";
    public string ManualTransmissionColor=>_transmission.Latched?"#794653":"#373041";
    public string StartLabel=>Busy?"Отменить":IsRunning?"Остановить":"Запустить";
    public string RouteStatus=>IsRunning?"WASAPI Shared · подключён":"Не подключён";
    public string TransmissionStatus=>_engine.State==BridgeState.Transmitting?(_transmission.Latched?"Передаётся · без удержания":"Передаётся · удержание"):"Цифровая тишина";
    public double SignalLevel { get; private set; }
    public string SignalLabel=>$"{SignalLevel:0}% · вход";
    public string Message { get; private set; }="Выберите устройства. Передача выключена.";
    public string AiVoiceLabel=>!Visible?"Индикатор на паузе":!_speech.Available?"Канал ИИ недоступен":_speech.Speaking?"ИИ говорит":"ИИ молчит";
    public string AiVoiceColor=>Visible&&_speech.Available?(_speech.Speaking?"#8BD5A8":"#655D76"):"#55535F";
    public string LoopStatus=>RouteValidation.Validate(Settings,Devices)??"Прямых конфликтов нет. Проверьте внешние связи Voicemeeter.";
    public ICommand ToggleCommand { get; }
    public ICommand ManualTransmissionCommand { get; }
    public ICommand EmergencyCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand SoundCheckCommand { get; }
    private SoundCheck? _soundCheck;
    public ICommand ErrorCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenChatGptCommand { get; }
    public ICommand RemapCommand { get; }
    public ICommand VoicemeeterCommand { get; }
    public MainViewModel(WindowsDevices devices,IAudioEngine engine,RawHotkey hotkey,ISettingsStore store,IAudioActivityMonitor? activity=null,IMicrophoneControl? microphone=null,IAudioActivityMonitor? microphoneMeter=null)
    {
        _devices=devices; _engine=engine; _hotkey=hotkey; _store=store; _activity=activity??new WindowsAudioActivityMonitor(); _microphone=microphone??new WindowsMicrophoneControl(); _microphoneMeter=microphoneMeter??new WindowsAudioActivityMonitor();
        try { Settings=store.Load(); } catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { Settings=new(); Report("Настройки повреждены: загружены значения по умолчанию."); }
        _hotkey.Key=Settings.Hotkey;
        RefreshDevices();
        _meter=new DispatcherTimer(TimeSpan.FromMilliseconds(100),DispatcherPriority.Background,(_,_)=>UpdateMeter(),_ui); _meter.Stop();
        ConfigureActivity();
        RefreshMicrophones();
        _engine.Changed+=()=>_ui.BeginInvoke(RefreshState);
        _hotkey.Pressed+=pressed=> { _transmission.SetRaw(pressed); ApplyGate(); Report($"{Settings.Hotkey}: {(pressed?"нажата":"отпущена")}"); };
        _hotkey.Diagnostic+=Report;
        _hotkey.DeviceLost+=()=> { _=StopAsync(); Report("Устройство ввода отключено. Маршрут остановлен."); };
        _devices.Changed+=()=>_ui.BeginInvoke(async ()=> { await StopAsync(); RefreshDevices(); ConfigureActivity(); RefreshMicrophones(); Report("Изменился список устройств. Проверьте маршруты и запустите заново."); });
        Settings.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName is nameof(Settings.GameVolume) or nameof(Settings.VoiceVolume)) _engine.SetVolumes(Settings.GameVolume,Settings.VoiceVolume);
            if(e.PropertyName==nameof(Settings.CaptureSource)) { _=ChangeCaptureSourceAsync(); }
            if(e.PropertyName==nameof(Settings.BrowserSource)) ConfigureActivity();
            if(e.PropertyName==nameof(Settings.PhysicalMicrophone))ConfigureMicrophone();
            if(e.PropertyName==nameof(Settings.Hotkey)) { _hotkey.Release(); _hotkey.Key=Settings.Hotkey; }
        };
        VoicemeeterCommand=new RelayCommand(async ()=>
        {
            await StopAsync();
            string Find(string name)=>Devices.FirstOrDefault(d=>d.Name.StartsWith(name+" (",StringComparison.OrdinalIgnoreCase))?.Id??"";
            Settings.GameSource=Find("Voicemeeter Input"); Settings.BrowserMicrophone=Find("Voicemeeter VAIO3 Input"); Settings.BrowserSource=Find("Voicemeeter AUX Input");
            Settings.RouteVoice=false; Settings.MonitorVoice=false; Settings.MonitorGame=false;
            Notify(nameof(Settings)); Notify(nameof(LoopStatus));
            Report("Пресет Potato: VAIO → мост → VAIO3/B1; браузер AUX → B2. Настройте шины вручную по README. Автозапуска звука нет.");
        });
        ManualTransmissionCommand=new RelayCommand(()=>
        {
            if(!IsRunning)return;
            _transmission.ToggleLatch(); ApplyGate(); NotifyTransmission();
            Report(_transmission.Latched?"Передача без удержания включена. Нажмите «Выключить передачу» для остановки.":"Передача выключена. Можно снова удерживать MOUSE5.");
        });
        MicrophoneCommand=new RelayCommand(()=>
        {
            try { bool muted=_microphone.Toggle(); Report(muted?"Ваш голос в игре выключен. Голос ИИ остаётся.":"Ваш голос в игре включён."); }
            catch(Exception e) { Report("Микрофон: "+e.Message); }
            RefreshMicrophoneState();
        });
        ToggleCommand=new RelayCommand(async ()=>await ToggleAsync());
        EmergencyCommand=new RelayCommand(async ()=> { await StopAsync(); Report("Все маршруты отключены."); });
        SoundCheckCommand=new RelayCommand(OpenSoundCheck);
        TestCommand=new RelayCommand(()=> { RefreshDevices(); RefreshMicrophones(); Report(LoopStatus+" Для реального теста запустите маршрут и удерживайте экранную кнопку."); Notify(nameof(LoopStatus)); });
        ErrorCommand=EmergencyCommand;
        SaveCommand=new RelayCommand(()=>Save());
        RemapCommand=new RelayCommand(()=>Report("Выберите MOUSE5 или F8. Если драйвер переназначил кнопку, назначьте ему F8. MOUSE4 зарезервирована для игры."));
        OpenChatGptCommand=new RelayCommand(()=> { try { Process.Start(new ProcessStartInfo("https://chatgpt.com/"){UseShellExecute=true}); } catch(Exception e) { Report(e.Message); } });
    }
    private void OpenSoundCheck()
    {
        try
        {
            var output=Devices.FirstOrDefault(d=>d.Id==Settings.BrowserMicrophone)?.Name;
            string? input=output switch
            {
                string name when name.StartsWith("Voicemeeter Input (",StringComparison.OrdinalIgnoreCase) => "Voicemeeter Out B1 (",
                string name when name.StartsWith("CABLE Input (",StringComparison.OrdinalIgnoreCase) => "CABLE Output (",
                _ => null
            };
            _soundCheck?.Dispose();
            _soundCheck=new SoundCheck(input,"Voicemeeter Out B2 (");
            Process.Start(new ProcessStartInfo(_soundCheck.Url){UseShellExecute=true});
        }
        catch(Exception e) { _soundCheck?.Dispose(); _soundCheck=null; Report("Проверка звука: "+e.Message); }
    }

    public void Report(string message) { if(!_ui.CheckAccess()) { _ui.BeginInvoke(()=>Report(message)); return; } Message=message; Notify(nameof(Message)); }
    private void RefreshDevices() { try { Devices=_devices.GetDevices(); Notify(nameof(Devices)); } catch(Exception e) { Report(e.Message); } }
    private async Task ToggleAsync()
    {
        if(Busy) { _start?.Cancel(); return; }
        if(IsRunning) { await StopAsync(); return; }
        var error=RouteValidation.Validate(Settings,Devices); if(error!=null) { _validationError=error; RefreshState(); Report(error); return; }
        _validationError=null;
        Busy=true; _transmission.Reset(); NotifyTransmission(); _hotkey.Release(); _start=new(); RefreshState();
        // Immutable snapshot prevents UI edits racing with audio initialization.
        var snapshot=JsonSerializer.Deserialize<BridgeSettings>(JsonSerializer.Serialize(Settings))!;
        try { await _engine.StartAsync(snapshot,_start.Token); if(_engine.State==BridgeState.Ready)Report(ProcessTargets.Name(Settings.CaptureSource)+": готово. Удерживайте MOUSE5 или нажмите «Включить передачу»."); }
        catch(OperationCanceledException) { await _engine.StopAsync(); Report("Запуск отменён."); }
        finally { _start.Dispose(); _start=null; Busy=false; RefreshState(); }
    }
    public async Task StopAsync()
    {
        _validationError=null; _start?.Cancel(); _transmission.Reset(); NotifyTransmission(); _hotkey.Release(); _engine.SetTransmission(false); _meter.Stop();
        await _engine.StopAsync(); RefreshState();
    }
    private void ApplyGate()=>_engine.SetTransmission(!Busy&&IsRunning&&_transmission.Requested);
    private void NotifyTransmission() { Notify(nameof(ManualTransmissionLabel)); Notify(nameof(ManualTransmissionColor)); Notify(nameof(TransmissionStatus)); }
    public void BeginDemo() { _transmission.SetLocal(true); ApplyGate(); }
    public void ReleaseDemo() { _transmission.SetLocal(false); ApplyGate(); }
    private async Task ChangeCaptureSourceAsync()
    {
        Notify(nameof(WholeChannel)); Notify(nameof(CaptureHint)); Notify(nameof(LoopStatus));
        await StopAsync();
        Report(ProcessTargets.Name(Settings.CaptureSource)+": источник выбран. Нажмите «Запустить», затем включите передачу.");
    }
    public void AudioSelectionChanged() { _=StopAsync(); Notify(nameof(LoopStatus)); }
    private void RefreshState()
    {
        if(_engine.State is BridgeState.Stopped or BridgeState.ConnectionError) { _transmission.Reset(); NotifyTransmission(); }
        foreach(var name in new[]{nameof(ConnectionDetail),nameof(ConnectionDetailVisibility),nameof(CanChooseSource),nameof(Status),nameof(StatusColor),nameof(IsRunning),nameof(StartLabel),nameof(RouteStatus),nameof(TransmissionStatus)})Notify(name);
        if(_engine.Error!=null) Report(_engine.Error);
        UpdateTimer();
        if(!IsRunning) { SignalLevel=0; Notify(nameof(SignalLevel)); Notify(nameof(SignalLabel)); }
    }
    public void SetVisible(bool visible) { Visible=visible; if(visible)RefreshMicrophoneState(); if(visible)_speech.Update(_activity.ReadPeak(),Environment.TickCount64); RefreshState(); }
    private void NotifyActivity() { Notify(nameof(AiVoiceLabel)); Notify(nameof(AiVoiceColor)); }
    private void ConfigureActivity()
    {
        _activity.Select(Settings.BrowserSource); _speech.Reset();
        _speech.Update(_activity.ReadPeak(),Environment.TickCount64);
        NotifyActivity(); UpdateTimer();
    }
    private void RefreshMicrophones()
    {
        try { Microphones=_devices.GetMicrophones(); } catch { Microphones=[]; }
        Notify(nameof(Microphones));
        // Initial selection only. A saved device is never silently replaced.
        if(string.IsNullOrEmpty(Settings.PhysicalMicrophone))
        {
            var physical=Microphones.Where(d=>!d.Name.Contains("Voicemeeter",StringComparison.OrdinalIgnoreCase)&&!d.Name.Contains("CABLE",StringComparison.OrdinalIgnoreCase)&&!d.Name.Contains("Virtual",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(physical.Length==1)Settings.PhysicalMicrophone=physical[0].Id;
        }
        ConfigureMicrophone();
    }
    private void ConfigureMicrophone() { _microphone.Select(Settings.PhysicalMicrophone); _microphoneMeter.Select(Settings.PhysicalMicrophone); Notify(nameof(MicrophoneName)); RefreshMicrophoneState(); UpdateTimer(); }
    private void RefreshMicrophoneState()
    {
        _microphoneMuted=_microphone.ReadMuted();
        foreach(var name in new[]{nameof(MicrophoneAvailable),nameof(MicrophoneLabel),nameof(MicrophoneColor),nameof(MicrophoneHint)})Notify(name);
    }
    private void UpdateTimer()
    {
        if(Visible&&(IsRunning||_activity.Available||MicrophoneAvailable)) _meter.Start();
        else _meter.Stop();
        if(!Visible)_speech.Reset();
        NotifyActivity();
    }
    public void UpdateMeter()
    {
        if(_microphone.ReadMuted()!=_microphoneMuted)RefreshMicrophoneState();
        var aiPeak=_activity.ReadPeak();
        if(_speech.Update(aiPeak,Environment.TickCount64))NotifyActivity();
        var now=Environment.TickCount64;
        MicLevel.Update(_microphoneMeter.ReadPeak()*100,now);
        ToAiLevel.Update(IsRunning?_engine.Peak*Settings.GameVolume/100:null,now);
        AiLevel.Update(aiPeak*100,now);
        Notify(nameof(MicLevel)); Notify(nameof(ToAiLevel)); Notify(nameof(AiLevel));
        var level=IsRunning?Math.Round(_engine.Peak):0;
        if(level!=SignalLevel) { SignalLevel=level; Notify(nameof(SignalLevel)); Notify(nameof(SignalLabel)); }
        if(!IsRunning&&!_activity.Available&&!MicrophoneAvailable)_meter.Stop();
    }    public bool Save()
    {
        try
        {
            using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(Settings.StartWithWindows) run.SetValue("AI Voice Bridge",'"'+Environment.ProcessPath+'"'); else run.DeleteValue("AI Voice Bridge",false);
            if(Settings.SaveSettings)_store.Save(Settings); else _store.Delete();
            Report("Настройки применены. Автозапуск настроен для текущего пользователя."); return true;
        }
        catch(Exception e) { Report("Сохранение: "+e.Message); return false; }
    }
    public void DetachMeter() { _soundCheck?.Dispose(); _meter.Stop(); _activity.Dispose(); _microphone.Dispose(); _microphoneMeter.Dispose(); _speech.Reset(); }
}













