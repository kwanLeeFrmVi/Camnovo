using System;
using LenovoWebcamPrototype;
class AttachmentPlanTests {
    const string Realtek="{09824200-9A44-4B06-8C74-92D99E09B435}";
    static void Plan(string legacy,string[] chain,string expected){string actual=String.Join("|",EngineSetup.PlanChain(legacy,chain));if(actual!=expected)throw new Exception("Unexpected chain plan: "+actual);}
    static void Reject(string legacy,string[] chain){try{EngineSetup.PlanChain(legacy,chain);}catch(InvalidOperationException){return;}throw new Exception("Unrecognized or invalid attachment was accepted.");}
    static int Main(){try{
        Plan(null,null,"");Plan(EngineSetup.Clsid,null,"");Plan(EngineSetup.Clsid,new[]{EngineSetup.Clsid},"");
        string paired=Realtek+"|"+EngineSetup.Clsid;
        Plan(Realtek,null,paired);Plan(Realtek,new[]{Realtek},paired);Plan(Realtek,new[]{Realtek,EngineSetup.Clsid},paired);
        Plan(null,new[]{Realtek},paired);
        Reject("{11111111-1111-1111-1111-111111111111}",null);
        Reject(null,new[]{Realtek,Realtek});Reject(Realtek,new[]{Realtek,EngineSetup.Clsid,EngineSetup.Clsid});
        Reject(null,new[]{Realtek,"{11111111-1111-1111-1111-111111111111}"});
        Console.WriteLine("PASS: Microsoft and Realtek attachment plans; vendor-first order; updates are idempotent; unknown, duplicated and full chains rejected.");return 0;
    }catch(Exception error){Console.WriteLine(error);return 1;}}
}
