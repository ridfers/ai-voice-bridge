using System.Diagnostics;
using AiVoiceBridge.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

static class ApplicationCaptureTests
{
    public static bool Helper(string[] args)
    {
        if(args.Length==0||args[0]!="--tone-helper")return false;
        using var devices=new MMDeviceEnumerator();
        using var device=devices.GetDevice(args[1]);
        using var player=new WasapiOut(device,AudioClientShareMode.Shared,true,40);
        var signal=new SignalGenerator(48000,2){Frequency=double.Parse(args[2]),Gain=0.003,Type=SignalGeneratorType.Sin};
        player.Init(new LiveOutputProvider(signal,player.OutputWaveFormat)); player.Play();
        Console.WriteLine("ready"); Console.Out.Flush(); Console.ReadLine(); player.Stop();
        return true;
    }
    public static async Task Run(Action<bool,string> check,string endpoint)
    {
        Process Tone(int frequency)
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,CreateNoWindow=true};
            start.ArgumentList.Add("--tone-helper");start.ArgumentList.Add(endpoint);start.ArgumentList.Add(frequency.ToString());
            return Process.Start(start)!;
        }
        using var chosen=Tone(440);using var unrelated=Tone(880);
        try
        {
            check(await chosen.StandardOutput.ReadLineAsync()=="ready","Selected synthetic audio process started");
            check(await unrelated.StandardOutput.ReadLineAsync()=="ready","Unrelated synthetic audio process started");
            using var capture=new ProcessLoopback(chosen.Id);
            double selectedSin=0,selectedCos=0,otherSin=0,otherCos=0;long sample=0;
            Exception? stopped=null;
            var exit=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.RecordingStopped+=(_,e)=> { stopped=e.Exception; exit.TrySetResult(); };
            capture.DataAvailable+=(_,e)=>
            {
                for(int at=0;at<e.BytesRecorded;at+=4)
                {
                    double value=BitConverter.ToInt16(e.Buffer,at)/32768.0;
                    double time=sample++/48000.0;
                    selectedSin+=value*Math.Sin(2*Math.PI*440*time);selectedCos+=value*Math.Cos(2*Math.PI*440*time);
                    otherSin+=value*Math.Sin(2*Math.PI*880*time);otherCos+=value*Math.Cos(2*Math.PI*880*time);
                }
            };
            capture.StartRecording();await Task.Delay(1800);
            chosen.StandardInput.WriteLine();await chosen.WaitForExitAsync();
            await exit.Task.WaitAsync(TimeSpan.FromSeconds(3));capture.StopRecording();
            double selected=Math.Sqrt(selectedSin*selectedSin+selectedCos*selectedCos),other=Math.Sqrt(otherSin*otherSin+otherCos*otherCos);
            Console.WriteLine($"ISOLATION frames={sample} selected440={selected:F5} unrelated880={other:F5}");
            check(sample>20000&&selected>1,"Native capture receives selected process audio");
            check(other<selected*0.03,"Native capture excludes unrelated process on SAME endpoint");
            check(stopped is InvalidOperationException,"Process exit stops capture with actionable error");
        }
        finally
        {
            foreach(var process in new[]{chosen,unrelated})
                if(!process.HasExited) { process.StandardInput.WriteLine(); await process.WaitForExitAsync(); }
        }
    }
}
