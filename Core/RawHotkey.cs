using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.ComponentModel;
namespace AiVoiceBridge.Core;
public sealed class RawHotkey : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError=true)] private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    private readonly IntPtr _buffer = Marshal.AllocHGlobal(256);
    private HwndSource? _source;
    private bool _down;
    public string Key { get; set; } = "MOUSE5";
    public event Action<bool>? Pressed;
    public event Action<string>? Diagnostic;
    public event Action? DeviceLost;
    public void Attach(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd); _source.AddHook(Hook);
        var devices = new[] { new Device { Page=1, Usage=2, Flags=0x2100, Target=hwnd }, new Device { Page=1, Usage=6, Flags=0x2100, Target=hwnd } };
        if (!RegisterRawInputDevices(devices,2,(uint)Marshal.SizeOf<Device>())) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Release() { if (!_down) return; _down=false; Pressed?.Invoke(false); }
    private IntPtr Hook(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled)
    {
        if (message == 0xFE && w.ToInt32()==2) { Release(); DeviceLost?.Invoke(); }
        if (message != 0xFF) return IntPtr.Zero;
        uint size=256, header=(uint)(8+2*IntPtr.Size);
        var read=GetRawInputData(l,0x10000003,_buffer,ref size,header);
        if (read==uint.MaxValue || read<header+16) return IntPtr.Zero;
        var type=Marshal.ReadInt32(_buffer); int offset=(int)header;
        bool? pressed=null;
        if (type==0)
        {
            var flags=(ushort)Marshal.ReadInt16(_buffer,offset+4);
            if ((flags&0x300)!=0) Diagnostic?.Invoke($"Raw Input: MOUSE5 flags=0x{flags:X}");
            // XBUTTON1/MOUSE4 is neither assigned nor suppressed.
            if (Key=="MOUSE5") { if ((flags&0x100)!=0) pressed=true; if ((flags&0x200)!=0) pressed=false; }
        }
        else if (type==1)
        {
            var vk=(ushort)Marshal.ReadInt16(_buffer,offset+6);
            if (Key=="F8" && vk==0x77) pressed=(Marshal.ReadInt16(_buffer,offset+2)&1)==0;
        }
        if (pressed.HasValue && pressed.Value!=_down) { _down=pressed.Value; Pressed?.Invoke(_down); }
        return IntPtr.Zero; // Never consume legacy or raw game input.
    }
    public void Dispose()
    {
        Release();
        if (_source!=null) { _source.RemoveHook(Hook); RegisterRawInputDevices(new[] {new Device{Page=1,Usage=2,Flags=1},new Device{Page=1,Usage=6,Flags=1}},2,(uint)Marshal.SizeOf<Device>()); }
        Marshal.FreeHGlobal(_buffer);
    }
}
