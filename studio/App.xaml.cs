using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Interop;

namespace LenovoCameraStudio;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Keep the window compositor independent of the camera/AI GPU path.
        // The hardware WPF surface can remain blank even while controls and
        // capture are running. This is scoped to this app's presentation only.
        RenderOptions.ProcessRenderMode=RenderMode.SoftwareOnly;
        base.OnStartup(e);
        if(e.Args.Length>0&&e.Args[0].EndsWith("self-test"))ShutdownMode=ShutdownMode.OnExplicitShutdown;
        try {
            if(e.Args.Length>0&&e.Args[0].StartsWith("--")&&e.Args[0] is not "--self-test" and not "--live-self-test" and not "--model-self-test" and not "--exposure-self-test") {
                var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"CameraSetup.exe")){UseShellExecute=false};
                foreach(string arg in e.Args)info.ArgumentList.Add(arg);
                using var process=Process.Start(info)!;await process.WaitForExitAsync();Shutdown(process.ExitCode);return;
            }
            // Retire the old digital-gain setting, including values left by
            // previous app versions. Old presets no longer read or write it.
            if(SettingsStore.Installed&&SettingsStore.Writable)SettingsStore.Write("SoftwareGainThirds",0);
            var window=new MainWindow(e.Args.Length==0||e.Args[0]!="--self-test");MainWindow=window;
            if(e.Args.Length==2&&e.Args[0]=="--self-test") {await window.RenderTests(e.Args[1]);Shutdown(0);return;}
            if(e.Args.Length==2&&e.Args[0]=="--live-self-test") {await window.LiveTests(e.Args[1]);Shutdown(0);return;}
            if(e.Args.Length==2&&e.Args[0]=="--exposure-self-test") {await window.ExposureLiveTests(e.Args[1]);Shutdown(0);return;}
            if(e.Args.Length is 2 or 3&&e.Args[0]=="--model-self-test") {await window.ModelLiveTests(e.Args[1],e.Args.Length==3?int.Parse(e.Args[2]):0);Shutdown(0);return;}
            window.Show();
        } catch(Exception error) {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"studio-error.txt"),error.ToString());
            if(e.Args.Length==0) MessageBox.Show(error.Message,"Lenovo Camera Studio",MessageBoxButton.OK,MessageBoxImage.Error);
            CameraApi.CameraClose();Shutdown(1);
        }
    }
}
