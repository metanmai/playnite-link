using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlaytimeRclone;

public sealed class LiveHostProbe : GenericPlugin
{
    public override Guid Id{get{return new Guid("f8bc8da6-03f2-485c-acd2-b62817acff5e");}}
    PlaytimePlugin subject;JObject ticket;Guid gameId;int stops;ulong elapsed;string root;string statePath;bool started;DispatcherTimer watcher;
    public LiveHostProbe(IPlayniteAPI api):base(api){Properties=new GenericPluginProperties{HasSettings=false};}
    void Report(object result){File.WriteAllText(Path.Combine(root,"live-host-result.json"),JsonConvert.SerializeObject(result,Formatting.Indented));}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        root=Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,"8ca18482-3ebe-4bf7-b229-a259b2094767");statePath=Path.Combine(root,"live-probe-ticket.json");
        if(!File.Exists(statePath)||!File.Exists(Path.Combine(root,"live-probe-seed-ready")))return;
        try
        {
            ticket=JObject.Parse(File.ReadAllText(statePath));gameId=new Guid((string)ticket["GameId"]);
            subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
            if(PlayniteApi.Database.Games.Get(gameId)==null)PlayniteApi.Database.Games.Add(new Game{Id=gameId,Name="Playnite Link Cloud Demo",Playtime=100,IsInstalled=true,
                GameActions=new ObservableCollection<GameAction>{new GameAction{Type=GameActionType.File,Path=Path.Combine((string)ticket["Work"],"FixtureGame.exe"),WorkingDir=(string)ticket["Work"],IsPlayAction=true,TrackingMode=TrackingMode.OriginalProcess}}});
            Begin();
        }catch(Exception ex){Report(new{passed=false,error=ex.Message});}
    }
    async void Begin()
    {
        try
        {
            await Task.Delay(3000);bool ready=false;
            for(int attempt=0;attempt<8&&!ready;attempt++)
            {
                try{await subject.SyncPersonalAsync(false,null);ready=true;}
                catch(IOException ex){Report(new{passed=false,pending=true,stage="provider retry",attempt=attempt+1,error=ex.Message});}
                if(!ready)await Task.Delay(30000);
            }
            if(!ready)throw new IOException("Google Drive did not permit a complete initial sync after retries.");started=true;PlayniteApi.StartGame(gameId);
        }
        catch(Exception ex){Report(new{passed=false,stage="initial cloud sync",error=ex.Message});}
    }
    public override void OnGameStopped(OnGameStoppedEventArgs args)
    {
        if(!started||args.Game.Id!=gameId)return;stops++;elapsed+=args.ElapsedSeconds;Continue();
    }
    async void Continue()
    {
        try
        {
            await subject.SyncPersonalAsync(false,null);
            if(stops<2){await Task.Delay(400);PlayniteApi.StartGame(gameId);return;}
            var work=(string)ticket["Work"];var game=PlayniteApi.Database.Games.Get(gameId);var state=JsonConvert.DeserializeObject<State>(File.ReadAllText(Path.Combine(root,"state.json")));
            var key=(string)ticket["Key"];var records=state.Entries.Where(e=>e.Game==key&&e.Kind=="session").ToList();
            var data=subject.PersonalVault.Heads(key,"save").Single();var progress=Encoding.UTF8.GetString(subject.SaveBlobs.ReadSmall(data.Data["progress.dat"]));
            var launches=File.ReadAllLines(Path.Combine(work,"demo-launches.txt"));
            var checkRoot=Path.Combine(root,"live-cloud-verification");Directory.CreateDirectory(checkRoot);var metadata=new RcloneTransport((string)ticket["Exe"],(string)ticket["Config"],(string)ticket["Remote"],"playnite-link-v1/revisions/objects",limitSeconds:600);
            await Task.Run(()=>metadata.Pull(checkRoot));var published=subject.PersonalVault.Read(checkRoot).ContainsKey(data.Hash());
            var settings=JsonConvert.DeserializeObject<DevicePreferences>(File.ReadAllText(Path.Combine(root,"device-settings.json")));
            var devices=subject.ConnectedDevices.Views();var passed=stops==2&&records.Count==2&&records.Aggregate(0UL,(s,e)=>s+e.Seconds)==elapsed&&game.Playtime==100+elapsed&&
                launches.Length==2&&launches[0]=="cloud-before-launch"&&launches[1]=="session-1"&&progress=="session-2"&&published&&game.Favorite&&game.Notes=="From peer using real Google Drive"&&settings.IntervalSeconds==600&&devices.Count>=2;
            if(!passed)throw new Exception("Installed-host runtime assertions failed.");
            subject.ConnectedDevices.SetState((string)ticket["Me"],(string)ticket["Peer"],"removed");
            subject.ConnectedDevices.SetSettings((string)ticket["Me"],(string)ticket["Me"],new DevicePreferences{Name=Environment.MachineName,IntervalSeconds=120});await subject.SyncPersonalAsync(false,null);
            watcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(650)};watcher.Tick+=(_,e)=>{
                var window=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.IsVisible&&w.Title=="Playnite Link — connected devices");if(window==null)return;
                window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"installed-devices.png")))encoder.Save(file);watcher.Stop();window.Close();
            };watcher.Start();subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Connected devices").Action(null);
            Report(new{passed=true,installedPath=typeof(PlaytimePlugin).Assembly.Location,actualPlayniteProfile=PlayniteApi.Paths.ConfigurationPath,provider="Existing Rclone Google Drive",game="Synthetic Playnite Link Cloud Demo",callbacks=stops,elapsedSeconds=elapsed,displayedPlaytime=game.Playtime,expectedPlaytime=100+elapsed,
                launches,restoredBeforeLaunch=true,afterPlayRevisionDownloadedFromDrive=published,saveProgress=progress,remoteDeviceConfigurationApplied=true,connectedDevicesAtTest=devices.Count,testPeerRemoved=true,screenshot="installed-devices.png",passcodeConfigured=false});
            File.Move(statePath,statePath+".completed");
        }catch(Exception ex){Report(new{passed=false,stage="installed host validation",error=ex.Message,callbacks=stops});}
    }
}
