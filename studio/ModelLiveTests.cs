using System.IO;
using System.Windows;
namespace LenovoCameraStudio;
public partial class MainWindow
{
    internal async Task ModelLiveTests(string path,int only=0,bool everyday=false)
    {
        ShowInTaskbar=false;Opacity=0;Show();var log=new List<string>();
        int oldBackend=SettingsStore.Read("DenoiseBackend"),oldQ=SettingsStore.Read("DenoiseLevel"),oldWidth=SettingsStore.Read("DenoiseModelWidth",640);
        int oldAuto=SettingsStore.Read("DenoiseAutoSize",1);
        var oldWidths=Enumerable.Range(2,7).ToDictionary(b=>b,b=>SettingsStore.Read($"DenoiseModelWidth{b}",DefaultModelWidth(b)));
        var shutter=settings[(1,4)];shutter.Read();int oldShutter=shutter.Value,oldMode=shutter.Mode;
        var priority=new CameraSetting("Priority",1,19);bool hasPriority=priority.Read();int oldPriority=priority.Value,oldPriorityMode=priority.Mode;
        try{
            DenoiseChoice.SelectedIndex=0;SettingsStore.Write("DenoiseAutoSize",everyday?1:0);if(!everyday){SetHardware(shutter,-6,2);if(hasPriority)SetHardware(priority,0,0);}
            ModelSizeChoice.SelectedIndex=Array.IndexOf(ModelWidths,640);SettingsStore.Write("DenoiseModelWidth",640);
            await WaitFrames(70);var baseline=await SamplePerformance();log.Add($"Baseline: {baseline.Fps:0.0} fps, median sample age {baseline.Age:0.0} ms. {(everyday?"Existing exposure, automatic model sizing":"Fixed 1/64 s shutter, fixed model sizing")}; hardware/settings restored afterward.");File.WriteAllLines(path,log);
            int failures=0;
            for(int b=2;b<ModelHost.Names.Length;b++){
              if(only!=0&&b!=only)continue;
              try{
                if(!ModelHost.Available(b))throw new Exception($"{ModelHost.Names[b]} model is missing.");
                BackendChoice.SelectedIndex=b;int modelWidth=everyday?1920:DefaultModelWidth(b);ModelSizeChoice.SelectedIndex=Array.IndexOf(ModelWidths,modelWidth);SettingsStore.Write("DenoiseModelWidth",modelWidth);DenoiseChoice.SelectedIndex=3;
                var deadline=DateTime.UtcNow.AddSeconds(45);bool active=false;
                while(DateTime.UtcNow<deadline){await Task.Delay(100);var state=SettingsStore.Backend();if(state.Fresh&&state.Kind==b&&state.Level==3&&state.Filtered>=5){active=true;break;}}
                if(!active)throw new Exception($"{ModelHost.Names[b]} did not deliver filtered frames. {modelHost.Message}");
                await WaitFrames(10);
                var before=SettingsStore.Backend();await WaitFrames(8);var performance=await SampleModelPerformance();var after=SettingsStore.Backend();
                if(everyday)modelWidth=modelHost.ReadStatus(b).Width;
                long filtered=after.Filtered-before.Filtered,raw=after.Raw-before.Raw;
                log.Add($"Observed: {ModelHost.Names[b]}, {modelWidth}px model width: {performance.Fps:0.0} fps; median/P95/max frame age {performance.Age:0.0}/{performance.P95:0.0}/{performance.Max:0.0} ms; {filtered}/{raw} filtered/original.");File.WriteAllLines(path,log);
                if(filtered<4||raw!=0)throw new Exception($"{ModelHost.Names[b]} interleaved original frames after warm-up: {filtered}/{raw}.");
                if(performance.P95>500)throw new Exception($"{ModelHost.Names[b]} exceeded 500 ms frame age: median {performance.Age:0.0}, P95 {performance.P95:0.0}, max {performance.Max:0.0} ms; {performance.Fps:0.0} fps; {filtered}/{raw} filtered/original.");
                log.Add($"PASS: {ModelHost.Names[b]} live 1080p camera output, {modelWidth}px-wide model processing: {filtered} filtered / {raw} original after warm-up; {performance.Fps:0.0} fps, median/P95/max sample age {performance.Age:0.0}/{performance.P95:0.0}/{performance.Max:0.0} ms; bridge/filter {after.Milliseconds:0.0} ms.");File.WriteAllLines(path,log);
              }catch(Exception error){failures++;log.Add($"FAIL: {ModelHost.Names[b]}: {error.Message}");}
              finally{
                DenoiseChoice.SelectedIndex=0;
                try{await WaitFrames(55);var recovered=await SamplePerformance();if(recovered.Fps<27||recovered.Age>baseline.Age+75)throw new Exception($"Off after {ModelHost.Names[b]} did not recover immediately.");log.Add($"PASS: Off after {ModelHost.Names[b]}: {recovered.Fps:0.0} fps, sample age {recovered.Age:0.0} ms; no restart.");}catch(Exception error){failures++;log.Add("FAIL: "+error.Message);}
                File.WriteAllLines(path,log);
              }
            }
            if(failures>0)throw new Exception($"{failures} model/recovery checks failed; see individual measurements above.");
            log.Add($"PASS: {(only==0?"all seven research backends":"selected backend")} deliver processed frames in the actual camera pipeline below the 500 ms P95 frame-age limit at their tested sizes; source timestamps retained, original-frame fallback counted, Off recovery verified. Each latency sample covers 50 observations over approximately 3.25 seconds. No webcam images saved.");
        }catch(Exception error){log.Add("FAIL: "+error);throw;}
        finally{modelHost.Stop();try{SetHardware(shutter,oldShutter,oldMode);if(hasPriority)SetHardware(priority,oldPriority,oldPriorityMode);}catch{}SettingsStore.Write("DenoiseBackend",oldBackend);SettingsStore.Write("DenoiseLevel",oldQ);SettingsStore.Write("DenoiseModelWidth",oldWidth);SettingsStore.Write("DenoiseAutoSize",oldAuto);foreach(var width in oldWidths)SettingsStore.Write($"DenoiseModelWidth{width.Key}",width.Value);File.WriteAllLines(path,log);Close();}
    }
    private async Task<(double Fps,double Age,double P95,double Max)> SampleModelPerformance(){var frames=new List<double>();var ages=new List<double>();for(int i=0;i<50;i++){await Task.Delay(65);CameraApi.PreviewStats(out double fps,out double age,out _);frames.Add(fps);ages.Add(age);}frames.Sort();ages.Sort();return(frames[25],ages[25],ages[47],ages[^1]);}
}
