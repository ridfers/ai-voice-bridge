using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace AiVoiceBridge.Core;

// Bounded, reusable float ring. No recording, no growing queues, no packet allocations here.
public sealed class GatedBuffer : ISampleProvider
{
    private readonly object _sync = new();
    private readonly float[] _ring;
    private int _read, _write, _count;
    private bool _open;
    private float _gain, _last;
    public bool IsOpen { get { lock(_sync) return _open; } }
    public float Volume { get; set; } = 1;
    public long Overflows { get; private set; }
    public float Peak { get; private set; }
    public WaveFormat WaveFormat { get; }
    public GatedBuffer(int rate,int channels) { WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(rate,channels); _ring=new float[rate*channels/5]; }
    public void Gate(bool open) { lock(_sync) { _open=open; Peak=0; _read=_write=_count=0; if(open) _gain=0; } }
    public void Add(byte[] bytes,int count) => Add(MemoryMarshal.Cast<byte,float>(bytes.AsSpan(0,count)));
    public void Add(ReadOnlySpan<float> samples)
    {
        lock(_sync)
        {
            if (!_open) { Peak=0; return; }

            float peak=0;
            if (_count+samples.Length>_ring.Length) { _count=0; _read=_write=0; Overflows++; }
            foreach(var sample in samples) { peak=Math.Max(peak,Math.Abs(sample)); if(_count==_ring.Length)break; _ring[_write]=sample; _write=(_write+1)%_ring.Length; _count++; }
            Peak=peak;
        }
    }
    public int Read(float[] buffer,int offset,int count)
    {
        lock(_sync)
        {
            float step=1f/(WaveFormat.SampleRate*WaveFormat.Channels*0.003f);
            for(int i=0;i<count;i++)
            {
                if(_open && _count>0) { _last=_ring[_read]; _read=(_read+1)%_ring.Length; _count--; _gain=Math.Min(1,_gain+step); }
                else { _gain=Math.Max(0,_gain-step); }
                buffer[offset+i]=_last*_gain*Volume;
            }
            return count;
        }
    }
}
internal sealed class EventLoopback(MMDevice device) : WasapiCapture(device,true,20)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}
internal sealed class AudioPipe : IDisposable
{
    private MMDevice? _input, _output;
    private IWaveIn? _capture;
    private WasapiOut? _playback;

    public GatedBuffer Buffer { get; private set; } = null!;
    private volatile bool _disposing;
    public AudioPipe(string source,string destination,bool gated,Action<Exception> failure,float volume=1,int? processId=null)
    {
        try
        {
            using var devices=new MMDeviceEnumerator();
            _output=devices.GetDevice(destination);
            if(processId.HasValue) _capture=new ProcessLoopback(processId.Value);
            else { _input=devices.GetDevice(source); _capture=new EventLoopback(_input); }
            var format=_capture.WaveFormat;
            // Keep the native mix format, including its extensible channel mask.
            Buffer=new GatedBuffer(format.SampleRate,format.Channels); Buffer.Volume=Math.Clamp(volume,0,3); Buffer.Gate(!gated);
            PcmDecoder.Validate(format);
            var scratch=new float[format.SampleRate*format.Channels/5];
            _capture.DataAvailable+=(_,e)=>
            {
                if(!Buffer.IsOpen)return;
                int bytes=format.BitsPerSample/8;
                for(int at=0;at<e.BytesRecorded;)
                {
                    int length=Math.Min(scratch.Length*bytes,e.BytesRecorded-at);
                    int decoded=PcmDecoder.Decode(e.Buffer.AsSpan(at,length),scratch,format);
                    Buffer.Add(scratch.AsSpan(0,decoded));at+=length;
                }
            };
            _capture.RecordingStopped+=(_,e)=> { if(!_disposing) failure(e.Exception ?? new InvalidOperationException("Захват остановлен устройством.")); };
            _playback=new WasapiOut(_output,AudioClientShareMode.Shared,true,40);
            _playback.PlaybackStopped+=(_,e)=> { if(!_disposing) failure(e.Exception ?? new InvalidOperationException("Аудиовыход остановлен устройством.")); };
            // NAudio adapts rate/channel/PCM format to the output mix format in shared mode.
            var output=new LiveOutputProvider(Buffer,_playback.OutputWaveFormat);
            _playback.Init(output);
            _capture.StartRecording(); _playback.Play();
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        _disposing=true;
        Buffer?.Gate(false);
        try { _capture?.Dispose(); } finally { try { _playback?.Dispose(); } finally { _input?.Dispose(); _output?.Dispose(); } }
        _capture=null; _playback=null; _input=null; _output=null;
    }
}
public interface IAudioEngine
{
    BridgeState State { get; }
    string? Error { get; }
    double Peak { get; }
    event Action? Changed;
    Task StartAsync(BridgeSettings settings,CancellationToken cancellation);
    Task StopAsync();
    void SetTransmission(bool pressed);
    void SetVolumes(double game,double voice);
}
public sealed class AudioEngine : IAudioEngine
{
    private readonly SemaphoreSlim _lifecycle=new(1);
    private readonly object _gate=new();
    private readonly List<AudioPipe> _pipes=[];
    private AudioPipe? _game, _voice, _voiceMonitor;
    private bool _wanted;
    private long _generation;
    public BridgeState State { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public double Peak { get { lock(_gate) return (_game?.Buffer.Peak??0)*100; } }
    public long Overflows { get { lock(_gate) return (_game?.Buffer.Overflows??0)+(_voice?.Buffer.Overflows??0); } }
    public async Task StartAsync(BridgeSettings s, CancellationToken cancellation)
    {
        await _lifecycle.WaitAsync(cancellation);
        try
        {
            await Task.Run(()=>
            {
                StopCore(); cancellation.ThrowIfCancellationRequested();
                long generation=Interlocked.Increment(ref _generation);
                Action<Exception> fail=e=> { _=FailAsync(e,generation); };
                try
                {
                    if(!Enum.IsDefined(s.CaptureSource))throw new InvalidOperationException("Выберите источник звука для ИИ.");
                    using var target=s.CaptureSource==CaptureSource.All?null:ProcessTargets.Resolve(s.CaptureSource);
                    var game=new AudioPipe(s.GameSource,s.BrowserMicrophone,true,fail,(float)(s.GameVolume/100),target?.Id); _pipes.Add(game);
                    AudioPipe? voice=null;
                    if(s.RouteVoice) { voice=new AudioPipe(s.BrowserSource,s.GameMicrophone,false,fail,(float)(s.VoiceVolume/100)); _pipes.Add(voice); }
                    AudioPipe? voiceMonitor=null; if(s.MonitorVoice) { voiceMonitor=new AudioPipe(s.BrowserSource,s.Headphones,false,fail,(float)(s.VoiceVolume/100)); _pipes.Add(voiceMonitor); }
                    if(s.MonitorGame) _pipes.Add(new AudioPipe(s.GameSource,s.Headphones,false,fail,1,target?.Id));
                    cancellation.ThrowIfCancellationRequested();
                    lock(_gate) { _game=game; _voice=voice; _voiceMonitor=voiceMonitor; _wanted=false; State=BridgeState.Ready; Error=null; }
                    SetVolumes(s.GameVolume,s.VoiceVolume);
                }
                catch { StopCore(); throw; }
            },cancellation);
        }
        catch(OperationCanceledException) { throw; }
        catch(Exception e) { Error=e.Message; State=BridgeState.ConnectionError; }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }
    public void SetTransmission(bool pressed)
    {
        lock(_gate)
        {
            if(State is not (BridgeState.Ready or BridgeState.Transmitting))return;
            if(_wanted==pressed)return;
            _wanted=pressed; _game?.Buffer.Gate(pressed); State=pressed?BridgeState.Transmitting:BridgeState.Ready;
        }
        Changed?.Invoke();
    }
    public void SetVolumes(double game,double voice)
    {
        lock(_gate) { if(_game!=null)_game.Buffer.Volume=(float)Math.Clamp(game/100,0,3); if(_voice!=null)_voice.Buffer.Volume=(float)Math.Clamp(voice/100,0,1); if(_voiceMonitor!=null)_voiceMonitor.Buffer.Volume=(float)Math.Clamp(voice/100,0,1); }
    }
    private async Task FailAsync(Exception e,long generation)
    {
        await _lifecycle.WaitAsync();
        try { if(generation!=Interlocked.Read(ref _generation))return; await Task.Run(StopCore); Error=e.Message; State=BridgeState.ConnectionError; }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }
    public async Task StopAsync()
    {
        SetTransmission(false);
        lock(_gate) State=BridgeState.Stopped;
        await _lifecycle.WaitAsync();
        try { await Task.Run(StopCore); } finally { _lifecycle.Release(); Changed?.Invoke(); }
    }
    private void StopCore()
    {
        Interlocked.Increment(ref _generation);
        lock(_gate) { _game=null; _voice=null; _voiceMonitor=null; _wanted=false; State=BridgeState.Stopped; }
        foreach(var pipe in _pipes) { try { pipe.Dispose(); } catch(Exception e) { Error=e.Message; } }
        _pipes.Clear();
    }
}











