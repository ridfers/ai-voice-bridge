using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
namespace AiVoiceBridge.Core;

public interface IMicrophoneControl : IDisposable
{
    void Select(string deviceId);
    bool? ReadMuted();
    bool Toggle();
}
// B2 route on the physical Voicemeeter strip only. Never writes Windows endpoint mute.
public sealed class WindowsMicrophoneControl : IMicrophoneControl
{
    private bool _loggedIn;
    private string? _name;
    private const string Remote=@"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
    public void Select(string deviceId)
    {
        _name=null;
        try
        {
            using var devices=new MMDeviceEnumerator();using var device=devices.GetDevice(deviceId);
            if(device.DataFlow!=DataFlow.Capture||device.State!=DeviceState.Active)return;
            _name=device.FriendlyName;
            if(!_loggedIn)_loggedIn=Login()==0;
        }
        catch { _name=null; }
    }
    private int FindStrip()
    {
        if(!_loggedIn||_name==null)return -1;
        if(Dirty()<0)return -1;
        int found=-1;
        for(int index=0;index<3;index++)
        {
            var text=new StringBuilder(512);
            if(GetString($"Strip[{index}].device.name",text)==0&&string.Equals(text.ToString(),_name,StringComparison.OrdinalIgnoreCase))
            {
                if(found>=0)return -1;
                found=index;
            }
        }
        return found;
    }
    public bool? ReadMuted()
    {
        try { int strip=FindStrip(); if(strip<0)return null; if(GetFloat($"Strip[{strip}].B1",out float toAi)!=0)return null; if(toAi>=0.5f&&SetFloat($"Strip[{strip}].B1",0)!=0)return null; return GetFloat($"Strip[{strip}].B2",out float enabled)==0?enabled<0.5f:null; }
        catch { return null; }
    }
    public bool Toggle()
    {
        int strip=FindStrip();
        if(strip<0||GetFloat($"Strip[{strip}].B2",out float enabled)!=0)
            throw new InvalidOperationException("Откройте Voicemeeter Banana и назначьте выбранный физический микрофон одному аппаратному входу.");
        if(SetFloat($"Strip[{strip}].B1",0)!=0)throw new InvalidOperationException("Не удалось исключить микрофон из канала ChatGPT B1.");
        bool muted=enabled>=0.5f;
        if(SetFloat($"Strip[{strip}].B2",muted?0:1)!=0)throw new InvalidOperationException("Voicemeeter не принял переключение игрового маршрута.");
        // Remote writes are asynchronous. UI polling reads the confirmed state.
        return muted;
    }
    public void Dispose() { if(_loggedIn) { try { Logout(); } catch { } _loggedIn=false; } _name=null; }
    [DllImport(Remote,EntryPoint="VBVMR_Login")] private static extern int Login();
    [DllImport(Remote,EntryPoint="VBVMR_Logout")] private static extern int Logout();
    [DllImport(Remote,EntryPoint="VBVMR_IsParametersDirty")] private static extern int Dirty();
    [DllImport(Remote,EntryPoint="VBVMR_GetParameterStringW",CharSet=CharSet.Ansi)] private static extern int GetString(string name,[MarshalAs(UnmanagedType.LPWStr)]StringBuilder value);
    [DllImport(Remote,EntryPoint="VBVMR_GetParameterFloat",CharSet=CharSet.Ansi)] private static extern int GetFloat(string name,out float value);
    [DllImport(Remote,EntryPoint="VBVMR_SetParameterFloat",CharSet=CharSet.Ansi)] private static extern int SetFloat(string name,float value);
}

