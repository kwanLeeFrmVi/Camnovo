using System.IO;
using System.Text.Json;
namespace LenovoCameraStudio;

internal sealed class DenoiserRanking
{
    internal sealed class Profile
    {
        public int Backend {get;set;}
        public int Rank {get;set;}
        public int Width {get;set;}
        public int Level {get;set;}
        public double P95 {get;set;}
        public double MaxAge {get;set;}
        public double Fps {get;set;}
        public double Score {get;set;}
        public string Detail {get;set;}="";
        public string Noise {get;set;}="";
        public string Note {get;set;}="";
        public bool Verified {get;set;}
        public string Strength=>new[]{"Off","Low","Medium","High","Ultra"}[Math.Clamp(Level,0,4)];
        public string Size=>Width==1920?"1080p processing":$"{Width} px processing";
    }
    public string Date {get;set;}="";
    public string Gpu {get;set;}="";
    public string Summary {get;set;}="";
    public List<Profile> Profiles {get;set;}=[];
    internal IEnumerable<Profile> Qualified=>Profiles.Where(p=>p.Backend is >=0 and <9&&p.Level is >=1 and <=4&&p.Rank>0&&new[]{128,192,320,640,960,1280,1920}.Contains(p.Width)&&p.Verified&&p.P95 is >0 and <500&&p.MaxAge>=p.P95&&p.MaxAge<500&&p.Fps>0).OrderBy(p=>p.Rank).DistinctBy(p=>p.Backend);
    internal Profile? Best=>Qualified.FirstOrDefault();
    internal static DenoiserRanking Load()
    {
        try{var ranking=JsonSerializer.Deserialize<DenoiserRanking>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Models","denoiser-ranking.json")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new();ranking.Profiles=ranking.Profiles?.Where(p=>p is not null).ToList()??[];return ranking;}
        catch{return new();}
    }
}
