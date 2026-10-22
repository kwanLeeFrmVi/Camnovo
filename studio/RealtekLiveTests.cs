using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LenovoCameraStudio;

public partial class MainWindow
{
    internal async Task RealtekLiveTests(string path)
    {
        ShowInTaskbar=false;Opacity=0;Show();var log=new List<string>();
        var original=settings.Values.Where(s=>s.Read()).ToDictionary(s=>s,s=>(s.Value,s.Mode));
        int oldFlags=SettingsStore.Read("FlipFlags"),oldLevel=DenoiseChoice.SelectedIndex,oldBackend=BackendChoice.SelectedIndex;
        int oldShadow=SettingsStore.Read("ShadowCleanupEnabled"),oldWidth=SettingsStore.Read("DenoiseModelWidth",640);
        int oldFastWidth=SettingsStore.Read("DenoiseModelWidth2",640);
        try{
            const string ours="{73DF2569-1A6D-4976-BA38-F089C0162864}",vendor="{09824200-9A44-4B06-8C74-92D99E09B435}";
            if(SettingsStore.IsAttached(ours,new[]{vendor})||!SettingsStore.IsAttached(vendor,new[]{vendor,ours})||!SettingsStore.IsAttached(ours,null)||SettingsStore.IsAttached(vendor,null))throw new Exception("Effective attachment detection is incorrect.");
            if(!SettingsStore.DriverProvider.Contains("Realtek",StringComparison.OrdinalIgnoreCase)||!SettingsStore.Installed)throw new Exception("Realtek and Camnovo are not both attached to the active camera.");
            if(FramingTab.Visibility!=Visibility.Visible||!settings.ContainsKey((1,3))||!settings.ContainsKey((1,0))||!settings.ContainsKey((1,1)))throw new Exception("Realtek framing controls missing.");
            DenoiseChoice.SelectedIndex=0;ShadowCleanupToggle.IsChecked=false;
            var deadline=DateTime.UtcNow.AddSeconds(15);while(!running&&DateTime.UtcNow<deadline)await Task.Delay(40);if(!running)throw new Exception("Preview did not start.");
            var shutter=settings[(1,4)];SetHardware(shutter,shutter.Snap(-6),2);refresh[shutter]();
            await WaitFrames(40);var baseline=await SamplePerformance();
            log.Add($"PASS: active Realtek driver + Camnovo attachment; preview {bitmap!.PixelWidth}x{bitmap.PixelHeight}, {baseline.Fps:0.0} fps, median sample age {baseline.Age:0} ms.");
            foreach(var target in new[]{(3,200),(0,8),(1,-8)}){
                var setting=settings[(1,target.Item1)];int value=setting.Snap(target.Item2);var slider=sliders[setting];
                slider.Value=(value-setting.Min)/(double)Math.Max(1,setting.Step);
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent});
                if(!setting.Read()||setting.Value!=value||setting.Mode!=2)throw new Exception("Framing slider did not apply on release: "+setting.Name);
                await WaitFrames(12);log.Add($"PASS: {setting.Name} slider release: {setting.Display(value)}, exact driver readback and continuing preview.");
            }
            FramingControls.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach(int property in new[]{0,1,3}){var setting=settings[(1,property)];if(!setting.Read()||setting.Value!=setting.Default)throw new Exception("Framing reset failed.");}
            log.Add("PASS: Reset framing restores driver defaults.");

            MirrorToggle.IsChecked=!MirrorToggle.IsChecked;FlipToggle.IsChecked=!FlipToggle.IsChecked;
            if(SettingsStore.Read("FlipFlags")!=((MirrorToggle.IsChecked==true?1:0)|(FlipToggle.IsChecked==true?2:0)))throw new Exception("Immediate orientation settings failed.");
            await WaitFrames(20);log.Add("PASS: immediate mirror/flip preferences and continuing chained preview; native installer trace separately verifies orientation delivery.");
            BackendChoice.SelectedIndex=0;DenoiseChoice.SelectedIndex=1;
            await WaitFrames(65);await VerifyFilteredDelivery("Realtek + RTX Low",log);
            DenoiseChoice.SelectedIndex=0;await WaitFrames(40);var recovered=await SamplePerformance();
            if(recovered.Fps<baseline.Fps*.8||recovered.Age>baseline.Age+200)throw new Exception("RTX Off did not recover toward the unfiltered baseline.");
            log.Add($"PASS: RTX Off recovery: {recovered.Fps:0.0} fps, median sample age {recovered.Age:0} ms.");
            BackendChoice.SelectedIndex=1;DenoiseChoice.SelectedIndex=2;
            await WaitFrames(65);await VerifyFilteredDelivery("Realtek + Bilateral GPU",log);
            DenoiseChoice.SelectedIndex=0;
            if(ModelHost.Available(2)){
                BackendChoice.SelectedIndex=2;ModelSizeChoice.SelectedIndex=3;DenoiseChoice.SelectedIndex=1;
                var warmDeadline=DateTime.UtcNow.AddSeconds(45);bool filtered=false;
                while(DateTime.UtcNow<warmDeadline){await Task.Delay(100);var state=SettingsStore.Backend();if(state.Fresh&&state.Kind==2&&state.Level==1&&state.Filtered>=5&&state.Result==0){filtered=true;break;}}
                if(!filtered)throw new Exception("FastDVDnet did not finish loading: "+modelHost.Message);
                await WaitFrames(30);await VerifyFilteredDelivery("Realtek + FastDVDnet",log);
                var neural=await SamplePerformance();log.Add($"MEASURED: FastDVDnet 640 px: {neural.Fps:0.0} fps, median sample age {neural.Age:0} ms.");
                if(neural.Age>=500)throw new Exception("Neural sample age exceeded the requested target during this check.");
                DenoiseChoice.SelectedIndex=0;await WaitFrames(40);
            }
            await StopPreview();await StartPreview();await WaitFrames(15);
            if(!settings[(1,3)].Read()||!SettingsStore.Installed)throw new Exception("Camera restart lost framing or Camnovo attachment.");
            log.Add("PASS: preview stop/start preserves framing access and the combined attachment.");
        }catch(Exception error){log.Add("FAIL: "+error);throw;}
        finally{
            foreach(var item in original)try{SetHardware(item.Key,item.Value.Value,item.Value.Mode);}catch(Exception error){log.Add("RESTORE: "+error.Message);}
            modelHost.Stop();SettingsStore.Write("FlipFlags",oldFlags);SettingsStore.Write("DenoiseBackend",oldBackend);SettingsStore.Write("DenoiseLevel",oldLevel);SettingsStore.Write("ShadowCleanupEnabled",oldShadow);SettingsStore.Write("DenoiseModelWidth",oldWidth);SettingsStore.Write("DenoiseModelWidth2",oldFastWidth);
            File.WriteAllLines(path,log);Close();
        }
    }
}
