using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlaytimeRclone;

public sealed class HostProbe:GenericPlugin
{
    public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    string Root{get{return PlayniteApi.Paths.ConfigurationPath;}}
    PlaytimePlugin subject;Plugin ga,hltb;PluginActivityStore store;Game game;JObject scenario;int starts,stops;ulong elapsed;bool onboardingClicked;TaskCompletionSource<bool> stopped;System.Diagnostics.Stopwatch launchWatch;long launchMilliseconds;bool launchedDuringSync;
    public HostProbe(IPlayniteAPI api):base(api){}
    static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    object Database(Plugin plugin){return PluginActivityStore.Member(plugin,"PluginDatabase");}
    Repository Repository{get{return (Repository)typeof(PlaytimePlugin).GetField("repository",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);}}
    static IEnumerable<T> Controls<T>(DependencyObject root)where T:DependencyObject{if(root is T)yield return (T)root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Controls<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    void Capture(string name,FrameworkElement view)
    {view.UpdateLayout();var bitmap=new RenderTargetBitmap(Math.Max(1,(int)view.ActualWidth),Math.Max(1,(int)view.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(view);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(Root,name+".png")))encoder.Save(file);}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{try{await Run();}catch(Exception ex){Finish(false,ex.ToString());}}));}
    public override void OnGameStarted(OnGameStartedEventArgs args){if(game!=null&&args.Game.Id==game.Id){starts++;if(launchWatch!=null){launchWatch.Stop();launchMilliseconds=launchWatch.ElapsedMilliseconds;}}}
    public override void OnGameStopped(OnGameStoppedEventArgs args){if(game!=null&&args.Game.Id==game.Id){stops++;elapsed+=args.ElapsedSeconds;if(stopped!=null)stopped.TrySetResult(true);}}
    async Task Wait(Func<bool> ready,string message,int seconds=90)
    {var until=DateTime.UtcNow.AddSeconds(seconds);while(!ready()){if(DateTime.UtcNow>until)throw new TimeoutException(message);await Task.Delay(200);}}
    async Task Run()
    {
        Assert(File.Exists(Path.Combine(Root,"PROBE-ONLY")),"Refused outside fixture profile");
        scenario=JObject.Parse(File.ReadAllText(Path.Combine(Root,"activity-scenario.json")));
        subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
        ga=PlayniteApi.Addons.Plugins.Single(p=>p.GetType().FullName=="GameActivity.GameActivity");
        hltb=PlayniteApi.Addons.Plugins.Single(p=>p.GetType().FullName=="HowLongToBeat.HowLongToBeat");
        var onlinePath=Path.Combine(Root,"online-fixture.private.json");var online=File.Exists(onlinePath)?JObject.Parse(File.ReadAllText(onlinePath)):null;
        var baseline=online==null?60UL:(ulong)online["BaselineSeconds"];Assert(baseline>=20,"Fixture baseline too small");
        var id=new Guid((string)scenario["GameId"]);game=PlayniteApi.Database.Games.Get(id);
        if(game==null)
        {
            game=new Game{Id=id,Name="Session Bridge — shared demo",PluginId=new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab"),GameId="session-bridge-demo",Playtime=baseline,PlayCount=2,IsInstalled=true,
                GameActions=new ObservableCollection<GameAction>{new GameAction{Type=GameActionType.File,Path=Path.Combine(Root,"FixtureGame.exe"),WorkingDir=Root,IsPlayAction=true,TrackingMode=TrackingMode.OriginalProcess}}};
            PlayniteApi.Database.Games.Add(game);
        }
        await Wait(()=>Database(ga)!=null&&Equals(PluginActivityStore.Member(Database(ga),"IsLoaded"),true)&&Database(hltb)!=null&&Equals(PluginActivityStore.Member(Database(hltb),"IsLoaded"),true),"Activity extensions did not finish loading");
        if(online!=null)
        {
            var recordType=hltb.GetType().Assembly.GetType("HowLongToBeat.Models.GameHowLongToBeat",true);
            var model=JObject.FromObject(new{Id=id.ToString(),Name=game.Name,UserGameId=(string)online["Submission"],Items=new[]{new{Id=(string)online["HltbId"],Name=(string)online["Name"]}}});
            PluginActivityStore.Call(Database(hltb),"Update",JsonConvert.DeserializeObject(model.ToString(),recordType));
        }
        store=new PluginActivityStore(ga,g=>PlayniteApi.Database.Games.Get(g));
        var action=(string)scenario["Action"];
        if(action!="Verify"&&store.Read(id).Count==0)
        {
            store.Add(id,new ActivitySession{Utc=new DateTime(2026,1,1,12,0,0,DateTimeKind.Utc),Seconds=20});
            store.Add(id,new ActivitySession{Utc=new DateTime(2026,1,2,12,0,0,DateTimeKind.Utc),Seconds=baseline-20});
        }
        if(action!="Verify")
        {
            var watcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            watcher.Tick+=(_,e)=>{var window=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Connect your storage"&&w.IsVisible);if(window==null)return;var button=Controls<Button>(window).FirstOrDefault(b=>Convert.ToString(b.Content)=="Connect"&&b.IsEnabled);if(button==null)return;watcher.Stop();try{var picker=Controls<ComboBox>(window).Single(c=>!c.IsEditable&&c.Items.Contains("fixture:"));picker.SelectedItem="fixture:";Assert(Convert.ToString(picker.SelectedItem)=="fixture:","Fixture cloud connection was not selected");Capture("activity-onboarding",window);onboardingClicked=true;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}catch(Exception ex){window.Close();Finish(false,ex.ToString());}};watcher.Start();
            typeof(PlaytimePlugin).GetMethod("ConnectStorage",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(subject,null);
            await Wait(()=>onboardingClicked,"Connection wizard did not complete");
        }
        await subject.SyncPersonalAsync(true,null);
        Assert(Repository.Snapshot().Mappings.ContainsKey(id.ToString()),"Connected library game was not linked automatically");
        var before=game.Playtime;
        if(action!="Verify")
        {
            var inFlight=subject.SyncPersonalAsync(false,null,true);await Task.Delay(100);
            launchedDuringSync=subject.IsSyncRunning;Assert(launchedDuringSync,"Timing test did not overlap a background synchronization");
            stopped=new TaskCompletionSource<bool>();launchWatch=System.Diagnostics.Stopwatch.StartNew();PlayniteApi.StartGame(id);
            var finished=await Task.WhenAny(stopped.Task,Task.Delay(TimeSpan.FromSeconds(90)));Assert(finished==stopped.Task,"Actual fixture game never completed");
            await Task.Delay(3000);await Wait(()=>!store.Busy(id),"GameActivity writer remained active after game stopped");
            Assert(starts==1&&stops==1&&elapsed>0,"Unexpected game event count");
            Assert(launchMilliseconds<10000,"Game launch waited more than ten seconds behind background sync");await inFlight;
        }
        await subject.SyncPersonalAsync(true,null);
        var expected=action=="Verify"?(ulong)scenario["ExpectedTotal"]:before+elapsed;
        var expectedCount=action=="Verify"?4:action=="A"?3:4;
        for(int i=0;i<5;i++)await subject.SyncPersonalAsync(true,null);
        var metadataWatch=System.Diagnostics.Stopwatch.StartNew();await subject.SyncPersonalAsync(false,null,true);metadataWatch.Stop();
        Assert(game.Playtime==expected,"Playnite total duplicated or lost elapsed time");
        Assert(game.PlayCount==(ulong)expectedCount,"Playnite session count diverged");
        var sessions=store.Read(id);Assert(sessions.Count==expectedCount,"GameActivity duplicated or lost a session");
        Assert((ulong)sessions.Sum(s=>(long)s.Seconds)==expected,"GameActivity session seconds disagree with total playtime");
        var gaRecord=PluginActivityStore.Call(Database(ga),"Get",id,true,false);
        Assert(Convert.ToUInt64(PluginActivityStore.Member(gaRecord,"Playtime"))==expected,"GameActivity display total diverged");
        var hltbRecord=PluginActivityStore.Call(Database(hltb),"Get",id,true,false);
        if(hltbRecord==null)
        {
            var type=hltb.GetType().Assembly.GetType("HowLongToBeat.Models.GameHowLongToBeat",true);hltbRecord=Activator.CreateInstance(type);type.GetProperty("Id").SetValue(hltbRecord,id,null);PluginActivityStore.Call(Database(hltb),"Update",hltbRecord);
        }
        Assert(Convert.ToUInt64(PluginActivityStore.Member(hltbRecord,"Playtime"))==expected,"HowLongToBeat display total diverged");
        Assert(Convert.ToUInt64(PluginActivityStore.Member(hltbRecord,"PlayCount"))==(ulong)expectedCount,"HowLongToBeat display session count diverged");
        Assert(starts==(action=="Verify"?0:1)&&stops==(action=="Verify"?0:1),"Sync replayed native gameplay events");
        if(PlayniteApi.ApplicationInfo.Mode.ToString()=="Desktop")
        {
            var gaView=Activator.CreateInstance(ga.GetType().Assembly.GetType("GameActivity.Views.GameActivityViewSingle",true),new object[]{ga,game}) as FrameworkElement;
            var review=new Window{Title="GameActivity integration review",Width=1000,Height=700,Content=gaView};review.Show();await Task.Delay(1200);Capture("activity-gameactivity",review);review.Close();
        }
        var historyWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        historyWatcher.Tick+=(_,e)=>{var window=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Session history — "+game.Name&&w.IsVisible);if(window==null)return;historyWatcher.Stop();try{window.UpdateLayout();Assert(Controls<ListBox>(window).Single().Items.Count==expectedCount,"Session history UI duplicated a session");Capture("activity-session-history",window);Controls<Button>(window).Single(b=>Convert.ToString(b.Content)=="Close").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}catch(Exception ex){window.Close();Finish(false,ex.ToString());}};historyWatcher.Start();
        typeof(PlaytimePlugin).GetMethod("ShowSessionHistory",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(subject,new object[]{game});
        if(online!=null)
        {
            var adapter=Activator.CreateInstance(typeof(PlaytimePlugin).GetNestedType("OnlineProgress",BindingFlags.NonPublic),true);var type=adapter.GetType();type.GetField("Api").SetValue(adapter,PluginActivityStore.Member(Database(hltb),"HowLongToBeatApi"));type.GetField("Game").SetValue(adapter,game);type.GetField("Submission").SetValue(adapter,(string)online["Submission"]);
            var progress=(IHltbProgress)adapter;var edit=await progress.Read();Assert(edit!=null&&progress.Seconds(edit)==game.Playtime,"Online HLTB progress did not equal the merged total");
        }
        File.WriteAllText(Path.Combine(Root,"activity-result.json"),JsonConvert.SerializeObject(new{passed=true,peer=(string)scenario["Peer"],action,mode=PlayniteApi.ApplicationInfo.Mode.ToString(),playtime=game.Playtime,playCount=game.PlayCount,gameActivitySessions=sessions.Count,gameActivitySeconds=sessions.Sum(s=>(long)s.Seconds),howLongToBeatPlaytime=PluginActivityStore.Member(hltbRecord,"Playtime"),howLongToBeatPlayCount=PluginActivityStore.Member(hltbRecord,"PlayCount"),actualGameStarts=starts,actualGameStops=stops,elapsedSeconds=elapsed,launchMilliseconds,metadataMilliseconds=metadataWatch.ElapsedMilliseconds,launchTargetMilliseconds=10000,launchedDuringBackgroundSync=launchedDuringSync,repeatedSyncs=5,onboardingClicked,automaticProviderLink=true,gameActivityEnabled=true,howLongToBeatEnabled=true,onlineHltbWritesTested=online!=null},Formatting.Indented));
        Application.Current.Shutdown();
    }
    void Finish(bool passed,string error){File.WriteAllText(Path.Combine(Root,"activity-result.json"),JsonConvert.SerializeObject(new{passed,error},Formatting.Indented));Application.Current.Shutdown();}
}

