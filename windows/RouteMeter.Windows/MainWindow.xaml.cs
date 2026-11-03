using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RouteMeter.Windows;
public partial class MainWindow : Window
{
    private readonly CoreClient client=new();
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly string root=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RouteMeter");
    private string? manifest;
    private CoreSample? current,previous;
    private bool loading,quitting,connected;
    private DateTimeOffset? updated;
    private Baseline? baseline;
    private readonly Dictionary<string,Counts> rates=[];
    private Counts directRate=new(),proxyRate=new(),unknownRate=new();
    public sealed class Baseline {public string Epoch {get;set;}="";public long Since {get;set;}public Routes Totals {get;set;}=new();public Dictionary<string,Counts> Apps {get;set;}=[];}
    public sealed record Row(string Name,string Download,string Upload,string Total,string Path="");
    public MainWindow()
    {
        InitializeComponent();Height=Math.Min(Height,SystemParameters.WorkArea.Height-40);
        Directory.CreateDirectory(root);
        try {manifest=File.ReadAllText(System.IO.Path.Combine(root,"core-path.txt"));} catch { }
        try {baseline=JsonSerializer.Deserialize<Baseline>(File.ReadAllText(System.IO.Path.Combine(root,"baseline.json")),CoreClient.Json);} catch { }
        tray=new System.Windows.Forms.NotifyIcon {Icon=System.Drawing.SystemIcons.Application,Text="RouteMeter",Visible=true};
        tray.MouseClick+=(_,e)=>{if(e.Button==System.Windows.Forms.MouseButtons.Left){if(IsVisible)Hide();else{Show();WindowState=WindowState.Normal;Activate();}}};
        var menu=new System.Windows.Forms.ContextMenuStrip();menu.Items.Add("Open",null,(_,_)=>{Show();Activate();});menu.Items.Add("Quit",null,(_,_)=>Quit());tray.ContextMenuStrip=menu;
        timer.Tick+=async (_,_)=>await Poll();timer.Start();
        Loaded+=async (_,_)=>await Poll();
    }
    private async Task Poll()
    {
        if(loading || string.IsNullOrWhiteSpace(manifest))return;loading=true;
        var selected=manifest;
        try
        {
            var sample=await client.Read(selected);
            if(selected!=manifest)return;
            directRate=new();proxyRate=new();unknownRate=new();rates.Clear();
            if(current is not null && current.Instance==sample.Instance && sample.Timestamp>current.Timestamp)
            {
                var seconds=(sample.Timestamp-current.Timestamp)/1000m;
                directRate=Rate(sample.Totals.Direct,current.Totals.Direct,seconds);
                proxyRate=Rate(sample.Totals.Proxy,current.Totals.Proxy,seconds);
                unknownRate=Rate(sample.Totals.Unknown,current.Totals.Unknown,seconds);
                var old=current.Apps.ToDictionary(a=>a.Id);
                foreach(var app in sample.Apps)rates[app.Id]=Rate(app.Usage.Proxy,old.GetValueOrDefault(app.Id)?.Usage.Proxy??new Counts(),seconds);
            }
            previous=current;current=sample;updated=DateTimeOffset.UtcNow;connected=true;Status.Text="Core connected";Render();
        }
        catch {connected=false;Status.Text="Core unavailable";Render();LiveDirect.Text="Direct   — | —";LiveProxy.Text="Proxy    — | —";LiveTotal.Text="Total    — | —";}
        finally {loading=false;}
    }
    private Counts Adjust(Counts value,Counts? before)=>Counts.Difference(value,baseline?.Epoch==current?.Epoch?before??new():new());
    private void Render()
    {
        if(current is null)return;
        var fresh=connected && updated.HasValue && DateTimeOffset.UtcNow-updated.Value<TimeSpan.FromSeconds(5);
        string Pair(Counts c)=>Format(c.Download,true)+" | "+Format(c.Upload,true);
        LiveDirect.Text="Direct   "+Pair(directRate);LiveProxy.Text="Proxy    "+Pair(proxyRate);LiveTotal.Text="Total    "+Pair(Counts.Sum(Counts.Sum(directRate,proxyRate),unknownRate));
        if(!fresh){LiveDirect.Text="Direct   — | —";LiveProxy.Text="Proxy    — | —";LiveTotal.Text="Total    — | —";}
        var d=Adjust(current.Totals.Direct,baseline?.Totals.Direct);var p=Adjust(current.Totals.Proxy,baseline?.Totals.Proxy);var u=Adjust(current.Totals.Unknown,baseline?.Totals.Unknown);
        Row UsageRow(string name,Counts c)=>new(name,Format(c.Download),Format(c.Upload),Format(c.Total));
        var summary=new List<Row>{UsageRow("Direct",d),UsageRow("Proxy",p)};if(u.Total>0)summary.Add(UsageRow("Unknown",u));summary.Add(UsageRow("Total",Counts.Sum(Counts.Sum(d,p),u)));UsageGrid.ItemsSource=summary;
        var since=baseline?.Epoch==current.Epoch?baseline.Since:current.Since;Since.Text="Since "+DateTimeOffset.FromUnixTimeMilliseconds(since).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var live=ShowRates.IsChecked==true;var search=Filter.Text??"";
        var apps=current.Apps.Select(app=>new {App=app,Count=live?rates.GetValueOrDefault(app.Id)??new():Adjust(app.Usage.Proxy,baseline?.Apps.GetValueOrDefault(app.Id))})
            .Where(a=>a.Count.Total>0 && a.App.Name.Contains(search,StringComparison.OrdinalIgnoreCase)).OrderByDescending(a=>a.Count.Total).Take(50)
            .Select(a=>new Row(a.App.Name,live&&!fresh?"—":Format(a.Count.Download,live),live&&!fresh?"—":Format(a.Count.Upload,live),live&&!fresh?"—":Format(a.Count.Total,live),a.App.Executable??""));
        AppGrid.ItemsSource=apps.ToList();
        var unattributed=current.Apps.Where(a=>a.Attribution!="executable").Aggregate(new Counts(),(sum,a)=>Counts.Sum(sum,Adjust(a.Usage.Proxy,baseline?.Apps.GetValueOrDefault(a.Id))));
        Quality.Text=!string.IsNullOrEmpty(current.PersistenceError)?current.PersistenceError:current.Overflow?"Counter overflow: measurement is incomplete.":current.UncertainUpload>0?"Transfer errors left "+Format(current.UncertainUpload)+" of possible upload unmeasured.":unattributed.Total>0?"Unattributed Proxy: "+Format(unattributed.Total):"Source: RouteMeter Core · complete connection lifecycle counters";
        tray.Text="RouteMeter | Proxy ↓"+Format(proxyRate.Download,true)+" ↑"+Format(proxyRate.Upload,true);
    }
    private static Counts Rate(Counts now,Counts before,decimal seconds)
    {
        var delta=Counts.Difference(now,before);
        ulong Value(ulong n)=>(ulong)Math.Min(ulong.MaxValue,n/seconds);
        return new Counts{Download=Value(delta.Download),Upload=Value(delta.Upload)};
    }
    private static string Format(ulong bytes,bool rate=false)
    {
        string[] units=["B","KB","MB","GB","TB","PB","EB"];double n=bytes;int unit=0;
        while(n>=1024 && unit<units.Length-1){n/=1024;unit++;}
        return n.ToString(unit==0||n>=100?"0":"0.0",System.Globalization.CultureInfo.InvariantCulture)+" "+units[unit]+(rate?"/s":"");
    }
    private async void Connect_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="Choose RouteMeter Core connection.json",Filter="JSON connection file|*.json"};
        if(dialog.ShowDialog()!=true)return;
        manifest=dialog.FileName;current=null;previous=null;File.WriteAllText(System.IO.Path.Combine(root,"core-path.txt"),manifest);await Poll();
    }
    private void Reset_Click(object sender,RoutedEventArgs e)
    {
        if(current is null)return;
        baseline=new Baseline{Epoch=current.Epoch,Since=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Totals=current.Totals,Apps=current.Apps.ToDictionary(a=>a.Id,a=>a.Usage.Proxy)};
        var file=System.IO.Path.Combine(root,"baseline.json");var temporary=file+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(baseline));File.Move(temporary,file,true);}catch{Quality.Text="Could not persist the view baseline.";}
        Render();
    }
    private void Mode_Changed(object sender,RoutedEventArgs e){if(IsLoaded)Render();}
    private void Apps_Click(object sender,RoutedEventArgs e){AppGrid.BringIntoView();}
    private void Filter_Changed(object sender,TextChangedEventArgs e){if(IsLoaded)Render();}
    private void Guide_Click(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo("https://github.com/MatchalatteR/RouteMeter/blob/main/docs/CORE-SETUP.md"){UseShellExecute=true});
    private void Quit_Click(object sender,RoutedEventArgs e)=>Quit();
    private void Quit(){quitting=true;timer.Stop();tray.Dispose();client.Dispose();System.Windows.Application.Current.Shutdown();}
    protected override void OnClosing(CancelEventArgs e){if(!quitting){e.Cancel=true;Hide();}base.OnClosing(e);}
}
