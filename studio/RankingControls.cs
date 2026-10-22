using System.Windows;
using System.Windows.Controls;
namespace LenovoCameraStudio;
public partial class MainWindow
{
    private readonly DenoiserRanking ranking=DenoiserRanking.Load();
    private int SelectedBackend {get=>BackendChoice.SelectedValue is int id?id:0;set=>BackendChoice.SelectedValue=value;}
    private void InitializeRanking()
    {
        var qualified=ranking.Qualified.ToDictionary(p=>p.Backend);
        var order=qualified.Keys.OrderBy(b=>qualified[b].Rank).Concat(Enumerable.Range(0,ModelHost.Names.Length).Where(b=>!qualified.ContainsKey(b)));
        foreach(int backend in order){string label=qualified.TryGetValue(backend,out var p)?$"{p.Rank}. {ModelHost.Names[backend]}":ModelHost.Names[backend];BackendChoice.Items.Add(new ComboBoxItem{Tag=backend,Content=label});}
        if(ranking.Best is not { } best){RankingSummary.Text="No local comparison is available.";UseRecommendedButton.IsEnabled=false;RankingExpander.Visibility=Visibility.Collapsed;return;}
        RankingSummary.Text=$"Recommended: {ModelHost.Names[best.Backend]} · {best.Strength}";
        RankingHint.Text=$"Detail and noise balance. Measured delay {best.P95:0} ms · {best.Fps:0.#} fps.";
        foreach(var p in ranking.Qualified){
            var button=new Button{HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,0,0,8),Padding=new Thickness(10),ToolTip="Use this tested model and strength immediately."};
            var text=new StackPanel();text.Children.Add(new TextBlock{Text=$"{p.Rank}. {ModelHost.Names[p.Backend]}",FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            text.Children.Add(new TextBlock{Text=$"{p.Strength} · {p.Size} · {p.Fps:0.#} fps · {p.P95:0} ms",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
            text.Children.Add(new TextBlock{Text=$"{p.Detail} detail · {p.Noise} noise reduction",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
            if(p.Note.Length>0)text.Children.Add(new TextBlock{Text=p.Note,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0),FontSize=12});
            button.Content=text;button.Click+=(_,_)=>ApplyRankedProfile(p);RankingList.Children.Add(button);
        }
        RankingFootnote.Text=$"Compared {ranking.Date} on {ranking.Gpu}. Order favours fine detail while reducing noise. Click a result to use its tested settings. Delay is the measured 95th percentile; every listed profile's tested maximum was under 500 ms. Lighting and movement can change results.";
        UseRecommendedButton.Click+=(_,_)=>ApplyRankedProfile(best);
    }
    private void ApplyRankedProfile(DenoiserRanking.Profile profile)
    {
        if(!BackendChoice.IsEnabled)return;
        if(profile.Backend>=2&&(!ModelHost.Available(profile.Backend)||!ModelHost.EngineCurrent)){Status("This model is not available. Check the runtime and camera processing installation.",true);return;}
        loading=true;
        try{
            SelectedBackend=profile.Backend;
            ModelSizeChoice.SelectedIndex=Array.IndexOf(ModelWidths,profile.Width);
            // Use the exact detail-preserving size that passed the comparison.
            // Automatic sizing remains available as a separate user choice.
            AutoModelSizeToggle.IsChecked=false;ModelSizeLabel.Text="Fixed processing size";
            DenoiseChoice.SelectedIndex=profile.Level;
            ShadowCleanupToggle.IsChecked=false;
            SettingsStore.Write("DenoiseModelWidth",profile.Width);SettingsStore.Write($"DenoiseModelWidth{profile.Backend}",profile.Width);
            SettingsStore.Write("DenoiseAutoSize",0);SettingsStore.Write("ShadowCleanupEnabled",0);
        }finally{loading=false;}
        RefreshBackendAvailability();SaveDenoise();
        Status($"Using {ModelHost.Names[profile.Backend]} · {profile.Strength}. Tested for detail and noise below 500 ms.");
    }
}
