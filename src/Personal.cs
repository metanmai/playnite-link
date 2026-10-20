using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private Vault vault;
        private BlobStore blobs;
        private DeviceDirectory devices;
        private DevicePreferences devicePreferences;
        private volatile bool transferCancelled;
        private DeviceView[] deviceViewCache=new DeviceView[0];
        public BlobStore SaveBlobs {get{return blobs;}}
        public DeviceDirectory ConnectedDevices {get{return devices;}}
        public Vault PersonalVault { get { return vault; } } // Also used by the isolated host probe.
        public bool IsSyncRunning { get { return syncing!=0; } }
        private string LastBackup;
        private void Network(string text,Action action)
        {
            var result=PlayniteApi.Dialogs.ActivateGlobalProgress(_=>action(),new GlobalProgressOptions(text) { IsIndeterminate=true,Cancelable=false });
            if(result.Error!=null)throw new IOException(text+": "+result.Error.Message,result.Error);
            if(result.Canceled)throw new OperationCanceledException();
        }
        private readonly System.Threading.SemaphoreSlim syncGate = new System.Threading.SemaphoreSlim(1,1);
        private readonly HashSet<Guid> pendingLaunches = new HashSet<Guid>();
        private readonly HashSet<Guid> permittedLaunches = new HashSet<Guid>();
        private readonly HashSet<Guid> cancelledChecks = new HashSet<Guid>();
        private readonly HashSet<Guid> launchReservations = new HashSet<Guid>();
        private readonly HashSet<Guid> restoringSaves = new HashSet<Guid>();
        private HashSet<string> savedConflicts=new HashSet<string>();
        private HashSet<string> ReadSaveConflicts(){return new HashSet<string>(vault.State.Links.Where(p=>!String.IsNullOrWhiteSpace(p.Value.Folder)&&vault.Heads(p.Key,"save").Count>1).Select(p=>p.Key));}
        private bool GameBusy(Guid id){var game=PlayniteApi.Database.Games.Get(id);return launchReservations.Contains(id)||game==null||game.IsRunning||game.IsLaunching;}
        private bool CachedSaveConflict(Game game,Mapping map)
        {
            // Never queue behind network work, or read the mutable vault concurrently.
            if(!devicePreferences.SyncSaves)return false;
            if(savedConflicts.Contains(map.Key))return true;
            if(!syncGate.Wait(0))return false;
            try{SaveLink link;return vault.State.Links.TryGetValue(map.Key,out link)&&!String.IsNullOrWhiteSpace(link.Folder)&&vault.Heads(map.Key,"save").Count>1;}
            finally{syncGate.Release();}
        }
        private async void ReleaseLaunchReservation(Guid id)
        {await System.Threading.Tasks.Task.Delay(10000);if(!closing)UI(()=>launchReservations.Remove(id));}
        private async void LaunchAfterRestore(Game game)
        {
            if(!pendingLaunches.Add(game.Id))return;
            try{var watch=Stopwatch.StartNew();while(!closing&&UI(()=>restoringSaves.Contains(game.Id))&&watch.ElapsedMilliseconds<10000)await System.Threading.Tasks.Task.Delay(50);
                if(!closing)UI(()=>{if(!restoringSaves.Contains(game.Id))PlayniteApi.StartGame(game.Id);else NotifyError(new IOException("A save replacement is still finishing. Retry launch when it completes; cloud transfers do not block launch."));});}
            finally{UI(()=>pendingLaunches.Remove(game.Id));}
        }
        public bool IsLaunchPreflight(Guid id) { return pendingLaunches.Contains(id); }
        public bool WasLaunchCheckCancelled(Guid id) { return cancelledChecks.Contains(id); }
        private T UI<T>(Func<T> action) { return PlayniteApi.MainView.UIDispatcher.CheckAccess() ? action() : (T)PlayniteApi.MainView.UIDispatcher.Invoke(action); }
        private void UI(Action action) { if(PlayniteApi.MainView.UIDispatcher.CheckAccess())action();else PlayniteApi.MainView.UIDispatcher.Invoke(action); }
        private sealed class SyncGame { public Game Game; public string Key; public SortedDictionary<string,string> Preferences; }
        private List<SyncGame> GameSnapshot()
        {
            var result=new List<SyncGame>();
            foreach(var pair in repository.Snapshot().Mappings)
            {
                var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));if(game==null||GameBusy(game.Id))continue;
                CheckIdentity(game,pair.Value);result.Add(new SyncGame {Game=game,Key=pair.Value.Key,Preferences=Preferences(game)});
            }
            return result;
        }
        private void QueuePersonalSync()
        {
            if(closing||repository==null||config==null||String.IsNullOrWhiteSpace(config.RemoteFolder) )return;
            if(syncing!=0){Interlocked.Exchange(ref queuedSync,1);return;}
            RequestSync(false);
        }
        private async void CheckIdleSync()
        {
            if(closing)return;
            if(syncing!=0||repository==null||config==null||String.IsNullOrWhiteSpace(config.RemoteFolder)){ScheduleIdle(false);return;}
            try{await SyncPersonalAsync(false,null,true);}
            catch(OperationCanceledException){}
            catch(Exception ex){if(!closing){logger.Error(ex,"Background cloud check failed; retrying with backoff.");}}
        }
        private void ScheduleIdle(bool reset)
        {
            if(reset)idleSchedule.Reset();
            if(!closing&&timer!=null)try{timer.Change(TimeSpan.FromSeconds(idleSchedule.DelaySeconds(devicePreferences.IntervalSeconds)),Timeout.InfiniteTimeSpan);}catch(ObjectDisposedException){}
        }
        private async void RequestSync(bool interactive)
        {
            try { await SyncPersonalAsync(interactive,null); }
            catch(OperationCanceledException){status="Transfer stopped. Local changes remain queued.";}
            catch(Exception ex){if(!closing)UI(()=>NotifyError(ex));}
        }
        private async void LaunchAfterSync(Game game)
        {
            if(!pendingLaunches.Add(game.Id))return;
            cancelledChecks.Remove(game.Id);
            PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.Checking."+game.Id,"Reviewing known save changes for "+game.Name+"…",NotificationType.Info));
            bool start=false;
            try { start=await SyncPersonalAsync(true,game); }
            catch(OperationCanceledException){start=false;}
            catch(Exception ex)
            {
                if(!closing)start=UI(()=>PlayniteApi.Dialogs.ShowMessage("Cloud sync could not finish: "+ex.Message+"\n\nPlay offline with local saves? Another device may have newer progress. Conflicts will need review after reconnection.","Playnite Link",MessageBoxButton.YesNo)==MessageBoxResult.Yes);
            }
            finally
            {
                UI(()=>{pendingLaunches.Remove(game.Id);PlayniteApi.Notifications.Remove("PlayniteLink.Checking."+game.Id);});
            }
            if(closing)return;
            if(!start){UI(()=>cancelledChecks.Add(game.Id));return;}
            await System.Threading.Tasks.Task.Delay(150);
            UI(()=>{permittedLaunches.Add(game.Id);PlayniteApi.StartGame(game.Id);});
        }
        private SaveLink Link(string game)
        {
            SaveLink link;if(!vault.State.Links.TryGetValue(game,out link)) { link=new SaveLink();vault.State.Links.Add(game,link);vault.Save(); }return link;
        }
        private SortedDictionary<string,string> Preferences(Game game)
        { return new SortedDictionary<string,string>(StringComparer.Ordinal) { {"Favorite",game.Favorite.ToString()}, {"Hidden",game.Hidden.ToString()}, {"Notes",game.Notes??""} }; }
        private void RecordLocal(string game,string kind,SortedDictionary<string,string> data,Checkpoint checkpoint)
        {
            var hash=Revision.ContentHashOf(data);
            if(hash==checkpoint.Content)return;
            // First contact must compare with the remote before publishing a new root.
            if(checkpoint.Revision==null)return;
            // An empty folder may mean an unavailable disk or a wrong path; require review.
            if(kind=="save" && data.Count==0)return;
            var revision=new Revision { Version=kind=="save"?2:1,Game=game,Kind=kind,Device=repository.Snapshot().Device,Data=data,Parents=new List<string>{checkpoint.Revision} };
            vault.Add(revision);checkpoint.Revision=revision.Hash();checkpoint.Content=hash;vault.Save();
        }
        private void CaptureLocalChanges(List<SyncGame> games)
        {
            foreach(var item in games)
            {
                if(UI(()=>GameBusy(item.Game.Id)))continue;
                SaveLink link;if(!vault.State.Links.TryGetValue(item.Key,out link))continue;
                if(devicePreferences.SyncPreferences)RecordLocal(item.Key,"preferences",item.Preferences,link.Preferences);
                if(devicePreferences.SyncSaves&&!String.IsNullOrWhiteSpace(link.Folder)){var captured=blobs.Capture(link.Folder);if(!UI(()=>GameBusy(item.Game.Id)))RecordLocal(item.Key,"save",captured,link.Save);}
            }
        }
        public System.Threading.Tasks.Task<bool> SyncPersonalAsync(bool interactive,Game launching,bool metadataOnly=false)
        {
            return System.Threading.Tasks.Task.Run(async ()=> {
                await syncGate.WaitAsync();transferCancelled=false;Interlocked.Exchange(ref syncing,1);
                var watch=System.Diagnostics.Stopwatch.StartNew();bool idleUnchanged=false;bool completed=false;
                try
                {
                    if(closing)return false;
                    if(repository==null)throw new InvalidOperationException(status);
                    UI(()=>EnsureAutomaticLinks());
                    var games=UI(()=>GameSnapshot());
                    if(devicePreferences.SyncPlaytime)UI(()=>CaptureActivityHistory());
                    var me=repository.Snapshot().Device;
                    vault.UpgradeLegacySaves(blobs,me);
                    if(!metadataOnly)CaptureLocalChanges(games); // Durable local snapshots also when the network is unavailable.
                    if(String.IsNullOrWhiteSpace(config.RemoteFolder))
                    {
                        status="Personal cloud is not connected. Sessions are stored locally.";
                        return launching==null||UI(()=>PlayniteApi.Dialogs.ShowMessage(status+"\nPlay offline?","Playnite Link",MessageBoxButton.YesNo)==MessageBoxResult.Yes);
                    }
                    var captured=config;captured.Validate();vault.Pin(captured.RemoteFolder);
                    var deviceTransport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/devices/objects",cancelled:()=>closing||transferCancelled);
                    var metadataCache=Path.Combine(Root,"cloud-cache","metadata");
                    new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1",cancelled:()=>closing||transferCancelled).PullMetadata(metadataCache);
                    var deviceCache=Path.Combine(metadataCache,"devices","objects");Directory.CreateDirectory(deviceCache);
                    devices.Import(deviceCache);
                    var requested=devices.Heads(me,"settings");
                    if(requested.Count==1)
                    {
                        devicePreferences=requested[0].Settings.Clone();Atomic.Write(Path.Combine(Root,"device-settings.json"),JsonConvert.SerializeObject(devicePreferences,Formatting.Indented));

                    }
                    var deviceState=devices.EffectiveState(me);
                    if(deviceState!="removed")devices.PublishProfile(me,devicePreferences,UI(()=>DeviceGames()));
                    deviceViewCache=devices.Views().ToArray();
                    deviceTransport.PushFiles(devices.Objects,Directory.GetFiles(devices.Objects,"*.json").Select(Path.GetFileName).Where(n=>!File.Exists(Path.Combine(deviceCache,n))));
                    if(deviceState!="active"||requested.Count>1||(!interactive&&!devicePreferences.AutoSync))
                    {status="Device "+deviceState+(requested.Count>1?"; settings need review":"")+". Local saves remain available.";return true;}
                    var blobTransport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,()=>closing||transferCancelled);
                    blobs.Fetch=names=>blobTransport.PullFiles(blobs.DirectoryPath,names);
                    vault.UpgradeLegacySaves(blobs,me);
                    if(!metadataOnly)CaptureLocalChanges(games);
                    var transport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/revisions/objects",cancelled:()=>closing||transferCancelled);
                    var incoming=Path.Combine(metadataCache,"revisions","objects");Directory.CreateDirectory(incoming);
                    // Persistent caches let Rclone skip objects already downloaded. No history is deleted.
                    if(devicePreferences.SyncPlaytime)new SyncEngine().Run(repository,new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,cancelled:()=>closing||transferCancelled),Path.Combine(Root,"scratch"),Path.Combine(metadataCache,"ledger","objects"),false,false);
                    if(devicePreferences.SyncPlaytime)new SyncEngine().Run(repository,new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/activity/objects",cancelled:()=>closing||transferCancelled),Path.Combine(Root,"scratch"),Path.Combine(metadataCache,"activity","objects"),true,false);
                    vault.Import(incoming);var review=ReadSaveConflicts();UI(()=>savedConflicts=review);
                    if(metadataOnly)
                    {
                        var pendingLocal=vault.Read(vault.Objects).Any(p=>((p.Value.Kind=="save"&&devicePreferences.SyncSaves)||(p.Value.Kind=="preferences"&&devicePreferences.SyncPreferences))&&!File.Exists(Path.Combine(incoming,p.Key+".json")));
                        var preferencesChanged=devicePreferences.SyncPreferences&&games.Any(item=>{
                            SaveLink link;return vault.State.Links.TryGetValue(item.Key,out link)&&Revision.ContentHashOf(item.Preferences)!=link.Preferences.Content;
                        });
                        var unappliedRemote=games.Any(item=>{
                            SaveLink link;if(!vault.State.Links.TryGetValue(item.Key,out link))return true;
                            return (devicePreferences.SyncSaves&&!String.IsNullOrWhiteSpace(link.Folder)&&vault.Heads(item.Key,"save").Any(r=>r.Hash()!=link.Save.Revision))||
                                (devicePreferences.SyncPreferences&&vault.Heads(item.Key,"preferences").Any(r=>r.Hash()!=link.Preferences.Revision));
                        });
                        if(!pendingLocal&&!preferencesChanged&&!unappliedRemote)
                        {
                            UI(()=>Project());await SyncOnlineHltb();TrySyncSetup();idleUnchanged=true;completed=true;return true;
                        }
                        CaptureLocalChanges(games);
                    }
                    vault.UpgradeLegacySaves(blobs,me);
                    var cancelled=false;
                    games=UI(()=>GameSnapshot());
                    foreach(var item in games)
                    {
                        if(closing)return false;
                        if(UI(()=>GameBusy(item.Game.Id)))continue;
                        var game=item.Game;var link=Link(item.Key);
                        Func<Conflict,string> choose=c=>interactive?UI(()=>ChooseConflict(game,c)):"cancel";
                        if(devicePreferences.SyncSaves&&!String.IsNullOrWhiteSpace(link.Folder))
                        {
                            var local=blobs.Capture(link.Folder);var hash=Revision.ContentHashOf(local);
                            var reconciled=false;
                            try
                            {
                                var outcome=vault.Reconcile(item.Key,"save",repository.Snapshot().Device,local,link.Save,choose,data=> {
                                    LastBackup=blobs.Restore(link.Folder,data,hash,()=>UI(()=>{if(GameBusy(game.Id))throw new IOException("The game is starting or running; restore deferred.");restoringSaves.Add(game.Id);}));
                                });
                                reconciled=!outcome.Cancelled;
                                if(outcome.Cancelled){cancelled=true;if(launching!=null&&game.Id==launching.Id)return false;}
                            }
                            finally{UI(()=>{if(reconciled)savedConflicts.Remove(item.Key);else savedConflicts.Add(item.Key);restoringSaves.Remove(game.Id);});}
                        }
                        if(devicePreferences.SyncPreferences){var prefs=vault.Reconcile(item.Key,"preferences",repository.Snapshot().Device,item.Preferences,link.Preferences,choose,data=>UI(()=> {
                            var current=PlayniteApi.Database.Games.Get(game.Id);
                            if(Revision.ContentHashOf(Preferences(current))!=Revision.ContentHashOf(item.Preferences))throw new IOException("Preferences changed during sync; retrying preserves your edit.");
                            current.Favorite=Boolean.Parse(data["Favorite"]);current.Hidden=Boolean.Parse(data["Hidden"]);current.Notes=data["Notes"];PlayniteApi.Database.Games.Update(current);
                        }));
                        if(prefs.Cancelled)cancelled=true;}
                    }
                    var pending=vault.Read(vault.Objects).Where(p=>!File.Exists(Path.Combine(incoming,p.Key+".json"))&&
                        ((p.Value.Kind=="save"&&devicePreferences.SyncSaves)||(p.Value.Kind=="preferences"&&devicePreferences.SyncPreferences))).ToList();
                    // A downloaded manifest is the commit receipt for its blobs. Send
                    // only blobs needed by new manifests, before publishing those manifests.
                    if(devicePreferences.SyncSaves&&pending.Any(p=>p.Value.Kind=="save"))
                    {
                        var committed=new HashSet<string>(vault.Read(incoming).Values.Where(r=>r.Kind=="save").SelectMany(r=>r.Data.Values).Where(v=>v.StartsWith("blob:",StringComparison.Ordinal)));
                        var needed=pending.Where(p=>p.Value.Kind=="save").SelectMany(p=>p.Value.Data.Values).Where(v=>v.StartsWith("blob:",StringComparison.Ordinal)&&!committed.Contains(v)).Select(v=>BlobReference.Parse(v).Hash+".blob");
                        blobTransport.PushFiles(blobs.DirectoryPath,needed);
                    }
                    transport.PushFiles(vault.Objects,pending.Select(p=>p.Key+".json"));
                    UI(()=> {
                        savedConflicts.UnionWith(ReadSaveConflicts());Project();status=cancelled?"Versions need review. Use Sync personal vault now.":"Personal vault synced in "+watch.Elapsed.TotalSeconds.ToString("0.0")+"s at "+DateTime.Now.ToString("t")+".";
                        if(cancelled)PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.Conflict",status,NotificationType.Error));
                        else {PlayniteApi.Notifications.Remove("PlayniteLink.Conflict");if(activityReview.Count==0)PlayniteApi.Notifications.Remove("PlaytimeRclone.Error");}
                    });
                    if(launching==null){if(!cancelled)await SyncOnlineHltb();TrySyncSetup();}completed=true;return true;
                }
                finally {
                    if(metadataOnly)idleSchedule.Observe(!completed||idleUnchanged);else idleSchedule.Reset();
                    Interlocked.Exchange(ref syncing,0);syncGate.Release();ScheduleIdle(false);
                    if(Interlocked.Exchange(ref queuedSync,0)!=0&&!closing)QueuePersonalSync();
                }
            });
        }
        private string ChooseConflict(Game game,Conflict conflict)
        {
            if(!EnsureAccess())return "cancel";
            string choice="cancel";
            var window=Window("Choose a version — "+game.Name,780,740);var panel=Page(window,"Choose your progress",game.Name+" · "+(conflict.Kind=="save"?"Save conflict":"Preferences conflict"));
            Label(panel,"Both devices changed since the last shared version. Choose which progress to continue with. The other version stays in history; replacing local saves also keeps a folder backup.",14,true);
            var local=Card(panel,"This device",conflict.EmptyLocal?"This folder is empty or unavailable. Check its location before choosing it.":"The progress currently saved on this device.");
            Button(local,"Use this device"+(conflict.EmptyLocal?" (empty folder)":""),()=>{choice="local";window.Close();});
            foreach(var revision in conflict.Heads)
            {
                var captured=revision;
                DateTimeOffset date;var when=DateTimeOffset.TryParse(revision.CreatedUtc,out date)?date.ToLocalTime().ToString("g"):revision.CreatedUtc;
                var cloud=Card(panel,"Cloud version",when+"  ·  "+revision.Data.Count+" files or fields");
                var pick=Button(cloud,"Use this cloud version",()=>{choice=captured.Hash();window.Close();});pick.Tag=revision.Hash();
                var details=new StackPanel();Label(details,"Device: "+revision.Device+"\nVersion: "+revision.Hash(),12,true);Details(cloud,"Version details",details);
            }
            Label(panel,"Dates are informational. The newest clock does not decide the winner.",13,true);
            Button(Footer(window),"Cancel — keep versions separate",()=>window.Close());window.ShowDialog();return choice;
        }
        private Window Window(string title,int width,int height)
        {
            var window=PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton=false,ShowMaximizeButton=false });
            window.Title=title;window.MaxWidth=Math.Max(320,SystemParameters.WorkArea.Width-32);window.MaxHeight=Math.Max(300,SystemParameters.WorkArea.Height-32);window.Width=Math.Min(width,window.MaxWidth);window.Height=Math.Min(height,window.MaxHeight);window.MinWidth=Math.Min(window.Width,520);window.MinHeight=Math.Min(window.Height,360);window.Background=Canvas;window.Foreground=Ink;window.Owner=PlayniteApi.Dialogs.GetCurrentAppWindow();window.WindowStartupLocation=WindowStartupLocation.CenterOwner;return window;
        }
        private void ManageSaves(Game game)
        {
            if(syncing!=0)throw new InvalidOperationException("A background sync is finishing. Retry these settings in a moment.");
            if(game.IsRunning||game.IsLaunching)throw new InvalidOperationException("Stop the game before changing save settings.");
            Mapping map;if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map)){Enroll(game);return;}
            var link=Link(map.Key);var window=Window("Personal vault — "+game.Name,700,580);var panel=new StackPanel { Margin=new Thickness(22) };
            Text(panel,"Save files, playtime and portable preferences");
            var folderSummary=Text(panel,"Shared game: "+map.Source+"\nSave directory: "+(link.Folder??"Not configured")+"\nFavorite, Hidden and Notes sync for linked games. Cloud credentials and local paths stay on this device.");
            Button(panel,"Choose this device's save directory",()=> {
                var folder=PlayniteApi.Dialogs.SelectFolder();if(String.IsNullOrWhiteSpace(folder))return;
                if(syncing!=0)throw new InvalidOperationException("Wait for the background sync before changing the save root.");
                folder=SaveFiles.Root(folder);
                if(repository.Snapshot().Mappings.Any(p=>p.Value.Key!=map.Key && vault.State.Links.ContainsKey(p.Value.Key) && !String.IsNullOrWhiteSpace(vault.State.Links[p.Value.Key].Folder) && Overlap(folder,vault.State.Links[p.Value.Key].Folder)))
                    throw new InvalidOperationException("Save roots for separate games must not overlap.");
                if(!String.IsNullOrWhiteSpace(link.Folder) && !String.Equals(folder,link.Folder,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This game already has a save root. Review its current history before moving it; use a separate device profile for a new location.");
                if(PlayniteApi.Dialogs.ShowMessage("Manage this entire directory as one save snapshot?\n"+folder+"\n\nDo not select a folder managed by Steam Cloud, Save Manager or Syncthing at the same time. Restores replace its contents and keep a sibling backup. Files are streamed and deduplicated without a save-size cap.","Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                link.Folder=folder;vault.Save();folderSummary.Text="Shared game: "+map.Source+"\nSave directory: "+link.Folder+"\nFavorite, Hidden and Notes sync for linked games. Cloud credentials and local paths stay on this device.";
            });
            Button(panel,"Sync and review conflicts",()=>{RequestSync(true);});
            Button(panel,"Show save history",()=>ShowHistory(game,map.Key));
            Button(panel,"Recover unfinished playtime session",()=>Recover(game));
            Button(panel,"Open local state and backup locations",()=>Process.Start(Root));
            if(LastBackup!=null)Text(panel,"Last restore backup: "+LastBackup);
            LiveStatus(panel);Button(panel,"Close",()=>window.Close());window.Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };window.ShowDialog();
        }
        private static bool Overlap(string a,string b)
        {a=Path.GetFullPath(a).TrimEnd('\\')+"\\";b=Path.GetFullPath(b).TrimEnd('\\')+"\\";return a.StartsWith(b,StringComparison.OrdinalIgnoreCase)||b.StartsWith(a,StringComparison.OrdinalIgnoreCase);}
        private void ShowHistory(Game game,string key)
        {
            var window=Window("Preserved save history — "+game.Name,720,560);var panel=new StackPanel { Margin=new Thickness(20) };
            Text(panel,"Every uploaded revision is preserved. Restoring history creates a new revision; later unseen branches still require review.");
            foreach(var r in vault.Read(vault.Objects).Values.Where(r=>r.Game==key&&r.Kind=="save").OrderByDescending(r=>r.CreatedUtc))
            {
                var revision=r;Text(panel,r.CreatedUtc+" · device "+r.Device.Substring(0,8)+" · "+r.Data.Count+" files");
                Button(panel,"Restore "+r.Hash().Substring(0,8),()=> {
                    if(syncing!=0)throw new InvalidOperationException("Wait for the background sync before restoring history.");
                    var link=Link(key);if(String.IsNullOrWhiteSpace(link.Folder))throw new InvalidOperationException("Configure the save directory first.");
                    if(PlayniteApi.Dialogs.ShowMessage("Restore this historical version and preserve the current folder as a backup?", "Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    window.Close();RestoreHistoryAsync(key,revision);
                });
            }
            window.Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };window.ShowDialog();
        }
        private void BuildOverview(Window window)
        {
            var panel=Page(window,"Your progress, everywhere","Saves and playtime in your own cloud storage.","Overview");
            var connected=!String.IsNullOrWhiteSpace(config.RemoteFolder);
            var throttled=status.IndexOf("throttl",StringComparison.OrdinalIgnoreCase)>=0;
            var summary=Card(panel,syncing!=0?"Syncing in the background":throttled?"Waiting for your cloud provider":connected?"Storage configured":"Connect your cloud",syncing!=0?"You can keep browsing Playnite while your progress syncs.":status);
            overviewStatus=(TextBlock)summary.Children[1];overviewHeading=(TextBlock)summary.Children[0];
            if(connected)Label(summary,config.RemoteFolder,14,true);
            var actions=Actions(summary);
            if(connected)
            {
                var sync=Button(actions,"Sync now",()=>{RequestSync(true);});StyleAction(sync,true);sync.IsEnabled=syncing==0;overviewSync=sync;
                overviewStop=Button(actions,"Stop transfer",()=>{transferCancelled=true;});overviewStop.IsEnabled=syncing!=0;
            }
            else {var connect=Button(actions,"Connect cloud storage",()=>{ConnectStorage();});StyleAction(connect,true);}
            var count=repository.Snapshot().Mappings.Count;var games=Card(panel,count+(count==1?" linked game":" linked games"),onboarding.AutomaticGames?"Library games link automatically using their provider identity.":"Only games you choose are synchronized.");
            var automatic=Toggle(games,"Automatically link library games",onboarding.AutomaticGames);
            automatic.Click+=(_,e)=>{onboarding.AutomaticGames=automatic.IsChecked==true;SaveOnboarding();if(onboarding.AutomaticGames)QueueSync();};
            var online=Toggle(games,"Update my online HowLongToBeat progress",onboarding.OnlineHltb);
            online.Click+=(_,e)=>{onboarding.OnlineHltb=online.IsChecked==true;SaveOnboarding();if(onboarding.OnlineHltb)QueueSync();};
            Label(games,"Uses HowLongToBeat's own sign-in and existing profile entries. Preserves greater online progress, lists, scores and completion times.",13,true);
            activityStatusText=Label(games,activityStatus,13,true);
            Label(games,"Your linked games sync automatically. Choose a game to set up or manage synchronization.",14);
            if(connected)
            {
                var mappings=repository.Snapshot().Mappings;
                var missing=mappings.Values.Count(m=>!vault.State.Links.ContainsKey(m.Key)||String.IsNullOrWhiteSpace(vault.State.Links[m.Key].Folder));
                if(missing>0)Label(games,missing+" linked games sync playtime and session history; their save location still needs to be selected on this device. No save folder is guessed or treated as empty.",13,true);
                var manual=PlayniteApi.Database.Games.Count(g=>g.PluginId==Guid.Empty&&!mappings.ContainsKey(g.Id.ToString()));
                if(manual>0)Label(games,manual+" manual games need a shared link name. Titles alone cannot safely identify a game across devices.",13,true);
            }
            Label(games,"Saves · Playtime · Favorites and notes",13,true);
            Button(games,"Manage games",()=>{ManageGames();});
            var storage=Card(panel,"Storage connection","Use the same vault folder on every device. Each device keeps its own sign-in.");
            var storageActions=Actions(storage);Button(storageActions,connected?"Connection settings":"Use existing Rclone setup",()=>{Configure();});
            if(connected)Button(storageActions,"Connect another provider",()=>{ConnectProvider();});
            var advanced=new StackPanel();Label(advanced,"Your storage provider controls access. Use an Rclone crypt remote if you want encrypted storage. Store passwords and the Playnite database stay on this device.",13,true);
            Label(advanced,"Device ID: "+repository.Snapshot().Device,12,true);
            if(connected)Button(advanced,"Disconnect cloud on this device",()=>{
                if(PlayniteApi.Dialogs.ShowMessage("Disconnect this device from cloud sync? Local history stays available. Reconnect to the same vault to resume.","Disconnect cloud",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                SaveConfig(new SyncConfig{RcloneExe=config.RcloneExe,ConfigFile=config.ConfigFile,RemoteFolder=""});
            });
            Details(panel,"Advanced & privacy",advanced);
        }
        private void ConnectProvider()
        {
            var exe=config.RcloneExe;if(String.IsNullOrWhiteSpace(exe)||!File.Exists(exe))exe=Ask("Choose your installed rclone.exe:",exe);if(exe==null)return;
            if(!Path.IsPathRooted(exe)||!File.Exists(exe)||!exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Choose an existing rclone.exe.");
            string provider=null;
            var providerWindow=Window("Choose cloud storage",620,440);var providerPanel=Page(providerWindow,"Choose your cloud","Your saves stay in your own storage. Your browser handles sign-in.");
            foreach(var pair in new[]{new[]{"Google Drive","drive"},new[]{"Dropbox","dropbox"},new[]{"OneDrive","onedrive"}}){var choice=pair;Button(providerPanel,choice[0],()=>{provider=choice[1];providerWindow.Close();});}
            Button(Footer(providerWindow),"Cancel",()=>providerWindow.Close());providerWindow.ShowDialog();
            if(provider==null)return;
            if(provider!="drive"&&provider!="dropbox"&&provider!="onedrive")throw new ArgumentException("Unsupported provider.");
            Dictionary<string,string> initial=null;
            if(provider=="drive")
            {
                PlayniteApi.Dialogs.ShowMessage("Google Drive requires your own Desktop OAuth client to avoid Rclone's shared quota. Select the client JSON downloaded from your Google Cloud project. This remains on your device. An already configured Rclone connection can be selected without this step.","Connect Google Drive");
                var picker=new Microsoft.Win32.OpenFileDialog{Title="Choose your Google Desktop OAuth client",Filter="Google client JSON|*.json",CheckFileExists=true};
                if(picker.ShowDialog()!=true)return;
                var document=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(picker.FileName));var client=document["installed"];
                if(client==null||String.IsNullOrWhiteSpace((string)client["client_id"])||String.IsNullOrWhiteSpace((string)client["client_secret"]))throw new InvalidDataException("Choose a Google Desktop app client JSON file.");
                initial=new Dictionary<string,string>{{"client_id",(string)client["client_id"]},{"client_secret",(string)client["client_secret"]}};
            }
            var cfg=Path.Combine(Root,"personal-rclone-"+Guid.NewGuid().ToString("N").Substring(0,8)+".conf");
            var remote="personal";
            CloudAccount.Configure(exe,cfg,remote,provider,question=> {
                if(!question.Secret)return Ask(question.Name+"\n"+question.Help,question.Default);
                string value=null;var dialog=Window("Cloud account — "+question.Name,650,400);var panel=new StackPanel{Margin=new Thickness(20)};
                Text(panel,question.Help);var password=new PasswordBox{Padding=new Thickness(8)};panel.Children.Add(password);
                Button(panel,"Continue",()=>{value=password.Password;dialog.Close();});Button(panel,"Cancel",()=>dialog.Close());dialog.Content=panel;dialog.ShowDialog();return value;
            },Network,initial);
            var folder="PlayniteLink";
            onboarding.AutomaticGames=true;SaveOnboarding();
            SaveConfig(new SyncConfig {RcloneExe=exe,ConfigFile=cfg,RemoteFolder=remote+":"+folder});
        }
    }
}


