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
        private int transferGeneration;
        private int activeTransferGeneration;
        private bool TransferStopped {get{return transferCancelled||Volatile.Read(ref transferGeneration)!=Volatile.Read(ref activeTransferGeneration);}}
        public bool IsSyncPaused {get{return onboarding!=null&&onboarding.SyncPaused;}}
        public void StopTransfer()
        {
            transferCancelled=true;Interlocked.Increment(ref transferGeneration);Interlocked.Exchange(ref queuedSync,0);
            if(onboarding!=null){onboarding.SyncPaused=true;SaveOnboarding();}
            ReportStatus(syncing!=0?"Stopping transfer… Local changes are safe. You can resume once it stops.":"Sync paused. Local changes are safe. Choose Resume sync to retry.",syncing!=0?SyncDisplayState.Stopping:SyncDisplayState.Paused,syncing!=0?"Stopping safely":"Paused");
        }
        private void ResumeTransfers(){if(onboarding!=null){onboarding.SyncPaused=false;SaveOnboarding();}}
        private void CheckTransfer(){if(closing||TransferStopped)throw new OperationCanceledException();}
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
        private readonly MetadataValidationCache metadataValidation=new MetadataValidationCache();
        private readonly System.Threading.SemaphoreSlim syncGate = new System.Threading.SemaphoreSlim(1,1);
        // Written by the worker for the active cycle and read by launch preflight.
        private volatile System.Collections.Concurrent.ConcurrentDictionary<Guid,bool> checkedLaunchSaves;
        private readonly HashSet<Guid> pendingLaunches = new HashSet<Guid>();
        private readonly HashSet<Guid> permittedLaunches = new HashSet<Guid>();
        private readonly HashSet<Guid> cancelledChecks = new HashSet<Guid>();
        private readonly HashSet<Guid> launchReservations = new HashSet<Guid>();
        private readonly HashSet<Guid> restoringSaves = new HashSet<Guid>();
        private HashSet<string> savedConflicts=new HashSet<string>();
        private HashSet<string> ReadSaveConflicts(){return new HashSet<string>(vault.State.Links.Where(p=>p.Value.HasSaves&&vault.HeadCount(p.Key,"save")>1).Select(p=>p.Key));}
        private void RefreshKnownSaveConflicts(ISet<string> games=null)
        {
            var review=games==null?ReadSaveConflicts():new HashSet<string>(games.Where(key=>{
                SaveLink link;
                return vault.State.Links.TryGetValue(key,out link)&&link.HasSaves&&vault.HeadCount(key,"save")>1;
            }));
            UI(()=>{
                savedConflicts.RemoveWhere(key=>{
                    SaveLink link;
                    return (games==null||games.Contains(key))&&(!vault.State.Links.TryGetValue(key,out link)||!link.HasSaves);
                });
                // A single head can still need review after an empty source or
                // failed replacement. Keep that decision until reconciliation.
                savedConflicts.UnionWith(review);
            });
            RefreshCloudBadges();
        }
        private bool GameBusy(Guid id){var game=PlayniteApi.Database.Games.Get(id);return launchReservations.Contains(id)||game==null||game.IsRunning||game.IsLaunching;}
        private bool CachedSaveConflict(Game game,Mapping map)
        {
            // Never queue behind network work, or read the mutable vault concurrently.
            if(!devicePreferences.SyncSaves)return false;
            if(savedConflicts.Contains(map.Key))return true;
            if(!syncGate.Wait(0))return false;
            try{SaveLink link;return vault.State.Links.TryGetValue(map.Key,out link)&&SaveCloudOwnership.CanSync(game,link)&&vault.HeadCount(map.Key,"save")>1;}
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
        private List<SyncGame> GameSnapshot(IEnumerable<KeyValuePair<string,Mapping>> mappings)
        {
            var result=new List<SyncGame>();
            foreach(var pair in mappings)
            {
                var id=new Guid(pair.Key);
                var game=PlayniteApi.Database.Games.Get(id);if(game==null||GameBusy(game.Id))continue;
                CheckIdentity(game,pair.Value);result.Add(new SyncGame {Game=game,Key=pair.Value.Key,Preferences=Preferences(game)});
            }
            return result;
        }
        private void QueueGameSync(Guid game)
        {
            if(closing||repository==null)return;
            repository.QueueGameSync(game.ToString());
            RefreshCloudBadges();
            QueuePersonalSync();
        }
        private void QueuePersonalSync()
        {
            if(vaultSwitchPending||closing||repository==null||config==null||onboarding==null||!onboarding.SetupComplete||walkthroughWindow!=null||IsSyncPaused||String.IsNullOrWhiteSpace(config.RemoteFolder))return;
            if(syncing!=0){Interlocked.Exchange(ref queuedSync,1);return;}
            RequestSync(false);
        }
        private async void CheckIdleSync()
        {
            if(closing)return;
            if(syncing!=0||repository==null||config==null||IsSyncPaused||!onboarding.SetupComplete||walkthroughWindow!=null||String.IsNullOrWhiteSpace(config.RemoteFolder)){ScheduleIdle(false);return;}
            try{await SyncPersonalAsync(false,null,true);}
            catch(OperationCanceledException){}
            catch(Exception ex){if(!closing){logger.Error(ex,"Background cloud check failed; retrying with backoff.");}}
        }
        private void ScheduleIdle(bool reset)
        {
            if(reset)idleSchedule.Reset();
            if(!closing&&timer!=null)try{timer.Change(TimeSpan.FromSeconds(idleSchedule.DelaySeconds(devicePreferences.IntervalSeconds)),Timeout.InfiniteTimeSpan);}catch(ObjectDisposedException){}
        }
        private async void RequestSync(bool interactive,Guid? game=null,bool backgroundOnly=false)
        {
            try { if(!onboarding.SetupComplete){ShowWalkthrough();return;}await SyncPersonalAsync(interactive,null,backgroundOnly||!interactive,game); }
            catch(OperationCanceledException){ReportStatus("Sync paused. Local changes are safe. Choose Resume sync to retry.",SyncDisplayState.Paused,"Paused");}
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
                badgeResults[game.Id]=ex is CloudTransferException?SaveBadgeState.Waiting:SaveBadgeState.Failed;
                RefreshCloudBadges();
                if(!closing)start=UI(()=>ConfirmOfflineLaunch(game,ex.Message));
            }
            finally
            {
                UI(()=>{pendingLaunches.Remove(game.Id);PlayniteApi.Notifications.Remove("PlayniteLink.Checking."+game.Id);RefreshCloudBadges();});
            }
            if(closing)return;
            if(!start){UI(()=>cancelledChecks.Add(game.Id));return;}
            UI(()=>{
                if(restoringSaves.Contains(game.Id)){LaunchAfterRestore(game);return;}
                if(KnownLaunchConflict(game)){cancelledChecks.Add(game.Id);return;}
                permittedLaunches.Add(game.Id);PlayniteApi.StartGame(game.Id);
            });
        }
        private bool KnownLaunchConflict(Game game)
        {
            var mapping=repository.ReadMapping(game.Id.ToString());
            return mapping!=null&&CachedSaveConflict(game,mapping);
        }
        private bool ConfirmOfflineLaunch(Game game,string reason)
        {
            if(KnownLaunchConflict(game)||restoringSaves.Contains(game.Id))
            {
                NotifyError(new IOException("Review this game's save version or wait for its current save replacement before launching."));
                return false;
            }
            return PlayniteApi.Dialogs.ShowMessage("Cloud sync could not finish: "+reason+"\n\nPlay offline with local saves? Another device may have newer progress. Conflicts will need review after reconnection.","Playnite Link",MessageBoxButton.YesNo)==MessageBoxResult.Yes;
        }
        private async System.Threading.Tasks.Task<bool> SyncLaunchAsync(Game game,int generation)
        {
            // Allow the current cycle to finish this game while unrelated work continues.
            // Once the short wait expires, the caller can explicitly choose an offline launch.
            if(await LaunchSyncWait.ReuseOrEnter(syncGate,()=>{
                if(closing||generation!=Volatile.Read(ref transferGeneration))throw new OperationCanceledException();
                var current=checkedLaunchSaves;bool ready;return current!=null&&current.TryGetValue(game.Id,out ready)&&ready;
            },2000))return !UI(()=>KnownLaunchConflict(game)||restoringSaves.Contains(game.Id));
            var previousCancel=blobs.CancelRequested;
            var previousFetch=blobs.Fetch;
            try
            {
                if(closing||generation!=Volatile.Read(ref transferGeneration))throw new OperationCanceledException();
                if(!devicePreferences.SyncSaves)return true;
                Volatile.Write(ref activeTransferGeneration,generation);transferCancelled=false;Interlocked.Exchange(ref syncing,1);
                var id=game.Id.ToString();
                var mapping=repository.ReadMapping(id);
                if(mapping==null)return false;
                var item=UI(()=>GameSnapshot(new[]{new KeyValuePair<string,Mapping>(id,mapping)}).SingleOrDefault());
                if(item==null)return false;
                SaveLink link;
                if(!vault.State.Links.TryGetValue(item.Key,out link)||!SaveCloudOwnership.CanSync(game,link))
                {
                    UI(()=>savedConflicts.Remove(item.Key));
                    return true;
                }
                BeginBadgeChecks(new[]{item});
                var watch=Stopwatch.StartNew();
                Func<bool> expired=()=>watch.ElapsedMilliseconds>=8000;
                Func<bool> cancelled=()=>closing||TransferStopped||expired();
                Action check=()=>{CheckTransfer();if(expired())throw new TimeoutException("The pre-launch cloud check reached its time limit.");};
                blobs.CancelRequested=cancelled;
                var me=repository.DeviceId;
                var saveCapture=new SaveCaptureCache();
                vault.UpgradeLegacySaves(blobs,me,item.Key);
                CaptureLocalChanges(new List<SyncGame>{item},saveCapture,savesOnly:true);
                if(unavailableSaveGames.Remove(item.Key))throw new IOException("Could not read this game's save location.");
                if(String.IsNullOrWhiteSpace(config.RemoteFolder))throw new IOException("Personal cloud is not connected.");
                check();var captured=config;CheckActiveGameVault(captured,cancelled);
                var metadataCache=Path.Combine(Root,"cloud-cache","metadata");
                new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1",cancelled:cancelled)
                    .PullMetadata(metadataCache,new[]{"devices","revisions"},metadataValidation);
                check();devices.Import(Path.Combine(metadataCache,"devices","objects"));
                var requested=devices.Heads(me,"settings");
                if(devices.EffectiveState(me)!="active"||requested.Count>1)throw new IOException("This device cannot sync right now.");
                if(requested.Count==1)
                {
                    devicePreferences=devicePreferences.ApplyRequested(requested[0],me);
                    Atomic.Write(Path.Combine(Root,"device-settings.json"),JsonConvert.SerializeObject(devicePreferences,Formatting.Indented));
                }
                var incoming=Path.Combine(metadataCache,"revisions","objects");
                vault.Import(incoming);RefreshKnownSaveConflicts(new HashSet<string>{item.Key});
                if(!devicePreferences.SyncSaves)return true;
                var blobTransport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,cancelled);
                blobs.Fetch=names=>blobTransport.PullFiles(blobs.DirectoryPath,names);
                vault.UpgradeLegacySaves(blobs,me,item.Key);
                check();
                var reconciled=ReconcileSave(item,link,c=>{
                    watch.Stop();
                    try{return UI(()=>ChooseConflict(game,c));}
                    finally{watch.Start();}
                },check,saveCapture,markFailureAsConflict:false);
                badgeResults[game.Id]=reconciled?SaveBadgeState.Checked:SaveBadgeState.Review;
                return reconciled;
            }
            catch(OperationCanceledException){CheckTransfer();throw new TimeoutException("The pre-launch cloud check reached its time limit.");}
            finally
            {
                blobs.CancelRequested=previousCancel;blobs.Fetch=previousFetch;
                FinishBadgeChecks();
                Interlocked.Exchange(ref syncing,0);syncGate.Release();RefreshCloudBadges();ScheduleIdle(false);
                if(Interlocked.Exchange(ref queuedSync,0)!=0&&!closing&&!IsSyncPaused&&!transferCancelled)QueuePersonalSync();
            }
        }
        private SaveLink Link(string game)
        {
            SaveLink link;if(!vault.State.Links.TryGetValue(game,out link)) { link=new SaveLink();vault.State.Links.Add(game,link);vault.Save(); }return link;
        }
        private SortedDictionary<string,string> Preferences(Game game)
        { return new SortedDictionary<string,string>(StringComparer.Ordinal) { {"Favorite",game.Favorite.ToString()}, {"Hidden",game.Hidden.ToString()}, {"Notes",game.Notes??""} }; }
        private void RecordLocal(string game,string kind,SortedDictionary<string,string> data,Checkpoint checkpoint,SortedDictionary<string,string> fullSave=null)
        {
            var hash=Revision.ContentHashOf(data);
            if(hash==checkpoint.Content)return;
            // First contact must compare with the remote before publishing a new root.
            if(checkpoint.Revision==null)return;
            // An empty folder may mean an unavailable disk or a wrong path; require review.
            if(kind=="save" && !SavePlan.HasFiles(data))return;
            var publication=fullSave??data;
            var revision=new Revision { Version=kind=="save"?(publication.Keys.Any(k=>k.StartsWith(SavePlan.Mounts,StringComparison.Ordinal))?3:2):1,Game=game,Kind=kind,Device=repository.DeviceId,Data=publication,Parents=new List<string>{checkpoint.Revision} };
            vault.Add(revision);checkpoint.Revision=revision.Hash();checkpoint.Content=hash;vault.Save();
        }
        private void SeedHistoryCache(string destination)
        {
            var expected=vault.State.ConnectionIdentity;if(String.IsNullOrWhiteSpace(expected))return;
            var normal=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Playnite","ExtensionsData",Id.ToString());
            foreach(var root in new[]{BaseRoot,normal}.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if(!Directory.Exists(root))continue;
                try { foreach(var profile in VaultProfiles.List(root))
                {
                    if(String.Equals(Path.GetFullPath(profile.Directory),Path.GetFullPath(Root),StringComparison.OrdinalIgnoreCase))continue;
                    try { var marker=Path.Combine(profile.Directory,"vault-state.json");if(!File.Exists(marker))continue;
                    var previous=JsonSafety.Deserialize<VaultState>(File.ReadAllText(marker));if(previous==null||previous.ConnectionIdentity!=expected)continue;
                    var copied=MetadataCache.Seed(Path.Combine(profile.Directory,"cloud-cache","metadata"),destination);
                    if(copied>0)logger.Info("Reused "+copied+" verified cached history records from the same cloud vault. No local mappings or settings copied.");
                    } catch(Exception ex) { logger.Warn("Could not reuse an optional history cache: "+ex.GetType().Name); }
                } } catch(Exception ex) { logger.Warn("Could not inspect an optional history cache: "+ex.GetType().Name); }
            }
        }
        private readonly HashSet<string> unavailableSaveGames=new HashSet<string>();
        private void CaptureLocalChanges(List<SyncGame> games,SaveCaptureCache saveCapture,bool savesOnly=false)
        {
            foreach(var item in games)
            {
                if(UI(()=>GameBusy(item.Game.Id)))continue;
                SaveLink link;if(!vault.State.Links.TryGetValue(item.Key,out link))continue;
                if(!savesOnly&&devicePreferences.SyncPreferences)RecordLocal(item.Key,"preferences",item.Preferences,link.Preferences);
                try{if(devicePreferences.SyncSaves&&SaveCloudOwnership.CanSync(item.Game,link)){var plan=Plan(item.Game,item.Key);var full=plan.Capture(true,saveCapture);var captured=plan.Filter(full);var previous=vault.FindRevision(link.Save.Revision);if(previous!=null&&plan.NeedsSourceReview(previous.Data,captured))continue;var publication=previous==null?full:plan.ForPublication(full,previous.Data);if(!UI(()=>GameBusy(item.Game.Id)))RecordLocal(item.Key,"save",captured,link.Save,publication);}}
                catch(OperationCanceledException){throw;}
                catch(Exception ex){unavailableSaveGames.Add(item.Key);logger.Warn(ex,"Save capture deferred; session history will still synchronize.");}
            }
        }
        public System.Threading.Tasks.Task<bool> SyncPersonalAsync(bool interactive,Game launching,bool metadataOnly=false,Guid? requestedGame=null)
        {
            if(preparingExistingBackups)return System.Threading.Tasks.Task.FromResult(false);
            if(MigrationBlocksSync)throw new InvalidOperationException("Existing backups are being prepared automatically. Restart Playnite after preparation finishes so Link can take over from the loaded Save Manager extension.");
            if(vaultSwitchPending)throw new InvalidOperationException("Restart Playnite to finish switching vaults before syncing.");
            if(interactive)UI(()=>ResumeTransfers());
            else if(IsSyncPaused)return System.Threading.Tasks.Task.FromResult(false);
            var generation=Volatile.Read(ref transferGeneration);
            if(launching!=null)return System.Threading.Tasks.Task.Run(()=>SyncLaunchAsync(launching,generation));
            return System.Threading.Tasks.Task.Run(async ()=> {
                await syncGate.WaitAsync();
                // A stop also cancels requests already waiting for the gate. They must
                // never clear the stop flag and silently restart a cancelled transfer.
                if(generation!=Volatile.Read(ref transferGeneration)||(!interactive&&IsSyncPaused)){syncGate.Release();throw new OperationCanceledException();}
                Volatile.Write(ref activeTransferGeneration,generation);transferCancelled=false;Interlocked.Exchange(ref syncing,1);
                var watch=System.Diagnostics.Stopwatch.StartNew();bool idleUnchanged=false;bool completed=false;
                var launchResults=new System.Collections.Concurrent.ConcurrentDictionary<Guid,bool>();checkedLaunchSaves=launchResults;
                try
                {
                    CheckTransfer();
                    unavailableSaveGames.Clear();
                    ReportStatus("Reading local changes before checking the cloud.",SyncDisplayState.Working,"Preparing sync");
                    if(repository==null)throw new InvalidOperationException(status);
                    UI(()=>EnsureAutomaticLinks());
                    var pendingGames=repository.PendingGameSyncs();
                    var local=requestedGame.HasValue?requestedGame.Value.ToString():null;
                    var mappings=repository.Mappings().Where(pair=>local!=null
                        ? pair.Key==local : !metadataOnly||pendingGames.ContainsKey(pair.Key)).ToArray();
                    ISet<string> scope=metadataOnly||requestedGame.HasValue
                        ? new HashSet<string>(mappings.Select(pair=>pair.Value.Key),StringComparer.Ordinal)
                        : null;
                    var games=UI(()=>GameSnapshot(mappings));
                    var saveScope=UI(()=>new HashSet<string>(mappings.Where(pair=>{
                        var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));
                        SaveLink link;
                        return game!=null&&vault.State.Links.TryGetValue(pair.Value.Key,out link)&&SaveCloudOwnership.CanSync(game,link);
                    }).Select(pair=>pair.Value.Key),StringComparer.Ordinal));
                    BeginBadgeChecks(games);
                    if(mappings.Length>0)
                    {
                        if(devicePreferences.SyncPlaytime)await CaptureActivityHistoryAsync(mappings);
                        await CaptureAchievements(mappings);
                        await CaptureAchievementCustomizations(mappings);
                    }
                    var me=repository.DeviceId;
                    if(devicePreferences.SyncSaves)UpgradeSelectedLegacySaves(me,saveScope);
                    var saveCapture=new SaveCaptureCache();
                    var capturedSaves=devicePreferences.SyncSaves;var capturedPreferences=devicePreferences.SyncPreferences;
                    CaptureLocalChanges(games,saveCapture); // Keep local revisions when the network is unavailable.
                    if(String.IsNullOrWhiteSpace(config.RemoteFolder))
                    {
                        ReportStatus("Personal cloud is not connected. Sessions are stored locally.",SyncDisplayState.Ready,"Connect storage");
                        return true;
                    }
                    CheckTransfer();ReportStatus("Looking for changes from your other devices.",SyncDisplayState.Working,"Checking cloud");
                    var captured=config;CheckActiveGameVault(captured,()=>closing||TransferStopped);
                    var deviceTransport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/devices/objects",cancelled:()=>closing||TransferStopped);
                    var metadataCache=Path.Combine(Root,"cloud-cache","metadata");
                    if(scope==null||scope.Count>0)SeedHistoryCache(metadataCache);
                    var metadataTransport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1",cancelled:()=>closing||TransferStopped,progress:p=>ReportStatus(p.Downloaded>0?p.Downloaded+" records downloaded":"",SyncDisplayState.Working,"Downloading cloud history"));
                    var requestedAreas=SyncMetadataAreas(scope,interactive);
                    metadataTransport.PullMetadata(metadataCache,requestedAreas,metadataValidation);
                    var deviceCache=Path.Combine(metadataCache,"devices","objects");Directory.CreateDirectory(deviceCache);
                    CheckTransfer();devices.Import(deviceCache);
                    var requested=devices.Heads(me,"settings");
                    if(requested.Count==1)
                    {
                        devicePreferences=devicePreferences.ApplyRequested(requested[0],me);Atomic.Write(Path.Combine(Root,"device-settings.json"),JsonConvert.SerializeObject(devicePreferences,Formatting.Indented));

                    }
                    var deviceState=devices.EffectiveState(me);
                    if(deviceState!="removed")devices.PublishProfile(me,devicePreferences,UI(()=>DeviceGames()));
                    deviceViewCache=devices.Views().ToArray();
                    deviceTransport.PushFiles(devices.Objects,Directory.GetFiles(devices.Objects,"*.json").Select(Path.GetFileName).Where(n=>!File.Exists(Path.Combine(deviceCache,n))));
                    if(deviceState!="active"||requested.Count>1||(!interactive&&!devicePreferences.AutoSync))
                    {FinishBadgeChecks(SaveBadgeState.Waiting);ReportStatus("Device "+deviceState+(requested.Count>1?"; settings need review":"")+". Local saves remain available.",SyncDisplayState.Review,"Device sync unavailable");return true;}
                    if(scope!=null&&scope.Count==0)
                    {
                        TrySyncSetup();
                        ReportStatus("Game saves are checked on launch. Use Sync now for all linked games.",SyncDisplayState.Completed,"Cloud checked");
                        idleUnchanged=true;completed=true;return true;
                    }
                    var newlyEnabledAreas=SyncMetadataAreas(scope,interactive).Except(requestedAreas).ToArray();
                    if(newlyEnabledAreas.Length>0)metadataTransport.PullMetadata(metadataCache,newlyEnabledAreas,metadataValidation);
                    SyncReceipts.Import(Path.Combine(metadataCache,"receipts","objects"),ObserveBadgeReceipt);
                    var backupCache=Path.Combine(metadataCache,"backups","objects");
                    if(devicePreferences.SyncSaves)CloudBackups.Import(backupCache);
                    var blobTransport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,()=>closing||TransferStopped);
                    blobs.Fetch=names=>blobTransport.PullFiles(blobs.DirectoryPath,names);
                    if(devicePreferences.SyncSaves)SeedLocalBackupCatalog(games);
                    if(!capturedSaves&&devicePreferences.SyncSaves)BeginBadgeChecks(games);
                    if((!capturedSaves&&devicePreferences.SyncSaves)||(!capturedPreferences&&devicePreferences.SyncPreferences))
                        CaptureLocalChanges(games,saveCapture);
                    var transport=new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/revisions/objects",cancelled:()=>closing||TransferStopped);
                    var incoming=Path.Combine(metadataCache,"revisions","objects");Directory.CreateDirectory(incoming);
                    // Persistent caches let Rclone skip objects already downloaded. No history is deleted.
                    CheckTransfer();ReportStatus("",SyncDisplayState.Working,"Combining playtime and sessions");
                    var sentSessions=repository.Snapshot().Entries;
                    if(devicePreferences.SyncPlaytime)new SyncEngine().Run(repository,new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,cancelled:()=>closing||TransferStopped),Path.Combine(Root,"scratch"),Path.Combine(metadataCache,"ledger","objects"),false,false,scope);
                    if(devicePreferences.SyncPlaytime)new SyncEngine().Run(repository,new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/activity/objects",cancelled:()=>closing||TransferStopped),Path.Combine(Root,"scratch"),Path.Combine(metadataCache,"activity","objects"),true,false,scope);
                    try{await SyncAchievementRecords(metadataCache,scope);}catch(OperationCanceledException){throw;}catch(Exception ex){achievementSyncPending=true;logger.Warn(ex,"Achievement record transfer deferred; saves and sessions continue.");}
                    // Sessions and their displayed totals must not wait for a save
                    // archive upload, missing directory or optional add-on adapter.
                    if(devicePreferences.SyncPlaytime)await ProjectAsync(mappings);
                    await SyncOnlineHltb(mappings);
                    if(devicePreferences.SyncSaves||devicePreferences.SyncPreferences)vault.Import(incoming);
                    if(devicePreferences.SyncSaves)
                    {
                        RefreshKnownSaveConflicts(saveScope);
                        UpgradeSelectedLegacySaves(me,saveScope);
                    }
                    var cancelled=false;
                    var completedGames=new HashSet<string>(StringComparer.Ordinal);
                    games=UI(()=>GameSnapshot(mappings));
                    var checkedGames=0;
                    foreach(var item in games)
                    {
                        if(unavailableSaveGames.Contains(item.Key)){cancelled=true;continue;}
                        CheckTransfer();ReportStatus("Checking "+item.Game.Name+" for save and library changes.",SyncDisplayState.Working,"Checking selected games",++checkedGames,games.Count);
                        if(UI(()=>GameBusy(item.Game.Id)))continue;
                        var game=item.Game;var link=Link(item.Key);
                        Func<Conflict,string> choose=c=>interactive&&!metadataOnly?UI(()=>ChooseConflict(game,c)):"cancel";
                        if(devicePreferences.SyncSaves&&SaveCloudOwnership.CanSync(game,link))
                        {
                            var reconciled=ReconcileSave(item,link,choose,CheckTransfer,saveCapture,allowRemoteApply:!metadataOnly);
                            if(!metadataOnly)launchResults[game.Id]=reconciled;
                            if(!reconciled)cancelled=true;
                        }
                        if(devicePreferences.SyncPreferences){var prefs=vault.Reconcile(item.Key,"preferences",repository.DeviceId,item.Preferences,link.Preferences,choose,data=>UI(()=> {
                            var current=PlayniteApi.Database.Games.Get(game.Id);
                            if(Revision.ContentHashOf(Preferences(current))!=Revision.ContentHashOf(item.Preferences))throw new IOException("Preferences changed during sync; retrying preserves your edit.");
                            current.Favorite=Boolean.Parse(data["Favorite"]);current.Hidden=Boolean.Parse(data["Hidden"]);current.Notes=data["Notes"];PlayniteApi.Database.Games.Update(current);
                        }));
                        if(prefs.Cancelled)cancelled=true;}
                        completedGames.Add(game.Id.ToString());
                    }
                    var pending=vault.Read(vault.Objects).Where(p=>(scope==null||scope.Contains(p.Value.Game))&&!File.Exists(Path.Combine(incoming,p.Key+".json"))&&
                        ((p.Value.Kind=="save"&&devicePreferences.SyncSaves&&saveScope.Contains(p.Value.Game))||(p.Value.Kind=="preferences"&&devicePreferences.SyncPreferences))).ToList();
                    // A downloaded manifest is the commit receipt for its blobs. Send
                    // only blobs needed by new manifests, before publishing those manifests.
                    if(devicePreferences.SyncSaves&&pending.Any(p=>p.Value.Kind=="save"))
                    {
                        var committed=new HashSet<string>(vault.Read(incoming).Values.Where(r=>r.Kind=="save").SelectMany(r=>r.Data.Values).Where(v=>v.StartsWith("blob:",StringComparison.Ordinal)));
                        var needed=pending.Where(p=>p.Value.Kind=="save").SelectMany(p=>p.Value.Data.Values).Where(v=>v.StartsWith("blob:",StringComparison.Ordinal)&&!committed.Contains(v)).Select(v=>BlobReference.Parse(v).Hash+".blob");
                        CheckTransfer();ReportStatus("",SyncDisplayState.Working,"Uploading saves");blobTransport.PushFiles(blobs.DirectoryPath,needed);
                    }
                    CheckTransfer();transport.PushFiles(vault.Objects,pending.Select(p=>p.Key+".json"));
                    if(devicePreferences.SyncSaves)PublishCloudBackups(blobTransport,backupCache,scope);
                    PublishGameSyncReceipts(sentSessions,saveCapture,scope);
                    FinishBadgeChecks();
                    RefreshKnownSaveConflicts(saveScope);
                    UI(()=> {
                        if(cancelled)PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.Conflict","Save versions need your review. Your local progress is kept.",NotificationType.Error));
                        else {PlayniteApi.Notifications.Remove("PlayniteLink.Conflict");if(activityReview.Count==0)PlayniteApi.Notifications.Remove("PlaytimeRclone.Error");}
                    });
                    ReportStatus("",SyncDisplayState.Working,"Finishing sync");
                    await SyncAchievementDetails(metadataCache,interactive&&!metadataOnly,mappings,scope);
                    CheckTransfer();
                    if(!achievementSyncPending)repository.CompleteGameSyncs(pendingGames.Where(pair=>completedGames.Contains(pair.Key)));
                    TrySyncSetup();
                    var needsReview=achievementSyncPending||cancelled||projectionFailures>0||UI(()=>activityReview.Count>0||saveScope.Any(savedConflicts.Contains));
                    ReportStatus(achievementSyncPending?"Some achievement updates are pending. Session and save sync continue.":unavailableSaveGames.Count>0?"Sessions synchronized; "+unavailableSaveGames.Count+" save location(s) could not be read. Check save paths.":needsReview?"Choose a version to continue.":"",needsReview?SyncDisplayState.Review:SyncDisplayState.Completed,needsReview?"Review needed":"Up to date");completed=true;return true;
                }
                catch(OperationCanceledException){ReportStatus("Sync paused. Local changes are safe. Choose Resume sync to retry.",SyncDisplayState.Paused,"Paused");throw;}
                catch(Exception ex){FinishBadgeChecks(ex is CloudTransferException?SaveBadgeState.Waiting:SaveBadgeState.Failed);ReportStatus(ex.Message,ex is CloudTransferException?SyncDisplayState.Waiting:SyncDisplayState.Failed,ex is CloudTransferException?"Waiting for cloud":"Could not sync");throw;}
                finally {
                    if(blobs!=null)blobs.Fetch=null;
                    checkedLaunchSaves=null;
                    if(metadataOnly)idleSchedule.Observe(!completed||idleUnchanged);else idleSchedule.Reset();
                    FinishBadgeChecks();
                    Interlocked.Exchange(ref syncing,0);syncGate.Release();RefreshCloudBadges();ScheduleIdle(false);
                    if(Interlocked.Exchange(ref queuedSync,0)!=0&&!closing&&!IsSyncPaused&&!transferCancelled)QueuePersonalSync();
                }
            });
        }
        private string[] SyncMetadataAreas(ISet<string> scope,bool interactive)
        {
            if((scope!=null&&scope.Count==0)||(!interactive&&!devicePreferences.AutoSync))return new[]{"devices"};
            var areas=new List<string>{"devices","receipts"};
            if(devicePreferences.SyncPlaytime)areas.AddRange(new[]{"ledger","activity"});
            if(devicePreferences.SyncSaves||devicePreferences.SyncPreferences)areas.Add("revisions");
            if(devicePreferences.SyncSaves)areas.Add("backups");
            if(onboarding.SyncAchievements)areas.AddRange(new[]{"achievements","achievement-catalogs","achievement-customizations"});
            return areas.ToArray();
        }
        private void UpgradeSelectedLegacySaves(string device,ISet<string> games)
        {
            if(games==null)vault.UpgradeLegacySaves(blobs,device);
            else foreach(var game in games)vault.UpgradeLegacySaves(blobs,device,game);
        }
        private bool ReconcileSave(SyncGame item,SaveLink link,Func<Conflict,string> choose,Action check,SaveCaptureCache saveCapture,bool markFailureAsConflict=true,bool allowRemoteApply=true)
        {
            var game=item.Game;
            var plan=Plan(game,item.Key);var full=plan.Capture(true,saveCapture);var local=plan.Filter(full);var hash=Revision.ContentHashOf(local);
            var previous=vault.FindRevision(link.Save.Revision);
            var sourceReview=previous!=null&&plan.NeedsSourceReview(previous.Data,local);
            var publication=previous==null?full:plan.ForPublication(full,previous.Data);
            var reconciled=false;
            var knownReview=sourceReview||vault.HeadCount(item.Key,"save")>1;
            try
            {
                check();
                var outcome=vault.Reconcile(item.Key,"save",repository.DeviceId,local,link.Save,conflict=>{
                    knownReview=true;
                    return choose(conflict);
                },data=>{
                    LastBackup=plan.Restore(data,hash,()=>UI(()=>{
                        check();
                        if(GameBusy(game.Id))throw new IOException("The game is starting or running; restore deferred.");
                        restoringSaves.Add(game.Id);
                    }));
                },plan.Filter,_=>publication,sourceReview,allowRemoteApply);
                reconciled=!outcome.Cancelled;
                return reconciled;
            }
            finally{UI(()=>{if(reconciled)savedConflicts.Remove(item.Key);else if(knownReview||markFailureAsConflict)savedConflicts.Add(item.Key);restoringSaves.Remove(game.Id);});}
        }
        private string ChooseConflict(Game game,Conflict conflict)
        {
            if(!EnsureAccess())return "cancel";
            string choice="cancel";
            var saves=conflict.Kind=="save";
            var window=Window("Cloud conflict — "+game.Name,680,620);var panel=Page(window,saves?"Which saves do you want to use?":"Which game details do you want to use?",game.Name);
            Label(panel,conflict.MissingSource?"A save location became empty or unavailable. Review the complete save set before replacing another device's progress. Other versions remain in history.":"Both devices changed. The other version will be kept in history.",14,true);
            var localVersion=conflict.Heads.FirstOrDefault(r=>r.ContentHash()==conflict.LocalHash);
            var localInfo=localVersion==null?null:DateTimeOffset.Parse(localVersion.CreatedUtc).ToLocalTime().ToString("g")+" · "+(saves?SavePlan.FileCount(localVersion.Data):localVersion.Data.Count)+(saves?" files":" fields");
            var local=Card(panel,"This device",conflict.EmptyLocal?"This save folder is empty.":localInfo);
            Button(local,saves?(conflict.EmptyLocal?"Keep empty local folder":"Keep local saves"):"Keep local details",()=>{choice="local";window.Close();});
            foreach(var revision in conflict.Heads.Where(r=>r.ContentHash()!=conflict.LocalHash))
            {
                var captured=revision;var device=devices.Views().FirstOrDefault(d=>d.Id==revision.Device);
                DateTimeOffset date;var when=DateTimeOffset.TryParse(revision.CreatedUtc,out date)?date.ToLocalTime().ToString("g"):"";
                var cloud=Card(panel,"Cloud"+(device==null?"":" · "+device.Name),when+" · "+(saves?SavePlan.FileCount(revision.Data):revision.Data.Count)+(saves?" files":" fields"));
                var pick=Button(cloud,saves?"Use cloud saves":"Use cloud details",()=>{choice=captured.Hash();window.Close();});pick.Tag=revision.Hash();
            }
            Button(Footer(window),"Decide later",()=>window.Close());window.ShowDialog();return choice;
        }
        private Window Window(string title,int width,int height)
        {
            var window=PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton=false,ShowMaximizeButton=false });
            window.Title=title;window.MaxWidth=Math.Max(320,SystemParameters.WorkArea.Width-32);window.MaxHeight=Math.Max(300,SystemParameters.WorkArea.Height-32);window.Width=Math.Min(width,window.MaxWidth);window.Height=Math.Min(height,window.MaxHeight);window.MinWidth=Math.Min(window.Width,520);window.MinHeight=Math.Min(window.Height,360);window.Background=Canvas;window.Foreground=Ink;window.Owner=walkthroughWindow??accountWindow??PlayniteApi.Dialogs.GetCurrentAppWindow();window.WindowStartupLocation=WindowStartupLocation.CenterOwner;return window;
        }
        private void SetSaveFolder(Game game,string folder,bool linking=false)
        {
            if(game.IsRunning||game.IsLaunching)throw new InvalidOperationException("Stop the game before choosing its save folder.");
            if(syncing!=0&&!linking)throw new InvalidOperationException("A sync is finishing. Try again in a moment.");
            Mapping map;if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map))throw new InvalidOperationException("Link this game first.");
            var link=Link(map.Key);
            if(!SaveCloudOwnership.UsesLink(game,link.LinkManagesSaves))
                throw new InvalidOperationException("Choose Playnite Link as the only sync tool for these save paths in Save paths & options first.");
                folder=SaveFiles.Root(folder);
                if(repository.Snapshot().Mappings.Any(p=>p.Value.Key!=map.Key && vault.State.Links.ContainsKey(p.Value.Key) && !String.IsNullOrWhiteSpace(vault.State.Links[p.Value.Key].Folder) && Overlap(folder,vault.State.Links[p.Value.Key].Folder)))
                    throw new InvalidOperationException("Save roots for separate games must not overlap.");
                if(!String.IsNullOrWhiteSpace(link.Folder) && !String.Equals(folder,link.Folder,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This game already has a save root. Review its current history before moving it; use a separate device profile for a new location.");
                if(!linking&&PlayniteApi.Dialogs.ShowMessage("Include save files from this folder?\n"+folder+"\n\nChoose only this game’s saves, and use one cloud-sync tool for this folder. Replacing saves keeps a local backup.","Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                var candidate=JsonSafety.Deserialize<SaveLink>(JsonConvert.SerializeObject(link));candidate.Folder=folder;candidate.Sources=new List<SaveSource>{new SaveSource{Id="main",Path=SavePaths.Portable(folder,game.InstallDirectory,link.EmulatorDirectory)}};ValidatePlan(game,candidate);
                link.Folder=folder;link.Sources=candidate.Sources;vault.Save();ResetSaveBadge(game.Id);
        }
        private void ManageSaves(Game game) { Manage(game); }
        private static bool Overlap(string a,string b)
        {a=Path.GetFullPath(a).TrimEnd('\\')+"\\";b=Path.GetFullPath(b).TrimEnd('\\')+"\\";return a.StartsWith(b,StringComparison.OrdinalIgnoreCase)||b.StartsWith(a,StringComparison.OrdinalIgnoreCase);}
        private void ShowHistory(Game game,string key)
        {
            var window=Window("Preserved save history — "+game.Name,720,560);var panel=new StackPanel { Margin=new Thickness(20) };
            Text(panel,"Every uploaded revision is preserved. Restoring history creates a new revision; later unseen branches still require review.");
            foreach(var r in vault.Read(vault.Objects).Values.Where(r=>r.Game==key&&r.Kind=="save").OrderByDescending(r=>r.CreatedUtc))
            {
                var revision=r;Text(panel,r.CreatedUtc+" · device "+r.Device.Substring(0,8)+" · "+SavePlan.FileCount(r.Data)+" files");
                Button(panel,"Restore "+r.Hash().Substring(0,8),()=> {
                    if(syncing!=0)throw new InvalidOperationException("Wait for the background sync before restoring history.");
                    var link=Link(key);if(!link.HasSaves)throw new InvalidOperationException("Configure the save paths first.");
                    if(PlayniteApi.Dialogs.ShowMessage("Restore this historical version and preserve the current folder as a backup?", "Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    window.Close();RestoreHistoryAsync(key,revision);
                });
            }
            window.Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };window.ShowDialog();
        }
        private void BuildOverview(Window window)
        {
            var panel=Page(window,"Your progress, everywhere","","Overview");
            var connected=!String.IsNullOrWhiteSpace(config.RemoteFolder);
            var throttled=status.IndexOf("throttl",StringComparison.OrdinalIgnoreCase)>=0;
            var summary=Card(panel,syncing!=0?"Syncing in the background":throttled?"Waiting for your cloud provider":connected?"Storage configured":"Connect your cloud");
            overviewStatus=SyncStatusView(summary);overviewHeading=(TextBlock)summary.Children[0];
            if(connected)Label(summary,config.RemoteFolder,14,true);
            var actions=Actions(summary);
            if(connected)
            {
                var sync=Button(actions,IsSyncPaused?"Resume sync":"Sync now",()=>{RequestSync(true);});StyleAction(sync,true);sync.IsEnabled=syncing==0;overviewSync=sync;
                overviewStop=Button(actions,"Stop transfer",StopTransfer);overviewStop.IsEnabled=syncing!=0;
            }
            else {var connect=Button(actions,"Set up Playnite Link",ShowWalkthrough);StyleAction(connect,true);}
            Button(actions,onboarding.SetupComplete?"Review setup":"Continue setup",ShowWalkthrough);
            var connectionTest=Button(actions,"Test connection",()=>{});var connectionFeedback=overviewStatus;
            connectionTest.Click+=(_,e)=>TestConnection(connectionFeedback,connectionTest);
            Button(actions,"Reset connection",()=>{ResetConnection();RefreshOverviewPage();});
            Button(actions,"Switch vault / start fresh",SwitchVault);
            var count=repository.Snapshot().Mappings.Count;var games=Card(panel,count+(count==1?" linked game":" linked games"),onboarding.AutomaticGames?"Store games are included automatically.":"");
            if(connected)
            {
                var mappings=repository.Snapshot().Mappings;
                var missing=mappings.Values.Count(m=>!vault.State.Links.ContainsKey(m.Key)||!vault.State.Links[m.Key].HasSaves);
                if(missing>0)Label(games,missing+" games without a save folder",13,true);
                var manual=PlayniteApi.Database.Games.Count(g=>g.PluginId==Guid.Empty&&!mappings.ContainsKey(g.Id.ToString()));
                if(manual>0)Label(games,manual+" games available to link",13,true);
            }
            
            Button(games,"Your games",()=>{ManageGames();});
            var storage=Card(panel,"Storage connection","Use the same cloud account and folder on each device.");
            var storageActions=Actions(storage);Button(storageActions,connected?"Connection settings":"Use existing Rclone setup",()=>{Configure();});
            if(connected)Button(storageActions,"Connect another provider",()=>{ConnectProvider();});
            var advanced=new StackPanel();Label(advanced,"Your storage provider controls access. Use an Rclone crypt remote if you want encrypted storage. Store passwords and the Playnite database stay on this device.",13,true);
            Label(advanced,"Device ID: "+repository.DeviceId,12,true);
            if(connected)Button(advanced,"Disconnect cloud on this device",()=>{
                if(PlayniteApi.Dialogs.ShowMessage("Disconnect this device from cloud sync? Local history stays available. Reconnect to the same vault to resume.","Disconnect cloud",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                SaveConfig(new SyncConfig{RcloneExe=config.RcloneExe,ConfigFile=config.ConfigFile,RemoteFolder=""});
            });
            Details(panel,"Advanced & privacy",advanced);
        }
        private async void ConnectProvider(){try{await ConnectProviderAsync();}catch(OperationCanceledException){status="Connection cancelled. Your previous connection was kept.";}catch(Exception ex){NotifyError(ex);}}
        private async System.Threading.Tasks.Task ConnectProviderAsync()
        {
            connectionCancelled=false;
            var exe=config.RcloneExe;if(String.IsNullOrWhiteSpace(exe)||!File.Exists(exe))exe=Ask("Choose your installed rclone.exe:",exe);if(exe==null)return;
            if(!Path.IsPathRooted(exe)||!File.Exists(exe)||!exe.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Choose an existing rclone.exe.");
            string provider=null;
            var providerWindow=Window("Choose cloud storage",620,620);var providerPanel=Page(providerWindow,"Choose your cloud","Your saves stay in your own storage. Browser sign-in or endpoint credentials are handled on this device.");
            foreach(var pair in new[]{new[]{"Google Drive","drive"},new[]{"Dropbox","dropbox"},new[]{"OneDrive","onedrive"},new[]{"WebDAV / Nextcloud","webdav"},new[]{"Cloudflare R2","s3"},new[]{"pCloud","pcloud"}}){var choice=pair;Button(providerPanel,choice[0],()=>{provider=choice[1];providerWindow.Close();});}
            Button(Footer(providerWindow),"Cancel",()=>providerWindow.Close());providerWindow.ShowDialog();
            if(provider==null)return;
            if(!new[]{"drive","dropbox","onedrive","webdav","s3","pcloud"}.Contains(provider))throw new ArgumentException("Unsupported provider.");
            Dictionary<string,string> initial=null;
            if(provider=="drive")
            {
                
                var picker=new Microsoft.Win32.OpenFileDialog{Title="Choose your Google Desktop OAuth client",Filter="Google client JSON|*.json",CheckFileExists=true};
                if(picker.ShowDialog(walkthroughWindow??accountWindow??PlayniteApi.Dialogs.GetCurrentAppWindow())!=true)return;
                var document=JsonSafety.Object(File.ReadAllText(picker.FileName));var client=document["installed"];
                if(client==null||String.IsNullOrWhiteSpace((string)client["client_id"])||String.IsNullOrWhiteSpace((string)client["client_secret"]))throw new InvalidDataException("Choose a Google Desktop app client JSON file.");
                initial=new Dictionary<string,string>{{"client_id",(string)client["client_id"]},{"client_secret",(string)client["client_secret"]}};
            }
            else if(provider=="webdav"||provider=="s3")
            {
                initial=new Dictionary<string,string>();
                Func<string,string,bool,string> question=(name,help,secret)=>{var value=AskCloudQuestion(new CloudQuestion{Name=name,Help=help,Secret=secret,Required=true});if(value==null)throw new OperationCanceledException();return secret?value:value.Trim();};
                if(provider=="webdav")
                {
                    var endpoint=question("WebDAV URL","Enter your HTTPS WebDAV endpoint, such as your Nextcloud remote.php/dav/files/user/ address.",false);CloudAccount.WebDavEndpoint(endpoint);
                    initial["url"]=endpoint;initial["vendor"]="other";initial["user"]=question("WebDAV username","Account username.",false);initial["pass"]=question("WebDAV password","Account password or app password. Stored only in your local Rclone configuration.",true);
                }
                else
                {
                    var endpoint=question("R2 endpoint","Full HTTPS endpoint from your Cloudflare account, ending in r2.cloudflarestorage.com.",false);Uri uri;if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0)throw new ArgumentException("Enter a complete HTTPS R2 endpoint without credentials in the URL.");
                    initial["provider"]="Cloudflare";initial["env_auth"]="false";initial["endpoint"]=endpoint;initial["access_key_id"]=question("R2 access key ID","Your bucket access key.",true);initial["secret_access_key"]=question("R2 secret access key","Stored only in your local Rclone configuration.",true);initial["no_check_bucket"]="true";
                }
            }
            var cfg=Path.Combine(Root,"personal-rclone-"+Guid.NewGuid().ToString("N").Substring(0,8)+".conf");
            var remote="personal";
            ReportStatus("Connecting storage. Browser providers may open sign-in. You can cancel this connection from setup.",SyncDisplayState.Working,"Connecting storage");
            var folder=provider=="s3"?Ask("Enter the R2 bucket and vault folder (bucket/PlayniteLink).","bucket/PlayniteLink"):SelectedVaultFolder();
            if(folder==null)throw new OperationCanceledException();
            try
            {
                await System.Threading.Tasks.Task.Run(()=>CloudAccount.Configure(exe,cfg,remote,provider,question=>UI(()=>AskCloudQuestion(question)),(message,action)=>{ReportStatus(message,SyncDisplayState.Working,"Connecting storage");action();},initial,()=>closing||connectionCancelled));
                SaveOnboarding();
                await SaveConnectionAsync(new SyncConfig {RcloneExe=exe,ConfigFile=cfg,RemoteFolder=remote+":"+folder});
            }
            finally{if(config.ConfigFile!=cfg&&File.Exists(cfg))File.Delete(cfg);}
        }
    }
}




