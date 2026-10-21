using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LenovoWebcamPrototype;

// Integration regression: actual slider events -> hardware -> live preview pixels.
internal static class SliderReleaseRegression {
    static readonly MethodInfo MouseDown=typeof(TrackBar).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic);
    static readonly MethodInfo MouseUp=typeof(TrackBar).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic);
    static double[] Pixels() {
        int w,h;ulong seq,start;CameraNative.Check(CameraNative.PreviewInfo(out w,out h,out start));
        var deadline=DateTime.UtcNow.AddSeconds(10);
        do{Thread.Sleep(35);CameraNative.Check(CameraNative.PreviewInfo(out w,out h,out seq));}while(seq<start+15&&DateTime.UtcNow<deadline);
        if(w==0||seq<start+15)throw new Exception("Preview frames stopped advancing.");
        var bytes=new byte[w*h*4];var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try{ulong copied=0;CameraNative.Check(CameraNative.PreviewCopy(pinned.AddrOfPinnedObject(),w,h,w*4,ref copied));}finally{pinned.Free();}
        double mean=0,chroma=0;int count=0;
        for(int i=0;i<bytes.Length;i+=400){mean+=(bytes[i]+bytes[i+1]+bytes[i+2])/3.0;chroma+=Math.Abs(bytes[i]-bytes[i+1])+Math.Abs(bytes[i+2]-bytes[i+1]);++count;}
        return new[]{mean/count,chroma/count};
    }
    static TrackBar Slider(Form form,HardwareSetting setting){return (TrackBar)form.Controls.Find("Slider_"+setting.Group+"_"+setting.Property,true)[0];}
    static void Release(TrackBar slider,int position) {
        MouseDown.Invoke(slider,new object[]{new MouseEventArgs(MouseButtons.Left,1,5,5,0)});
        slider.Value=position;
        MouseUp.Invoke(slider,new object[]{new MouseEventArgs(MouseButtons.Left,1,5,5,0)});
    }
    [STAThread]static int Main() {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {using(var form=new AdvancedCameraForm()) {
            CameraNative.Check(CameraNative.PreviewStart());Pixels();
            foreach(var pair in new[]{new[]{0,0},new[]{0,1},new[]{0,2},new[]{0,3},new[]{0,4},new[]{0,5},new[]{0,7},new[]{0,8},new[]{1,4}}) {
                var setting=new HardwareSetting {Group=pair[0],Property=pair[1]};if(!setting.Read())throw new Exception("Missing control.");
                int original=setting.Value,mode=setting.Mode;var slider=Slider(form,setting);
                try {
                    int target=setting.Snap(original<setting.Max?original+setting.Step:original-setting.Step);
                    MouseDown.Invoke(slider,new object[]{new MouseEventArgs(MouseButtons.Left,1,5,5,0)});
                    slider.Value=(target-setting.Min)/Math.Max(1,setting.Step);
                    Thread.Sleep(180);Application.DoEvents();
                    if(!setting.Read()||(mode==2&&setting.Value!=original)||setting.Mode!=mode)throw new Exception("Slider applied before release.");
                    var clock=Stopwatch.StartNew();MouseUp.Invoke(slider,new object[]{new MouseEventArgs(MouseButtons.Left,1,5,5,0)});clock.Stop();
                    if(!setting.Read()||setting.Value!=target||setting.Mode!=2)throw new Exception("Slider release did not immediately set manual hardware value.");
                    Console.WriteLine("PASS release "+pair[0]+":"+pair[1]+" -> "+target+"; commit "+clock.ElapsedMilliseconds+" ms");
                } finally{CameraNative.Check(CameraNative.CameraSet(pair[0],pair[1],original,mode));}
            }
            foreach(int property in new[]{0,3}) {
                var setting=new HardwareSetting {Group=0,Property=property};setting.Read();int original=setting.Value,mode=setting.Mode;var slider=Slider(form,setting);
                try {
                    Release(slider,0);var low=Pixels();
                    if(property==0){CameraNative.PreviewStop();CameraNative.Check(CameraNative.PreviewStart());var restarted=Pixels();if(!setting.Read()||setting.Value!=setting.Min||setting.Mode!=2||Math.Abs(restarted[0]-low[0])>30)throw new Exception("Preview restart did not preserve the active brightness setting.");Console.WriteLine("PASS: preview restart preserved brightness in hardware and live pixels.");}
                    Release(slider,slider.Maximum);var high=Pixels();
                    Console.WriteLine("VIDEO control 0:"+property+" low mean="+low[0].ToString("0.00")+" chroma="+low[1].ToString("0.00")+" high mean="+high[0].ToString("0.00")+" chroma="+high[1].ToString("0.00"));
                    if(property==0&&high[0]-low[0]<50)throw new Exception("Brightness release did not change live video pixels.");
                    if(property==3&&high[1]-low[1]<2)throw new Exception("Saturation release did not change live color pixels.");
                }finally{CameraNative.Check(CameraNative.CameraSet(0,property,original,mode));}
            }
            Console.WriteLine("PASS: all nine slider release handlers applied immediately; brightness and saturation changes confirmed in live pixels; original hardware preferences restored. No images saved.");
        }return 0;}catch(Exception error){Console.WriteLine("FAIL: "+error);return 1;}finally{CameraNative.CameraClose();}
    }
}
