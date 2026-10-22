using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Security.Cryptography;

namespace LenovoCameraStudio;

internal sealed class ModelHost : IDisposable
{
    internal sealed record RuntimeStatus(string Phase,string Message,int Width=0,double OutputAge=0,string Error="",double SourceAge=0);
    internal static readonly string[] Names=["NVIDIA RTX","Bilateral GPU","FastDVDnet","ASwin","ViDeNN","FlowDenoise","UDVD","ShiftNet Small","PVDD sRGB"];
    internal static readonly string[] Descriptions=[
        "RTX denoising + edge-aware GPU colour-noise cleanup. Original frames continue during model warm-up.",
        "Custom MIT-licensed spatial bilateral filter. Direct3D 11 on AMD, Intel or NVIDIA. No trained model or temporal history.",
        "Five-frame neural video denoiser, without optical flow. Uses the official clipped-noise model. Two future frames add capture delay. MIT license.",
        "Six-frame attention model with official real-noise weights. Uses its latest-frame prediction to avoid future-frame delay. CC BY-NC 4.0: non-commercial use only.",
        "Three-frame blind neural denoiser. Official TensorFlow weights converted to matching GPU inference. Low-light results depend on the camera. MIT license.",
        "FlowDenoise motion-aligned averaging with RAFT Small. Three-frame window through High; five frames at Ultra. Forward/backward motion checks. MIT license.",
        "Five-frame unsupervised neural denoiser, using official weights and noise-dependent posterior reconstruction. MIT license.",
        "Five-frame grouped-shift neural denoiser. Official Small-model weights. Research evaluation: upstream licensing terms are not specified in a standalone license file.",
        "Real-world sRGB video denoising with the official PVDD checkpoint. Five-frame recurrent attention processing. MIT license."];
    private Process? process;
    private string token="";
    private readonly string statusPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LenovoCameraStudio","model-status.json");
    internal static string? Root {
        get {var dir=new DirectoryInfo(AppContext.BaseDirectory);for(int i=0;i<5&&dir is not null;i++,dir=dir.Parent){string path=Path.Combine(dir.FullName,"LenovoDenoiserRuntime");if(File.Exists(Path.Combine(path,"python","python.exe")))return path;}return null;}
    }
    internal static bool Available(int backend) {
        string? root=Root;if(root is null)return false;
        string relative=backend switch{2=>@"fastdvdnet\model_clipped_noise.pth",3=>@"aswin\weights.pth",4=>@"videnn\converted.pth",5=>@"flowdenoise\raft_small.pth",6=>@"udvd\weights.pth",7=>@"shiftnet\weights.pth",8=>@"pvdd\weights.pth",_=>""};
        return relative.Length>0&&File.Exists(Path.Combine(root,"models",relative));
    }
    internal static bool EngineCurrent {
        get {try{if(!SettingsStore.Installed)return false;using var installed=File.OpenRead(Path.Combine(SettingsStore.InstallFolder,"LenovoPassThrough.dll"));using var bundled=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"LenovoPassThrough.dll"));return SHA256.HashData(installed).SequenceEqual(SHA256.HashData(bundled));}catch{return false;}}
    }
    internal string Message=>ReadStatus(-1).Message;
    internal RuntimeStatus ReadStatus(int backend) {
        if(process is null)return new("stopped","Model worker stopped.");
        if(process.HasExited)return new("error","Model stopped unexpectedly. Select Off, then a level to retry.");
        try{
            using var file=new FileStream(statusPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var doc=JsonDocument.Parse(file);var data=doc.RootElement;
            if(data.GetProperty("pid").GetInt32()!=process.Id||(backend>=0&&data.GetProperty("backend").GetInt32()!=backend))return new("loading","Starting selected model…");
            string phase=data.GetProperty("state").GetString()??"starting";
            string error=data.TryGetProperty("lastError",out var failure)?failure.GetString()??"":"";
            int width=data.TryGetProperty("modelWidth",out var size)?size.GetInt32():0;
            double age=data.TryGetProperty("outputAgeMs",out var time)?time.GetDouble():0;
            double sourceAge=data.TryGetProperty("sourceAgeMs",out var inputTime)?inputTime.GetDouble():0;
            if(phase=="error"&&error.Length==0)error=data.GetProperty("message").GetString()??"Model inference failed.";
            if(error.Length>0)return new("error","Model could not process the image. Select Off, then a level to retry.",width,age,error);
            string message=phase switch{"starting" or "ready"=>"Starting selected model…","loading"=>"Loading selected model…",_=>width>0?$"Actual processing size: {width} px wide.":"Preparing first result…"};
            return new(phase,message,width,age,SourceAge:sourceAge);
        }catch{return new("starting","Starting selected model…");}
    }
    internal void Start() {
        if(!EngineCurrent)throw new InvalidOperationException("Choose Camera → Install / update camera processing to activate the new models and 500 ms limit.");
        if(process is not null&&!process.HasExited)return;
        string root=Root??throw new InvalidOperationException("The model runtime folder is missing. Keep LenovoDenoiserRuntime beside the app folder.");
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);if(File.Exists(statusPath))File.Delete(statusPath);
        token=Guid.NewGuid().ToString("N");
        var start=new ProcessStartInfo(Path.Combine(root,"python","python.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=root};
        foreach(string argument in new[]{Path.Combine(AppContext.BaseDirectory,"Models","model_worker.py"),"--pipe",token,"--sid",WindowsIdentity.GetCurrent().User!.Value,"--models",Path.Combine(root,"models"),"--status",statusPath})start.ArgumentList.Add(argument);
        start.Environment["PYTHONUNBUFFERED"]="1";start.Environment["TORCH_HOME"]=Path.Combine(root,"cache");
        process=Process.Start(start)??throw new InvalidOperationException("The model worker could not start.");
        string log=Path.Combine(Path.GetDirectoryName(statusPath)!,"model-worker.log");var child=process;
        _=Task.Run(async()=>{try{string output=await child.StandardOutput.ReadToEndAsync();await File.WriteAllTextAsync(log+".out",output);}catch{}});
        _=Task.Run(async()=>{try{string errors=await child.StandardError.ReadToEndAsync();if(errors.Length>0)await File.AppendAllTextAsync(log,$"\n{DateTime.Now:O}\n"+errors);}catch{}});
        SettingsStore.WriteText("DenoisePipeId",token);
    }
    internal void Stop(){if(process is null)return;try{if(!process.HasExited)process.Kill(true);}catch{}process.Dispose();process=null;}
    public void Dispose()=>Stop();
}
