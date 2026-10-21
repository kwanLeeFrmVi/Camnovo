using System;
using System.IO;
using System.Windows.Forms;
namespace LenovoWebcamPrototype {
    internal static class CameraControls {
        [STAThread] private static int Main(string[] args) {
            bool setup=args.Length>0&&(args[0]=="--install-engine"||args[0]=="--restore-engine"||args[0]=="--verify-controls"||args[0]=="--restart-camera");
            try {
                if(setup) {
                    if(args[0]=="--install-engine") {if(args.Length!=2)throw new ArgumentException("The settings user identity is required.");EngineSetup.Install(args[1]);}
                    else if(args[0]=="--restore-engine")EngineSetup.Restore();
                    else if(args[0]=="--verify-controls")EngineSetup.VerifyAllControls();
                    else EngineSetup.RestartCamera();
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine-setup-result.txt"),"Camera setup completed.");return 0;
                }
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                if(args.Length==2&&args[0]=="--self-test")return AdvancedCameraForm.RenderTest(Path.GetFullPath(args[1]));
                if(args.Length==2&&args[0]=="--live-self-test")return AdvancedCameraForm.LiveTest(Path.GetFullPath(args[1]));
                Application.Run(new AdvancedCameraForm());return 0;
            }catch(Exception error) {
                if(setup){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine-setup-result.txt"),error.ToString());return 1;}
                if(args.Length>0&&(args[0]=="--self-test"||args[0]=="--live-self-test")){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"app-test-error.txt"),error.ToString());return 1;}
                MessageBox.Show(error.Message,"Lenovo Camera Studio could not start",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;
            }
        }
    }
}
