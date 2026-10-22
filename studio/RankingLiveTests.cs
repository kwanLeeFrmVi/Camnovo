using System.IO;
using System.Text.Json;
namespace LenovoCameraStudio;
public partial class MainWindow
{
    internal async Task RankingLiveTests(string candidates,string report)
    {
        ShowInTaskbar=false;Opacity=0;Show();
        var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
        var profiles=JsonSerializer.Deserialize<List<DenoiserRanking.Profile>>(File.ReadAllText(candidates),options)??throw new Exception("No comparison profiles.");
        var records=new List<object>();var saved=new[]{"DenoiseBackend","DenoiseLevel","DenoiseModelWidth","DenoiseAutoSize","ShadowCleanupEnabled"}.Concat(Enumerable.Range(2,7).Select(b=>$"DenoiseModelWidth{b}")).ToDictionary(n=>n,n=>SettingsStore.Read(n));
        void Save()=>File.WriteAllText(report,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));
        try{
            var until=DateTime.UtcNow.AddSeconds(30);while(!running&&DateTime.UtcNow<until)await Task.Delay(100);
            if(!running)throw new Exception("Camera preview did not start.");
            DenoiseChoice.SelectedIndex=0;ShadowCleanupToggle.IsChecked=false;SettingsStore.Write("ShadowCleanupEnabled",0);AutoModelSizeToggle.IsChecked=false;
            await WaitFrames(45);
            foreach(var group in profiles.GroupBy(p=>p.Backend))foreach(var p in group){
                bool passed=false;
                try{
                    SelectedBackend=p.Backend;ModelSizeChoice.SelectedIndex=Array.IndexOf(ModelWidths,p.Width);SettingsStore.Write("DenoiseModelWidth",p.Width);DenoiseChoice.SelectedIndex=p.Level;
                    var deadline=DateTime.UtcNow.AddSeconds(45);bool active=false;
                    while(DateTime.UtcNow<deadline){await Task.Delay(100);var state=SettingsStore.Backend();if(state.Fresh&&state.Kind==p.Backend&&state.Level==p.Level&&state.Filtered>=8){active=true;break;}}
                    if(!active)throw new Exception("No filtered delivery within 45 seconds.");
                    await WaitFrames(10);var before=SettingsStore.Backend();var perf=await SampleRankingPerformance(p.Backend==ranking.Best?.Backend?180:50);var after=SettingsStore.Backend();
                    long filtered=after.Filtered-before.Filtered,raw=after.Raw-before.Raw;
                    passed=filtered>=4&&raw==0&&perf.P95<500&&perf.Max<500;
                    records.Add(new{p.Backend,p.Width,p.Level,p.Score,Verified=passed,perf.Fps,Median=perf.Age,perf.P95,MaxAge=perf.Max,Filtered=filtered,Original=raw,Error=passed?"":"Latency or filtered delivery failed."});Save();
                }catch(Exception e){records.Add(new{p.Backend,p.Width,p.Level,p.Score,Verified=false,Error=e.Message});Save();}
                finally{DenoiseChoice.SelectedIndex=0;await WaitFrames(45);}
                if(passed)break;
            }
        }finally{modelHost.Stop();foreach(var pair in saved)SettingsStore.Write(pair.Key,pair.Value);Save();Close();}
    }
    private async Task<(double Fps,double Age,double P95,double Max)> SampleRankingPerformance(int count)
    {
        var fps=new List<double>();var ages=new List<double>();
        for(int i=0;i<count;i++){await Task.Delay(65);CameraApi.PreviewStats(out double f,out double age,out _);fps.Add(f);ages.Add(age);}
        fps.Sort();ages.Sort();return(fps[count/2],ages[count/2],ages[(int)Math.Floor((count-1)*.95)],ages[^1]);
    }
}
