using System.Runtime.InteropServices;
using System.IO;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace LenovoCameraStudio;

internal static class CameraApi
{
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int CameraQuery(int group,int property,out int min,out int max,out int step,out int def,out int caps,out int value,out int mode);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int CameraSet(int group,int property,int value,int mode);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewStartSized(int width,int height);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern void PreviewStop();
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewInfo(out int width,out int height,out ulong serial);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewCopy(IntPtr target,int width,int height,int stride,ref ulong serial);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewStats(out double fps,out double ageMs,out double copyMs);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewExposure(out ulong exposureTime100ns);
    [DllImport("CameraNative.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern void CameraClose();
    internal static void Check(int hr) { if(hr<0) Marshal.ThrowExceptionForHR(hr); }
}

internal sealed class CameraSetting(string name,int group,int property)
{
    internal string Name=name;
    internal int Group=group,Property=property,Min,Max,Step,Default,Caps,Value,Mode;
    internal bool Read() => CameraApi.CameraQuery(Group,Property,out Min,out Max,out Step,out Default,out Caps,out Value,out Mode)>=0;
    internal int Snap(int value) => Math.Clamp(Min+(int)Math.Round((value-Min)/(double)Math.Max(1,Step))*Math.Max(1,Step),Min,Max);
    internal string Display(int value) {
        if(Group==1&&Property==4) return value<0? $"1/{Math.Pow(2,-value):0} s" : $"{Math.Pow(2,value):0.##} s";
        if(Group==0&&Property==7) return $"{value} K";
        if(Group==0&&Property==8) return value==0?"Off":$"Level {value}";
        if(Property==2) return $"{value}°";
        if(Property is 4 or 5 or 8) return value.ToString();
        return $"{Math.Round(100.0*(value-Min)/Math.Max(1,Max-Min)):0}%";
    }
    internal static string ShutterTime(ulong time100ns) {
        if(time100ns==0)return "Auto";
        double seconds=time100ns/10000000.0;
        return seconds<1?$"1/{1/seconds:0.##} s":$"{seconds:0.##} s";
    }
}

internal static class SettingsStore
{
    internal const string Key=@"SOFTWARE\LenovoWebcamPrototype";
    internal static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"LenovoWebcamPrototype");
    internal static bool Installed {
        get {using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);using var key=root.OpenSubKey(@"SOFTWARE\Classes\CLSID\{73DF2569-1A6D-4976-BA38-F089C0162864}\InprocServer32");return string.Equals(Convert.ToString(key?.GetValue(null)),Path.Combine(InstallFolder,"LenovoPassThrough.dll"),StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(InstallFolder,"LenovoPassThrough.dll"));}
    }
    internal static int Read(string name,int fallback=0) {
        using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var key=root.OpenSubKey(Key);return Convert.ToInt32(key?.GetValue(name,fallback)??fallback);
    }
    internal static void Write(string name,int value) {
        using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var key=root.OpenSubKey(Key,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.ReadKey|RegistryRights.SetValue);
        if(key is null) throw new InvalidOperationException("Camera processing is not installed.");
        key.SetValue(name,value,RegistryValueKind.DWord);
    }
    internal static void WriteText(string name,string value){using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);using var key=root.OpenSubKey(Key,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.ReadKey|RegistryRights.SetValue);if(key is null)throw new InvalidOperationException("Camera processing is not installed.");key.SetValue(name,value,RegistryValueKind.String);}
    internal static bool Writable { get { try {using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);using var key=root.OpenSubKey(Key,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryRights.ReadKey|RegistryRights.SetValue);return key is not null;}catch{return false;} } }
    internal static (bool Fresh,int Result,int Level,int Kind,double Milliseconds,long Filtered,long Raw) Backend() {
        using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var key=root.OpenSubKey(Key);
        long tick=Convert.ToInt64(key?.GetValue("BackendTick",0L)??0L);
        return (tick>0&&Environment.TickCount64-tick is >=0 and <2500,Convert.ToInt32(key?.GetValue("BackendNvidiaResult",1)??1),Convert.ToInt32(key?.GetValue("BackendDenoiseLevel",0)??0),Convert.ToInt32(key?.GetValue("BackendDenoiseKind",0)??0),Convert.ToInt32(key?.GetValue("BackendGpuMicroseconds",0)??0)/1000.0,Convert.ToInt64(key?.GetValue("BackendFilteredFrames",0L)??0L),Convert.ToInt64(key?.GetValue("BackendRawFrames",0L)??0L));
    }
}
