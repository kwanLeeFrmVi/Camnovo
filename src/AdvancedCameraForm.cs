using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LenovoWebcamPrototype {
    internal static class CameraNative {
        private const string Dll="CameraNative.dll";
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int CameraQuery(int group,int property,out int min,out int max,out int step,out int def,out int caps,out int value,out int mode);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int CameraSet(int group,int property,int value,int mode);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewStart();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void PreviewStop();
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewInfo(out int width,out int height,out ulong sequence);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int PreviewCopy(IntPtr destination,int width,int height,int stride,ref ulong sequence);
        [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void CameraClose();
        internal static void Check(int hr) {if(hr<0)Marshal.ThrowExceptionForHR(hr);}
    }
    internal sealed class HardwareSetting {
        internal string Name;internal int Group,Property,Min,Max,Step,Default,Caps,Value,Mode;
        internal bool Read() {return CameraNative.CameraQuery(Group,Property,out Min,out Max,out Step,out Default,out Caps,out Value,out Mode)>=0;}
        internal int Snap(int value) {return Math.Min(Max,Math.Max(Min,Min+(int)Math.Round((value-Min)/(double)Math.Max(1,Step))*Math.Max(1,Step)));}
        internal string Display(int value) {
            if(Group==0&&Property==7)return value+" K";
            if(Group==1&&Property==4)return (1000*Math.Pow(2,value)).ToString("0.##")+" ms";
            if(Property==2)return value+" degrees";
            if(Property==4||Property==5||Property==8)return value.ToString();
            return Math.Round(100.0*(value-Min)/Math.Max(1,Max-Min))+"%";
        }
    }
    internal sealed class AdvancedCameraForm : Form {
        private static readonly Color Background=Color.FromArgb(23,25,28),PanelColor=Color.FromArgb(32,35,39),Muted=Color.FromArgb(166,174,185),Accent=Color.FromArgb(0,211,184);
        private readonly PictureBox preview=new PictureBox();
        private readonly Label previewMessage=new Label(),status=new Label(),streamInfo=new Label();
        private readonly Button previewButton=new Button();
        private readonly Button compareButton=new Button();
        private readonly ComboBox previewZoom=new ComboBox();
        private Panel previewContainer;
        private bool originalHeld;
        private readonly System.Windows.Forms.Timer previewTimer=new System.Windows.Forms.Timer();
        private readonly List<HardwareSetting> hardware=new List<HardwareSetting>();
        private readonly List<Action> refreshRows=new List<Action>();
        private readonly List<PendingSetting> pending=new List<PendingSetting>();
        private readonly CheckBox mirror=new CheckBox(),flip=new CheckBox(),qhd=new CheckBox();
        private readonly ComboBox denoise=new ComboBox(),presets=new ComboBox();
        private Bitmap frame;private ulong shownSequence;private bool previewRunning,loading,closing;private bool oldQhd;
        private readonly System.Windows.Forms.Timer settingTimer=new System.Windows.Forms.Timer();
        private readonly Dictionary<string,Dictionary<string,int[]>> savedPresets=new Dictionary<string,Dictionary<string,int[]>>();
        private sealed class PendingSetting {internal HardwareSetting Setting;internal int Value,Mode;}

        internal AdvancedCameraForm() {
            Text="Lenovo Camera Studio";ClientSize=new Size(1160,740);MinimumSize=new Size(1100,700);
            StartPosition=FormStartPosition.CenterScreen;BackColor=Background;ForeColor=Color.White;
            Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Padding=new Padding(24)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,62));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,72));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(layout);
            var heading=new Label {Text="Lenovo Camera Studio",Font=new Font("Segoe UI",20,FontStyle.Bold),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
            layout.Controls.Add(heading,0,0);
            var device=new Label {Text="T24mv-30 RGB Webcam\nExisting Windows camera pipeline",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight,ForeColor=Muted};layout.Controls.Add(device,1,0);
            var left=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=4,ColumnCount=1,Margin=new Padding(0,0,22,0)};
            left.RowStyles.Add(new RowStyle(SizeType.Percent,100));left.RowStyles.Add(new RowStyle(SizeType.Absolute,45));left.RowStyles.Add(new RowStyle(SizeType.Absolute,64));left.RowStyles.Add(new RowStyle(SizeType.Absolute,64));layout.Controls.Add(left,0,1);
            var previewPanel=new Panel {Dock=DockStyle.Fill,BackColor=Color.Black,AutoScroll=true};previewContainer=previewPanel;left.Controls.Add(previewPanel,0,0);
            preview.Dock=DockStyle.Fill;preview.SizeMode=PictureBoxSizeMode.Zoom;previewPanel.Controls.Add(preview);
            previewMessage.Text="Start preview to adjust your camera";previewMessage.Dock=DockStyle.Fill;previewMessage.TextAlign=ContentAlignment.MiddleCenter;previewMessage.ForeColor=Muted;previewPanel.Controls.Add(previewMessage);
            streamInfo.Dock=DockStyle.Fill;streamInfo.ForeColor=Muted;streamInfo.Text="Preview is stopped. No recording or snapshots are saved.";streamInfo.TextAlign=ContentAlignment.MiddleLeft;left.Controls.Add(streamInfo,0,1);
            var previewActions=new FlowLayoutPanel {Dock=DockStyle.Fill};Style(previewButton,"Start preview",125);previewButton.Click+=async delegate {if(previewRunning)await StopPreview();else StartPreview();};previewActions.Controls.Add(previewButton);
            var refresh=Button("Refresh controls",125);refresh.Click+=delegate {RefreshHardware();};previewActions.Controls.Add(refresh);
            Style(compareButton,"Hold: original",140);compareButton.Enabled=false;previewActions.Controls.Add(compareButton);
            compareButton.MouseDown+=delegate {BeginOriginal();};compareButton.MouseUp+=delegate {EndOriginal();};compareButton.MouseCaptureChanged+=delegate {if(!compareButton.Capture)EndOriginal();};
            compareButton.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Space)BeginOriginal();};compareButton.KeyUp+=delegate {EndOriginal();};compareButton.Leave+=delegate {EndOriginal();};
            previewZoom.DropDownStyle=ComboBoxStyle.DropDownList;previewZoom.Items.AddRange(new object[]{"Fit","100%","200%"});previewZoom.SelectedIndex=0;previewZoom.Width=95;previewZoom.Margin=new Padding(6,13,0,0);previewActions.Controls.Add(previewZoom);previewZoom.SelectedIndexChanged+=delegate {LayoutPreview();};
            left.Controls.Add(previewActions,0,2);
            status.Dock=DockStyle.Fill;status.ForeColor=Muted;status.Text="Adjustments change the camera feed used by your video apps.\nSome apps also mirror their own preview.";left.Controls.Add(status,0,3);
            var tabs=new TabControl {Dock=DockStyle.Fill,Font=new Font("Segoe UI",10)};layout.Controls.Add(tabs,1,1);
            var image=Page(tabs,"Image");var exposure=Page(tabs,"Exposure");var processing=Page(tabs,"Processing");
            ReadHardware();
            AddPresetControls(image);
            AddSetting(image,"White balance",0,7);
            AddSetting(image,"Brightness",0,0);AddSetting(image,"Contrast",0,1);AddSetting(image,"Saturation",0,3);
            AddSetting(image,"Sharpness",0,4);AddSetting(image,"Gamma",0,5);AddSetting(image,"Hue",0,2);
            AddNote(image,"Saturation adjusts overall color intensity. A separate vibrance control is not available in this version.");
            AddSetting(exposure,"Exposure time",1,4);AddSetting(exposure,"Backlight compensation",0,8);
            AddNote(exposure,"Longer exposure brightens the image but can blur movement. Auto exposure lets the camera choose.");
            AddSection(exposure,"Focus and framing");
            AddNote(exposure,"This camera does not expose focus, optical zoom, pan or tilt adjustments.");
            var defaults=Button("Restore camera defaults",330);defaults.Click+=delegate {ApplyDefaults();};exposure.Controls.Add(defaults);
            AddProcessing(processing);
            settingTimer.Interval=140;settingTimer.Tick+=delegate {settingTimer.Stop();CommitPending();};
            previewTimer.Interval=50;previewTimer.Tick+=delegate {UpdatePreview();};
            FormClosing+=delegate {closing=true;EndOriginal();previewTimer.Stop();settingTimer.Stop();CommitPending();CameraNative.CameraClose();if(frame!=null){preview.Image=null;frame.Dispose();}};
        }
        private static void Style(Button button,string text,int width) {button.Text=text;button.Size=new Size(width,38);button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=Color.FromArgb(75,83,91);button.BackColor=PanelColor;button.ForeColor=Color.White;button.Margin=new Padding(0,8,10,8);}
        private static Button Button(string text,int width){var button=new Button();Style(button,text,width);return button;}
        private static FlowLayoutPanel Page(TabControl tabs,string title) {
            var page=new TabPage(title){BackColor=PanelColor,Padding=new Padding(15)};tabs.TabPages.Add(page);
            var content=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,BackColor=PanelColor};page.Controls.Add(content);return content;
        }
        private static void AddNote(FlowLayoutPanel panel,string text) {panel.Controls.Add(new Label {Text=text,ForeColor=Muted,Width=335,AutoSize=true,MaximumSize=new Size(335,0),Margin=new Padding(0,10,0,16)});}
        private static void AddSection(FlowLayoutPanel panel,string text) {panel.Controls.Add(new Label {Text=text,Width=335,Height=30,Font=new Font("Segoe UI",11,FontStyle.Bold),Margin=new Padding(0,8,0,4)});}
        private void ReadHardware() {
            foreach(var spec in new[]{new[]{0,0},new[]{0,1},new[]{0,2},new[]{0,3},new[]{0,4},new[]{0,5},new[]{0,7},new[]{0,8},new[]{1,4}}) {
                var item=new HardwareSetting {Group=spec[0],Property=spec[1]};if(item.Read())hardware.Add(item);
            }
        }
        private HardwareSetting Find(int group,int property) {return hardware.Find(delegate(HardwareSetting x){return x.Group==group&&x.Property==property;});}
        private void AddSetting(FlowLayoutPanel panel,string name,int group,int property) {
            var setting=Find(group,property);if(setting==null){AddNote(panel,name+": unavailable on this camera.");return;}setting.Name=name;
            var row=new Panel {Width=335,Height=(setting.Caps&1)!=0?119:87,Margin=new Padding(0,6,0,4)};
            var title=new Label {Text=name,Location=new Point(0,0),Size=new Size(220,24),Font=new Font("Segoe UI",10,FontStyle.Bold)};
            var valueLabel=new Label {Location=new Point(220,0),Size=new Size(115,24),TextAlign=ContentAlignment.TopRight,ForeColor=Accent};
            var slider=new TrackBar {Name="Slider_"+group+"_"+property,AccessibleName=name,Minimum=0,Maximum=Math.Max(1,(setting.Max-setting.Min)/Math.Max(1,setting.Step)),TickStyle=TickStyle.None,Location=new Point(-5,30),Size=new Size(340,40),BackColor=PanelColor,SmallChange=1,LargeChange=1};
            var auto=new CheckBox {Text="Auto "+name.ToLowerInvariant(),Location=new Point(0,80),Size=new Size(330,28),ForeColor=Color.White,Visible=(setting.Caps&1)!=0};
            row.Controls.AddRange(new Control[]{title,valueLabel,slider,auto});panel.Controls.Add(row);
            bool refreshing=false,dragging=false;
            Action refresh=delegate {refreshing=true;try{if(!setting.Read()){row.Enabled=false;return;}row.Enabled=true;slider.Value=Math.Max(0,Math.Min(slider.Maximum,(setting.Value-setting.Min)/Math.Max(1,setting.Step)));auto.Checked=(setting.Mode&1)!=0;slider.Enabled=(setting.Caps&2)!=0;valueLabel.Text=auto.Checked?"Auto":setting.Display(setting.Value);}finally{refreshing=false;}};
            refreshRows.Add(refresh);refresh();
            slider.MouseDown+=delegate {dragging=true;settingTimer.Stop();};
            slider.ValueChanged+=delegate {
                if(refreshing||loading)return;
                if(auto.Checked){refreshing=true;auto.Checked=false;refreshing=false;}
                int value=setting.Min+slider.Value*Math.Max(1,setting.Step);valueLabel.Text=setting.Display(value);Queue(setting,value,2);
                if(dragging)settingTimer.Stop();
            };
            slider.MouseUp+=delegate {dragging=false;settingTimer.Stop();CommitPending();};
            slider.MouseCaptureChanged+=delegate {if(dragging&&!slider.Capture){dragging=false;settingTimer.Stop();CommitPending();}};
            slider.KeyUp+=delegate {settingTimer.Stop();CommitPending();};
            slider.Leave+=delegate {dragging=false;settingTimer.Stop();CommitPending();};
            auto.CheckedChanged+=delegate {if(refreshing||loading)return;int value=setting.Min+slider.Value*Math.Max(1,setting.Step);valueLabel.Text=auto.Checked?"Auto":setting.Display(value);Queue(setting,value,auto.Checked?1:2);settingTimer.Stop();CommitPending();};
        }
        private void Queue(HardwareSetting setting,int value,int mode) {pending.RemoveAll(delegate(PendingSetting p){return p.Setting==setting;});pending.Add(new PendingSetting {Setting=setting,Value=setting.Snap(value),Mode=mode});settingTimer.Stop();settingTimer.Start();}
        private void CommitPending() {
            var list=pending.ToArray();pending.Clear();
            foreach(var item in list)try {
                CameraNative.Check(CameraNative.CameraSet(item.Setting.Group,item.Setting.Property,item.Value,item.Mode));
                if(!item.Setting.Read()||item.Setting.Mode!=item.Mode||(item.Mode==2&&item.Setting.Value!=item.Value))throw new Exception("The camera did not accept the "+item.Setting.Name.ToLowerInvariant()+" setting.");
                status.ForeColor=Muted;status.Text=item.Setting.Name+" applied: "+(item.Mode==1?"Auto":item.Setting.Display(item.Setting.Value))+".";
            }catch(Exception error){ShowError(error);}
            if(list.Length>0&&previewRunning)UpdatePreview();
        }
        private void RefreshHardware(){CommitPending();foreach(var refresh in refreshRows)refresh();status.Text="Read current camera settings.";}
        private void ShowError(Exception error){status.ForeColor=Color.FromArgb(255,183,117);status.Text=error.Message;}
        private void ApplyDefaults() {
            CommitPending();try {foreach(var item in hardware)CameraNative.Check(CameraNative.CameraSet(item.Group,item.Property,item.Snap(item.Default),(item.Caps&1)!=0?1:2));RefreshHardware();status.Text="Camera defaults restored. White balance and exposure are automatic.";}catch(Exception e){ShowError(e);}
        }
        private void AddPresetControls(FlowLayoutPanel panel) {
            AddSection(panel,"Preset");presets.Width=335;presets.DropDownStyle=ComboBoxStyle.DropDownList;presets.Items.Add("Current settings");presets.Items.Add("Camera defaults");
            using(var key=Registry.CurrentUser.OpenSubKey(@"SOFTWARE\LenovoCameraStudio\Presets"))if(key!=null)foreach(string name in key.GetValueNames()) {
                try{var values=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,int[]>>(Convert.ToString(key.GetValue(name)));savedPresets[name]=values;presets.Items.Add(name);}catch{}
            }
            presets.SelectedIndex=0;panel.Controls.Add(presets);
            presets.SelectedIndexChanged+=delegate {if(loading||presets.SelectedIndex<1)return;if(presets.SelectedIndex==1){ApplyDefaults();return;}ApplyPreset(Convert.ToString(presets.SelectedItem));};
            var save=Button("Save current as preset",335);save.Click+=delegate {SavePreset();};panel.Controls.Add(save);
        }
        private void SavePreset() {
            CommitPending();using(var dialog=new Form {Text="Save camera preset",ClientSize=new Size(370,140),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false}) {
                var input=new TextBox {Text="My settings",Location=new Point(20,20),Width=325};var ok=new Button {Text="Save",DialogResult=DialogResult.OK,Location=new Point(240,75),Width=105};dialog.Controls.AddRange(new Control[]{input,ok});dialog.AcceptButton=ok;
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;string name=input.Text.Trim();if(name.Length==0||name.Length>60||name=="Current settings"||name=="Camera defaults"){ShowError(new Exception("Choose another preset name (1 to 60 characters)."));return;}
                var values=new Dictionary<string,int[]>();foreach(var item in hardware)if(item.Read())values[item.Group+":"+item.Property]=new[]{item.Value,item.Mode};
                using(var key=Registry.CurrentUser.CreateSubKey(@"SOFTWARE\LenovoCameraStudio\Presets"))key.SetValue(name,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(values));
                savedPresets[name]=values;loading=true;if(!presets.Items.Contains(name))presets.Items.Add(name);presets.SelectedItem=name;loading=false;status.Text="Preset saved: "+name;
            }
        }
        private void ApplyPreset(string name) {
            Dictionary<string,int[]> values;if(!savedPresets.TryGetValue(name,out values))return;CommitPending();
            try{foreach(var item in hardware){int[] entry;if(values.TryGetValue(item.Group+":"+item.Property,out entry)&&entry.Length==2)CameraNative.Check(CameraNative.CameraSet(item.Group,item.Property,item.Snap(entry[0]),entry[1]));}RefreshHardware();status.Text="Preset applied: "+name;}catch(Exception e){ShowError(e);}
        }
        private void AddProcessing(FlowLayoutPanel panel) {
            bool installed=EngineSetup.Installed(),writable=EngineSetup.CanWriteSettings();
            AddSection(panel,"Orientation");mirror.Text="Mirror left / right";flip.Text="Flip upside down";
            foreach(var box in new[]{mirror,flip}){box.Size=new Size(335,32);panel.Controls.Add(box);box.Enabled=installed&&writable;}
            AddSection(panel,"NVIDIA GPU denoising");denoise.DropDownStyle=ComboBoxStyle.DropDownList;denoise.Width=335;denoise.Items.AddRange(new object[]{"Off","Low - preserve detail","Medium","High","Ultra - stronger smoothing"});denoise.SelectedIndex=0;panel.Controls.Add(denoise);
            denoise.Enabled=installed&&writable&&File.Exists(Path.Combine(EngineSetup.InstallFolder,@"Nvidia\NVVideoEffects.dll"));
            AddSection(panel,"Video output");qhd.Text="Offer 2560 x 1440 (2K / 1440p)";qhd.Size=new Size(335,36);qhd.Enabled=installed&&writable;panel.Controls.Add(qhd);
            AddNote(panel,"1440p is upscaled from the camera's 1080p video. Select 2560 x 1440 in your video app. Resolution changes restart the camera and require Windows approval.");
            using(var key=Registry.LocalMachine.OpenSubKey(EngineSetup.SettingsKey))if(key!=null){int flags=Convert.ToInt32(key.GetValue("FlipFlags",0));mirror.Checked=(flags&1)!=0;flip.Checked=(flags&2)!=0;qhd.Checked=Convert.ToInt32(key.GetValue("Enable1440p",0))==1;oldQhd=qhd.Checked;denoise.SelectedIndex=Math.Max(0,Math.Min(4,Convert.ToInt32(key.GetValue("DenoiseLevel",0))));}
            denoise.SelectedIndexChanged+=delegate {ApplyDenoise();};
            AddNote(panel,"Denoising applies when you select a level. Use 100% zoom and hold Original to compare fine grain in shadows. Higher levels can soften detail.");
            var apply=Button("Apply processing",335);apply.Enabled=installed&&writable;apply.Click+=async delegate {await ApplyProcessing();};panel.Controls.Add(apply);
            var reset=Button("Reset processing",335);reset.Enabled=installed&&writable;reset.Click+=async delegate {mirror.Checked=flip.Checked=qhd.Checked=false;denoise.SelectedIndex=0;await ApplyProcessing();};panel.Controls.Add(reset);
            if(!installed){var install=Button("Install camera processing",335);install.Click+=async delegate {await RunSetup("--install-engine");};panel.Controls.Add(install);AddNote(panel,"Hardware image controls work independently of the processing extension.");}
            else {var restore=Button("Restore original camera pipeline",335);restore.Click+=async delegate {await RunSetup("--restore-engine");};panel.Controls.Add(restore);}
        }
        private async Task ApplyProcessing() {
            try {
                CommitPending();using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var key=machine.OpenSubKey(EngineSetup.SettingsKey,RegistryKeyPermissionCheck.ReadWriteSubTree,System.Security.AccessControl.RegistryRights.ReadKey|System.Security.AccessControl.RegistryRights.SetValue)) {
                    if(key==null)throw new Exception("Camera processing settings are missing.");key.SetValue("FlipFlags",(mirror.Checked?1:0)|(flip.Checked?2:0),RegistryValueKind.DWord);key.SetValue("DenoiseLevel",denoise.SelectedIndex,RegistryValueKind.DWord);key.SetValue("Enable1440p",qhd.Checked?1:0,RegistryValueKind.DWord);
                }
                if(qhd.Checked!=oldQhd)await RunSetup("--restart-camera");else status.Text="Processing applied. Flips and denoising update during capture.";
            }catch(Exception e){ShowError(e);}
        }
        private static void WriteDenoise(int level) {
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var key=machine.OpenSubKey(EngineSetup.SettingsKey,RegistryKeyPermissionCheck.ReadWriteSubTree,System.Security.AccessControl.RegistryRights.ReadKey|System.Security.AccessControl.RegistryRights.SetValue)) {
                if(key==null)throw new Exception("Camera processing settings are missing.");key.SetValue("DenoiseLevel",level,RegistryValueKind.DWord);
            }
        }
        private void ApplyDenoise() {
            try {if(!denoise.Enabled)return;WriteDenoise(originalHeld?0:denoise.SelectedIndex);compareButton.Enabled=previewRunning&&denoise.SelectedIndex>0;status.ForeColor=Muted;status.Text="NVIDIA denoising saved: "+denoise.SelectedItem+".\nUse 100% zoom and hold Original to compare noise.";}
            catch(Exception e){ShowError(e);}
        }
        private void BeginOriginal() {
            if(originalHeld||!previewRunning||denoise.SelectedIndex==0)return;
            try{WriteDenoise(0);originalHeld=true;compareButton.Text="Original shown";status.Text="Showing original feed. Release to restore NVIDIA denoising.";}catch(Exception e){ShowError(e);}
        }
        private void EndOriginal() {
            if(!originalHeld)return;
            try{WriteDenoise(denoise.SelectedIndex);originalHeld=false;compareButton.Text="Hold: original";status.Text="NVIDIA denoising restored: "+denoise.SelectedItem+".";}catch(Exception e){ShowError(e);}
        }
        private void LayoutPreview() {
            if(frame==null||previewZoom.SelectedIndex==0){preview.Dock=DockStyle.Fill;preview.SizeMode=PictureBoxSizeMode.Zoom;previewContainer.AutoScrollMinSize=Size.Empty;}
            else {preview.Dock=DockStyle.None;preview.Location=Point.Empty;preview.SizeMode=PictureBoxSizeMode.StretchImage;int scale=previewZoom.SelectedIndex;preview.Size=new Size(frame.Width*scale,frame.Height*scale);previewContainer.AutoScrollMinSize=preview.Size;}
            preview.Invalidate();
        }
        private async Task RunSetup(string action) {
            if(previewRunning)await StopPreview();CameraNative.CameraClose();Enabled=false;
            try {
                var args=action+(action=="--install-engine"?" "+WindowsIdentity.GetCurrent().User.Value:"");
                using(var process=Process.Start(new ProcessStartInfo(Application.ExecutablePath,args){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden})) {
                    await Task.Run(delegate {process.WaitForExit();});if(process.ExitCode!=0)throw new Exception(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine-setup-result.txt")));
                }Application.Restart();
            }catch(Exception e){RefreshHardware();ShowError(e);}finally{Enabled=true;}
        }
        private void StartPreview() {
            try{CommitPending();CameraNative.Check(CameraNative.PreviewStart());shownSequence=0;previewRunning=true;compareButton.Enabled=denoise.Enabled&&denoise.SelectedIndex>0;previewButton.Text="Stop preview";previewMessage.Visible=true;previewMessage.Text="Opening camera...";previewTimer.Start();}catch(Exception e){ShowError(e);}
        }
        private async Task StopPreview() {
            EndOriginal();previewTimer.Stop();previewButton.Enabled=false;compareButton.Enabled=false;await Task.Run(delegate {CameraNative.PreviewStop();});previewRunning=false;
            if(closing)return;previewButton.Enabled=true;previewButton.Text="Start preview";previewMessage.Text="Preview stopped";previewMessage.Visible=true;streamInfo.Text="Preview is stopped. No recording or snapshots are saved.";
        }
        private void UpdatePreview() {
            try {
                int w,h;ulong sequence;int hr=CameraNative.PreviewInfo(out w,out h,out sequence);CameraNative.Check(hr);if(w==0||h==0||sequence==shownSequence)return;
                if(frame==null||frame.Width!=w||frame.Height!=h){preview.Image=null;if(frame!=null)frame.Dispose();frame=new Bitmap(w,h,PixelFormat.Format32bppRgb);preview.Image=frame;LayoutPreview();}
                var bits=frame.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
                try{CameraNative.Check(CameraNative.PreviewCopy(bits.Scan0,w,h,bits.Stride,ref shownSequence));}finally{frame.UnlockBits(bits);}
                previewMessage.Visible=false;preview.Invalidate();streamInfo.Text=w+" x "+h+"  |  "+(originalHeld?"Original":("Denoise: "+denoise.Items[denoise.SelectedIndex]))+"  |  "+previewZoom.Text;
            }catch(Exception e){previewTimer.Stop();previewMessage.Text="Preview unavailable\nClose other camera apps, then stop and start preview.";previewMessage.Visible=true;ShowError(e);}
        }
        internal static int RenderTest(string path) {
            using(var form=new AdvancedCameraForm()) {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();if(form.hardware.Count==0)throw new Exception("No hardware controls were discovered.");
                TabControl tabs=null;foreach(Control child in form.Controls[0].Controls)if(child is TabControl)tabs=(TabControl)child;
                if(tabs==null||tabs.TabPages.Count!=3)throw new Exception("Advanced settings tabs are missing.");
                for(int i=0;i<tabs.TabPages.Count;++i){tabs.SelectedIndex=i;Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(i==0?path:Path.Combine(Path.GetDirectoryName(path),Path.GetFileNameWithoutExtension(path)+"-"+tabs.TabPages[i].Text+".png"),ImageFormat.Png);}}
                if(EngineSetup.Installed()&&EngineSetup.CanWriteSettings()&&(!form.mirror.Enabled||!form.flip.Enabled||!form.qhd.Enabled))throw new Exception("Installed processing controls are disabled.");
                form.Close();
            }return 0;
        }
        internal static int LiveTest(string path) {
            var log=new List<string>();try {
                var controls=new List<HardwareSetting>();foreach(var spec in new[]{new[]{0,0},new[]{0,1},new[]{0,2},new[]{0,3},new[]{0,4},new[]{0,5},new[]{0,7},new[]{0,8},new[]{1,4}}){var item=new HardwareSetting {Group=spec[0],Property=spec[1]};if(item.Read()){controls.Add(item);log.Add("Control "+item.Group+":"+item.Property+" range "+item.Min+".."+item.Max+" step "+item.Step+" current "+item.Value+" mode "+item.Mode);}}
                if(controls.Count<8)throw new Exception("Expected Lenovo hardware controls were not discovered.");
                CameraNative.Check(CameraNative.PreviewStart());var deadline=DateTime.UtcNow.AddSeconds(20);int w=0,h=0;ulong seq=0;
                while(DateTime.UtcNow<deadline){CameraNative.Check(CameraNative.PreviewInfo(out w,out h,out seq));if(seq>=120)break;Thread.Sleep(50);}if(seq<120)throw new Exception("Live preview did not deliver 120 frames.");
                using(var bitmap=new Bitmap(w,h,PixelFormat.Format32bppRgb)){var bits=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);ulong copied=0;try{CameraNative.Check(CameraNative.PreviewCopy(bits.Scan0,w,h,bits.Stride,ref copied));}finally{bitmap.UnlockBits(bits);}}
                log.Add("Preview: "+w+"x"+h+", "+seq+" frames; RGB frame copied into a Windows bitmap. No image saved.");
                foreach(var item in controls) {
                    int oldValue=item.Value,oldMode=item.Mode;
                    try{int changed=item.Snap(oldValue<item.Max?oldValue+Math.Max(1,item.Step):oldValue-Math.Max(1,item.Step));CameraNative.Check(CameraNative.CameraSet(item.Group,item.Property,changed,2));if(!item.Read()||item.Value!=changed||(item.Mode&2)==0)throw new Exception("Hardware control readback did not match.");log.Add("PASS set/read during preview "+item.Group+":"+item.Property);}
                    finally{CameraNative.Check(CameraNative.CameraSet(item.Group,item.Property,oldValue,oldMode));if(!item.Read()||item.Mode!=oldMode||(oldMode==2&&item.Value!=oldValue))throw new Exception("Hardware setting restoration did not match.");}
                }
                log.Add("PASS: all original hardware settings and automatic modes restored.");
                for(int cycle=0;cycle<3;++cycle){CameraNative.PreviewStop();CameraNative.Check(CameraNative.PreviewStart());deadline=DateTime.UtcNow.AddSeconds(15);seq=0;while(DateTime.UtcNow<deadline){CameraNative.Check(CameraNative.PreviewInfo(out w,out h,out seq));if(seq>=5)break;Thread.Sleep(50);}if(seq<5)throw new Exception("Preview restart failed.");log.Add("PASS preview stop/start cycle "+(cycle+1));}
                File.WriteAllLines(path,log);return 0;
            }catch(Exception e){log.Add(e.ToString());File.WriteAllLines(path,log);return 1;}finally{CameraNative.CameraClose();}
        }
    }
}
