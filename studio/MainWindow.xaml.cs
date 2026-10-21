using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LenovoCameraStudio;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer renderTimer=new(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(16)};
    private readonly DispatcherTimer reconnectTimer=new(){Interval=TimeSpan.FromSeconds(2)};
    private readonly DispatcherTimer controlsTimer=new(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly Dictionary<(int,int),CameraSetting> settings=[];
    private readonly Dictionary<CameraSetting,Action> refresh=[];
    private readonly Dictionary<CameraSetting,Action> present=[];
    private readonly Dictionary<CameraSetting,Func<bool>> canRefresh=[];
    private readonly Dictionary<CameraSetting,CheckBox> autoControls=[];
    private readonly Dictionary<CameraSetting,TextBlock> controlValues=[],controlHints=[];
    private readonly Dictionary<CameraSetting,Slider> sliders=[];
    private readonly Dictionary<string,Dictionary<string,int[]>> presets=[];
    private WriteableBitmap? bitmap;
    private ulong displayedSerial;
    private bool loading=true,running,originalHeld,closing,busy;
    private long renderCount;
    private int appliedResolution;
    private bool hardwareLoaded;
    private bool pollingHardware;
    private long hardwareRevision;
    private readonly object hardwarePollGate=new();


    private long filterWindowTick,filterWindowFrames,filterWindowRaw;
    private (int,int) filterWindowMode=(-1,-1);
    private string filterShare="";
    private readonly ModelHost modelHost=new();
    private static readonly int[] ModelWidths=[128,192,320,640,960,1280,1920];
    private static int DefaultModelWidth(int backend)=>backend is 3 or 7?192:backend is 5 or 6 or 8?320:640;
    internal readonly List<string> Verification=[];

    public MainWindow(bool autoPreview=true)
    {
        InitializeComponent();ApplyPalette();
        ThemeChoice.SelectionChanged+=(_,_)=>ApplyPalette();
        SystemEvents.UserPreferenceChanged+=OnWindowsThemeChanged;
        LoadHardware();LoadPresets();
        int flags=SettingsStore.Read("FlipFlags");MirrorToggle.IsChecked=(flags&1)!=0;FlipToggle.IsChecked=(flags&2)!=0;
        DenoiseChoice.SelectedIndex=Math.Clamp(SettingsStore.Read("DenoiseLevel"),0,4);
        BackendChoice.SelectedIndex=Math.Clamp(SettingsStore.Read("DenoiseBackend"),0,8);
        ModelSizeChoice.SelectedIndex=Array.IndexOf(ModelWidths,SettingsStore.Read("DenoiseModelWidth",640));if(ModelSizeChoice.SelectedIndex<0)ModelSizeChoice.SelectedIndex=3;
        ModelSizeChoice.SelectionChanged+=(_,_)=>{if(!loading){int width=ModelWidths[ModelSizeChoice.SelectedIndex];SettingsStore.Write("DenoiseModelWidth",width);SettingsStore.Write($"DenoiseModelWidth{BackendChoice.SelectedIndex}",width);Status("Model processing size updated. The 500 ms limit remains active.");}};
        ResolutionChoice.SelectedIndex=appliedResolution=SettingsStore.Read("Enable1440p")==1?1:0;
        bool writable=SettingsStore.Installed&&SettingsStore.Writable;MirrorToggle.IsEnabled=FlipToggle.IsEnabled=ResolutionChoice.IsEnabled=writable;

        BackendChoice.IsEnabled=writable;RefreshBackendAvailability();
        MirrorToggle.Checked+=OrientationChanged;MirrorToggle.Unchecked+=OrientationChanged;FlipToggle.Checked+=OrientationChanged;FlipToggle.Unchecked+=OrientationChanged;
        DenoiseChoice.SelectionChanged+=(_,_)=>SaveDenoise();
        BackendChoice.SelectionChanged+=(_,_)=>{int backend=BackendChoice.SelectedIndex;if(!loading&&backend>=2){int width=SettingsStore.Read($"DenoiseModelWidth{backend}",DefaultModelWidth(backend));loading=true;ModelSizeChoice.SelectedIndex=Math.Max(0,Array.IndexOf(ModelWidths,width));loading=false;SettingsStore.Write("DenoiseModelWidth",width);}RefreshBackendAvailability();SaveDenoise();};
        ResolutionChoice.SelectionChanged+=async(_,_)=>{if(!loading)await ChangeResolution();};
        OriginalButton.PreviewMouseLeftButtonDown+=(_,e)=>{BeginOriginal();OriginalButton.CaptureMouse();e.Handled=true;};
        OriginalButton.PreviewMouseLeftButtonUp+=(_,e)=>{EndOriginal();OriginalButton.ReleaseMouseCapture();e.Handled=true;};
        OriginalButton.LostMouseCapture+=(_,_)=>EndOriginal();
        OriginalButton.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Space){BeginOriginal();e.Handled=true;}};
        OriginalButton.PreviewKeyUp+=(_,e)=>{if(e.Key==Key.Space){EndOriginal();e.Handled=true;}};
        ZoomChoice.SelectionChanged+=(_,_)=>LayoutPreview();DpiChanged+=(_,_)=>LayoutPreview();
        DefaultsButton.Click+=(_,_)=>RestoreDefaults();SavePresetButton.Click+=(_,_)=>SavePreset();
        CameraMenuButton.Click+=(_,_)=>{CameraMenuButton.ContextMenu.PlacementTarget=CameraMenuButton;CameraMenuButton.ContextMenu.Placement=PlacementMode.Bottom;CameraMenuButton.ContextMenu.IsOpen=true;};
        InstallMenuItem.Click+=async(_,_)=>await RunSetup("--install-engine");
        RestartMenuItem.Click+=async(_,_)=>await RunSetup("--restart-camera");
        RestoreMenuItem.Click+=async(_,_)=>await RunSetup("--restore-engine");
        PresetChoice.SelectionChanged+=(_,_)=>{if(!loading&&PresetChoice.SelectedIndex>0)ApplyPreset(PresetChoice.SelectedItem?.ToString()??"");};
        ResetEffectsButton.Click+=async(_,_)=>{loading=true;MirrorToggle.IsChecked=FlipToggle.IsChecked=false;DenoiseChoice.SelectedIndex=0;ResolutionChoice.SelectedIndex=0;loading=false;OrientationChanged(null,null);SaveDenoise();if(appliedResolution!=0)await ChangeResolution();};
        renderTimer.Tick+=(_,_)=>RenderLatest();
        controlsTimer.Tick+=async(_,_)=>await PollHardware();
        reconnectTimer.Tick+=async(_,_)=>{if(closing)return;UpdateEffectLabel();if(running&&bitmap is not null){CameraApi.PreviewStats(out double fps,out double age,out double copy);VideoInfo.Text=$"{bitmap.PixelWidth} × {bitmap.PixelHeight}  ·  {fps:0.0} fps";PerformanceInfo.Text=$"Frame age {age:0} ms  ·  Preview copy {copy:0.0} ms";}if(!running&&!busy)await StartPreview();};
        if(autoPreview){
            EventHandler? firstPaint=null;
            firstPaint=async(_,_)=>{ContentRendered-=firstPaint;reconnectTimer.Start();controlsTimer.Start();await StartPreview();};
            ContentRendered+=firstPaint;
        }
        Closing+=(_,_)=>{closing=true;EndOriginal();modelHost.Dispose();renderTimer.Stop();reconnectTimer.Stop();controlsTimer.Stop();SystemEvents.UserPreferenceChanged-=OnWindowsThemeChanged;lock(hardwarePollGate)CameraApi.CameraClose();};
        loading=false;if(autoPreview&&BackendChoice.SelectedIndex>=2&&DenoiseChoice.SelectedIndex>0)try{modelHost.Start();}catch(Exception error){Status(error.Message,true);}UpdateEffectLabel();if(!writable)Status("Choose Camera → Install / update camera processing to enable effects.");
    }
    private void OnWindowsThemeChanged(object sender,UserPreferenceChangedEventArgs e)=>Dispatcher.BeginInvoke(ApplyPalette);
    private void ApplyPalette()
    {
        if(ThemeChoice is null)return;
        int selected=ThemeChoice.SelectedIndex;
        Application.Current.ThemeMode=selected==1?ThemeMode.Light:selected==2?ThemeMode.Dark:ThemeMode.System;
        ThemeMode=Application.Current.ThemeMode;
        bool light=selected==1||(selected==0&&Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1))!=0);
        var colors=light?new[]{"#F3F3F3","#FFFFFF","#E2E2E2","#191919","#626262"}:new[]{"#202020","#2B2B2B","#3D3D3D","#F3F3F3","#B9B9B9"};
        string[] keys=["CanvasBrush","CardBrush","StrokeBrush","InkBrush","MutedBrush"];
        for(int i=0;i<keys.Length;i++)Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if(SystemParameters.HighContrast){Application.Current.Resources["CanvasBrush"]=SystemColors.WindowBrush;Application.Current.Resources["CardBrush"]=SystemColors.WindowBrush;Application.Current.Resources["InkBrush"]=SystemColors.WindowTextBrush;Application.Current.Resources["MutedBrush"]=SystemColors.WindowTextBrush;Application.Current.Resources["StrokeBrush"]=SystemColors.WindowTextBrush;}
    }
    private static TextBlock Text(string text,bool muted=false){var item=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap};if(muted)item.SetResourceReference(StyleProperty,"Muted");return item;}
    private void Status(string message,bool error=false){StatusText.Text=message;StatusText.SetResourceReference(TextBlock.ForegroundProperty,error?"SystemFillColorCriticalBrush":"MutedBrush");}
    private void LoadHardware()
    {
        foreach(var spec in new[]{("White balance",0,7),("Brightness",0,0),("Contrast",0,1),("Saturation",0,3),("Sharpness",0,4),("Gamma",0,5),("Hue",0,2),("Shutter speed",1,4),("Backlight compensation",0,8)}){
            var setting=new CameraSetting(spec.Item1,spec.Item2,spec.Item3);if(setting.Read()){settings[(setting.Group,setting.Property)]=setting;AddSetting(setting,setting.Group==1||setting.Property==8?ExposureControls:ImageControls);}
        }
        hardwareLoaded=settings.Count>0;
        if(settings.TryGetValue((1,4),out var exposure)){
            var row=new WrapPanel{Margin=new Thickness(0,2,0,15)};
            foreach(var preset in new[]{("Motion · 1/64 s",-6),("Low light · 1/16 s",-4)}){var button=new Button{Content=preset.Item1,Padding=new Thickness(11,7,11,7),Margin=new Thickness(0,0,8,8)};int value=preset.Item2;button.Click+=(_,_)=>{try{SetHardware(exposure,exposure.Snap(value),2);refresh[exposure]();}catch(Exception e){Status(e.Message,true);}};row.Children.Add(button);}ExposureControls.Children.Add(row);
        }
        var priority=new CameraSetting("Maintain 30 fps",1,19);
        if(priority.Read()){
            var toggle=new CheckBox{Content="Prioritize frame rate over low-light brightness",IsChecked=priority.Value==0,Margin=new Thickness(0,7,0,10)};
            toggle.Checked+=(_,_)=>{try{SetHardware(priority,0,0);}catch(Exception e){Status(e.Message,true);}};toggle.Unchecked+=(_,_)=>{try{SetHardware(priority,1,0);}catch(Exception e){Status(e.Message,true);}};ExposureControls.Children.Add(toggle);
            ExposureControls.Children.Add(Text("With automatic exposure, turning this off allows a slower frame rate in low light. The camera decides whether to use it.",true));
        }
        ExposureControls.Children.Add(Text("Longer shutter times can blur motion and add camera delay. Manual shutter values are shown as fractions of a second.",true));
        if(!hardwareLoaded)ImageControls.Children.Add(Text("Connect the Lenovo webcam and reopen the app.",true));
    }
    private void AddSetting(CameraSetting setting,Panel parent)
    {
        var row=new StackPanel{Margin=new Thickness(0,0,0,22)};
        var header=new DockPanel();var value=Text("");value.FontWeight=FontWeights.SemiBold;value.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(value,Dock.Right);header.Children.Add(value);header.Children.Add(Text(setting.Name));row.Children.Add(header);
        var slider=new Slider{Minimum=0,Maximum=Math.Max(1,(setting.Max-setting.Min)/Math.Max(1,setting.Step)),Tag=setting,Name=$"Slider_{setting.Group}_{setting.Property}",SmallChange=1,LargeChange=1};
        sliders[setting]=slider;
        AutomationProperties.SetName(slider,setting.Name);row.Children.Add(slider);
        var auto=new CheckBox{Content=setting.Group==1&&setting.Property==4?"Automatic exposure":"Automatic",Visibility=(setting.Caps&1)!=0?Visibility.Visible:Visibility.Collapsed,Margin=new Thickness(0,9,0,0)};row.Children.Add(auto);
        var hint=Text("",true);hint.Margin=new Thickness(0,7,0,0);row.Children.Add(hint);parent.Children.Add(row);
        autoControls[setting]=auto;controlValues[setting]=value;controlHints[setting]=hint;
        bool changing=false,dragging=false,pending=false;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        void Present(){changing=true;try{
            slider.Value=(setting.Value-setting.Min)/(double)Math.Max(1,setting.Step);auto.IsChecked=(setting.Mode&1)!=0;value.Text=setting.Display(setting.Value);hint.Text="";
            if(setting.Group==1&&setting.Property==4&&(setting.Mode&1)!=0){
                bool live=CameraApi.PreviewExposure(out ulong time)==0&&time>0;
                value.Text=live?CameraSetting.ShutterTime(time):"Auto";
                hint.Text=live?"Camera controls exposure automatically; image noise can also change.":"Camera controls exposure automatically; image noise can also change. Live shutter time is unavailable.";
            }
            if(setting.Group==0&&setting.Property==8){
                bool automatic=settings.TryGetValue((1,4),out var shutter)&&(shutter.Mode&1)!=0;
                hint.Text=automatic?"For a subject in front of a bright background. The effect depends on the scene.":"Use Automatic exposure for backlight metering.";
            }
            hint.Visibility=hint.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        }finally{changing=false;}}
        void Refresh(){if(setting.Read())Present();}
        present[setting]=Present;canRefresh[setting]=()=>!dragging&&!pending;
        void Commit(){timer.Stop();if(!pending)return;pending=false;try{SetHardware(setting,setting.Min+(int)Math.Round(slider.Value)*Math.Max(1,setting.Step),auto.IsChecked==true?1:2);Present();if(settings.TryGetValue((0,8),out var backlight)&&present.TryGetValue(backlight,out var update))update();}catch(Exception e){Refresh();Status(e.Message,true);}}
        refresh[setting]=Refresh;Refresh();timer.Tick+=(_,_)=>Commit();
        slider.ValueChanged+=(_,_)=>{if(changing||loading)return;++hardwareRevision;changing=true;auto.IsChecked=false;changing=false;value.Text=setting.Display(setting.Min+(int)Math.Round(slider.Value)*Math.Max(1,setting.Step));pending=true;timer.Stop();if(!dragging)timer.Start();};
        slider.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>{dragging=true;timer.Stop();}));
        slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>{dragging=false;Commit();}));
        slider.AddHandler(Mouse.PreviewMouseUpEvent,new MouseButtonEventHandler((_,_)=>{dragging=false;Commit();}),true);
        slider.KeyUp+=(_,_)=>Commit();slider.LostKeyboardFocus+=(_,_)=>Commit();
        void AutoChanged(){if(changing||loading)return;pending=true;Commit();}
        auto.Checked+=(_,_)=>AutoChanged();auto.Unchecked+=(_,_)=>AutoChanged();
    }
    private void SetHardware(CameraSetting setting,int value,int mode)
    {
        ++hardwareRevision;
        CameraApi.Check(CameraApi.CameraSet(setting.Group,setting.Property,setting.Snap(value),mode));
        if(!setting.Read()||setting.Mode!=mode||(mode!=1&&setting.Value!=setting.Snap(value)))throw new InvalidOperationException($"The camera did not accept {setting.Name.ToLowerInvariant()}.");
        Status($"{setting.Name}: {(mode==1?"Automatic":setting.Display(setting.Value))}");
    }
    private async Task PollHardware()
    {
        if(closing||!running||busy||pollingHardware)return;
        pollingHardware=true;long revision=hardwareRevision;
        try{
            var targets=settings.Values.Where(s=>(s.Caps&1)!=0||(s.Group==0&&s.Property==8)).ToArray();
            var reads=await Task.Run(()=>{lock(hardwarePollGate){if(closing)return [];return targets.Select(s=>{var copy=new CameraSetting(s.Name,s.Group,s.Property);return (Setting:s,Copy:copy,Success:copy.Read());}).ToArray();}});
            if(closing||revision!=hardwareRevision)return;
            foreach(var read in reads)if(read.Success&&canRefresh[read.Setting]()){read.Setting.Value=read.Copy.Value;read.Setting.Mode=read.Copy.Mode;}
            foreach(var read in reads)if(read.Success&&canRefresh[read.Setting]())present[read.Setting]();
        }catch{ /* A transient camera read must not stop preview or change a setting. */ }
        finally{pollingHardware=false;}
    }
    private void OrientationChanged(object? sender,RoutedEventArgs? e)
    {
        if(loading)return;try{SettingsStore.Write("FlipFlags",(MirrorToggle.IsChecked==true?1:0)|(FlipToggle.IsChecked==true?2:0));Status("Orientation updated.");}catch(Exception error){Status(error.Message,true);}
    }
    private void SaveDenoise()
    {
        if(loading)return;try{if(BackendChoice.SelectedIndex>=2&&DenoiseChoice.SelectedIndex>0)modelHost.Start();else modelHost.Stop();SettingsStore.Write("DenoiseBackend",BackendChoice.SelectedIndex);SettingsStore.Write("DenoiseLevel",originalHeld?0:DenoiseChoice.SelectedIndex);UpdateEffectLabel();Status(DenoiseChoice.SelectedIndex==0?"Noise reduction off. Fresh frames bypass the worker immediately.":"Noise reduction updated. Use 100% zoom and hold Original to compare.");}catch(Exception e){SettingsStore.Write("DenoiseLevel",0);Status(e.Message,true);}
    }
    private void RefreshBackendAvailability(){bool nvidia=File.Exists(Path.Combine(SettingsStore.InstallFolder,@"Nvidia\NVVideoEffects.dll"));((ComboBoxItem)BackendChoice.Items[0]).IsEnabled=nvidia;for(int i=2;i<ModelHost.Names.Length;i++)((ComboBoxItem)BackendChoice.Items[i]).IsEnabled=ModelHost.Available(i);int selected=BackendChoice.SelectedIndex;DenoiseChoice.IsEnabled=BackendChoice.IsEnabled&&((ComboBoxItem)BackendChoice.Items[selected]).IsEnabled&&(selected<2||ModelHost.EngineCurrent);BackendDescription.Text=ModelHost.Descriptions[selected];ModelOptions.Visibility=selected>=2?Visibility.Visible:Visibility.Collapsed;}
    private void UpdateEffectLabel(){
        int wanted=originalHeld?0:DenoiseChoice.SelectedIndex;
        var backend=SettingsStore.Backend();
        EffectInfo.Text=originalHeld?"Original feed":wanted==0?"Noise reduction off":$"{ModelHost.Names[BackendChoice.SelectedIndex]} · {new[]{"Off","Low","Medium","High","Ultra"}[wanted]}";
        if(BackendChoice.SelectedIndex>=2)ModelStatus.Text=!ModelHost.Available(BackendChoice.SelectedIndex)?"Model files missing. Keep LenovoDenoiserRuntime beside the app folder.":!ModelHost.EngineCurrent?"Models are installed. Choose Camera → Install / update camera processing to activate them and the 500 ms limit.":modelHost.Message;
        long now=Environment.TickCount64;
        if(filterWindowMode!=(backend.Kind,backend.Level)||backend.Filtered<filterWindowFrames||backend.Raw<filterWindowRaw){filterWindowMode=(backend.Kind,backend.Level);filterWindowTick=now;filterWindowFrames=backend.Filtered;filterWindowRaw=backend.Raw;filterShare="";}
        if(now-filterWindowTick>=1000){long filtered=backend.Filtered-filterWindowFrames,raw=backend.Raw-filterWindowRaw;filterShare=filtered+raw>0?$" · {100.0*filtered/(filtered+raw):0}% filtered":"";filterWindowTick=now;filterWindowFrames=backend.Filtered;filterWindowRaw=backend.Raw;}
        if(running&&wanted>0)EffectInfo.Text+=backend.Fresh&&backend.Level==wanted&&backend.Kind==BackendChoice.SelectedIndex?(backend.Result>=0&&backend.Filtered>0?$" · Filter {backend.Milliseconds:0.0} ms{filterShare}":backend.Result>=0?" · warming / waiting":backend.Result==unchecked((int)0x800705B4)&&BackendChoice.SelectedIndex>=2?" · 500 ms limit / original feed":" · bypassed (error)"):" · waiting for confirmation";
        EffectInfo.ToolTip=backend.Fresh?$"Delivered filtered frames: {backend.Filtered}; original warm-up / fallback frames: {backend.Raw}. Result: 0x{backend.Result:X8}. The percentage uses recent delivered frames. Filter time includes transfers and cleanup; it is not total pipeline latency.":"Waiting for camera backend status.";
        OriginalButton.IsEnabled=running&&DenoiseChoice.IsEnabled&&wanted>0&&!originalHeld;
        if(originalHeld)OriginalButton.IsEnabled=true;
    }
    private void BeginOriginal(){if(originalHeld||!running||DenoiseChoice.SelectedIndex==0)return;try{SettingsStore.Write("DenoiseLevel",0);originalHeld=true;OriginalButton.Content="Original shown";UpdateEffectLabel();}catch(Exception e){Status(e.Message,true);}}
    private void EndOriginal(){if(!originalHeld)return;try{SettingsStore.Write("DenoiseLevel",DenoiseChoice.SelectedIndex);originalHeld=false;OriginalButton.Content="Hold original";UpdateEffectLabel();}catch(Exception e){Status(e.Message,true);}}
    private async Task StartPreview()
    {
        if(busy||running||closing)return;busy=true;try{int width=ResolutionChoice.SelectedIndex==1?2560:1920,height=ResolutionChoice.SelectedIndex==1?1440:1080;await Task.Run(()=>CameraApi.Check(CameraApi.PreviewStartSized(width,height)));if(closing)return;displayedSerial=0;running=true;LiveBadge.Text="LIVE";renderTimer.Start();UpdateEffectLabel();Status("Live preview. All controls apply automatically.");}catch(Exception e){running=false;LiveBadge.Text="RECONNECTING";Status($"{e.Message} Retrying automatically.",true);}finally{busy=false;}
    }
    private async Task StopPreview()
    {
        if(busy)return;busy=true;EndOriginal();renderTimer.Stop();try{await Task.Run(CameraApi.PreviewStop);running=false;bitmap=null;PreviewImage.Source=null;LiveBadge.Text="RECONNECTING";PreviewPlaceholder.Visibility=Visibility.Visible;VideoInfo.Text="Reconnecting camera…";PerformanceInfo.Text="No recording. No snapshots saved.";UpdateEffectLabel();}catch(Exception e){Status(e.Message,true);}finally{busy=false;}
    }
    private void RenderLatest()
    {
        if(closing)return;
        try{
            CameraApi.Check(CameraApi.PreviewInfo(out int width,out int height,out ulong serial));if(width==0||height==0||serial==displayedSerial)return;
            bool first=displayedSerial==0;
            if(bitmap is null||bitmap.PixelWidth!=width||bitmap.PixelHeight!=height){bitmap=new WriteableBitmap(width,height,96,96,PixelFormats.Bgr32,null);PreviewImage.Source=bitmap;LayoutPreview();}
            if(!bitmap.TryLock(new Duration(TimeSpan.Zero)))return;
            try{CameraApi.Check(CameraApi.PreviewCopy(bitmap.BackBuffer,width,height,bitmap.BackBufferStride,ref displayedSerial));bitmap.AddDirtyRect(new Int32Rect(0,0,width,height));}finally{bitmap.Unlock();}
            renderCount++;PreviewPlaceholder.Visibility=Visibility.Collapsed;
            if(first&&width!=(ResolutionChoice.SelectedIndex==1?2560:1920))Status($"Preview is {width} × {height}. To load the selected output, choose Camera → Restart camera.");
            if(renderCount%8==0){CameraApi.PreviewStats(out double fps,out double age,out double copy);VideoInfo.Text=$"{width} × {height}  ·  {fps:0.0} fps";PerformanceInfo.Text=age is >=0 and <10000?$"Frame age {age:0} ms  ·  Preview copy {copy:0.0} ms":$"Preview copy {copy:0.0} ms";UpdateEffectLabel();}
        }catch(Exception e){renderTimer.Stop();running=false;LiveBadge.Text="RECONNECTING";CameraApi.PreviewStop();Status($"{e.Message} Retrying automatically.",true);}
    }
    private void LayoutPreview()
    {
        bool fit=ZoomChoice.SelectedIndex==0||bitmap is null;
        PreviewScroll.HorizontalScrollBarVisibility=PreviewScroll.VerticalScrollBarVisibility=fit?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
        if(fit){PreviewImage.Width=PreviewImage.Height=PreviewCanvas.Width=PreviewCanvas.Height=double.NaN;PreviewImage.Stretch=Stretch.Uniform;PreviewScroll.ScrollToHome();}
        else{var dpi=VisualTreeHelper.GetDpi(this);double scale=ZoomChoice.SelectedIndex;PreviewCanvas.Width=PreviewImage.Width=bitmap!.PixelWidth*scale/dpi.DpiScaleX;PreviewCanvas.Height=PreviewImage.Height=bitmap.PixelHeight*scale/dpi.DpiScaleY;PreviewImage.Stretch=Stretch.Fill;}
    }
    private async Task ChangeResolution()
    {
        if(busy)return;int chosen=ResolutionChoice.SelectedIndex;bool resume=running;
        try{SettingsStore.Write("Enable1440p",chosen);if(resume)await StopPreview();else CameraApi.CameraClose();appliedResolution=chosen;if(resume)await StartPreview();Status("Output preference saved. If the size stays unchanged, choose Camera → Restart camera.");}catch(Exception e){Status(e.Message,true);}
    }
    private async Task RunSetup(string operation)
    {
        if(busy)return;
        bool resume=running;if(resume)await StopPreview();CameraApi.CameraClose();
        busy=true;CameraMenuButton.IsEnabled=SettingsTabs.IsEnabled=DefaultsButton.IsEnabled=false;
        try{
            Status("Windows approval is needed to update the camera pipeline. Close other camera apps first.");
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"CameraSetup.exe")){UseShellExecute=true,Verb="runas"};start.ArgumentList.Add(operation);
            if(operation=="--install-engine")start.ArgumentList.Add(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
            using var process=Process.Start(start)??throw new InvalidOperationException("Camera setup did not start.");await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new InvalidOperationException("Camera setup failed. See engine-setup-result.txt in the app folder for details.");
            bool writable=SettingsStore.Installed&&SettingsStore.Writable;MirrorToggle.IsEnabled=FlipToggle.IsEnabled=ResolutionChoice.IsEnabled=BackendChoice.IsEnabled=writable;RefreshBackendAvailability();
            Status("Camera setup completed. Preview reconnects automatically.");
        }catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==1223){Status("Camera setup cancelled.");}catch(Exception e){Status(e.Message,true);}finally{busy=false;CameraMenuButton.IsEnabled=SettingsTabs.IsEnabled=DefaultsButton.IsEnabled=true;}
        if(resume)await StartPreview();
    }
    private void RestoreDefaults(){try{foreach(var setting in settings.Values){SetHardware(setting,setting.Default,(setting.Caps&1)!=0?1:2);refresh[setting]();}Status("Camera defaults restored.");}catch(Exception e){Status(e.Message,true);}}
    private void LoadPresets(){using var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\LenovoCameraStudio\Presets");if(key is null)return;foreach(string name in key.GetValueNames()){try{var values=JsonSerializer.Deserialize<Dictionary<string,int[]>>(Convert.ToString(key.GetValue(name))??"");if(values is not null){presets[name]=values;PresetChoice.Items.Add(name);}}catch{}}}
    private void SavePreset()
    {
        var dialog=new Window{Title="Save camera preset",Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize,ThemeMode=ThemeMode};var panel=new StackPanel{Margin=new Thickness(24),Width=300};var input=new TextBox{Text="My settings",Margin=new Thickness(0,0,0,15)};var button=new Button{Content="Save preset",HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(14,8,14,8),IsDefault=true};panel.Children.Add(input);panel.Children.Add(button);dialog.Content=panel;button.Click+=(_,_)=>dialog.DialogResult=true;
        if(dialog.ShowDialog()!=true)return;string name=input.Text.Trim();if(name.Length is 0 or >60){Status("Choose a preset name with 1–60 characters.",true);return;}
        try{var values=new Dictionary<string,int[]>();foreach(var item in settings.Values)if(item.Read())values[$"{item.Group}:{item.Property}"]=[item.Value,item.Mode];using var key=Registry.CurrentUser.CreateSubKey(@"SOFTWARE\LenovoCameraStudio\Presets");key.SetValue(name,JsonSerializer.Serialize(values));if(!presets.ContainsKey(name))PresetChoice.Items.Add(name);presets[name]=values;loading=true;PresetChoice.SelectedItem=name;loading=false;Status($"Preset saved: {name}");}catch(Exception e){Status(e.Message,true);}
    }
    private void ApplyPreset(string name){if(!presets.TryGetValue(name,out var values))return;try{foreach(var item in settings.Values)if(values.TryGetValue($"{item.Group}:{item.Property}",out var entry)&&entry.Length==2){SetHardware(item,entry[0],entry[1]);refresh[item]();}Status($"Preset loaded: {name}");}catch(Exception e){Status(e.Message,true);}}
    internal async Task RenderTests(string path)
    {
        ShowInTaskbar=false;Opacity=0;Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        loading=true;BackendChoice.SelectedIndex=3;ModelSizeChoice.SelectedIndex=0;RefreshBackendAvailability();loading=false;
        foreach(int theme in new[]{1,2})foreach(int tab in new[]{0,1,2}){ThemeChoice.SelectedIndex=theme;SettingsTabs.SelectedIndex=tab;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();var content=(FrameworkElement)Content;double width=content.ActualWidth+56,height=content.ActualHeight+44;var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()){drawing.DrawRectangle((Brush)Application.Current.Resources["CanvasBrush"],null,new Rect(0,0,width,height));drawing.DrawRectangle(new VisualBrush(content),null,new Rect(28,20,content.ActualWidth,content.ActualHeight));}var image=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);image.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,Path.GetFileNameWithoutExtension(path)+(theme==1?"-light":"-dark")+(tab==0?"":tab==1?"-exposure":"-effects")+".png"));png.Save(file);}
        ThemeChoice.SelectedIndex=0;Close();
    }
    internal async Task LiveTests(string path)
    {
        ShowInTaskbar=false;Opacity=0;Show();var log=new List<string>();int oldFlags=SettingsStore.Read("FlipFlags"),oldDenoise=SettingsStore.Read("DenoiseLevel"),oldBackend=SettingsStore.Read("DenoiseBackend");
        var originalSettings=settings.Values.Where(s=>s.Read()).ToDictionary(s=>s,s=>(s.Value,s.Mode));
        var priority=new CameraSetting("Frame rate priority",1,19);bool hasPriority=priority.Read();int oldPriority=priority.Value,oldPriorityMode=priority.Mode;
        try{
            if(!hardwareLoaded)throw new Exception("No camera controls.");
            if(settings[(1,4)].Display(-6)!="1/64 s")throw new Exception("Shutter display regression.");
            MirrorToggle.IsChecked=!MirrorToggle.IsChecked;if(SettingsStore.Read("FlipFlags")!=((MirrorToggle.IsChecked==true?1:0)|(FlipToggle.IsChecked==true?2:0)))throw new Exception("Immediate mirror update failed.");
            DenoiseChoice.SelectedIndex=0;
            
            var deadline=DateTime.UtcNow.AddSeconds(12);while(!running&&DateTime.UtcNow<deadline)await Task.Delay(40);if(!running)throw new Exception("Preview did not start automatically.");
            if(FindName("PreviewButton") is not null)throw new Exception("Manual preview button remains.");
            SetHardware(settings[(1,4)],-6,2);if(hasPriority)SetHardware(priority,0,0);log.Add("Performance phase: fixed 1/64 s shutter, frame-rate priority enabled; original hardware settings restored afterward.");
            await WaitFrames(75);log.Add("PASS: preview starts automatically without a preview button.");
            CameraApi.PreviewStats(out double offFps,out double offAge,out double offCopy);log.Add($"PASS: processing off: {offFps:0.0} fps, sampled frame age {offAge:0.0} ms, native copy {offCopy:0.00} ms.");
            if(offFps<27)throw new Exception("Fixed-shutter baseline did not reach normal camera frame rate.");
            double baseline=(await SamplePerformance()).Age;
            BackendChoice.SelectedIndex=0;DenoiseChoice.SelectedIndex=1;await WaitFrames(100);
            var rtx=await SamplePerformance();var confirmed=SettingsStore.Backend();if(!confirmed.Fresh||confirmed.Kind!=0||confirmed.Level!=1||confirmed.Result!=0)throw new Exception("RTX worker did not confirm success.");
            if(rtx.Age>baseline+200)throw new Exception($"RTX frame age grew excessively: {rtx.Age:0.0} ms vs {baseline:0.0} ms baseline.");
            log.Add($"PASS: RTX Low: median frame age {rtx.Age:0.0} ms, rolling {rtx.Fps:0.0} fps.");
            await VerifyFilteredDelivery("RTX Low",log);
            DenoiseChoice.SelectedIndex=4;await WaitFrames(70);await VerifyFilteredDelivery("RTX Ultra",log);var ultra=await SamplePerformance();log.Add($"PASS: RTX Ultra: median frame age {ultra.Age:0.0} ms, rolling {ultra.Fps:0.0} fps.");DenoiseChoice.SelectedIndex=0;await WaitFrames(65);var recovered=await SamplePerformance();
            if(recovered.Age>baseline+60||recovered.Fps<Math.Min(27,offFps*.9))throw new Exception($"Off did not recover: age {recovered.Age:0.0}, fps {recovered.Fps:0.0}.");
            log.Add($"PASS: Off after RTX Low/Ultra recovers without restart: median age {recovered.Age:0.0} ms, rolling {recovered.Fps:0.0} fps.");
            BackendChoice.SelectedIndex=1;DenoiseChoice.SelectedIndex=2;await WaitFrames(100);var open=await SamplePerformance();confirmed=SettingsStore.Backend();if(!confirmed.Fresh||confirmed.Kind!=1||confirmed.Level!=2||confirmed.Result!=0)throw new Exception("Open GPU worker did not confirm success.");
            if(open.Age>baseline+200)throw new Exception("Open GPU accumulated frame delay.");
            log.Add($"PASS: open-source GPU Medium: median age {open.Age:0.0} ms, rolling {open.Fps:0.0} fps.");
            await VerifyFilteredDelivery("open-source GPU Medium",log);
            ZoomChoice.SelectedIndex=0;Width=1080;Height=720;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();double narrow=PreviewCanvas.ActualWidth;
            if(PreviewScroll.ExtentWidth>PreviewScroll.ViewportWidth+1||PreviewScroll.ExtentHeight>PreviewScroll.ViewportHeight+1)throw new Exception("Fit overflows small window.");
            Width=1420;Height=900;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();if(PreviewCanvas.ActualWidth<narrow+100||PreviewScroll.ExtentWidth>PreviewScroll.ViewportWidth+1||PreviewScroll.ExtentHeight>PreviewScroll.ViewportHeight+1)throw new Exception("Fit did not track resize.");
            ZoomChoice.SelectedIndex=1;LayoutPreview();if(bitmap is null||double.IsNaN(PreviewCanvas.Width))throw new Exception("100% zoom lost pixel size.");ZoomChoice.SelectedIndex=0;LayoutPreview();if(!double.IsNaN(PreviewCanvas.Width))throw new Exception("Fit retained fixed pixel size.");Width=1260;Height=820;log.Add("PASS: Fit follows smaller/larger window dimensions, has no overflow, and resets correctly after 100% zoom.");
            DenoiseChoice.SelectedIndex=0;await WaitFrames(40);
            foreach(var item in settings.Values){refresh[item]();int target=item.Value+Math.Max(1,item.Step);if(target>item.Max)target=item.Value-Math.Max(1,item.Step);var slider=sliders[item];slider.Value=(item.Snap(target)-item.Min)/(double)Math.Max(1,item.Step);slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent});if(!item.Read()||item.Value!=item.Snap(target)||item.Mode!=2)throw new Exception($"Release event did not commit {item.Name}.");}
            log.Add($"PASS: all {settings.Count} hardware slider release handlers committed and read back from the live preview source.");
            var brightness=settings[(0,0)];SetHardware(brightness,brightness.Min,2);await WaitFrames(20);double dark=PresentedLuma();SetHardware(brightness,brightness.Max,2);await WaitFrames(20);double light=PresentedLuma();if(light-dark<25)throw new Exception($"Presented pixels did not follow brightness: {dark:0.0} → {light:0.0}.");log.Add($"PASS: actual WPF image pixels changed with brightness: mean luma {dark:0.0} → {light:0.0}.");
            foreach(var item in originalSettings){SetHardware(item.Key,item.Value.Value,item.Value.Mode);refresh[item.Key]();}
            DenoiseChoice.SelectedIndex=1;if(SettingsStore.Read("DenoiseLevel")!=1)throw new Exception("Immediate denoise update failed.");await WaitFrames(120);
            BeginOriginal();if(SettingsStore.Read("DenoiseLevel")!=0)throw new Exception("Original comparison failed.");EndOriginal();if(SettingsStore.Read("DenoiseLevel")!=1)throw new Exception("Comparison restoration failed.");
            CameraApi.PreviewStats(out double fps,out double age,out double copy);log.Add($"PASS: actual WPF presenter displayed {displayedSerial} frames; capture {fps:0.0} fps, sampled frame age {age:0.0} ms, native copy {copy:0.00} ms.");
            log.Add("PASS: mirror and both denoiser selections save immediately without Apply; original comparison restores denoising; shutter displays 1/64 s; no focus section.");
            await StopPreview();await StartPreview();await WaitFrames(10);log.Add("PASS: WPF preview stops and restarts.");
        }catch(Exception error){log.Add("FAIL: "+error);throw;}finally{foreach(var item in originalSettings)try{SetHardware(item.Key,item.Value.Value,item.Value.Mode);}catch{}if(hasPriority)try{SetHardware(priority,oldPriority,oldPriorityMode);}catch{}SettingsStore.Write("FlipFlags",oldFlags);SettingsStore.Write("DenoiseBackend",oldBackend);SettingsStore.Write("DenoiseLevel",oldDenoise);File.WriteAllLines(path,log);Close();}
    }
    private async Task WaitFrames(ulong count){ulong target=displayedSerial+count;var deadline=DateTime.UtcNow.AddSeconds(20);while(displayedSerial<target&&DateTime.UtcNow<deadline)await Task.Delay(35);if(displayedSerial<target)throw new Exception("Live preview did not produce enough frames.");}
    private async Task VerifyFilteredDelivery(string name,List<string> log){var before=SettingsStore.Backend();await WaitFrames(65);var after=SettingsStore.Backend();long filtered=after.Filtered-before.Filtered,raw=after.Raw-before.Raw;double share=filtered/(double)Math.Max(1,filtered+raw);if(!after.Fresh||after.Result!=0||after.Kind!=BackendChoice.SelectedIndex||after.Level!=DenoiseChoice.SelectedIndex||filtered<30||raw<0||share<.98)throw new Exception($"{name} did not deliver filtered images consistently: {filtered} filtered / {raw} original.");log.Add($"PASS: {name} actual delivered frames: {filtered} filtered / {raw} original ({share:P0} filtered), including colour-noise cleanup when RTX selected.");}
    private async Task<(double Fps,double Age)> SamplePerformance(){var frames=new List<double>();var ages=new List<double>();for(int i=0;i<12;i++){await Task.Delay(65);CameraApi.PreviewStats(out double fps,out double age,out _);frames.Add(fps);ages.Add(age);}frames.Sort();ages.Sort();return(frames[6],ages[6]);}
    private double PresentedLuma(){if(bitmap is null)throw new Exception("No presented bitmap.");byte[] pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);double sum=0;int count=0;for(int y=10;y<bitmap.PixelHeight;y+=13)for(int x=10;x<bitmap.PixelWidth;x+=13){int i=(y*bitmap.PixelWidth+x)*4;sum+=.0722*pixels[i]+.7152*pixels[i+1]+.2126*pixels[i+2];count++;}return sum/Math.Max(1,count);}
}


