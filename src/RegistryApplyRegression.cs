using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
class RegistryApplyRegression {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool SetDllDirectory(string path);
    static int[] Read() {using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var key=root.OpenSubKey(@"SOFTWARE\LenovoWebcamPrototype"))return new[]{Convert.ToInt32(key.GetValue("FlipFlags")),Convert.ToInt32(key.GetValue("DenoiseLevel")),Convert.ToInt32(key.GetValue("Enable1440p"))};}
    [STAThread] static int Main(string[] args) {
        try {
            if(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new Exception("Regression must run without administrator elevation.");
            SetDllDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));var type=assembly.GetType("LenovoWebcamPrototype.AdvancedCameraForm",true);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            int[] before=Read();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try {using(var form=(Form)Activator.CreateInstance(type,true)) {
                ((Task)type.GetMethod("ApplyProcessing",flags).Invoke(form,null)).GetAwaiter().GetResult();
                string status=((Label)type.GetField("status",flags).GetValue(form)).Text;
                Console.WriteLine("Actual Apply processing result: "+status);
                if(!status.StartsWith("Processing applied."))throw new Exception("Apply processing failed: "+status);
                int[] after=Read();for(int i=0;i<before.Length;++i)if(before[i]!=after[i])throw new Exception("Current settings changed unexpectedly.");
                Console.WriteLine("PASS: all three processing values written and read back through the real Apply handler without elevation; preferences preserved.");
            }}finally {assembly.GetType("LenovoWebcamPrototype.CameraNative",true).GetMethod("CameraClose",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);}
            return 0;
        }catch(Exception error){Console.WriteLine("FAIL: "+error);return 1;}
    }
}
