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
        private string Root { get { return GetPluginUserDataPath(); } }
        public PlaytimePlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            try{security=new LocalSecurity(Root);}catch(Exception ex){protectionError=ex.Message;logger.Error(ex,"Playnite protection could not be loaded; app remains locked.");}
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
                blobs.CancelRequested=()=>closing||transferCancelled;
                devices = new DeviceDirectory(Root);
                try{InitializeSetup();}catch(Exception ex){setup=null;setupStatus="Add-on synchronization is unavailable: "+ex.Message;logger.Error(ex,"Optional add-on state could not be loaded; game sync remains available.");}
                devicePreferences = File.Exists(Path.Combine(Root,"device-settings.json")) ? JsonConvert.DeserializeObject<DevicePreferences>(File.ReadAllText(Path.Combine(Root,"device-settings.json"))) : new DevicePreferences();
                if(devicePreferences==null)throw new InvalidDataException("Invalid local device settings.");devicePreferences.Validate();
                config = File.Exists(path) ? JsonConvert.DeserializeObject<SyncConfig>(File.ReadAllText(path)) : Defaults();
                if (config == null) throw new InvalidDataException("Invalid Rclone settings.");
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
            if (!String.IsNullOrWhiteSpace(next.RemoteFolder)) next.Validate();
            if (!String.IsNullOrWhiteSpace(next.RemoteFolder)) vault.Pin(next.RemoteFolder);
            Atomic.Write(Path.Combine(Root, "rclone-settings.json"), JsonConvert.SerializeObject(next, Formatting.Indented));
            config = next; RefreshOverviewPage();QueueSync();
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
            status = ex.Message; logger.Error(ex, "Playnite Link sync/tracking error");
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
            var label=Text(panel,status);var updates=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
            updates.Tick+=(_,e)=>label.Text=status;label.Loaded+=(_,e)=>updates.Start();label.Unloaded+=(_,e)=>updates.Stop();
        }
        private Button Button(Panel panel, string title, Action action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left };
            StyleAction(button);button.Click += (_, e) => Guard(action); panel.Children.Add(button);return button;
        }
        private void ManageGames()
        {
            var window=Window("Playnite Link — manage games",680,620);
            var panel=Page(window,"Manage games","Choose a game to manage its saves, playtime and preferences.");
            var search=EntryField(panel,"Find a game","");
            var games=PlayniteApi.Database.Games.OrderBy(g=>g.Name).ToArray();
            var list=new ListBox{ItemsSource=games,DisplayMemberPath="Name",Height=300};
            StyleInput(list);panel.Children.Add(list);
            search.TextChanged+=(_,e)=>list.ItemsSource=games.Where(g=>(g.Name??"").IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();
            Action open=()=>{var game=list.SelectedItem as Game;if(game==null)return;window.Close();Manage(game);};
            Button(panel,"Manage sync",open);Button(panel,"Close",()=>window.Close());
            list.MouseDoubleClick+=(_,e)=>open();
            window.ShowDialog();
        }
        private void Manage(Game game)
        {
            var window = Window("Manage sync — "+game.Name,700,610);
            var panel = new StackPanel { Margin = new Thickness(22) };
            Action refresh=null;refresh = () => {
                panel.Children.Clear();
                Text(panel, "Your linked games sync automatically. Manage playtime here; use Save sync & preferences to choose save folders.");
                var snapshot = repository.Snapshot(); Mapping map;
                if (snapshot.Mappings.TryGetValue(game.Id.ToString(), out map))
                {
                    Button(panel,"Save sync & preferences",()=>{window.Close();ManageSaves(game);});
                    Button(panel,"Session history",()=>ShowSessionHistory(game));
                    Text(panel, "Linked identity: " + map.Source + "\nMerged playtime: " + new Ledger(snapshot.Entries).Total(map.Key) + " seconds\nOriginal local playtime at enrollment: " + map.Original + " seconds");
                    var rows = snapshot.Entries.Where(x => x.Game == map.Key).OrderByDescending(x => x.EndedUtc)
                        .Select(x => x.Kind + "    " + x.Seconds + " seconds    " + x.EndedUtc).ToArray();
                    panel.Children.Add(new ListBox { ItemsSource = rows, Height = 150,Background=Surface,Foreground=Ink,BorderBrush=Line });
                    Button(panel, "Sync now", QueueSync);
                    Button(panel, "Recover unfinished session", () => Recover(game));
                    Button(panel, "Resolve local edit using ledger", () => Resolve(game));
                    Button(panel, "Remove this game from playtime sync", () => {
                        if (game.IsRunning || game.IsLaunching) throw new InvalidOperationException("Stop the game first.");
                        if (PlayniteApi.Dialogs.ShowMessage("Stop tracking this local entry? The shared history and displayed playtime are retained. Re-adding requires reviewing the history baseline again.", "Playnite Link", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        { repository.Unenroll(game.Id.ToString()); refresh(); }
                    });
                }
                else
                {
                    Text(panel, "This game is not added. Add it on the first device, then link its manually added copy on the other device with the same shared key. For Dispatch, use manual:dispatch.");
                    Button(panel, "Add / link this game", () => { Enroll(game);refresh(); });
                }
                LiveStatus(panel);
                Button(panel, "Configure cloud storage", Configure);
                Button(panel, "Close",()=>window.Close());
            };
            refresh(); window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; window.ShowDialog();
        }
        private string Source(Game game)
        {
            return Identity.Provider(game.PluginId, game.GameId);
        }
        private void Enroll(Game game)
        {
            if(syncing!=0)throw new InvalidOperationException("Wait for the background sync before linking a game.");
            if (game.IsRunning || game.IsLaunching) throw new InvalidOperationException("Stop the game before enrollment.");
            string suggested;
            try { suggested = Source(game); } catch { suggested = ""; }
            var source = Ask("Choose a shared link name for " + game.Name + ", such as dispatch. Enter the same link name on both devices. Only this game is added; matching game titles are never linked automatically.", suggested);
            if (source == null) return;
            if (source.StartsWith("manual:", StringComparison.Ordinal)) source = Identity.Manual(source.Substring(7));
            else if (!source.StartsWith("provider:", StringComparison.Ordinal)) source = Identity.Manual(source);
            else if (source != Source(game)) throw new ArgumentException("Keep the exact provider identity or enter a shared link name.");
            var baseline = Ask("Enter the SHARED pre-extension history in whole seconds. This is a reviewed history baseline, not a new session. Baselines from devices use MAX, never SUM. Exclude sessions already captured by this extension on either device. For disjoint old histories, enter their reviewed combined total on one device. Current local seconds: " + game.Playtime, game.Playtime.ToString());
            if (baseline == null) return;
            ulong seconds;
            if (!UInt64.TryParse(baseline, out seconds)) throw new ArgumentException("Use whole nonnegative seconds.");
            repository.Enroll(game.Id.ToString(), source, game.Playtime, seconds,game.PlayCount);
            status = "Enrolled " + game.Name + ". Original playtime is retained in local state.json. Sync pending.";
            QueueSync();
        }
        private void CheckIdentity(Game game, Mapping map)
        {
            if (!map.Source.StartsWith("manual:", StringComparison.Ordinal) && Source(game) != map.Source)
                throw new InvalidOperationException("Provider identity changed for " + game.Name + ". Review the mapping before continuing.");
        }
        private void Project()
        {
            if (closing || repository == null) return;
            foreach (var pair in repository.Snapshot().Mappings)
            {
                var game = PlayniteApi.Database.Games.Get(new Guid(pair.Key));
                if (game == null || game.IsRunning || game.IsLaunching) continue;
                try
                {
                    CheckIdentity(game, pair.Value);
                    repository.EnsureBaselineCount(pair.Key,game.PlayCount);
                    if(game.Playtime!=pair.Value.Expected&&game.Playtime!=pair.Value.Intended)throw new InvalidOperationException("Playtime changed outside Playnite Link. Review the edit before activity history is updated.");
                    if(!ProjectActivity(game))continue;
                    var oldTime=game.Playtime;var oldCount=game.PlayCount;var oldDate=game.LastActivity;
                    repository.AcceptProjection(pair.Key, game.Playtime, value => {game.Playtime = value;});
                    SetActivityCounters(game);
                    if(oldTime!=game.Playtime||oldCount!=game.PlayCount||oldDate!=game.LastActivity)PlayniteApi.Database.Games.Update(game);
                    RefreshActivityDisplays(game);
                }
                catch (Exception ex) { NotifyError(ex); }
            }
        }
        private void QueueSync() { QueuePersonalSync(); }
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if(applicationLock!=null)applicationLock.Enforce();
            if (repository == null) return;
            if (repository.Snapshot().Active.Count > 0)
                PlayniteApi.Notifications.Add(new NotificationMessage("PlaytimeRclone.Pending", "Unfinished playtime sessions need review. Use the game's Playnite Link recovery menu.", NotificationType.Error));
            timer = new Timer(_ => CheckIdleSync(), null, Timeout.Infinite, Timeout.Infinite);
            QueueSync();
        }
        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if(!PlayniteApi.MainView.UIDispatcher.CheckAccess()){UI(()=>OnGameStarted(args));return;}
            launchReservations.Remove(args.Game.Id);
            try
            {
                if (repository == null) return;
                var snapshot = repository.Snapshot(); Mapping map;
                if (!snapshot.Mappings.TryGetValue(args.Game.Id.ToString(), out map)) return;
                CheckIdentity(args.Game, map);
                if (repository.Start(args.Game.Id.ToString(), args.Game.Playtime)) live.Add(args.Game.Id.ToString());
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
            if(!permittedLaunches.Remove(args.Game.Id)&&CachedSaveConflict(args.Game,mapping)){args.CancelStartup=true;LaunchAfterSync(args.Game);return;}
            launchReservations.Add(args.Game.Id);ReleaseLaunchReservation(args.Game.Id);
            // Launch local saves immediately. Cloud discovery and uploads run in
            // the background; they cannot replace files during this reservation.
        }
        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            UI(()=>launchReservations.Remove(args.Game.Id));
            try
            {
                if (repository == null) return;
                if (!live.Contains(args.Game.Id.ToString())) return;
                if (repository.Stop(args.Game.Id.ToString(), args.ElapsedSeconds))
                {
                    live.Remove(args.Game.Id.ToString());
                    logger.Info("Playnite Link recorded session: " + args.ElapsedSeconds + " seconds.");
                    // Playnite/add-ons may still hold their stop-event locks here.
                    // Projection runs on the UI dispatcher after native processing,
                    // never from inside the native game-stop callback.
                    QueueSync();
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
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Sync personal vault now", Action = _ => Guard(() => RequestSync(true)) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Add-ons", Action = _ => Guard(ManageSetup) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Connected devices", Action = _ => Guard(ManageDevices) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Passcode", Action = _ => Guard(ManageSecurity) },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Lock Playnite now", Action = _ => {if(security!=null&&!security.Enabled)ManageSecurity();else LockApplication();} },
                new MainMenuItem { MenuSection = "@Playnite Link", Description = "Show sync status", Action = _ => PlayniteApi.Dialogs.ShowMessage(status, "Playnite Link") }
            };
        }
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games == null || args.Games.Count != 1) return new GameMenuItem[0];
            var game = args.Games[0];
            return new[] {
                new GameMenuItem { MenuSection = "Playnite Link", Description = "Manage save sync & preferences", Action = _ => Guard(() => ManageSaves(game)) },
                new GameMenuItem { MenuSection = "Playnite Link", Description = "Manage sync", Action = _ => Guard(() => Manage(game)) },
                new GameMenuItem { MenuSection = "Playnite Link", Description = "Enable and migrate playtime", Action = _ => Guard(() => Enroll(game)) },
                new GameMenuItem { MenuSection = "Playnite Link", Description = "Recover unfinished session", Action = _ => Guard(() => Recover(game)) },
                new GameMenuItem { MenuSection = "Playnite Link", Description = "Resolve local edit using ledger", Action = _ => Guard(() => Resolve(game)) }
            };
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



