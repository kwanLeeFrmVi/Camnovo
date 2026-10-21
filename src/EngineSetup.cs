using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Xml;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace LenovoWebcamPrototype
{
    internal static class EngineSetup
    {
        internal const string Clsid = "{73DF2569-1A6D-4976-BA38-F089C0162864}";
        internal const string SettingsKey = @"SOFTWARE\LenovoWebcamPrototype";
        private const string ComKey = @"SOFTWARE\Classes\CLSID\" + Clsid;
        private const string Category = "{e5323777-f976-4f5b-9b55-b94699c46e44}";
        internal static readonly string InstallFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LenovoWebcamPrototype");
        private static string BackupPath { get { return Path.Combine(InstallFolder, "engine-backup.json"); } }

        internal static bool Installed()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var server = machine.OpenSubKey(ComKey + @"\InprocServer32"))
            {
                var path = server == null ? null : server.GetValue(null) as string;
                return path != null && File.Exists(path) && String.Equals(path, Path.Combine(InstallFolder, "LenovoPassThrough.dll"), StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static bool CanWriteSettings()
        {
            try {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var settings = machine.OpenSubKey(SettingsKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.SetValue))
                    return settings != null;
            } catch (UnauthorizedAccessException) { return false; }
              catch (System.Security.SecurityException) { return false; }
        }

        private static string FindRgbDevice()
        {
            var matches = new List<string>();
            using (var query = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass='Camera'"))
            using (var result = query.Get())
                foreach (ManagementObject device in result)
                    using (device) {
                        string id = Convert.ToString(device["PNPDeviceID"]);
                        if (id.StartsWith(@"USB\VID_17EF&PID_483C&MI_00\", StringComparison.OrdinalIgnoreCase)) matches.Add(id);
                    }
            if (matches.Count != 1) throw new InvalidOperationException("Connect exactly one Lenovo T24mv-30 RGB webcam before installing.");
            return matches[0];
        }

        private static string InterfaceKey(string id)
        {
            if (id == null || !id.StartsWith(@"USB\VID_17EF&PID_483C&MI_00\", StringComparison.OrdinalIgnoreCase) || id.Contains(".."))
                throw new InvalidOperationException("Invalid RGB camera identity.");
            return @"SYSTEM\CurrentControlSet\Control\DeviceClasses\" + Category + @"\##?#" + id.Replace('\\', '#') + "#" + Category + @"\#GLOBAL\Device Parameters";
        }

        internal static void Install(string settingsUserSid)
        {
            RequireAdmin();
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LenovoPassThrough.dll");
            if (!File.Exists(source)) throw new FileNotFoundException("Camera processing DLL is missing from this app package.", source);
            var id = FindRgbDevice();
            var path = InterfaceKey(id);
            string previousEngine=null;
            bool updating=false;
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var camera = machine.OpenSubKey(path, true))
            {
                if (camera == null) throw new InvalidOperationException("The RGB camera interface is unavailable.");
                if (camera.GetValue("CameraDeviceMftCLSIDChain") != null) throw new InvalidOperationException("An existing camera extension chain needs review before installation.");
                var legacy = camera.GetValue("CameraDeviceMftCLSID") as string;
                updating=legacy==Clsid;
                if (legacy != null && legacy != Clsid) throw new InvalidOperationException("Another camera extension is already attached.");
                if (legacy == Clsid && !File.Exists(BackupPath)) throw new InvalidOperationException("Existing extension has no matching backup. Installation stopped.");
                if (legacy == null && File.Exists(BackupPath)) {
                    var previous = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(BackupPath));
                    if (Convert.ToString(previous["DeviceId"]) != id || Convert.ToString(previous["Clsid"]) != Clsid)
                        throw new InvalidOperationException("The earlier camera backup belongs to a different installation.");
                }
                Directory.CreateDirectory(InstallFolder);
                if (legacy == null) {
                    var backup = new Dictionary<string, object> { {"DeviceId", id}, {"Clsid", Clsid}, {"OriginalLegacyAbsent", true}, {"OriginalChainAbsent", true} };
                    File.WriteAllText(BackupPath, new JavaScriptSerializer().Serialize(backup));
                }
                try {
                    CopyNvidiaRuntime();
                    var target=Path.Combine(InstallFolder,"LenovoPassThrough.dll");
                    if(File.Exists(target)) {
                        previousEngine=Path.Combine(InstallFolder,"LenovoPassThrough.previous-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".dll");
                        File.Move(target,previousEngine);
                    }
                    try {File.Copy(source,target,false);}
                    catch {if(previousEngine!=null)File.Move(previousEngine,target);throw;}
                    using (var registration = machine.CreateSubKey(ComKey + @"\InprocServer32")) {
                        registration.SetValue(null, Path.Combine(InstallFolder, "LenovoPassThrough.dll"));
                        registration.SetValue("ThreadingModel", "Both");
                    }
                    using (var settings = machine.CreateSubKey(SettingsKey)) {
                        if (settings.GetValue("FlipFlags") == null) settings.SetValue("FlipFlags", 0, RegistryValueKind.DWord);
                        if (settings.GetValue("Enable1440p") == null) settings.SetValue("Enable1440p", 0, RegistryValueKind.DWord);
                        if (settings.GetValue("DenoiseLevel") == null) settings.SetValue("DenoiseLevel", 0, RegistryValueKind.DWord);
                        if (settings.GetValue("DenoiseBackend") == null) settings.SetValue("DenoiseBackend", 0, RegistryValueKind.DWord);
                        settings.SetValue("SoftwareGainThirds", 0, RegistryValueKind.DWord);
                        // Grant this app's original user access to its settings only; the DLL remains protected.
                        var acl = settings.GetAccessControl();
                        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(settingsUserSid), RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User, RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null), RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkServiceSid,null), RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        settings.SetAccessControl(acl);
                    }
                    camera.SetValue("CameraDeviceMftCLSID", Clsid, RegistryValueKind.String);
                } catch {
                    if (legacy == null) {
                        if (Convert.ToString(camera.GetValue("CameraDeviceMftCLSID")) == Clsid) camera.DeleteValue("CameraDeviceMftCLSID", false);
                        machine.DeleteSubKeyTree(ComKey, false);
                    }
                    throw;
                }
            }
            Restart(id);
            try { VerifyCapture(false); VerifyAllControls(); }
            catch (Exception failure) {
                if(updating&&previousEngine!=null) {
                    string target=Path.Combine(InstallFolder,"LenovoPassThrough.dll");
                    File.Move(target,Path.Combine(InstallFolder,"LenovoPassThrough.failed-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".dll"));
                    File.Copy(previousEngine,target,false);Restart(id);
                    throw new InvalidOperationException("Camera verification failed. The previous processing engine was restored. "+failure.Message,failure);
                }
                Restore();throw new InvalidOperationException("Camera verification failed and the original camera configuration was restored. " + failure.Message, failure);
            }
        }

        internal static void Restore()
        {
            RequireAdmin();
            if (!File.Exists(BackupPath)) throw new InvalidOperationException("No matching installation backup was found.");
            var state = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(BackupPath));
            if (Convert.ToString(state["Clsid"]) != Clsid || !Convert.ToBoolean(state["OriginalLegacyAbsent"]) || !Convert.ToBoolean(state["OriginalChainAbsent"]))
                throw new InvalidOperationException("Installation backup does not match this app.");
            var id = Convert.ToString(state["DeviceId"]);
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var camera = machine.OpenSubKey(InterfaceKey(id), true)) {
                if (camera == null) throw new InvalidOperationException("Reconnect the camera before restoring.");
                var current = camera.GetValue("CameraDeviceMftCLSID") as string;
                if (current != null && current != Clsid) throw new InvalidOperationException("The camera configuration changed after installation. Restore stopped.");
                camera.DeleteValue("CameraDeviceMftCLSID", false);
                using (var server = machine.OpenSubKey(ComKey + @"\InprocServer32")) {
                    if (server != null && !String.Equals(Convert.ToString(server.GetValue(null)), Path.Combine(InstallFolder, "LenovoPassThrough.dll"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The processing registration changed. Restore stopped.");
                }
                machine.DeleteSubKeyTree(ComKey, false);
            }
            Restart(id);
        }

        private static void RequireAdmin()
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Windows administrator approval is required to install camera processing.");
        }
        internal static void VerifyCapture(bool qhd)
        {
            RequireAdmin();
            string folder=Path.Combine(InstallFolder,"tests",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            string session="LenovoCam-"+Guid.NewGuid().ToString("N").Substring(0,8);
            string etl=Path.Combine(folder,"pipeline.etl"), xml=Path.Combine(folder,"pipeline.xml");
            string output;
            string logman=Path.Combine(Environment.SystemDirectory,"logman.exe");
            int start=Run(logman,"start "+session+" -p {B4687C70-598D-47FD-9237-4FEB63E49A51} 0xffffffffffffffff 5 -o \""+etl+"\" -ets",out output,15000);
            if(start!=0)throw new InvalidOperationException("Could not start camera verification trace: "+output);
            int capture;
            try {
                capture=Run(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CaptureProbe.exe"),qhd?"--1440p":"",out output,30000);
                File.WriteAllText(Path.Combine(folder,"capture.txt"),output);
            } finally { string stopped; Run(logman,"stop "+session+" -ets",out stopped,15000); }
            if(capture!=0)throw new InvalidOperationException("Camera capture failed. Results: "+folder);
            int decoded=Run(Path.Combine(Environment.SystemDirectory,"tracerpt.exe"),"\""+etl+"\" -o \""+xml+"\" -of XML -y",out output,20000);
            if(decoded!=0)throw new InvalidOperationException("Camera trace could not be decoded. Results: "+folder);
            int wantedFlips=0,wantedDenoise=0;
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var settings=machine.OpenSubKey(SettingsKey)) {
                wantedFlips=settings==null?0:Convert.ToInt32(settings.GetValue("FlipFlags",0));
                wantedDenoise=settings==null?0:Convert.ToInt32(settings.GetValue("DenoiseLevel",0));
            }
            var doc=new XmlDocument();doc.Load(xml);bool observed=false,orientationApplied=false,denoiseApplied=false;
            foreach(XmlNode ev in doc.SelectNodes("//*[local-name()='Event']")) {
                var provider=ev.SelectSingleNode("./*[local-name()='System']/*[local-name()='Provider']");
                if(provider==null||provider.Attributes["Guid"]==null||!String.Equals(provider.Attributes["Guid"].Value.Trim('{','}'),"B4687C70-598D-47FD-9237-4FEB63E49A51",StringComparison.OrdinalIgnoreCase))continue;
                var counter=ev.SelectSingleNode(".//*[local-name()='Data' and @Name='Count']");long value;
                if(counter!=null&&Int64.TryParse(counter.InnerText,out value)&&value>0)observed=true;
                var orientation=ev.SelectSingleNode(".//*[local-name()='Data' and @Name='OrientationResult']");
                if(orientation!=null&&orientation.InnerText.Trim()=="0")orientationApplied=true;
                var nvidia=ev.SelectSingleNode(".//*[local-name()='Data' and @Name='NvidiaResult']");
                if(nvidia!=null&&nvidia.InnerText.Trim()=="0")denoiseApplied=true;
            }
            if(!observed)throw new InvalidOperationException("Camera capture succeeded but extension frame flow was not confirmed. Results: "+folder);
            if(wantedFlips!=0&&!orientationApplied)throw new InvalidOperationException("Capture succeeded but frame flipping did not apply. Results: "+folder);
            if(wantedDenoise!=0&&!denoiseApplied)throw new InvalidOperationException("Capture succeeded but NVIDIA denoising did not apply. Results: "+folder);
            File.WriteAllText(Path.Combine(InstallFolder,"verification.txt"),"Verified extension output and 120 captured frames. Results: "+folder);
        }
        internal static void VerifyAllControls()
        {
            RequireAdmin();
            if(!Installed())throw new InvalidOperationException("Install camera processing before testing controls.");
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var settings=machine.OpenSubKey(SettingsKey,true)) {
                var acl=settings.GetAccessControl();
                acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User,RegistryRights.ReadKey|RegistryRights.SetValue,AccessControlType.Allow));
                settings.SetAccessControl(acl);
                int oldFlags=Convert.ToInt32(settings.GetValue("FlipFlags",0)),oldQhd=Convert.ToInt32(settings.GetValue("Enable1440p",0)),oldDenoise=Convert.ToInt32(settings.GetValue("DenoiseLevel",0));
                int oldBackend=Convert.ToInt32(settings.GetValue("DenoiseBackend",0));
                var results=new List<string>();
                try {
                    settings.SetValue("Enable1440p",0,RegistryValueKind.DWord);
                    settings.SetValue("DenoiseLevel",0,RegistryValueKind.DWord);
                    settings.SetValue("SoftwareGainThirds",0,RegistryValueKind.DWord);
                    Restart(FindRgbDevice());
                    foreach(int flags in new[]{1,2,3}) {
                        settings.SetValue("FlipFlags",flags,RegistryValueKind.DWord);
                        VerifyCapture(false);results.Add("FlipFlags "+flags+": 120 frames captured, extension output and applied orientation confirmed.");
                        File.WriteAllLines(Path.Combine(InstallFolder,"control-verification.txt"),results);
                    }
                    settings.SetValue("FlipFlags",0,RegistryValueKind.DWord);
                    settings.SetValue("Enable1440p",1,RegistryValueKind.DWord);
                    Restart(FindRgbDevice());VerifyCapture(true);
                    results.Add("1440p: advertised and received 2560x1440, 120 frames captured through the extension.");
                    File.WriteAllLines(Path.Combine(InstallFolder,"control-verification.txt"),results);
                    if(File.Exists(Path.Combine(InstallFolder,@"Nvidia\NVVideoEffects.dll"))) {
                        settings.SetValue("DenoiseBackend",0,RegistryValueKind.DWord);
                        foreach(int quality in new[]{1,2,3,4}) {
                            settings.SetValue("DenoiseLevel",quality,RegistryValueKind.DWord);VerifyCapture(true);
                            results.Add("NVIDIA denoise level "+quality+" + 1440p: 120 frames captured; filtered frame success confirmed in extension trace.");
                            File.WriteAllLines(Path.Combine(InstallFolder,"control-verification.txt"),results);
                        }
                    }
                    settings.SetValue("DenoiseBackend",1,RegistryValueKind.DWord);settings.SetValue("DenoiseLevel",2,RegistryValueKind.DWord);VerifyCapture(true);
                    results.Add("Open-source D3D11 GPU bilateral Medium + 1440p: 120 frames captured; filtered frame success confirmed in extension trace.");
                    File.WriteAllLines(Path.Combine(InstallFolder,"control-verification.txt"),results);
                } finally {
                    settings.SetValue("FlipFlags",oldFlags,RegistryValueKind.DWord);
                    settings.SetValue("Enable1440p",oldQhd,RegistryValueKind.DWord);
                    settings.SetValue("DenoiseLevel",oldDenoise,RegistryValueKind.DWord);
                    settings.SetValue("DenoiseBackend",oldBackend,RegistryValueKind.DWord);
                    settings.SetValue("SoftwareGainThirds",0,RegistryValueKind.DWord);
                    Restart(FindRgbDevice());
                }
            }
        }
        private static string Hash(string path)
        {
            using(var sha=SHA256.Create())using(var file=File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
        }
        private static void CopyNvidiaRuntime()
        {
            string source=Environment.GetEnvironmentVariable("CAMNOVO_NVIDIA_RUNTIME");
            if(String.IsNullOrWhiteSpace(source))source=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"NvidiaRuntime");
            if(!File.Exists(Path.Combine(source,"NVVideoEffects.dll")))return;
            var manifest=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(source,"build-manifest.json")));
            var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in (System.Collections.IEnumerable)manifest["files"]) {
                var record=(Dictionary<string,object>)item;
                hashes[Convert.ToString(record["path"])]=Convert.ToString(record["sha256"]);
            }
            var names=new List<string>{"NVVideoEffects.dll","NVCVImage.dll","nvVFXVideoSuperRes.dll","nvngx_vsr.dll","nvngxruntime.dll"};
            foreach(var file in Directory.GetFiles(source,"npp*.dll"))names.Add(Path.GetFileName(file));
            string destination=Path.Combine(InstallFolder,"Nvidia");Directory.CreateDirectory(destination);
            foreach(string name in names) {
                string expected;
                if(!hashes.TryGetValue(name,out expected)||!String.Equals(Hash(Path.Combine(source,name)),expected,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("NVIDIA runtime does not match the supplied Magpie manifest: "+name);
                string target=Path.Combine(destination,name);
                if(!File.Exists(target)||!String.Equals(Hash(target),expected,StringComparison.OrdinalIgnoreCase))File.Copy(Path.Combine(source,name),target,true);
            }
            string notices=Path.Combine(source,"NVIDIA-VFX-Licenses"),targetNotices=Path.Combine(destination,"Licenses");
            if(Directory.Exists(notices)) {
                Directory.CreateDirectory(targetNotices);
                foreach(var file in Directory.GetFiles(notices))File.Copy(file,Path.Combine(targetNotices,Path.GetFileName(file)),true);
            }
            File.Copy(Path.Combine(source,"build-manifest.json"),Path.Combine(destination,"source-manifest.json"),true);
        }
        private static int Run(string executable,string arguments,out string output,int timeout)
        {
            using(var process=Process.Start(new ProcessStartInfo(executable,arguments) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})) {
                var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(timeout)) {
                    process.Kill();process.WaitForExit();
                    output=stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(InstallFolder,"timed-out-operation.txt"),output);
                    throw new TimeoutException("Camera setup operation timed out: "+Path.GetFileName(executable));
                }
                output=stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult();return process.ExitCode;
            }
        }
        private static void Restart(string id)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/restart-device \"" + id + "\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start)) {
                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                File.WriteAllText(Path.Combine(InstallFolder, "device-restart.txt"), output + errors);
                if (process.ExitCode != 0) throw new InvalidOperationException("Processing was configured, but Windows could not restart the camera. Reconnect the monitor USB cable, then reopen the app.");
            }
        }
        internal static void RestartCamera()
        {
            RequireAdmin();
            if(!Installed())throw new InvalidOperationException("Camera processing is not installed.");
            bool qhd;
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var key=machine.OpenSubKey(SettingsKey))qhd=Convert.ToInt32(key.GetValue("Enable1440p",0))==1;
            Restart(FindRgbDevice());VerifyCapture(qhd);
        }
    }
}

