using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
namespace AiVoiceBridge.Core;
[ComImport,Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileCaptureCallback { }

// Windows process-tree loopback: no injection and no endpoint-mix fallback.
public sealed class ProcessLoopback : IWaveIn
{
    private readonly Process _process;
    private readonly AudioClient _client;
    private readonly AutoResetEvent _ready=new(false);
    private readonly ManualResetEvent _stop=new(false);
    private Thread? _thread;
    private bool _disposed;
    public WaveFormat WaveFormat { get; set; }=new WaveFormat(48000,16,2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public ProcessLoopback(int processId)
    {
        _process=Process.GetProcessById(processId);
        AudioClient? client=null;
        try
        {
            _process.EnableRaisingEvents=true; // Retain an exit-wait handle without requesting game memory access.
            client=Activate(processId).GetAwaiter().GetResult();
            client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.Loopback|AudioClientStreamFlags.EventCallback|AudioClientStreamFlags.AutoConvertPcm,0,0,WaveFormat,Guid.Empty);
            client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            _client=client;
        }
        catch { client?.Dispose(); _process.Dispose(); _ready.Dispose(); _stop.Dispose(); throw; }
    }
    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_thread!=null)throw new InvalidOperationException("Захват уже запущен.");
        _thread=new Thread(Capture){IsBackground=true,Name="Application audio capture"};
        _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
    }
    private void Capture()
    {
        Exception? error=null;
        try
        {
            var capture=_client.AudioCaptureClient;
            var buffer=new byte[Math.Max(_client.BufferSize*WaveFormat.BlockAlign,WaveFormat.AverageBytesPerSecond/5)];
            WaitHandle[] events=[_stop,_ready];
            _client.Start();
            while(WaitHandle.WaitAny(events,100)!=0)
            {
                if(_process.HasExited)throw new InvalidOperationException("Выбранное приложение закрыто. После его запуска нажмите «Запустить» в мосте.");
                while(!_stop.WaitOne(0)&&capture.GetNextPacketSize()>0)
                {
                    var pointer=capture.GetBuffer(out int frames,out var flags);
                    try
                    {
                        int size=checked(frames*WaveFormat.BlockAlign);
                        if(size>buffer.Length)throw new InvalidOperationException("Слишком большой аудиопакет приложения.");
                        if((flags&AudioClientBufferFlags.Silent)!=0)Array.Clear(buffer,0,size);
                        else Marshal.Copy(pointer,buffer,0,size);
                        DataAvailable?.Invoke(this,new WaveInEventArgs(buffer,size));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        catch(Exception e) { error=e; }
        finally
        {
            try { _client.Stop(); } catch(Exception e) { error??=e; }
            RecordingStopped?.Invoke(this,new StoppedEventArgs(error));
        }
    }
    public void StopRecording() { _stop.Set(); if(_thread!=null&&Thread.CurrentThread!=_thread)_thread.Join(); }
    public void Dispose()
    {
        if(_disposed)return; _disposed=true;
        StopRecording(); _client.Dispose(); _process.Dispose(); _ready.Dispose(); _stop.Dispose();
    }
    private static async Task<AudioClient> Activate(int processId)
    {
        // PROPVARIANT VT_BLOB: union at offset 8, pointer aligned to native architecture.
        IntPtr parameters=Marshal.AllocHGlobal(12),variant=Marshal.AllocHGlobal(IntPtr.Size==8?24:16);
        IActivateAudioInterfaceAsyncOperation? operation=null;
        var completion=new Completion();
        bool delivered=false;
        try
        {
            Marshal.WriteInt32(parameters,0,1); Marshal.WriteInt32(parameters,4,processId); Marshal.WriteInt32(parameters,8,0);
            for(int at=0;at<(IntPtr.Size==8?24:16);at+=4)Marshal.WriteInt32(variant,at,0);
            Marshal.WriteInt16(variant,65); Marshal.WriteInt32(variant,8,12); Marshal.WriteIntPtr(variant,IntPtr.Size==8?16:12,parameters);
            var iid=typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback",ref iid,variant,completion,out operation));
            // The OS owns the async operation until callback completion. On timeout the late
            // client is released below; never free activation parameters while still in use.
            var result=await completion.Result.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            delivered=true; return result;
        }
        finally
        {
            var pending=completion.Result.Task;
            if(operation!=null&&!delivered)
            {
                var held=operation;
                _=pending.ContinueWith(t=> { if(t.IsCompletedSuccessfully)t.Result.Dispose(); _=t.Exception; Marshal.FreeHGlobal(parameters); Marshal.FreeHGlobal(variant); Marshal.ReleaseComObject(held); GC.KeepAlive(completion); },TaskScheduler.Default);
            }
            else { Marshal.FreeHGlobal(parameters); Marshal.FreeHGlobal(variant); if(operation!=null)Marshal.ReleaseComObject(operation); }
            GC.KeepAlive(completion);
        }
    }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : IActivateAudioInterfaceCompletionHandler,IAgileCaptureCallback
    {
        public TaskCompletionSource<AudioClient> Result { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? unknown=null;
            try { operation.GetActivateResult(out int hr,out unknown); Marshal.ThrowExceptionForHR(hr); var client=new AudioClient((IAudioClient)unknown); unknown=null; Result.TrySetResult(client); }
            catch(Exception e) { if(unknown!=null&&Marshal.IsComObject(unknown))Marshal.ReleaseComObject(unknown); Result.TrySetException(e); }
        }
    }
    [DllImport("Mmdevapi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern int ActivateAudioInterfaceAsync(string path,ref Guid iid,IntPtr activationParams,IActivateAudioInterfaceCompletionHandler handler,out IActivateAudioInterfaceAsyncOperation operation);
}


