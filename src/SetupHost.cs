using System;
using System.IO;
namespace LenovoWebcamPrototype {
    internal static class SetupHost {
        private static int Main(string[] args) {
            try {
                if(args.Length==2&&args[0]=="--install-engine")EngineSetup.Install(args[1]);
                else if(args.Length==1&&args[0]=="--restore-engine")EngineSetup.Restore();
                else if(args.Length==1&&args[0]=="--verify-controls")EngineSetup.VerifyAllControls();
                else if(args.Length==1&&args[0]=="--restart-camera")EngineSetup.RestartCamera();
                else throw new ArgumentException("Unknown camera setup operation.");
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine-setup-result.txt"),"Camera setup completed.");return 0;
            }catch(Exception error){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"engine-setup-result.txt"),error.ToString());return 1;}
        }
    }
}
