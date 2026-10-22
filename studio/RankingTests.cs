namespace LenovoCameraStudio;
internal static class RankingTests
{
    internal static void Run()
    {
        DenoiserRanking.Profile P(int backend,int rank,bool verified=true,double p95=300,double max=400)=>new(){Backend=backend,Rank=rank,Width=1920,Level=1,Verified=verified,P95=p95,MaxAge=max,Fps=10};
        var ranking=new DenoiserRanking{Profiles=[P(0,2),P(2,1),P(1,3,false),P(4,4,true,450,510),P(99,5),P(2,6),P(5,7,true,double.NaN,400)]};
        var list=ranking.Qualified.ToArray();
        if(list.Length!=2||list[0].Backend!=2||list[1].Backend!=0||ranking.Best?.Backend!=2)throw new Exception("Ranking changed backend identity, admitted failed/late/invalid records, or duplicated a model.");
        if(new DenoiserRanking().Best is not null)throw new Exception("Missing measurements must not invent a recommendation.");
    }
}
