using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiVoiceBridge.Core;

if (args.Contains("--sound-check-web"))
{
    var assembly = typeof(AiVoiceBridge.Core.LevelIndicator).Assembly;
    var type = assembly.GetType("AiVoiceBridge.Core.SoundCheck", throwOnError:true)!;
    using var server = (IDisposable)Activator.CreateInstance(type, "Voicemeeter Out B1 (", "Voicemeeter Out B2 (")!;
    var url = new Uri((string)type.GetProperty("Url")!.GetValue(server)!);
    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    var html = await http.GetStringAsync(url);
    if (!url.IsLoopback || !html.Contains("Игра → вход ChatGPT") || !html.Contains("Голос ИИ → микрофон игры"))
        throw new InvalidOperationException("The sound check page is missing its two routes.");
    using var bad = await http.GetAsync(new Uri(url, "/levels"));
    if (bad.StatusCode != System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("Missing session token accepted.");
    _ = await http.GetStringAsync(new Uri(url, "levels"));
    await Task.Delay(350);
    var json = await http.GetStringAsync(new Uri(url, "levels"));
    using (var capturedLevels = System.Text.Json.JsonDocument.Parse(json))
    {
        foreach (var (key, name) in new[] { ("input", "Voicemeeter Out B1"), ("game", "Voicemeeter Out B2") })
        {
            var channel = capturedLevels.RootElement.GetProperty(key);
            if (channel.GetProperty("device").GetString()?.StartsWith(name) != true ||
                channel.GetProperty("error").ValueKind != System.Text.Json.JsonValueKind.Null)
                throw new InvalidOperationException($"Failed to capture {name}: {channel}");
        }
    }
    Console.WriteLine("PASS local page, token isolation and live B1/B2 capture");
    await Task.Delay(12500);
    _ = await http.GetStringAsync(new Uri(url, "levels"));
    Console.WriteLine("PASS page resumes after idle timeout");
    return;
}
if(ApplicationCaptureTests.Helper(args))return;
var count=0;
void Check(bool condition,string name) { if(!condition)throw new Exception(name); Console.WriteLine("PASS "+name); count++; }
var devices=new[]{new AudioDevice("game","Game"),new AudioDevice("to-ai","AI cable"),new AudioDevice("browser","Browser"),new AudioDevice("to-game","Game cable"),new AudioDevice("phones","Headphones")};
var s=new BridgeSettings{GameSource="game",BrowserMicrophone="to-ai",BrowserSource="browser",GameMicrophone="to-game",Headphones="phones"};
Check(RouteValidation.Validate(s,devices)==null,"Valid one-way route");
s.BrowserMicrophone="game"; Check(RouteValidation.Validate(s,devices)!=null,"Reject direct loop"); s.BrowserMicrophone="to-ai";
s.RouteVoice=true; Check(RouteValidation.Validate(s,devices)==null,"Separated bidirectional route");
s.BrowserSource="game"; Check(RouteValidation.Validate(s,devices)!=null,"Reject browser feedback"); s.BrowserSource="browser";
s.MonitorVoice=true; s.Headphones="to-ai"; Check(RouteValidation.Validate(s,devices)!=null,"Reject monitoring into AI input"); s.Headphones="phones";
Check(RouteValidation.Validate(s,devices)==null,"Separate monitoring accepted");
s.GameSource="missing"; Check(RouteValidation.Validate(s,devices)!=null,"No fallback for missing endpoint");
s.CaptureSource=CaptureSource.Discord;
Check(RouteValidation.Validate(s,devices)==null,"Process capture does not require endpoint source");
s.CaptureSource=(CaptureSource)999;Check(RouteValidation.Validate(s,devices)!=null,"Unknown capture mode rejected without fallback");
s.CaptureSource=CaptureSource.All;
var processTree=new[]{new ProcessEntry(10,1,"Discord.exe"),new ProcessEntry(11,10,"Discord.exe"),new ProcessEntry(12,11,"Discord.exe"),new ProcessEntry(20,1,"cs2.exe"),new ProcessEntry(21,1,"dota2.exe"),new ProcessEntry(30,1,"firefox.exe")};
Check(ProcessTargets.Roots(CaptureSource.Discord,processTree).SequenceEqual(new[]{10}),"Discord child audio processes included via single root");
Check(ProcessTargets.Roots(CaptureSource.CS2,processTree).SequenceEqual(new[]{20}),"CS2 exact process name selected");
Check(ProcessTargets.Roots(CaptureSource.Dota2,processTree).SequenceEqual(new[]{21}),"Dota 2 exact process name selected");
Check(ProcessTargets.Roots(CaptureSource.Discord,[]).Length==0,"Missing app never falls back to another process");
var modeFile=Path.Combine(Path.GetTempPath(),"voicebridge-mode-"+Guid.NewGuid()+".json");var modeStore=new JsonSettingsStore(modeFile);
try { modeStore.Save(new BridgeSettings{CaptureSource=CaptureSource.Dota2});Check(modeStore.Load().CaptureSource==CaptureSource.Dota2,"Capture selection persisted");File.WriteAllText(modeFile,"{}");Check(modeStore.Load().CaptureSource==CaptureSource.All,"Existing settings keep endpoint capture"); } finally { modeStore.Delete(); }
var levels=new LevelIndicator();levels.Update(null,0);Check(!levels.Available&&levels.Level==0,"Unavailable meter never reports live zero");
levels.Update(105,100);Check(levels.Level==100&&levels.Overload,"Amplified peak triggers overload and bounded display");
levels.Update(10,1500);Check(levels.Overload,"Brief overload held visibly");levels.Update(10,1601);Check(!levels.Overload,"Overload hold expires");
levels.Update(double.NaN,1700);Check(!levels.Available&&!levels.Overload,"Invalid meter clears overload");
Check(ConnectionExplanation.Title("Только CS2: приложение не запущено.")=="Запустите CS2","Missing CS2 has actionable title");
Check(ConnectionExplanation.Title("Только Dota 2: приложение не запущено.")=="Запустите Dota 2","Missing Dota has actionable title");
var ring=new GatedBuffer(48000,2); var samples=Enumerable.Repeat(0.5f,960).ToArray(); var bytes=new byte[samples.Length*4]; Buffer.BlockCopy(samples,0,bytes,0,bytes.Length); var output=new float[960];
ring.Add(bytes,bytes.Length); ring.Read(output,0,output.Length); Check(output.All(x=>x==0),"Silent before explicit gate");
ring.Gate(true); ring.Add(bytes,bytes.Length); ring.Read(output,0,output.Length); Check(output.Max()>0.49f && output[0]<0.01f,"Gate opens with ramp");
ring.Gate(false); ring.Read(output,0,output.Length); Check(output.Skip(300).All(x=>x==0),"Release clears queue and fades in 3 ms");
ring.Gate(true); ring.Read(output,0,output.Length); Check(output.All(x=>x==0),"No stale audio on reopen");
for(int i=0;i<1000;i++) { ring.Gate(true); ring.Add(bytes,bytes.Length); ring.Gate(false); }
ring.Read(output,0,output.Length); Check(output.Skip(300).All(x=>x==0),"1000 rapid press/release cycles");
ring.Gate(true); for(int i=0;i<30;i++)ring.Add(bytes,bytes.Length); Check(ring.Overflows>0,"Overflow bounded and counted");
ring.Volume=0; ring.Read(output,0,output.Length); Check(output.All(x=>x==0),"Zero volume");
var file=Path.Combine(Path.GetTempPath(),"voicebridge-"+Guid.NewGuid()+".json"); var store=new JsonSettingsStore(file);
try { store.Save(new BridgeSettings{Hotkey="F8",GameVolume=150,RouteVoice=true}); var read=store.Load(); Check(read.Hotkey=="F8"&&read.RouteVoice&&read.GameVolume==150,"Settings persistence and clamp"); store.Save(new BridgeSettings{Hotkey="MOUSE4"}); Check(store.Load().Hotkey=="MOUSE5","MOUSE4 reserved"); } finally { store.Delete(); }
var engine=new AudioEngine(); engine.SetTransmission(true); Check(engine.State==BridgeState.Stopped,"Cannot transmit before start"); await engine.StopAsync(); await engine.StopAsync(); Check(engine.State==BridgeState.Stopped,"Repeated stop is safe");
using var real=new WindowsDevices(); var endpoints=real.GetDevices(); Console.WriteLine("Real render endpoints: "+endpoints.Count); foreach(var d in endpoints)Console.WriteLine(d.Name+" | "+d.Id);
if(args.Contains("--process-capture"))await ApplicationCaptureTests.Run(Check,endpoints.First(d=>d.Name.Contains("MCHOSE")).Id);
Console.WriteLine($"{count} checks passed.");

if(args.Contains("--hardware-smoke"))
{
    var source=endpoints.First(d=>d.Name.StartsWith("Voicemeeter Input"));
    var destination=endpoints.First(d=>d.Name.Contains("MCHOSE"));
    var config=new BridgeSettings{GameSource=source.Id,BrowserMicrophone=destination.Id,GameVolume=0};
    for(int cycle=0;cycle<3;cycle++)
    {
        await engine.StartAsync(config,default);
        Check(engine.State==BridgeState.Ready,"Real WASAPI open cycle "+cycle+" "+engine.Error);
        for(int press=0;press<100;press++){engine.SetTransmission(true);engine.SetTransmission(false);}
        await Task.Delay(300);
        Check(engine.State==BridgeState.Ready,"Real streams survive rapid gates "+cycle+" "+engine.Error);
        await engine.StopAsync(); Check(engine.State==BridgeState.Stopped,"Real WASAPI disposal "+cycle);
    }
    Console.WriteLine($"{count} checks passed including hardware smoke (zero gain, no ChatGPT route).");
}

if(args.Contains("--measure"))
{
    async Task Measure(string label)
    {
        using var process=System.Diagnostics.Process.GetCurrentProcess();
        var cpu=process.TotalProcessorTime.TotalMilliseconds; var watch=System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(3000); process.Refresh();
        Console.WriteLine($"MEASURE {label}: wall={watch.ElapsedMilliseconds}ms cpu={process.TotalProcessorTime.TotalMilliseconds-cpu:F2}ms workingSet={process.WorkingSet64/1048576.0:F1}MiB private={process.PrivateMemorySize64/1048576.0:F1}MiB");
    }
    await Measure("stopped test host");
    var config=new BridgeSettings{GameSource=endpoints.First(d=>d.Name.StartsWith("Voicemeeter Input")).Id,BrowserMicrophone=endpoints.First(d=>d.Name.Contains("MCHOSE")).Id,GameVolume=0};
    await engine.StartAsync(config,default); Check(engine.State==BridgeState.Ready,"Measurement stream initialized");
    await Measure("ready capture open gate closed"); engine.SetTransmission(true);
    await Measure("gate open zero volume no injected source"); await engine.StopAsync();
}

if(args.Contains("--banana-open"))
{
 var config=new BridgeSettings{GameSource=endpoints.First(d=>d.Name.Contains("MCHOSE")).Id,BrowserMicrophone=endpoints.First(d=>d.Name.StartsWith("Voicemeeter Input (")).Id,GameVolume=0};
 await engine.StartAsync(config,default);
 Check(engine.State==BridgeState.Ready,"Banana physical-to-virtual route opens: "+engine.Error);
 await Task.Delay(400); Check(engine.State==BridgeState.Ready,"Banana route remains ready with transmission closed: "+engine.Error); await engine.StopAsync();
}


if(args.Contains("--continuity"))
{
 using var enumerator=new NAudio.CoreAudioApi.MMDeviceEnumerator();
 using var input=enumerator.GetDevice(endpoints.First(d=>d.Name.Contains("MCHOSE")).Id);
 using var destination=enumerator.GetDevice(endpoints.First(d=>d.Name.StartsWith("Voicemeeter Input (")).Id);
 using var inputClient=input.AudioClient;using var outputClient=destination.AudioClient;
 var sourceFormat=inputClient.MixFormat;var targetFormat=outputClient.MixFormat;
 Console.WriteLine($"CONTINUITY {sourceFormat} -> {targetFormat}");
 var realtime=new GatedBuffer(sourceFormat.SampleRate,sourceFormat.Channels);realtime.Gate(true);
 var converter=new LiveOutputProvider(realtime,targetFormat);

 var packet=Enumerable.Repeat(.25f,sourceFormat.SampleRate*sourceFormat.Channels/50).ToArray();
 var frame=new float[targetFormat.SampleRate*targetFormat.Channels/100];
 var rawFrame=new byte[frame.Length*4];
 int audible=0,total=0;
 for(int tick=0;tick<200;tick++)
 {
  if(tick%2==0)realtime.Add(packet.AsSpan());
  converter.Read(rawFrame,0,rawFrame.Length); Buffer.BlockCopy(rawFrame,0,frame,0,rawFrame.Length);
  if(tick<20)continue;
  for(int j=0;j<frame.Length;j+=targetFormat.Channels){total++;if(Math.Abs(frame[j])>.01)audible++;}
 }
 Console.WriteLine($"CONTINUITY nonzero front channel {audible}/{total} = {(double)audible/total:P1}");
 Check((double)audible/total>.99,"Continuous live conversion preserves signal");
}



foreach(int bits in new[]{16,24,32})
{
 var format=new NAudio.Wave.WaveFormat(48000,bits,2);
 var source=new GatedBuffer(48000,2);source.Gate(true);source.Add(Enumerable.Repeat(.25f,960).ToArray().AsSpan());
 var pack=new LiveOutputProvider(source,format);var data=new byte[480*format.BlockAlign];pack.Read(data,0,data.Length);
 var decoded=new float[960];PcmDecoder.Decode(data,decoded,format);
 Check(decoded.Skip(400).All(v=>Math.Abs(v-.25f)<.001),$"PCM {bits}-bit packing and decoding");
}
var boosted=new GatedBuffer(48000,2){Volume=3};boosted.Gate(true);boosted.Add(Enumerable.Repeat(.75f,960).ToArray().AsSpan());
var boostedOutput=new LiveOutputProvider(boosted,NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000,2));
var boostedBytes=new byte[960*4];boostedOutput.Read(boostedBytes,0,boostedBytes.Length);var boostedSamples=new float[960];Buffer.BlockCopy(boostedBytes,0,boostedSamples,0,boostedBytes.Length);
Check(boostedSamples.All(v=>v>=-1&&v<=1)&&boostedSamples.Skip(400).All(v=>v==1),"Boost above unity saturates within valid sample range");

var transmission=new TransmissionControl();
Check(!transmission.Requested&&!transmission.Latched,"Transmission defaults off");
transmission.ToggleLatch(); Check(transmission.Requested&&transmission.Latched,"One click latches transmission");
transmission.SetLocal(false); transmission.SetRaw(false); Check(transmission.Requested,"Releasing PTT or local focus does not cancel manual mode");
transmission.SetRaw(true); transmission.SetRaw(false); Check(transmission.Requested,"PTT press/release preserves manual mode");
transmission.SetRaw(true); transmission.ToggleLatch(); Check(!transmission.Requested&&!transmission.Latched,"Off click stops transmission even when PTT was held");
transmission.SetRaw(false); transmission.SetRaw(true); Check(transmission.Requested&&!transmission.Latched,"PTT works again after manual off");
transmission.SetRaw(false);Check(!transmission.Requested,"PTT release closes transmission");
transmission.ToggleLatch();transmission.SetLocal(true);transmission.SetRaw(true);transmission.Reset();
Check(!transmission.Requested&&!transmission.Latched,"Emergency stop clears all transmission sources");
for(int i=0;i<100;i++){transmission.ToggleLatch();transmission.ToggleLatch();}
Check(!transmission.Requested,"Repeated toggle cycles finish off");
Console.WriteLine($"TOTAL {count} checks passed.");


var speech=new SpeechActivityIndicator();
Check(speech.Update(0,0)&&speech.Available&&!speech.Speaking,"AI meter reports available silence");
Check(!speech.Update(.001f,10),"AI meter ignores low noise without extra UI changes");
Check(speech.Update(.1f,100)&&speech.Speaking,"AI lamp lights for audio signal");
Check(!speech.Update(0,300)&&speech.Speaking,"AI lamp bridges short speech pauses");
Check(speech.Update(0,351)&&!speech.Speaking,"AI lamp turns off after speech hold");
speech.Update(.2f,500);Check(speech.Update(null,501)&&!speech.Available&&!speech.Speaking,"Disconnected channel clears speech indication");
speech.Update(.2f,600);speech.Reset();Check(!speech.Speaking&&!speech.Available,"Suspending indicator clears stale activity");
Check(speech.Update(float.NaN,700)==false,"Invalid signal never lights the lamp");
using(var endpointMeter=new WindowsAudioActivityMonitor())
{
 endpointMeter.Select(endpoints.First(d=>d.Name.StartsWith("Voicemeeter AUX Input (")).Id);
 var peak=endpointMeter.ReadPeak();Check(peak.HasValue&&peak.Value>=0&&peak.Value<=1,"Real AUX endpoint peak readable without capture");
 endpointMeter.Select("missing-endpoint");Check(!endpointMeter.Available&&endpointMeter.ReadPeak()==null,"Missing meter endpoint safely unavailable");
}
Exception? uiFailure=null;
var uiThread=new System.Threading.Thread(()=>
{
 try
 {
  var app=new AiVoiceBridge.App();app.InitializeComponent();
  using var hardware=new WindowsDevices();using var key=new RawHotkey();
  var fake=new TestAudioEngine();
  var meter=new TestActivityMonitor();
  var mic=new TestMicrophoneControl { Muted=true };
  var micMeter=new TestActivityMonitor { Peak=.25f };
  var vm=new AiVoiceBridge.ViewModels.MainViewModel(hardware,fake,key,new TestSettingsStore(),meter,mic,micMeter);
  Check(mic.Toggles==0&&vm.MicrophoneLabel=="Мой голос в игре: ВЫКЛ"&&vm.MicrophoneColor=="#A33E4B","Startup reflects muted microphone without unmuting");
  vm.MicrophoneCommand.Execute(null);
  Check(mic.Muted==false&&vm.MicrophoneColor=="#28764C"&&fake.State==BridgeState.Stopped,"Mic button unmutes while bridge stopped");
  vm.MicrophoneCommand.Execute(null);
  Check(mic.Muted==true&&vm.MicrophoneColor=="#A33E4B","Mic button mutes and turns red");
  mic.Muted=false;vm.SetVisible(true);Check(vm.MicrophoneLabel=="Мой голос в игре: ВКЛ","External microphone state reflected on restore");
  mic.Muted=null;vm.SetVisible(true);Check(!vm.MicrophoneAvailable&&vm.MicrophoneColor=="#55535F","Unavailable mic is disabled and never falsely green");
  mic.Muted=true;vm.SetVisible(true);
  Check(vm.AiVoiceLabel=="ИИ молчит","UI lamp works when bridge is stopped");
  meter.Peak=.2f;vm.Settings.BrowserSource="other-source";
  Check(vm.AiVoiceLabel=="ИИ говорит"&&meter.Selected=="other-source","UI lamp follows selected browser output");
  vm.ManualTransmissionCommand.Execute(null);
  Check(fake.State==BridgeState.Stopped,"UI manual command cannot start stopped audio");
  fake.SetReady();
  vm.ManualTransmissionCommand.Execute(null);
  Check(fake.State==BridgeState.Transmitting&&vm.ManualTransmissionLabel=="Выключить передачу","UI command enables manual transmission");
  vm.ReleaseDemo();vm.SetVisible(false);
  Check(fake.State==BridgeState.Transmitting,"UI focus release and minimizing preserve manual transmission");
  vm.ManualTransmissionCommand.Execute(null);
  Check(fake.State==BridgeState.Ready&&vm.ManualTransmissionLabel=="Включить передачу","UI command disables manual transmission");
  vm.BeginDemo();vm.ReleaseDemo();Check(fake.State==BridgeState.Ready,"UI PTT still closes on release");
  vm.ManualTransmissionCommand.Execute(null);vm.EmergencyCommand.Execute(null);
  Check(fake.State==BridgeState.Stopped&&vm.ManualTransmissionLabel=="Включить передачу","UI emergency command resets latch");
  fake.SetReady();vm.ReleaseDemo();Check(fake.State==BridgeState.Ready,"UI restarting never restores manual transmission");
  Check(vm.AiVoiceLabel=="Индикатор на паузе","Hidden window does not claim live speech activity");
  vm.SetVisible(true);Check(vm.AiVoiceLabel=="ИИ говорит","Restoring window refreshes actual activity");
  fake.SetReady();vm.ManualTransmissionCommand.Execute(null);vm.Settings.CaptureSource=CaptureSource.Discord;
  Check(fake.State==BridgeState.Stopped&&vm.ManualTransmissionLabel=="Включить передачу","Changing source stops stream and clears manual latch");
  Check(!vm.WholeChannel&&vm.CaptureChoices.Count==4,"UI offers four modes and disables unused endpoint source");
  vm.UpdateMeter();Check(vm.MicLevel.Level==25&&!vm.ToAiLevel.Available&&vm.AiLevel.Level==20,"Physical mic level stays live when game route muted");
  mic.Muted=false;vm.UpdateMeter();Check(vm.MicLevel.Level==25,"Physical microphone level comes from selected meter");mic.Muted=true;
  vm.DetachMeter();Check(meter.Disposed,"Closing releases endpoint meter");
  Check(mic.Disposed&&mic.Muted==true&&mic.Toggles==2,"Closing preserves mute state and releases controller");
 }
 catch(Exception e){uiFailure=e;}
});
uiThread.SetApartmentState(System.Threading.ApartmentState.STA);uiThread.Start();uiThread.Join();
if(uiFailure!=null)throw new Exception("UI controls integration",uiFailure);
Console.WriteLine($"FINAL TOTAL {count} checks passed.");

sealed class TestSettingsStore : ISettingsStore
{
 public BridgeSettings Load()=>new();
 public void Save(BridgeSettings settings){}
 public void Delete(){}
}
sealed class TestAudioEngine : IAudioEngine
{
 public BridgeState State {get;private set;}
 public string? Error=>null;
 public double Peak=>0;
 public event Action? Changed;
 public void SetReady(){State=BridgeState.Ready;Changed?.Invoke();}
 public Task StartAsync(BridgeSettings settings,System.Threading.CancellationToken cancellation){SetReady();return Task.CompletedTask;}
 public Task StopAsync(){State=BridgeState.Stopped;Changed?.Invoke();return Task.CompletedTask;}
 public void SetTransmission(bool active){if(State is BridgeState.Ready or BridgeState.Transmitting)State=active?BridgeState.Transmitting:BridgeState.Ready;Changed?.Invoke();}
 public void SetVolumes(double game,double voice){}
}

sealed class TestActivityMonitor : IAudioActivityMonitor
{
 public float? Peak {get;set;}=0;
 public bool Available=>!Disposed;
 public string? Selected;
 public bool Disposed;
 public void Select(string deviceId){Selected=deviceId;Disposed=false;}
 public float? ReadPeak()=>Disposed?null:Peak;
 public void Dispose()=>Disposed=true;
}



sealed class TestMicrophoneControl : IMicrophoneControl
{
 public bool? Muted;
 public int Toggles;
 public bool Disposed;
 public void Select(string deviceId) { }
 public bool? ReadMuted()=>Muted;
 public bool Toggle() { if(!Muted.HasValue)throw new InvalidOperationException(); Toggles++;Muted=!Muted.Value;return Muted.Value; }
 public void Dispose()=>Disposed=true;
}





