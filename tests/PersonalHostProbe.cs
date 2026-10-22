using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlaytimeRclone;
using Newtonsoft.Json;

public sealed class HostProbe : GenericPlugin
{
    public override Guid Id {get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    static readonly Guid GameId=new Guid("f3d3b83a-fc3f-40ec-87c5-24094bdd81d9");
    PlaytimePlugin subject;int stops;ulong elapsed;bool settingsViewCreated;string gameKey=Identity.Hash("manual:demo-adventure");
    DispatcherTimer windowWatcher;bool cancelledLaunch;bool visualStarted;
    DispatcherTimer heartbeat;int backgroundUiHeartbeats;int progressWindowsObserved;string cloudChoiceHash;
    System.Diagnostics.Stopwatch launchWatch;List<long> launchTimes=new List<long>();
    int runningHistoryRestoresRejected;bool interruptedRestoreRecovered;
    public override void OnGameStarted(OnGameStartedEventArgs args){if(args.Game.Id==GameId&&launchWatch!=null){
        launchWatch.Stop();launchTimes.Add(launchWatch.ElapsedMilliseconds);
        try{typeof(PlaytimePlugin).GetMethod("RestoreHistoryCoreAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(subject,new object[]{gameKey,null});Fail(new Exception("History restore accepted a running game."));}
        catch(System.Reflection.TargetInvocationException ex){if(ex.InnerException is InvalidOperationException)runningHistoryRestoresRejected++;else Fail(ex);}
    }}
    string Root {get{return PlayniteApi.Paths.ConfigurationPath;}}
    public HostProbe(IPlayniteAPI api):base(api){Properties=new GenericPluginProperties{HasSettings=false};}
    SortedDictionary<string,string> Data(string value){return new SortedDictionary<string,string>(StringComparer.Ordinal){{"progress.dat",Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}};}
    Revision Rev(string kind,SortedDictionary<string,string> data,params string[] parents){return new Revision{Game=gameKey,Kind=kind,Device=Guid.NewGuid().ToString("N"),Data=data,Parents=parents.ToList()};}
    void Fail(Exception ex){File.WriteAllText(Path.Combine(Root,"host-result.json"),JsonConvert.SerializeObject(new{passed=false,error=ex.ToString()},Formatting.Indented));}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        try
        {
            if(!File.Exists(Path.Combine(Root,"PROBE-ONLY")))throw new Exception("Refused outside isolated profile.");
            subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
            heartbeat=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};heartbeat.Tick+=(_,e)=>{if(subject.IsSyncRunning)backgroundUiHeartbeats++;if(Application.Current.Windows.OfType<Window>().Any(w=>w.IsVisible&&w.GetType().Name.Contains("ProgressWindow")))progressWindowsObserved++;};heartbeat.Start();
            subject.GetSettings(false).BeginEdit();settingsViewCreated=subject.GetSettingsView(false)!=null;subject.GetSettings(false).CancelEdit();
            var game=new Game{Id=GameId,Name="Cloud Trail — Playnite Link Demo",Playtime=100,IsInstalled=true,
                GameActions=new ObservableCollection<GameAction>{new GameAction{Type=GameActionType.File,Path=Path.Combine(Root,"FixtureGame.exe"),IsPlayAction=true,TrackingMode=TrackingMode.OriginalProcess,WorkingDir=Root}}};
            if(PlayniteApi.Database.Games.Get(GameId)==null)PlayniteApi.Database.Games.Add(game);
            var folder=Path.Combine(Root,"DemoSaves");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"progress.dat"),"baseline");
            var a=Rev("save",Data("baseline"));var b=Rev("save",Data("cloud-before-launch"),a.Hash());
            var prefs=new SortedDictionary<string,string>(StringComparer.Ordinal){{"Favorite","False"},{"Hidden","False"},{"Notes",""}};
            var pa=Rev("preferences",prefs);var remotePrefs=new SortedDictionary<string,string>(prefs,StringComparer.Ordinal);remotePrefs["Favorite"]="True";remotePrefs["Notes"]="Preferences from the second device";var pb=Rev("preferences",remotePrefs,pa.Hash());
            var vault=subject.PersonalVault;vault.Add(a);vault.Add(pa);vault.State.Links[gameKey]=new SaveLink{Folder=folder,Save=new Checkpoint{Revision=a.Hash(),Content=a.ContentHash()},Preferences=new Checkpoint{Revision=pa.Hash(),Content=pa.ContentHash()}};vault.Save();
            var cloudObjects=Path.Combine(Root,"Cloud","personal","playnite-link-v1","revisions","objects");Directory.CreateDirectory(cloudObjects);
            foreach(var r in new[]{a,b,pa,pb})File.WriteAllText(Path.Combine(cloudObjects,r.Hash()+".json"),r.Canonical(),new UTF8Encoding(false));
            Task.Run(async()=>{await Task.Delay(5000);PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(()=>{launchWatch=System.Diagnostics.Stopwatch.StartNew();PlayniteApi.StartGame(GameId);}));});
        }catch(Exception ex){Fail(ex);}
    }
    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        if(args.Game.Id!=GameId)return;stops++;elapsed+=args.ElapsedSeconds;
        Task.Run(async()=>{await Task.Delay(5000);PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(()=>{
            try
            {
                if(stops<2){var saveRoot=Path.Combine(Root,"DemoSaves");var transaction=Guid.NewGuid().ToString("N");Atomic.Write(saveRoot+".pl-restore",transaction+"|1");Directory.Move(saveRoot,saveRoot+".pl-backup-"+transaction);launchWatch=System.Diagnostics.Stopwatch.StartNew();PlayniteApi.StartGame(GameId);interruptedRestoreRecovered=Directory.Exists(saveRoot)&&!File.Exists(saveRoot+".pl-restore");return;}
                var extensionRoot=Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,"8ca18482-3ebe-4bf7-b229-a259b2094767");
                var state=JsonConvert.DeserializeObject<State>(File.ReadAllText(Path.Combine(extensionRoot,"state.json")));
                var sessions=state.Entries.Where(e=>e.Kind=="session").ToList();var sessionSeconds=sessions.Aggregate(0UL,(sum,e)=>sum+e.Seconds);
                var game=PlayniteApi.Database.Games.Get(GameId);var launches=File.ReadAllLines(Path.Combine(Root,"demo-launches.txt"));
                var heads=subject.PersonalVault.Heads(gameKey,"save");var content=heads.Count==1?Encoding.UTF8.GetString(subject.SaveBlobs.ReadSmall(heads[0].Data["progress.dat"])):"conflict";
                var cloudObjects=Path.Combine(Root,"Cloud","personal","playnite-link-v1","revisions","objects");
                var allUploaded=Directory.GetFiles(subject.PersonalVault.Objects,"*.json").All(p=>File.Exists(Path.Combine(cloudObjects,Path.GetFileName(p))));
                var backups=Directory.GetDirectories(Root,"DemoSaves.pl-backup-*");
                interruptedRestoreRecovered=interruptedRestoreRecovered||Directory.Exists(Path.Combine(Root,"DemoSaves"))&&!File.Exists(Path.Combine(Root,"DemoSaves.pl-restore"));
                var passed=interruptedRestoreRecovered&&runningHistoryRestoresRejected==2&&stops==2&&sessions.Count==2&&elapsed>0&&sessionSeconds==elapsed&&game.Playtime==100+elapsed&&state.Active.Count==0&&
                    launches.Length==2&&launches[0]=="cloud-before-launch"&&launches[1]=="session-1"&&content=="session-2"&&allUploaded&&backups.Length>=1&&
                    game.Favorite&&game.Notes=="Preferences from the second device"&&settingsViewCreated&&launchTimes.Count==2&&launchTimes.All(ms=>ms<10000);
                File.WriteAllText(Path.Combine(Root,"runtime-result.json"),JsonConvert.SerializeObject(new{passed,mode=PlayniteApi.ApplicationInfo.Mode.ToString(),callbacks=stops,
                    interruptedRestoreRecovered,runningHistoryRestoresRejected,elapsedSeconds=elapsed,ledgerSessionSeconds=sessionSeconds,displayedPlaytime=game.Playtime,expectedPlaytime=100+elapsed,
                    launches,launchMilliseconds=launchTimes,saveHead=content,allRevisionsUploaded=allUploaded,restoreBackups=backups.Length,favorite=game.Favorite,notes=game.Notes,settingsViewCreated,
                    backgroundUiHeartbeats,game="Synthetic Cloud Trail fixture; not a commercial game",appVersion=PlayniteApi.ApplicationInfo.ApplicationVersion.ToString()},Formatting.Indented));
                if(!passed)throw new Exception("Runtime assertions failed; see runtime-result.json.");
                CheckPerformanceThenVisual();
            }catch(Exception ex){Fail(ex);}
        }));});
    }
    bool navigationStable,gameManagerVerified;
    void CheckGameManager(Window account)
    {
        var managerWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        managerWatcher.Tick+=(_,e)=>{
            var manager=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Playnite Link — manage games"&&w.IsVisible);if(manager==null)return;managerWatcher.Stop();manager.UpdateLayout();
            var list=Elements<ListBox>(manager).Single();var game=list.Items.OfType<Game>().Single(g=>g.Id==GameId);list.SelectedItem=game;Capture("manage-games",manager);
            var detailsWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            detailsWatcher.Tick+=(detailsSender,detailsArgs)=>{
                var details=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Playnite Link — Cloud Trail — Playnite Link Demo"&&w.IsVisible);if(details==null)return;detailsWatcher.Stop();Capture("manage-sync",details);
                gameManagerVerified=Buttons(details).Any(b=>Convert.ToString(b.Content)=="View save folder");Buttons(details).Single(b=>Convert.ToString(b.Content)=="Done").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };detailsWatcher.Start();
            Buttons(manager).Single(b=>Convert.ToString(b.Content)=="Open game").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(manager.IsVisible)manager.Close();
        };managerWatcher.Start();
        Buttons(account).Single(b=>Convert.ToString(b.Content)=="Your games").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(!gameManagerVerified||!account.IsVisible)throw new Exception("Game manager did not preserve the account window");
    }
    void Capture(string name,Window window)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(Root,name+".png")))encoder.Save(file);
    }
    System.Collections.Generic.IEnumerable<T> Elements<T>(DependencyObject parent)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var item in Elements<T>(child))yield return item;}}
    IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=VisualTreeHelper.GetChild(parent,i);if(child is Button)yield return (Button)child;foreach(var button in Buttons(child))yield return button;}
    }
    void Watch(string title,string image,string buttonText)
    {
        if(windowWatcher!=null)windowWatcher.Stop();
        windowWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(600)};
        windowWatcher.Tick+=(_,e)=> {
            try
            {
                var window=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title!=null&&w.Title.StartsWith(title,StringComparison.Ordinal)&&w.IsVisible);
                if(window==null)return;
                if(image=="personal-account"){
                    var original=window.Content;
                    Action<string> click=name=>{window.UpdateLayout();var button=Buttons(window).FirstOrDefault(b=>b.Content!=null&&b.Content.ToString()==name);if(button==null)throw new Exception("Missing account button: "+name);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();};
                    click("Devices");var devicesPage=window.Content;
                    click("Protection");var protectionPage=window.Content;var draft=Elements<PasswordBox>(window).First();draft.Password="disposable-ui-draft";
                    click("Overview");if(!Object.ReferenceEquals(original,window.Content)||!window.IsVisible)throw new Exception("Overview was recreated");
                    click("Devices");if(!Object.ReferenceEquals(devicesPage,window.Content))throw new Exception("Devices page was recreated");
                    click("Protection");if(!Object.ReferenceEquals(protectionPage,window.Content)||draft.Password!="disposable-ui-draft")throw new Exception("Protection page or its unsaved draft was lost");draft.Clear();
                    click("Overview");click("Sync now");if(!window.IsVisible||!Object.ReferenceEquals(original,window.Content))throw new Exception("Sync closed the account window");
                    CheckGameManager(window);navigationStable=true;
                }
                Capture(image,window);windowWatcher.Stop();
                if(buttonText==null)window.Close();
                else Buttons(window).First(b=>buttonText.StartsWith("Use cloud version ",StringComparison.Ordinal)?b.Tag!=null&&b.Tag.ToString().StartsWith(buttonText.Substring(18),StringComparison.Ordinal):b.Content!=null&&b.Content.ToString().StartsWith(buttonText,StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }catch(Exception ex){windowWatcher.Stop();Fail(ex);}
        };windowWatcher.Start();
    }
    async void BeginVisualScenario()
    {
        if(visualStarted)return;visualStarted=true;
        Capture("demo-library",PlayniteApi.Dialogs.GetCurrentAppWindow());
        var head=subject.PersonalVault.Heads(gameKey,"save").Single();var remote=Rev("save",Data("cloud-choice-after-conflict"),head.Hash());remote.Version=2;remote.Data=subject.SaveBlobs.ImportLegacy(remote.Data);cloudChoiceHash=remote.Hash();subject.PersonalVault.Add(remote);
        File.WriteAllText(Path.Combine(Root,"DemoSaves","progress.dat"),"local-choice-before-conflict");
        // Launches no longer scan/query the cloud. Background discovery records
        // the conflict first; a known conflict still requires a launch choice.
        await subject.SyncPersonalAsync(false,null);
        Watch("Cloud conflict —","conflict-cancel","Decide later");
        PlayniteApi.StartGame(GameId);
        VerifyCancellationAndResolve();
    }
    async void CheckPerformanceThenVisual()
    {
        try
        {
            var cache=Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,"8ca18482-3ebe-4bf7-b229-a259b2094767","cloud-cache");
            var before=Directory.GetFiles(cache,"*.json",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.GetLastWriteTimeUtc(p));
            var captures=subject.SaveBlobs.CaptureCount;
            var watch=System.Diagnostics.Stopwatch.StartNew();await subject.SyncPersonalAsync(false,null,true);watch.Stop();
            var noIdleSaveScans=subject.SaveBlobs.CaptureCount==captures;
            var schedule=new IdleSyncSchedule();if(schedule.DelaySeconds(120)!=120)throw new Exception("Wrong initial delay");
            schedule.Observe(true);if(schedule.DelaySeconds(120)!=240)throw new Exception("No idle backoff");
            for(int i=0;i<20;i++)schedule.Observe(true);
            if(schedule.DelaySeconds(120)!=900||schedule.DelaySeconds(3600)!=3600)throw new Exception("Wrong backoff cap");
            schedule.Observe(false);if(schedule.DelaySeconds(120)!=120)throw new Exception("Change did not reset delay");
            schedule.Observe(true);schedule.Reset();if(schedule.DelaySeconds(120)!=120)throw new Exception("Event did not reset delay");
            var cacheUnchanged=before.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value);
            var passed=noIdleSaveScans&&cacheUnchanged&&progressWindowsObserved==0&&backgroundUiHeartbeats>10&&watch.ElapsedMilliseconds<5000;
            File.WriteAllText(Path.Combine(Root,"performance-result.json"),JsonConvert.SerializeObject(new{passed,warmSyncMilliseconds=watch.ElapsedMilliseconds,cacheUnchanged,noIdleSaveScans,idleBackoffVerified=true,
                cachedObjects=before.Count,backgroundUiHeartbeats,progressWindowsObserved,backend="Rclone local alias; not a WAN/cloud latency benchmark"},Formatting.Indented));
            if(!passed)throw new Exception("Background performance checks failed.");BeginVisualScenario();
        }catch(Exception ex){Fail(ex);}
    }
    async void VerifyCancellationAndResolve()
    {
            try
            {
                for(int i=0;i<100&&!subject.WasLaunchCheckCancelled(GameId);i++)await Task.Delay(100);
                cancelledLaunch=subject.WasLaunchCheckCancelled(GameId);if(!cancelledLaunch)throw new Exception("Conflict cancellation did not finish within 10 seconds.");
                Watch("Cloud conflict —","conflict-resolve","Use cloud version "+cloudChoiceHash.Substring(0,8));
                await subject.SyncPersonalAsync(true,null);
                var restored=File.ReadAllText(Path.Combine(Root,"DemoSaves","progress.dat"));
                var window=PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions());window.Title="Portable settings visual test";window.Width=760;window.Height=610;
                subject.GetSettings(false).BeginEdit();window.Content=subject.GetSettingsView(false);Watch("Portable settings visual test","settings",null);window.ShowDialog();subject.GetSettings(false).CancelEdit();
                Watch("Playnite Link","personal-account",null);
                subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Personal cloud account & settings").Action(null);
                Watch("Playnite Link","devices",null);
                subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Connected devices").Action(null);
                Watch("Playnite Link","security",null);
                subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Passcode").Action(null);
                await subject.SyncPersonalAsync(false,null);
                var previous=subject.PersonalVault.Heads(gameKey,"save").Single();
                var remoteBranch=Rev("save",Data("peer-progress-for-local-choice"),previous.Hash());remoteBranch.Version=2;remoteBranch.Data=subject.SaveBlobs.ImportLegacy(remoteBranch.Data);subject.PersonalVault.Add(remoteBranch);
                File.WriteAllText(Path.Combine(Root,"DemoSaves","progress.dat"),"local-progress-kept");
                Watch("Cloud conflict —","conflict-local","Keep local saves");
                await subject.SyncPersonalAsync(true,null);
                var localChoiceVerified=File.ReadAllText(Path.Combine(Root,"DemoSaves","progress.dat"))=="local-progress-kept"&&subject.PersonalVault.Heads(gameKey,"save").Count==1&&subject.PersonalVault.Read(subject.PersonalVault.Objects).ContainsKey(remoteBranch.Hash());
                await (Task)typeof(PlaytimePlugin).GetMethod("RestoreHistoryCoreAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(subject,new object[]{gameKey,remoteBranch});
                var stoppedHistoryRestoreVerified=File.ReadAllText(Path.Combine(Root,"DemoSaves","progress.dat"))=="peer-progress-for-local-choice"&&subject.PersonalVault.Heads(gameKey,"save").Single().Hash()!=remoteBranch.Hash();
                var passed=stoppedHistoryRestoreVerified&&localChoiceVerified&&navigationStable&&cancelledLaunch&&stops==2&&File.ReadAllLines(Path.Combine(Root,"demo-launches.txt")).Length==2&&restored=="cloud-choice-after-conflict"&&
                    subject.PersonalVault.Heads(gameKey,"save").Count==1;
                File.WriteAllText(Path.Combine(Root,"visual-result.json"),JsonConvert.SerializeObject(new{passed,stoppedHistoryRestoreVerified,navigationStable,gameManagerVerified,unsavedDraftPreserved=navigationStable,cancelledLaunch,cloudChoiceVerified=restored=="cloud-choice-after-conflict",localChoiceVerified,conflictResolutions=2,remainingSaveHeads=subject.PersonalVault.Heads(gameKey,"save").Count,callbacks=stops,restored,
                    screenshots=new[]{"demo-library.png","manage-games.png","manage-sync.png","conflict-cancel.png","conflict-resolve.png","conflict-local.png","settings.png","personal-account.png","devices.png","security.png"},
                    method="Actual Playnite WPF windows rendered by test-only in-process companion; buttons invoked via WPF test events, not Windows input automation"},Formatting.Indented));
                File.WriteAllText(Path.Combine(Root,"host-result.json"),JsonConvert.SerializeObject(new{passed,runtime=JsonConvert.DeserializeObject(File.ReadAllText(Path.Combine(Root,"runtime-result.json"))),
                    visual=JsonConvert.DeserializeObject(File.ReadAllText(Path.Combine(Root,"visual-result.json"))),performance=JsonConvert.DeserializeObject(File.ReadAllText(Path.Combine(Root,"performance-result.json")))},Formatting.Indented));
            }catch(Exception ex){Fail(ex);}
    }
    public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
    {
        return new[]{new MainMenuItem{MenuSection="@Link test fixtures",Description="Create demo conflict for visual review",Action=_=>{
            var head=subject.PersonalVault.Heads(gameKey,"save").Single();var cloud=Rev("save",Data("cloud-branch-for-review"),head.Hash());subject.PersonalVault.Add(cloud);
            File.WriteAllText(Path.Combine(Root,"DemoSaves","progress.dat"),"local-branch-for-review");subject.SyncPersonalAsync(true,PlayniteApi.Database.Games.Get(GameId));
        }}};
    }
}
