using System.IO;
using System.Windows;

namespace LenovoCameraStudio;
public partial class MainWindow
{
    internal async Task DenoiseDiagnostic(string path)
    {
        DenoiseDeliveryTests.Run();
        ShowInTaskbar=false;Opacity=0;Show();var log=new List<string>();
        int oldBackend=BackendChoice.SelectedIndex,oldLevel=DenoiseChoice.SelectedIndex,oldWidth=SettingsStore.Read("DenoiseModelWidth",640),oldAuto=SettingsStore.Read("DenoiseAutoSize",1);
        var widths=Enumerable.Range(2,7).ToDictionary(b=>b,b=>SettingsStore.Read($"DenoiseModelWidth{b}",DefaultModelWidth(b)));
        async Task WaitFor(Func<bool> test,string error,int seconds=45){var until=DateTime.UtcNow.AddSeconds(seconds);while(DateTime.UtcNow<until){if(test())return;await Task.Delay(100);}throw new Exception(error);}
        void Save(string message){log.Add(message);File.WriteAllLines(path,log);}
        try{
            await WaitFor(()=>running,"Preview did not start.");
            DenoiseChoice.SelectedIndex=0;await WaitFrames(25);
            BackendChoice.SelectedIndex=4;ModelSizeChoice.SelectedIndex=Array.IndexOf(ModelWidths,1920);AutoModelSizeToggle.IsChecked=false;DenoiseChoice.SelectedIndex=3;
            await WaitFor(()=>{var s=SettingsStore.Backend();return s.Fresh&&s.Kind==4&&s.Level==3&&s.Result==unchecked((int)0x800705B4);},"Large fixed ViDeNN did not reproduce the late-result fallback.");
            UpdateEffectLabel();if(!DenoiseDeliveryTitle.Text.Contains("original")||!(DenoiseDeliveryTitle.Text.Contains("too slow")||DenoiseDeliveryTitle.Text.Contains("camera delay too high")))throw new Exception("Late fixed model is not clearly identified as original output: "+DenoiseDeliveryTitle.Text);
            Save("PASS: full-HD fixed ViDeNN reproduces late-result fallback; the app reports: "+DenoiseDeliveryTitle.Text+".");
            AutoModelSizeToggle.IsChecked=true;
            await WaitFor(()=>{var s=SettingsStore.Backend();return s.Fresh&&s.Kind==4&&s.Level==3&&s.Filtered>=8;},"Automatic sizing did not recover filtered video.");
            await WaitFor(()=>{UpdateEffectLabel();return DenoiseDeliveryTitle.Text.StartsWith("Denoising active");},"Recovered filtering is not confirmed in the UI.",10);
            if(modelHost.ReadStatus(4).Width>640)throw new Exception("Recovered processing size is not shown correctly.");
            var before=SettingsStore.Backend();await WaitFrames(20);var after=SettingsStore.Backend();
            if(after.Filtered-before.Filtered<15||after.Raw!=before.Raw)throw new Exception("Recovered model interleaves original frames.");
            Save($"PASS: enabling automatic sizing recovers actual filtering: {modelHost.ReadStatus(4).Width}px, {after.Filtered-before.Filtered} filtered / {after.Raw-before.Raw} original; {PreviewRecoveryCount} automatic preview reconnect(s), no service or driver restart.");
            // Switch while inference is running; ensure a previous backend's result
            // and status cannot be presented as success for the new selection.
            foreach(int backend in new[]{2,7,4}){
                BackendChoice.SelectedIndex=backend;DenoiseChoice.SelectedIndex=1;
                await WaitFor(()=>{var s=SettingsStore.Backend();return s.Fresh&&s.Kind==backend&&s.Level==1&&s.Filtered>=6;},"Model switch failed: "+ModelHost.Names[backend]);
                await WaitFor(()=>{UpdateEffectLabel();return DenoiseDeliveryTitle.Text.StartsWith("Denoising active");},"Live delivery status failed after switch.",10);
                Save("PASS: live switch to "+ModelHost.Names[backend]+" delivers filtered frames with matching status.");
            }
            DenoiseChoice.SelectedIndex=0;await WaitFrames(40);var off=await SamplePerformance();
            if(off.Fps<27||off.Age>150||DenoiseDeliveryPanel.Visibility!=Visibility.Collapsed)throw new Exception("Off failed to recover normal capture / status.");
            Save($"PASS: Off restores {off.Fps:0.0} fps, {off.Age:0.0} ms median sample age. {PreviewRecoveryCount} automatic preview reconnect(s) in total; no service or driver restart; no images saved.");
        }catch(Exception e){Save("FAIL: "+e);throw;}
        finally{modelHost.Stop();SettingsStore.Write("DenoiseBackend",oldBackend);SettingsStore.Write("DenoiseLevel",oldLevel);SettingsStore.Write("DenoiseModelWidth",oldWidth);SettingsStore.Write("DenoiseAutoSize",oldAuto);foreach(var width in widths)SettingsStore.Write($"DenoiseModelWidth{width.Key}",width.Value);File.WriteAllLines(path,log);Close();}
    }
}
