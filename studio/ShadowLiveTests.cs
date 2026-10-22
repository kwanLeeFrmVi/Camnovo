using System.IO;
using System.Windows;
using System.Windows.Input;

namespace LenovoCameraStudio;

public partial class MainWindow
{
    internal async Task ShadowLiveTests(string path)
    {
        ShowInTaskbar=false;Opacity=0;Show();
        var log=new List<string>();
        int oldEnabled=SettingsStore.Read("ShadowCleanupEnabled"),oldStrength=SettingsStore.Read("ShadowCleanupStrength",70),oldLevel=SettingsStore.Read("DenoiseLevel");
        void ReleaseStrength()=>ShadowStrengthSlider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent});
        void Require(bool value,string message){if(!value)throw new Exception(message);}
        try{
            Require(shadowReady&&ShadowCleanupToggle.IsEnabled,"Updated engine is not installed.");
            var deadline=DateTime.UtcNow.AddSeconds(15);while(!running&&DateTime.UtcNow<deadline)await Task.Delay(40);
            Require(running,"Automatic preview failed.");
            DenoiseChoice.SelectedIndex=0;SaveDenoise();ShadowCleanupToggle.IsChecked=false;SaveShadowCleanup();await WaitFrames(35);
            var baseline=await SamplePerformance();double offColour=ShadowColourMetric();
            ShadowCleanupToggle.IsChecked=true;
            Require(SettingsStore.Read("ShadowCleanupEnabled")==1,"Toggle did not apply immediately.");
            ShadowStrengthSlider.Value=100;ReleaseStrength();
            Require(SettingsStore.Read("ShadowCleanupStrength")==100,"Slider release did not apply strength.");
            await WaitFrames(35);var active=await SamplePerformance();double onColour=ShadowColourMetric();
            Require(active.Fps>0&&active.Age<baseline.Age+150,"Cleanup stalled preview or added excessive frame age.");
            log.Add($"PASS: independent cleanup with denoising Off; preview {baseline.Fps:0.0} → {active.Fps:0.0} fps; median sample age {baseline.Age:0.0} → {active.Age:0.0} ms.");
            log.Add($"OBSERVATION: dark-area RGB colour spread {offColour:0.00} → {onColour:0.00}; changing live scene, not a calibrated noise metric. No pictures saved.");
            BeginOriginal();Require(originalHeld&&SettingsStore.Read("ShadowCleanupEnabled")==0&&SettingsStore.Read("DenoiseLevel")==0,"Hold Original did not bypass cleanup.");
            EndOriginal();Require(!originalHeld&&SettingsStore.Read("ShadowCleanupEnabled")==1,"Original release did not restore cleanup.");
            ShadowStrengthSlider.Value=70;ReleaseStrength();Require(SettingsStore.Read("ShadowCleanupStrength")==70,"Strength did not restore.");
            ShadowCleanupToggle.IsChecked=false;Require(SettingsStore.Read("ShadowCleanupEnabled")==0&&!ShadowStrengthSlider.IsEnabled,"Off toggle did not bypass cleanup.");
            await WaitFrames(35);var recovered=await SamplePerformance();
            log.Add($"PASS: immediate toggle and slider-release writes; Original bypass/restore; Off preview {recovered.Fps:0.0} fps, age {recovered.Age:0.0} ms. Original user settings restored.");
        }catch(Exception error){log.Add("FAIL: "+error.Message);throw;}
        finally{
            EndOriginal();loading=true;ShadowCleanupToggle.IsChecked=oldEnabled==1;ShadowStrengthSlider.Value=oldStrength;DenoiseChoice.SelectedIndex=oldLevel;loading=false;
            SettingsStore.Write("ShadowCleanupStrength",oldStrength);SettingsStore.Write("ShadowCleanupEnabled",oldEnabled);SettingsStore.Write("DenoiseLevel",oldLevel);
            File.WriteAllLines(path,log);Close();
        }
    }
    private double ShadowColourMetric()
    {
        if(bitmap is null)return 0;
        byte[] pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
        double sum=0;int count=0;
        for(int y=4;y<bitmap.PixelHeight;y+=9)for(int x=4;x<bitmap.PixelWidth;x+=9){int i=(y*bitmap.PixelWidth+x)*4;int b=pixels[i],g=pixels[i+1],r=pixels[i+2];double light=.0722*b+.7152*g+.2126*r;if(light>4&&light<60){sum+=Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b));++count;}}
        return sum/Math.Max(1,count);
    }
}
