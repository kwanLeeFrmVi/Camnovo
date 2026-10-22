namespace LenovoCameraStudio;

internal static class DenoiseDelivery
{
    internal sealed record Presentation(string Title,string Advice,bool Warning);
    internal static Presentation Evaluate(bool requested,bool confirmed,int result,bool progressing,string share,long sinceSelection,string phase,bool automatic,bool mixed=false)
    {
        if(!requested)return new("Noise reduction off","",false);
        if(confirmed&&progressing&&result>=0)return mixed?new("Denoising intermittent"+share,"Some original frames are being shown. Try automatic sizing or a faster model.",true):new("Denoising active"+share,"Filtered frames are reaching the camera output. Hold Original to compare.",false);
        if(phase=="error")return new("Showing original · model failed","Select Off, then a strength to retry. Error details are available below.",true);
        if(confirmed&&result==unchecked((int)0x800705B4))return new("Showing original · model too slow",automatic?"Automatic sizing reduces detail to fit the latency budget. If this persists, use a faster model. The 500 ms limit is still active.":"Enable automatic sizing, choose a smaller processing size, or use a faster model. The 500 ms limit is still active.",true);
        if(phase is "starting" or "loading" or "ready"||sinceSelection<8000)return new("Loading · original image shown","Denoising starts when the first usable result reaches the camera output.",true);
        if(confirmed&&result<0)return new("Showing original · processing unavailable","Select Off, then a strength to retry. The requested effect is not being delivered.",true);
        if(progressing)return new("Denoising interrupted","Some original frames are being shown. Try automatic sizing or a faster model.",true);
        return new("Showing original · no filtered video",confirmed?"The model has not delivered a usable result. Try automatic sizing or a faster model.":"Waiting for the camera to confirm filtered output. Keep this app open.",true);
    }
}
