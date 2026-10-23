using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin : GenericPlugin
    {
        public override Guid Id { get { return new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"); } }
        private readonly ILogger logger = LogManager.GetLogger();
        private Repository repository;
        private SyncConfig config;
        private Timer timer;
        private readonly IdleSyncSchedule idleSchedule=new IdleSyncSchedule();
        private int queuedSync;
        private int syncing;
        private bool closing;
        private readonly HashSet<string> live = new HashSet<string>();
        private string status = "Not configured. Sessions are stored locally after game enrollment.";
        private CloudSettings settings;
        private string syncRoot;
        private string BaseRoot {get{return GetPluginUserDataPath();}}
        private string Root { get { return syncRoot??(syncRoot=VaultProfiles.Resolve(BaseRoot)); } }
        public PlaytimePlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            try{security=new LocalSecurity(BaseRoot);}catch(Exception ex){protectionError=ex.Message;logger.Error(ex,"Playnite protection could not be loaded; app remains locked.");}
            InitializeApplicationProtection();
            try
            {
                var fresh=!File.Exists(Path.Combine(Root,"state.json"));
                repository = new Repository(Root);
                InitializeOnboarding(fresh);
                var path = Path.Combine(Root, "rclone-settings.json");
                vault = new Vault(Root);
                savedConflicts=ReadSaveConflicts();
                blobs = new BlobStore(Path.Combine(Root,"blobs"));
                blobs.CancelRequested=()=>closing||TransferStopped;
                devices = new DeviceDirectory(Root);
                try{InitializeSetup();}catch(Exception ex){setup=null;setupStatus="Add-on synchronization is unavailable: "+ex.Message;logger.Error(ex,"Optional add-on state could not be loaded; game sync remains available.");}
                devicePreferences = File.Exists(Path.Combine(Root,"device-settings.json")) ? JsonSafety.Deserialize<DevicePreferences>(File.ReadAllText(Path.Combine(Root,"device-settings.json"))) : new DevicePreferences();
                if(devicePreferences==null)throw new InvalidDataException("Invalid local device settings.");devicePreferences.Validate();
                config = File.Exists(path) ? JsonSafety.Deserialize<SyncConfig>(File.ReadAllText(path)) : Defaults();
                if (config == null) throw new InvalidDataException("Invalid Rclone settings.");
                if(!String.IsNullOrWhiteSpace(config.RemoteFolder))status=onboarding.SyncPaused?"Sync is paused. Test your connection or continue setup when ready.":"Storage selected. Test the connection or sync when ready.";
                logger.Info("Playnite Link initialized; decentralized personal vault.");
            }
            catch (Exception ex) { if (repository != null) repository.Dispose(); repository = null; status = "Disabled: " + ex.Message; logger.Error(ex, status); }
        }
        private SyncConfig Defaults()
        {
            var result = new SyncConfig();
            var tools = Path.Combine(PlayniteApi.Paths.ExtensionsDataPath, "e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b", "Tools");
            if (File.Exists(Path.Combine(tools, "rclone.exe"))) result.RcloneExe = Path.Combine(tools, "rclone.exe");
            if (File.Exists(Path.Combine(tools, "rclone.conf"))) result.ConfigFile = Path.Combine(tools, "rclone.conf");
            return result;
        }
        private void SaveConfig(SyncConfig next)
        {
            if(!EnsureAccess())throw new OperationCanceledException("Cloud settings were not changed.");
            if(syncing!=0)throw new InvalidOperationException("Wait for the background sync before changing cloud settings.");
            ConnectionVerification proof=null;
            if(!String.IsNullOrWhiteSpace(next.RemoteFolder)&&ConnectionChanged(next))Network("Checking the selected cloud vault…",()=>proof=VerifyConnection(next));
            CommitConnection(next,proof);
        }
        public override ISettings GetSettings(bool firstRunSettings)
        {
            if (settings == null) settings = new CloudSettings(() => config ?? new SyncConfig(), SaveConfig,()=>Guard(ManageGames),()=>Guard(()=>RequestSync(true)));
            return settings;
        }
        public override UserControl GetSettingsView(bool firstRunView)
        {
            GetSettings(false);
            if(!IsApplicationLocked)return settings.View();
            var view=new UserControl();var panel=new StackPanel{Margin=new Thickness(20)};Text(panel,"Playnite is locked");
            var button=new Button{Content="Unlock settings",Padding=new Thickness(14,9,14,9)};button.Click+=(_,e)=>{if(EnsureAccess())view.Content=settings.View();};panel.Children.Add(button);view.Content=panel;return view;
        }
        private void Guard(Action action)
        {
            try { if (repository == null) throw new InvalidOperationException(status); if(!EnsureAccess())return;action(); }
            catch (Exception ex) { status = ex.Message; logger.Error(ex, "Playnite Link action failed"); PlayniteApi.Dialogs.ShowErrorMessage(status, "Playnite Link"); }
        }
        private void NotifyError(Exception ex)
        {
            if(ex is CloudTransferException){ReportStatus(ex.Message,SyncDisplayState.Waiting,"Waiting for cloud");logger.Warn("Cloud retry pending: "+ex.Message);return;}
            ReportStatus(ex.Message,SyncDisplayState.Failed,"Needs attention"); logger.Error(ex, "Playnite Link sync/tracking error");
            PlayniteApi.Notifications.Add(new NotificationMessage("PlaytimeRclone.Error", status, NotificationType.Error));
        }
        private string Ask(string text, string value)
        {
            var result = PlayniteApi.Dialogs.SelectString(text, "Playnite Link", value);
            return result.Result ? result.SelectedString : null;
        }
        private void Configure()
        {
            var window=Window("Playnite Link — storage connection",760,760);var panel=Page(window,"Connect existing storage","Use a cloud account already authorized in Rclone.");
            var location=Card(panel,"Your vault","Enter the same remote folder on every device. Example: gdrive:PlayniteLink");
            var folder=EntryField(location,"Remote and folder",config.RemoteFolder);
            var tools=Card(panel,"Rclone on this device");var exe=EntryField(tools,"Rclone executable",config.RcloneExe);
            Button(tools,"Browse for rclone.exe",()=>{var picker=new Microsoft.Win32.OpenFileDialog{Title="Choose rclone.exe",Filter="Rclone executable|rclone.exe",CheckFileExists=true};if(picker.ShowDialog(window)==true)exe.Text=picker.FileName;});
            var cfg=EntryField(tools,"Configuration file (optional)",config.ConfigFile);
            Button(tools,"Browse for configuration",()=>{var picker=new Microsoft.Win32.OpenFileDialog{Title="Choose Rclone configuration",Filter="Rclone configuration|*.conf|All files|*.*",CheckFileExists=true};if(picker.ShowDialog(window)==true)cfg.Text=picker.FileName;});
            Label(tools,"Leave the configuration blank to use Rclone's default. Existing credentials stay in that file.",13,true);
            var feedback=Label(panel,"",13);var actions=Actions(Footer(window));var save=Button(actions,"Save connection",()=>{
                try{var next=new SyncConfig{RcloneExe=exe.Text.Trim(),RemoteFolder=folder.Text.Trim(),ConfigFile=cfg.Text.Trim()};if(String.IsNullOrWhiteSpace(next.RemoteFolder))throw new ArgumentException("Enter a remote and folder. Use Overview to disconnect instead.");next.Validate();SaveConfig(next);window.Close();}
                catch(Exception ex){feedback.Text=ex.Message;}
            });StyleAction(save,true);Button(actions,"Cancel",()=>window.Close());window.ShowDialog();
        }
        private static TextBlock Text(Panel panel, string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8),FontSize=14 };
            block.Foreground=Ink;
            if(panel.Children.Count==0){block.FontSize=22;block.FontWeight=FontWeights.SemiBold;}
            panel.Children.Add(block);return block;
        }
        private void LiveStatus(Panel panel)
        {
            SyncStatusView(panel);
        }
        private Button Button(Panel panel, string title, Action action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left };
            StyleAction(button);button.Click += (_, e) => Guard(action); panel.Children.Add(button);return button;
        }
        private void ManageGames()
        {
            var window=Window("Playnite Link — manage games",680,620);
            var panel=Page(window,"Manage games","Link a game once. Its progress syncs automatically.");
            var search=EntryField(panel,"Find a game","");
            var games=PlayniteApi.Database.Games.OrderBy(g=>g.Name).ToArray();
            var list=new ListBox{ItemsSource=games,DisplayMemberPath="Name",Height=300};
            StyleInput(list);panel.Children.Add(list);
            search.TextChanged+=(_,e)=>list.ItemsSource=games.Where(g=>(g.Name??"").IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();
            Action open=()=>{var game=list.SelectedItem as Game;if(game==null)return;window.Close();Manage(game);};
            Button(panel,"Open game",open);Button(panel,"Close",()=>window.Close());
            list.MouseDoubleClick+=(_,e)=>open();
            window.ShowDialog();
        }
        private void Manage(Game game)
        {
            if(!repository.Snapshot().Mappings.ContainsKey(game.Id.ToString())){Enroll(game);return;}
            var window=Window("Playnite Link — "+game.Name,700,640);
            var panel=Page(window,game.Name,"");
            var snapshot=repository.Snapshot();var map=snapshot.Mappings[game.Id.ToString()];var link=Link(map.Key);
            var summary=Card(panel,"Linked to your cloud",GameLabel(map.Key,game.Name));
            Label(summary,"Playtime and sessions included.",14,true);
            var primaryActions=Actions(summary);
            StyleAction(Button(primaryActions,"Save manager…",()=>ShowSaveManager(game,map.Key)),true);
            Button(primaryActions,"Sessions & device sync status…",()=>ShowGameSyncHistory(game));
            var saves=Card(panel,"Save files",link.HasSaves?"":"Choose save paths to include saves.");
            var folderText=Label(saves,!link.HasSaves?"Save files aren't included yet.":link.Sources!=null&&link.Sources.Count>0?String.Join("\n",link.Sources.Select(s=>s.Path)):link.Folder,13,true);
            var choose=Button(saves,link.HasSaves?(String.IsNullOrWhiteSpace(link.Folder)?"Save paths & options…":"View save folder"):"Include save files…",()=>{});
            choose.Click+=(_,e)=>Guard(()=>{if(link.HasSaves){if(String.IsNullOrWhiteSpace(link.Folder)){ShowSavePaths(game,map.Key);return;}System.Diagnostics.Process.Start(Plan(game,map.Key).Sources[0].Path);return;}var folder=PlayniteApi.Dialogs.SelectFolder();if(String.IsNullOrWhiteSpace(folder))return;SetSaveFolder(game,folder);if(link.HasSaves){folderText.Text=link.Folder;choose.Content="View save folder";QueueSync();}});
            if(CachedSaveConflict(game,map))savedConflicts.Add(map.Key);
            var resolve=Button(saves,"Choose saves…",()=>RequestSync(true));
            Action updateChoice=()=>{resolve.Visibility=savedConflicts.Contains(map.Key)?Visibility.Visible:Visibility.Collapsed;resolve.IsEnabled=syncing==0;};
            var choiceTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};choiceTimer.Tick+=(_,e)=>updateChoice();window.Closed+=(_,e)=>choiceTimer.Stop();updateChoice();choiceTimer.Start();
            LiveStatus(panel);
            if(IsSyncPaused)Button(panel,"Resume syncing",()=>RequestSync(true));
            var more=new StackPanel();
            Button(more,"Session history",()=>ShowSessionHistory(game));
            Button(more,"Save history",()=>ShowHistory(game,map.Key));
            Button(more,"Check for changes",()=>RequestSync(true));
            var repairs=new StackPanel();
            Label(repairs,"Use these only when history needs repair. Your cloud history is preserved.",13,true);
            Button(repairs,"Review an interrupted session",()=>Recover(game));
            Button(repairs,"Restore playtime from cloud history",()=>Resolve(game));
            Label(repairs,"Cloud identity: "+map.Source+"\nOriginal playtime on this device: "+map.Original+" seconds",12,true);
            Details(more,"Troubleshooting",repairs);
            Button(more,"Unlink this game",()=>{if(game.IsRunning||game.IsLaunching)throw new InvalidOperationException("Stop the game first.");if(PlayniteApi.Dialogs.ShowMessage("Stop syncing this game on this device? Your cloud history, local saves and displayed playtime will be kept.","Playnite Link",MessageBoxButton.YesNo)==MessageBoxResult.Yes){repository.Unenroll(game.Id.ToString());window.Close();QueueSync();}});
            Details(panel,"History and options",more);
            Button(Footer(window),"Done",()=>window.Close());window.ShowDialog();
        }
        private string Source(Game game)
        {
            return Identity.Provider(game.PluginId, game.GameId);
        }
        private void CheckIdentity(Game game, Mapping map)
        {
            if (!map.Source.StartsWith("manual:", StringComparison.Ordinal) && Source(game) != map.Source)
                throw new InvalidOperationException("Provider identity changed for " + game.Name + ". Review the mapping before continuing.");
        }
        private int projectionFailures;
        private void Project() { if(closing||repository==null)return;ProjectMappings(repository.Snapshot().Mappings); }
        private void ProjectMappings(IEnumerable<KeyValuePair<string,Mapping>> mappings)
        {
            if (closing || repository == null) return;
            foreach (var pair in mappings)
            {
                var game = PlayniteApi.Database.Games.Get(new Guid(pair.Key));
                if (game == null || game.IsRunning || game.IsLaunching) continue;
                try
                {
                    CheckIdentity(game, pair.Value);
                    repository.EnsureBaselineCount(pair.Key,game.PlayCount);
                    if(game.Playtime!=pair.Value.Expected&&game.Playtime!=pair.Value.Intended)throw new InvalidOperationException("Playtime changed outside Playnite Link. Review the edit before activity history is updated.");
                    // Optional displays cannot veto Link's native ledger projection.
                    try{ProjectActivity(game);}catch(Exception ex){logger.Warn(ex,"Optional session display deferred; native history is retained.");}
                    var oldTime=game.Playtime;var oldCount=game.PlayCount;var oldDate=game.LastActivity;
                    repository.AcceptProjection(pair.Key, game.Playtime, value => {game.Playtime = value;});
                    SetActivityCounters(game);
                    if(oldTime!=game.Playtime||oldCount!=game.PlayCount||oldDate!=game.LastActivity)PlayniteApi.Database.Games.Update(game);
                    RefreshActivityDisplays(game);
                }
                catch (Exception ex) { projectionFailures++; NotifyError(ex); }
            }
        }
        private async System.Threading.Tasks.Task ProjectAsync()
        {
            var mappings=UI(()=>repository.Snapshot().Mappings.ToArray());projectionFailures=0;
            for(var i=0;i<mappings.Length;i+=4){CheckTransfer();var batch=mappings.Skip(i).Take(4).ToArray();ReportStatus("Updating playtime and session displays. Your existing records are preserved.",SyncDisplayState.Working,"Updating your library",Math.Min(i+4,mappings.Length),mappings.Length);UI(()=>ProjectMappings(batch));await System.Threading.Tasks.Task.Delay(1);}
        }
        private void QueueSync() { QueuePersonalSync(); }
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if(applicationLock!=null)applicationLock.Enforce();
            if (repository == null) return;
            CheckInstalledSaves();
            CompleteMigrationRestart();
            AdoptExistingSaveBackups();
            if(File.Exists(Path.Combine(MigrationDirectory,"retirement-result.json"))&&(bool?)JsonSafety.Object(File.ReadAllText(Path.Combine(MigrationDirectory,"retirement-result.json")))["Removed"]==true)ProtectRetiredSaveManager();
            var retirement=Path.Combine(MigrationDirectory,"retirement-result.json");
            if(File.Exists(retirement)){var outcome=JsonSafety.Object(File.ReadAllText(retirement));if((bool?)outcome["Removed"]==false)PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.Retirement","Save Manager removal did not finish: "+(string)outcome["Error"],NotificationType.Error,()=>Guard(ShowSaveBackups)));}
            if(setup!=null&&setup.RecoveryRequired)PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.SetupRecovery","An interrupted add-on installation needs recovery. Open Playnite Link → Add-ons → Recover interrupted installation and restart.",NotificationType.Error));
            if (repository.Snapshot().Active.Count > 0)
                PlayniteApi.Notifications.Add(new NotificationMessage("PlaytimeRclone.Pending", "Unfinished playtime sessions need review. Use the game's Playnite Link recovery menu.", NotificationType.Error));
            timer = new Timer(_ => CheckIdleSync(), null, Timeout.Infinite, Timeout.Infinite);
            if(!onboarding.SetupComplete)PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(()=>Guard(()=>ShowWalkthrough(false))));
            else {status=onboarding.SyncPaused?"Sync paused. Choose Resume sync when you are ready.":status;if(ManagerOptions.SyncOnStartup)QueueSync();else ScheduleIdle(true);}
        }
        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if(!PlayniteApi.MainView.UIDispatcher.CheckAccess()){UI(()=>OnGameStarted(args));return;}
            launchReservations.Remove(args.Game.Id);
            try
            {
                if (repository == null) return;
                var snapshot = repository.Snapshot(); Mapping map;
                if (!snapshot.Mappings.TryGetValue(args.Game.Id.ToString(), out map)){logger.Info("Playnite Link session not tracked: game is not linked on this device.");return;}
                CheckIdentity(args.Game, map);
                if (repository.Start(args.Game.Id.ToString(), args.Game.Playtime)){live.Add(args.Game.Id.ToString());logger.Info("Playnite Link session started; cloud game key: "+map.Key);}
                QueueSync();
            }
            catch (Exception ex) { NotifyError(ex); }
        }
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            if(!PlayniteApi.MainView.UIDispatcher.CheckAccess()){UI(()=>OnGameStarting(args));return;}
            // This precedes enrollment and one-shot cloud launch permissions: all games
            // are blocked, including IPC/shortcut requests for unlinked library entries.
            if(setupRestartPending){args.CancelStartup=true;return;}
            if(IsApplicationLocked){permittedLaunches.Remove(args.Game.Id);args.CancelStartup=true;if(applicationLock!=null)applicationLock.Enforce();return;}
            if(repository==null)return;
            Mapping mapping;if(!repository.Snapshot().Mappings.TryGetValue(args.Game.Id.ToString(),out mapping))return;
            if(repository.Snapshot().Active.ContainsKey(args.Game.Id.ToString()))
            {args.CancelStartup=true;NotifyError(new InvalidOperationException("Recover the unfinished session before launching again."));return;}
            if(restoringSaves.Contains(args.Game.Id)){args.CancelStartup=true;LaunchAfterRestore(args.Game);return;}
            try{SaveLink saveLink;if(vault.State.Links.TryGetValue(mapping.Key,out saveLink)&&saveLink.HasSaves)Plan(args.Game,mapping.Key).Recover();}
            catch(Exception ex){args.CancelStartup=true;NotifyError(ex);return;}
            SaveLink launchLink;vault.State.Links.TryGetValue(mapping.Key,out launchLink);
            if(!permittedLaunches.Remove(args.Game.Id)&&(CachedSaveConflict(args.Game,mapping)||(launchLink!=null&&launchLink.HasSaves&&launchLink.CheckBeforeLaunch))){args.CancelStartup=true;LaunchAfterSync(args.Game);return;}
            launchReservations.Add(args.Game.Id);ReleaseLaunchReservation(args.Game.Id);
            // Launch local saves immediately. Cloud discovery and uploads run in
            // the background; they cannot replace files during this reservation.
        }
        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if(!PlayniteApi.MainView.UIDispatcher.CheckAccess()){UI(()=>OnGameStopped(args));return;}
            UI(()=>launchReservations.Remove(args.Game.Id));
            try
            {
                if (repository == null) return;
                // The durable session is authoritative, including after a callback
                // ordering change. A volatile in-memory set must not discard it.
                if (!repository.Snapshot().Active.ContainsKey(args.Game.Id.ToString())) return;
                if (repository.Stop(args.Game.Id.ToString(), args.ElapsedSeconds))
                {
                    live.Remove(args.Game.Id.ToString());
                    logger.Info("Playnite Link recorded session: " + args.ElapsedSeconds + " seconds.");
                    // Playnite/add-ons may still hold their stop-event locks here.
                    // Projection runs on the UI dispatcher after native processing,
                    // never from inside the native game-stop callback.
                    BackupThenSync(args.Game);
                    RetryActivityAfterStop();
                }
            }
            catch (Exception ex) { NotifyError(ex); }
        }
        private void Recover(Game game)
        {
            if (game.IsRunning || game.IsLaunching) throw new InvalidOperationException("Stop the game before recovery.");
            var local = game.Id.ToString();
            if (!repository.Snapshot().Active.ContainsKey(local)) throw new InvalidOperationException("No unfinished session for this game.");
            var answer = Ask("Playnite stopped before a session was recorded. Enter verified session seconds, or 0 to discard it. Wall-clock time is not inferred because sleep/crash time is uncertain. The merged ledger will replace the current displayed total.", "0");
            if (answer == null) return;
            ulong seconds;
            if (!UInt64.TryParse(answer, out seconds)) throw new ArgumentException("Use whole nonnegative seconds.");
            repository.Stop(local, seconds);
            repository.AcknowledgeLocalValue(local, game.Playtime);
            Project(); QueueSync();
        }
        private void Resolve(Game game)
        {
            if (game.IsRunning || game.IsLaunching) throw new InvalidOperationException("Stop the game first.");
            var local = game.Id.ToString(); var snapshot = repository.Snapshot();
            if (!snapshot.Mappings.ContainsKey(local)) throw new InvalidOperationException("Game is not enrolled.");
            var total = new Ledger(snapshot.Entries).Total(snapshot.Mappings[local].Key);
            if (PlayniteApi.Dialogs.ShowMessage("Replace the current displayed playtime (" + game.Playtime + " seconds) with the cached merged ledger total (" + total + " seconds)? External edits are discarded. Sync successfully first to include all remote sessions.", "Playnite Link", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            repository.AcknowledgeLocalValue(local, game.Playtime); Project();
        }
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new[] {
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Personal cloud account & settings", Action = _ => Guard(PersonalAccount) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Setup walkthrough", Action = _ => Guard(ShowWalkthrough) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Sync personal vault now", Action = _ => Guard(() => RequestSync(true)) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Add-ons", Action = _ => Guard(ManageSetup) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Connected devices", Action = _ => Guard(ManageDevices) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Save backups", Action = _ => Guard(ShowSaveBackups) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Export sync diagnostics", Action = _ => Guard(ExportSyncDiagnostics) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Passcode", Action = _ => Guard(ManageSecurity) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Lock Playnite now", Action = _ => {if(security!=null&&!security.Enabled)ManageSecurity();else LockApplication();} },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Show sync status", Action = _ => PlayniteApi.Dialogs.ShowMessage(status, "Playnite Link") }
            };
        }
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (repository == null || args.Games == null || args.Games.Count == 0) return new GameMenuItem[0];
            if(args.Games.Count>1)return new[]{new GameMenuItem{MenuSection="Playnite Link",Description="Quick backup selected games",Action=_=>Guard(()=>QuickBackup(args.Games.ToArray()))}};
            var game = args.Games[0];
            var linked=repository.Snapshot().Mappings.ContainsKey(game.Id.ToString());
            var items=new List<GameMenuItem>{new GameMenuItem { MenuSection = "Playnite Link", Description = linked?"Cloud sync…":"Link game…", Action = _ => Guard(() => Manage(game)) }};
            if(linked){var key=repository.Snapshot().Mappings[game.Id.ToString()].Key;
                items.Add(new GameMenuItem{MenuSection="Playnite Link",Description="Save manager…",Action=_=>Guard(()=>ShowSaveManager(game,key))});
                items.Add(new GameMenuItem{MenuSection="Playnite Link",Description="Quick backup",Action=_=>Guard(()=>QuickBackup(new[]{game}))});
                items.Add(new GameMenuItem{MenuSection="Playnite Link",Description="Sessions & device sync status…",Action=_=>Guard(()=>ShowGameSyncHistory(game))});
                items.Add(new GameMenuItem{MenuSection="Playnite Link",Description="Achievements…",Action=_=>Guard(()=>ShowAchievements(game))});
                if(syncing==0)foreach(var backup in Backups.GetBackups(game.Id).OrderByDescending(b=>b.CreatedAt).Take(9)){var captured=backup;items.Add(new GameMenuItem{MenuSection="Playnite Link|Restore backup",Description=backup.CreatedAt.ToString("g")+" · "+backup.Description,Action=_=>QuickRestore(game,key,captured)});}}
            return items;
        }
        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            closing = true; if (timer != null) timer.Dispose();
            if(applicationLock!=null)applicationLock.Dispose();
            Microsoft.Win32.SystemEvents.SessionSwitch-=WindowsSessionChanged;
            // Sessions are already durable. Shutdown never waits for network or guesses an active duration.
        }
    }
}



