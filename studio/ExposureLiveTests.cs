using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LenovoCameraStudio;

public partial class MainWindow
{
    internal async Task ExposureLiveTests(string path)
    {
        ShowInTaskbar=false;Opacity=0;Show();var log=new List<string>();
        var shutter=settings[(1,4)];var backlight=settings[(0,8)];
        shutter.Read();backlight.Read();
        int oldShutter=shutter.Value,oldMode=shutter.Mode,oldBacklight=backlight.Value,oldBacklightMode=backlight.Mode;
        int oldDenoise=DenoiseChoice.SelectedIndex;
        try{
            DenoiseChoice.SelectedIndex=0;
            var deadline=DateTime.UtcNow.AddSeconds(15);while(!running&&DateTime.UtcNow<deadline)await Task.Delay(40);
            if(!running)throw new Exception("Preview failed to start.");
            await WaitFrames(12);controlsTimer.Stop();while(pollingHardware)await Task.Delay(5);
            if(CameraSetting.ShutterTime(156250)!="1/64 s"||CameraSetting.ShutterTime(20000000)!="2 s")throw new Exception("Metadata shutter units are wrong.");
            int target=shutter.Snap(-6);
            SetHardware(shutter,target,2);refresh[shutter]();
            autoControls[shutter].IsChecked=true;
            if(!shutter.Read()||shutter.Mode!=1)throw new Exception("Automatic exposure checkbox did not reach camera.");
            await WaitFrames(12);await PollHardware();
            bool metadata=CameraApi.PreviewExposure(out ulong exposure)==0&&exposure>0;
            string expected=metadata?CameraSetting.ShutterTime(exposure):"Auto";
            if(!metadata&&controlValues[shutter].Text!=expected)throw new Exception("Last manual shutter is still presented as live auto exposure.");
            log.Add($"PASS: automatic checkbox sets camera mode 1; shutter display '{controlValues[shutter].Text}'; capture exposure metadata {(metadata?"available":"unavailable")}. Manual cached value {shutter.Display(shutter.Value)} is not presented as a live auto measurement.");

            int beforeMode=shutter.Mode;
            for(int i=0;i<5;i++){await PollHardware();await Task.Delay(60);}
            if(!shutter.Read()||shutter.Mode!=beforeMode)throw new Exception("Read-only polling changed exposure mode.");
            log.Add("PASS: repeated read-only refresh keeps automatic exposure enabled.");

            CameraApi.Check(CameraApi.CameraSet(1,4,target,2));await PollHardware();
            if(autoControls[shutter].IsChecked!=false||controlValues[shutter].Text!=shutter.Display(target))throw new Exception("Live refresh did not reflect an external manual exposure change.");
            log.Add("PASS: live refresh reflects external manual mode and shutter value without a write-back loop.");

            var pending=PollHardware();int newer=shutter.Snap(target+Math.Max(1,shutter.Step));
            SetHardware(shutter,newer,2);refresh[shutter]();await pending;
            if(shutter.Mode!=2||shutter.Value!=newer||controlValues[shutter].Text!=shutter.Display(newer))throw new Exception("An older poll overwrote the latest user setting.");
            log.Add("PASS: an in-flight poll cannot overwrite a newer manual setting.");

            SetHardware(shutter,target,2);refresh[shutter]();autoControls[shutter].IsChecked=true;
            if(!controlHints[backlight].Text.Contains("bright background"))throw new Exception("Automatic backlight guidance did not refresh immediately.");
            for(int level=backlight.Min;level<=backlight.Max;level+=Math.Max(1,backlight.Step)){
                var slider=sliders[backlight];slider.Value=(level-backlight.Min)/(double)Math.Max(1,backlight.Step);
                slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent});
                if(!backlight.Read()||backlight.Value!=level)throw new Exception("Backlight slider did not commit on release.");
                await WaitFrames(12);double luma=PresentedLuma();
                log.Add($"READBACK: backlight {level}, auto exposure; presented mean luma {luma:0.00}. Acceptance is not proof of visible compensation in every scene.");
            }
            SetHardware(shutter,target,2);refresh[shutter]();await PollHardware();
            if(!controlHints[backlight].Text.Contains("Automatic exposure"))throw new Exception("Manual-mode backlight guidance missing.");
            log.Add("PASS: backlight slider values reach camera immediately; guidance distinguishes manual and automatic exposure.");
        }catch(Exception error){log.Add("FAIL: "+error);throw;}
        finally{
            try{SetHardware(backlight,oldBacklight,oldBacklightMode);SetHardware(shutter,oldShutter,oldMode);}finally{
                SettingsStore.Write("DenoiseLevel",oldDenoise);
                log.Add("Original shutter mode/value, backlight and denoise level restored. No camera images saved.");
                File.WriteAllLines(path,log);Close();
            }
        }
    }
}
