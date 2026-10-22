namespace LenovoCameraStudio;
internal static class DenoiseDeliveryTests
{
    internal static void Run()
    {
        void Check(bool valid,string error){if(!valid)throw new Exception(error);}
        const string prefix=@"\\?\USB#VID_17EF&PID_483C&MI_00#INSTANCE#";
        Check(SettingsStore.RegistrationPath(prefix+@"{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL")==@"{e5323777-f976-4f5b-9b55-b94699c46e44}\##?#USB#VID_17EF&PID_483C&MI_00#INSTANCE#{e5323777-f976-4f5b-9b55-b94699c46e44}","Camera registration path parser failed.");
        Check(SettingsStore.RegistrationPath(prefix+@"{65e8773d-8f56-11d0-a3b9-00a0c9223196}\GLOBAL")!.StartsWith("{65e8773d"),"Legacy route was falsely reported as the camera processing route.");
        Check(SettingsStore.RegistrationPath(@"\\?\USB#VID_17EF&PID_483C&MI_02#IR#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL")==null,"IR route must not be used for RGB attachment checks.");
        var waiting=DenoiseDelivery.Evaluate(true,true,0,false,"",10000,"active",true);
        Check(waiting.Warning&&waiting.Title.Contains("original"),"Worker activity alone must not claim delivered denoising.");
        var late=DenoiseDelivery.Evaluate(true,true,unchecked((int)0x800705B4),false,"",10000,"processing",true);
        Check(late.Warning&&late.Title.Contains("too slow"),"Late output must say the original image is shown.");
        var error=DenoiseDelivery.Evaluate(true,true,unchecked((int)0x80004005),false,"",10000,"error",true);
        Check(error.Warning&&error.Title.Contains("failed"),"Model error must remain visible during fallback.");
        var loading=DenoiseDelivery.Evaluate(true,false,0,false,"",1000,"loading",true);
        Check(loading.Warning&&loading.Title.Contains("original"),"Loading must disclose original output.");
        var active=DenoiseDelivery.Evaluate(true,true,1,true," · 100% filtered",10000,"processing",true);
        Check(!active.Warning&&active.Title.StartsWith("Denoising active"),"Fresh filtered delivery must be distinguished from waiting for the next result.");
        var mixed=DenoiseDelivery.Evaluate(true,true,0,true," · 50% filtered",10000,"processing",true,true);
        Check(mixed.Warning&&mixed.Title.Contains("intermittent"),"Interleaved original frames must not appear as uninterrupted filtering.");
        var stale=DenoiseDelivery.Evaluate(true,false,0,true,"",10000,"processing",true);
        Check(stale.Warning,"Stale telemetry must not confirm filtering.");
    }
}
