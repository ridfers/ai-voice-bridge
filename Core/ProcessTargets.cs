using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace AiVoiceBridge.Core;

public enum CaptureSource { All, Discord, CS2, Dota2 }
public sealed record CaptureChoice(CaptureSource Id,string Name) { public override string ToString()=>Name; }
public sealed record ProcessEntry(int Id,int ParentId,string Name);
public static class ProcessTargets
{
    public static IReadOnlyList<CaptureChoice> Choices { get; } = [
        new(CaptureSource.All,"Все приложения (выбранный выход)"),
        new(CaptureSource.Discord,"Только Discord"),new(CaptureSource.CS2,"Только CS2"),new(CaptureSource.Dota2,"Только Dota 2")];
    public static string Name(CaptureSource source)=>Choices.FirstOrDefault(x=>x.Id==source)?.Name??"Неизвестный источник";
    public static int[] Roots(CaptureSource source,IReadOnlyList<ProcessEntry> entries)
    {
        string[] names=source switch { CaptureSource.Discord=>["Discord.exe","DiscordPTB.exe","DiscordCanary.exe"],CaptureSource.CS2=>["cs2.exe"],CaptureSource.Dota2=>["dota2.exe"],_=>[] };
        var matches=entries.Where(p=>names.Contains(p.Name,StringComparer.OrdinalIgnoreCase)).ToArray();
        return matches.Where(p=>!matches.Any(parent=>parent.Id==p.ParentId)).Select(p=>p.Id).ToArray();
    }
    public static Process Resolve(CaptureSource source)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,20348))throw new NotSupportedException("Захват приложения требует Windows сборки 20348 или новее.");
        var roots=Roots(source,Snapshot());
        if(roots.Length==0)throw new InvalidOperationException($"{Name(source)}: приложение не запущено. Откройте его и нажмите «Запустить».");
        if(roots.Length>1)throw new InvalidOperationException($"{Name(source)}: найдено несколько экземпляров. Оставьте один и запустите мост заново.");
        var process=Process.GetProcessById(roots[0]);
        try { if(process.HasExited)throw new InvalidOperationException("Приложение уже закрыто. Запустите мост заново."); return process; }
        catch { process.Dispose(); throw; }
    }
    private static IReadOnlyList<ProcessEntry> Snapshot()
    {
        var handle=CreateToolhelp32Snapshot(2,0);
        if(handle==new IntPtr(-1))throw new Win32Exception();
        try
        {
            var result=new List<ProcessEntry>();
            var entry=new NativeEntry{Size=(uint)Marshal.SizeOf<NativeEntry>(),Exe=""};
            if(!Process32FirstW(handle,ref entry))throw new Win32Exception();
            do { result.Add(new((int)entry.Id,(int)entry.ParentId,entry.Exe)); } while(Process32NextW(handle,ref entry));
            return result;
        }
        finally { CloseHandle(handle); }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct NativeEntry
    {
        public uint Size,Usage,Id; public UIntPtr Heap; public uint Module,Threads,ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot,ref NativeEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot,ref NativeEntry entry);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}


