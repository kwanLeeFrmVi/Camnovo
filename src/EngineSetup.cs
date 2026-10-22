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
using System.ServiceProcess;
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
                if(path == null || !File.Exists(path) || !String.Equals(path, Path.Combine(InstallFolder, "LenovoPassThrough.dll"), StringComparison.OrdinalIgnoreCase))return false;
                try {
                    using(var camera=machine.OpenSubKey(InterfaceKey(FindRgbDevice()))) {
                        if(camera==null)return false;
                        var chain=camera.GetValue("CameraDeviceMftCLSIDChain") as string[];
                        return chain!=null?Array.Exists(chain,x=>String.Equals(x,Clsid,StringComparison.OrdinalIgnoreCase)):
                            String.Equals(camera.GetValue("CameraDeviceMftCLSID") as string,Clsid,StringComparison.OrdinalIgnoreCase);
                    }
                }catch(InvalidOperationException){return false;}
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
            string legacyBefore=null;string[] chainBefore=null;
            string backupBefore=File.Exists(BackupPath)?File.ReadAllText(BackupPath):null;
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var camera = machine.OpenSubKey(path, true))
            {
                if (camera == null) throw new InvalidOperationException("The RGB camera interface is unavailable.");
                var legacy = camera.GetValue("CameraDeviceMftCLSID") as string;
                var chain=camera.GetValue("CameraDeviceMftCLSIDChain") as string[];
                legacyBefore=legacy;chainBefore=chain;
                string[] planned=PlanChain(legacy,chain);
                updating=chain!=null?Array.Exists(chain,x=>String.Equals(x,Clsid,StringComparison.OrdinalIgnoreCase)):String.Equals(legacy,Clsid,StringComparison.OrdinalIgnoreCase);
                if (updating && !File.Exists(BackupPath)) throw new InvalidOperationException("Existing extension has no matching backup. Installation stopped.");
                if (File.Exists(BackupPath)) {
                    var previous = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(BackupPath));
                    if (Convert.ToString(previous["DeviceId"]) != id || Convert.ToString(previous["Clsid"]) != Clsid)
                        throw new InvalidOperationException("The earlier camera backup belongs to a different installation.");
                }
                Directory.CreateDirectory(InstallFolder);
                var backup=updating?new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(backupBefore):
                    new Dictionary<string,object>{{"DeviceId",id},{"Clsid",Clsid},{"OriginalLegacyAbsent",legacy==null},{"OriginalChainAbsent",chain==null},{"OriginalLegacyValue",legacy},{"OriginalChainValues",chain}};
                backup["InstalledChain"]=planned;backup["InstalledLegacyValue"]=planned.Length==0?Clsid:legacy;
                if(!updating&&backupBefore!=null)File.Copy(BackupPath,BackupPath+".previous-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"),false);
                File.WriteAllText(BackupPath,new JavaScriptSerializer().Serialize(backup));
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
                        if (settings.GetValue("ShadowCleanupEnabled") == null) settings.SetValue("ShadowCleanupEnabled", 0, RegistryValueKind.DWord);
                        if (settings.GetValue("ShadowCleanupStrength") == null) settings.SetValue("ShadowCleanupStrength", 70, RegistryValueKind.DWord);
                        settings.SetValue("SoftwareGainThirds", 0, RegistryValueKind.DWord);
                        // Grant this app's original user access to its settings only; the DLL remains protected.
                        var acl = settings.GetAccessControl();
                        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(settingsUserSid), RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        acl.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User, RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null), RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkServiceSid,null), RegistryRights.ReadKey | RegistryRights.SetValue, AccessControlType.Allow));
                        settings.SetAccessControl(acl);
                    }
                    SetAttachment(camera,backup["InstalledLegacyValue"]==null?null:Convert.ToString(backup["InstalledLegacyValue"]),planned.Length==0?null:planned);
                } catch {
                    SetAttachment(camera,legacyBefore,chainBefore);
                    if(backupBefore!=null)File.WriteAllText(BackupPath,backupBefore);else File.Delete(BackupPath);
                    throw;
                }
            }
            int savedLevel=0;
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var settings=machine.OpenSubKey(SettingsKey,true)){savedLevel=Convert.ToInt32(settings.GetValue("DenoiseLevel",0));settings.SetValue("DenoiseLevel",0,RegistryValueKind.DWord);}
            try { Restart(id); VerifyCapture(false); VerifyAllControls(); }
            catch (Exception failure) {
                if(previousEngine!=null) {
                    string target=Path.Combine(InstallFolder,"LenovoPassThrough.dll");
                    File.Move(target,Path.Combine(InstallFolder,"LenovoPassThrough.failed-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".dll"));
                    File.Copy(previousEngine,target,false);
                }
                using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
                using(var camera=machine.OpenSubKey(path,true))SetAttachment(camera,legacyBefore,chainBefore);
                if(backupBefore!=null)File.WriteAllText(BackupPath,backupBefore);else File.Delete(BackupPath);
                string restartNote="";
                try{Restart(id,false);}catch(Exception recovery){restartNote=" Camera reconnection also reported: "+recovery.Message;}
                throw new InvalidOperationException("Camera verification failed. The prior camera attachment and processing engine were restored. "+failure.Message+restartNote,failure);
            }
            finally{using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var settings=machine.OpenSubKey(SettingsKey,true)){if(settings!=null)settings.SetValue("DenoiseLevel",savedLevel,RegistryValueKind.DWord);}}
        }

        internal static void Restore()
        {
            RequireAdmin();
            if (!File.Exists(BackupPath)) throw new InvalidOperationException("No matching installation backup was found.");
            var state = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(BackupPath));
            if (Convert.ToString(state["Clsid"]) != Clsid)
                throw new InvalidOperationException("Installation backup does not match this app.");
            var id = Convert.ToString(state["DeviceId"]);
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var camera = machine.OpenSubKey(InterfaceKey(id), true)) {
                if (camera == null) throw new InvalidOperationException("Reconnect the camera before restoring.");
                var current = camera.GetValue("CameraDeviceMftCLSID") as string;
                var chain=camera.GetValue("CameraDeviceMftCLSIDChain") as string[];
                var wanted=state.ContainsKey("InstalledChain")?Strings(state["InstalledChain"]):new string[0];
                if(wanted.Length>0) {
                    if(chain==null||!String.Equals(String.Join("|",chain),String.Join("|",wanted),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The camera configuration changed after installation. Restore stopped.");
                }else if(chain!=null||!String.Equals(current,Clsid,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The camera configuration changed after installation. Restore stopped.");
                using (var server = machine.OpenSubKey(ComKey + @"\InprocServer32")) {
                    if (server != null && !String.Equals(Convert.ToString(server.GetValue(null)), Path.Combine(InstallFolder, "LenovoPassThrough.dll"), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The processing registration changed. Restore stopped.");
                }
                SetAttachment(camera,Convert.ToBoolean(state["OriginalLegacyAbsent"])?null:Convert.ToString(state["OriginalLegacyValue"]),Convert.ToBoolean(state["OriginalChainAbsent"])?null:Strings(state["OriginalChainValues"]));
                machine.DeleteSubKeyTree(ComKey, false);
            }
            Restart(id,false);
        }

        // Restrict the new chaining path to the observed Lenovo/Realtek DMFT.
        // Unknown vendors and occupied two-stage chains require separate review.
        internal static string[] PlanChain(string legacy,string[] chain)
        {
            const string realtek="{09824200-9A44-4B06-8C74-92D99E09B435}";
            if(legacy!=null&&!String.Equals(legacy,Clsid,StringComparison.OrdinalIgnoreCase)&&!String.Equals(legacy,realtek,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("An unrecognized camera extension needs review before installation.");
            if(chain!=null&&(chain.Length>2||new HashSet<string>(chain,StringComparer.OrdinalIgnoreCase).Count!=chain.Length))throw new InvalidOperationException("An invalid or full camera extension chain needs review before installation.");
            var others=new List<string>();
            if(chain!=null)foreach(string item in chain)if(!String.Equals(item,Clsid,StringComparison.OrdinalIgnoreCase))others.Add(item);
            if(chain==null&&legacy!=null&&!String.Equals(legacy,Clsid,StringComparison.OrdinalIgnoreCase))others.Add(legacy);
            if(others.Count==0)return new string[0];
            if(others.Count!=1||!String.Equals(others[0],realtek,StringComparison.OrdinalIgnoreCase)||
                (legacy!=null&&!String.Equals(legacy,Clsid,StringComparison.OrdinalIgnoreCase)&&!String.Equals(legacy,realtek,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An unrecognized or full camera extension chain needs review before installation.");
            return new[]{others[0],Clsid};
        }
        private static string[] Strings(object value){var list=new List<string>();if(value!=null)foreach(var item in (System.Collections.IEnumerable)value)list.Add(Convert.ToString(item));return list.ToArray();}
        private static void SetAttachment(RegistryKey camera,string legacy,string[] chain)
        {
            if(legacy==null)camera.DeleteValue("CameraDeviceMftCLSID",false);else camera.SetValue("CameraDeviceMftCLSID",legacy,RegistryValueKind.String);
            if(chain==null)camera.DeleteValue("CameraDeviceMftCLSIDChain",false);else camera.SetValue("CameraDeviceMftCLSIDChain",chain,RegistryValueKind.MultiString);
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
            int decoded=Run(Path.Combine(Environment.SystemDirectory,"tracerpt.exe"),"\""+etl+"\" -o \""+xml+"\" -of XML -y",out output,20000);
            if(capture!=0)throw new InvalidOperationException("Camera capture failed. Results: "+folder);
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
                    throw new System.TimeoutException("Camera setup operation timed out: "+Path.GetFileName(executable));
                }
                output=stdout.GetAwaiter().GetResult()+stderr.GetAwaiter().GetResult();return process.ExitCode;
            }
        }
        private static void Restart(string id,bool applyInstalled=true)
        {
            // Only the user-mode processing attachment is changing. Resetting
            // the USB device is unnecessary and the Realtek package can defer
            // repeated PnP restarts until a full Windows reboot.
            StopFrameServer();
            // Retain the recorded vendor-first attachment when reconnecting.
            if(applyInstalled&&File.Exists(BackupPath)) {
                var state=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(BackupPath));
                if(state.ContainsKey("InstalledChain"))using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
                using(var camera=machine.OpenSubKey(InterfaceKey(id),true)) {
                    var chain=Strings(state["InstalledChain"]);SetAttachment(camera,state["InstalledLegacyValue"]==null?null:Convert.ToString(state["InstalledLegacyValue"]),chain.Length==0?null:chain);
                }
            }
            // Frame Server can hold the old Device MFT instance and advertised
            // output modes. Release it
            // before verification or reconnecting preview. Camera apps must
            // be closed for this operation; Windows starts it again on demand.
            StopFrameServer();
        }
        private static void StopFrameServer()
        {
            using(var server=new ServiceController("FrameServer")) {
                if(server.Status!=ServiceControllerStatus.Stopped) {
                    uint previousProcess=FrameServerProcess();
                    if(server.Status!=ServiceControllerStatus.StopPending)server.Stop();
                    var deadline=DateTime.UtcNow.AddSeconds(15);
                    do {
                        server.Refresh();
                        if(server.Status==ServiceControllerStatus.Stopped)return;
                        // Windows can immediately start Frame Server again for
                        // another client. A new service process also proves
                        // that the old camera/DLL instance has been released.
                        if(previousProcess!=0&&FrameServerProcess()!=previousProcess)return;
                        System.Threading.Thread.Sleep(200);
                    }while(DateTime.UtcNow<deadline);
                    throw new System.TimeoutException("Windows did not release the previous camera service. Close other camera apps and try again.");
                }
            }
        }
        private static uint FrameServerProcess()
        {
            using(var query=new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Service WHERE Name='FrameServer'"))
            using(var result=query.Get())foreach(ManagementObject service in result)using(service)return Convert.ToUInt32(service["ProcessId"]);
            return 0;
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

