using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private SetupProfile setup;
        private DateTime nextSetupCapture=DateTime.MinValue;
        private bool setupRestartPending;
        private bool setupChecking;
        private TextBlock setupStatusText;
        private Button setupCheckButton;
        private KeyValuePair<string,SortedDictionary<string,string>>[] setupLocalCache=new KeyValuePair<string,SortedDictionary<string,string>>[0];
        private List<SetupChange> setupChanges=new List<SetupChange>();
        private string setupStatus="Add-on sync is off on this device.";
        private void InitializeSetup()
        {setup=new SetupProfile(Root,PlayniteApi.Paths.ConfigurationPath,blobs);var error=Path.Combine(setup.DirectoryPath,"apply-error.txt");if(File.Exists(error))setupStatus="The last add-on update was rolled back: "+File.ReadAllText(error);}
        private void SyncSetup()
        {
            if(setup==null||!setup.Options.Enabled)return;
            if(DateTime.UtcNow>=nextSetupCapture){setup.Capture(repository.Snapshot().Device);nextSetupCapture=DateTime.UtcNow.AddMinutes(5);}
            var cfg=config;var remote=new RcloneTransport(cfg.RcloneExe,cfg.ConfigFile,cfg.RemoteFolder,"playnite-link-v1/setup/objects",cancelled:()=>closing||transferCancelled);
            var incoming=Path.Combine(setup.DirectoryPath,"incoming");Directory.CreateDirectory(incoming);remote.Pull(incoming);setup.History.Import(incoming);setup.MergeEquivalent(repository.Snapshot().Device);
            var pending=setup.History.Read(setup.History.Objects).Where(p=>!File.Exists(Path.Combine(incoming,p.Key+".json"))).ToArray();
            if(pending.Length>0)
            {
                var transport=SetupBlobTransport();
                var committed=new HashSet<string>(setup.History.Read(incoming).Values.SelectMany(r=>SetupComponent.Files(r.Data).Values));
                transport.PushFiles(blobs.DirectoryPath,pending.SelectMany(p=>SetupComponent.Files(p.Value.Data).Values).Where(v=>!committed.Contains(v)).Select(v=>BlobReference.Parse(v).Hash+".blob"));
                remote.PushFiles(setup.History.Objects,pending.Select(p=>p.Key+".json"));
            }
            var changes=setup.Changes();setupChanges=changes;setupLocalCache=SetupPackageSnapshot();
            setupStatus=changes.Count==0?"Shared add-on installations are up to date.":changes.Count+" add-on changes are available. Review them before installing and restarting.";
            UI(()=>{
                if(changes.Count>0)PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.Setup",setupStatus+" Open Playnite Link → Add-ons.",NotificationType.Info));
                else PlayniteApi.Notifications.Remove("PlayniteLink.Setup");
            });
        }
        private RcloneTransport SetupBlobTransport()
        {return new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,()=>closing||transferCancelled);}
        private void TrySyncSetup()
        {
            try{SyncSetup();}
            catch(Exception ex){setupStatus="Add-on sync is waiting: "+ex.Message;logger.Error(ex,"Add-on synchronization deferred; game sync can continue.");}
        }
        private void ManageSetup(){OpenAccountPage("Add-ons");}
        private void RebuildSetupPage()
        {accountPages.Remove("Add-ons");if(accountWindow!=null&&accountSection=="Add-ons")ShowAccountPage("Add-ons");}
        private KeyValuePair<string,SortedDictionary<string,string>>[] SetupPackageSnapshot()
        {
            var known=setup.History.Read(setup.History.Objects).Values.GroupBy(r=>r.Game).ToDictionary(g=>g.Key,g=>g.First().Data);
            foreach(var pair in setup.State.Local)known[pair.Key]=pair.Value;
            return known.ToArray();
        }
        private bool UpdateSetupOptions(Action action)
        {
            if(!syncGate.Wait(0)){PlayniteApi.Dialogs.ShowMessage("Wait for the current sync before changing add-on sharing.","Playnite Link");return false;}
            try{action();setup.SaveOptions();nextSetupCapture=DateTime.MinValue;setupChanges=setup.Changes();setupLocalCache=SetupPackageSnapshot();return true;}
            finally{syncGate.Release();}
        }
        private async void RefreshSetup()
        {
            if(setupChecking)return;
            if(setup==null||!setup.Options.Enabled){setupStatus="Enable add-on sharing before checking for changes.";return;}
            if(String.IsNullOrWhiteSpace(config.RemoteFolder)){setupStatus="Connect your cloud storage on the Overview page first.";return;}
            setupChecking=true;
            setupStatus=syncing!=0?"Waiting for the current sync, then checking add-ons…":"Checking installed add-ons and your cloud…";
            if(setupStatusText!=null)setupStatusText.Text=setupStatus;
            if(setupCheckButton!=null){setupCheckButton.IsEnabled=false;setupCheckButton.Content="Checking…";}
            try{nextSetupCapture=DateTime.MinValue;await SyncPersonalAsync(true,null);if(setupStatus.StartsWith("Checking")||setupStatus.StartsWith("Waiting for the current"))setupStatus="Add-on check could not finish. "+status;}
            catch(Exception ex){setupStatus="Could not check add-ons: "+ex.Message;logger.Error(ex,"Manual add-on check failed.");}
            finally{setupChecking=false;RebuildSetupPage();}
        }
        private sealed class SetupChoice
        {
            public string Hash,Label;
            public override string ToString(){return Label;}
        }
        private void BuildSetup(Window window)
        {
            var panel=Page(window,"Extensions and themes","Optional, peer-to-peer installation sync through your own cloud. Each device keeps its active theme and all settings.","Add-ons");
            if(setup==null){Label(panel,"Add-on synchronization is unavailable. Check the extension log.");return;}
            var controls=Card(panel,"Sync on this device","");
            setupStatusText=Label(controls,setupStatus);
            var enabled=Toggle(controls,"Share extension and theme installations",setup.Options.Enabled);
            enabled.Click+=(_,e)=>{
                if(!UpdateSetupOptions(()=>setup.Options.Enabled=enabled.IsChecked==true)){enabled.IsChecked=setup.Options.Enabled;return;}
                setupStatus=setup.Options.Enabled?"Enabled. Check for changes to publish and discover add-ons.":"Add-on sync is off on this device.";
                RebuildSetupPage();
            };
            Label(controls,"This syncs add-on files only. Playnite settings, extension settings, sign-ins, launch mode and startup preferences stay on each device. Built-in add-ons and Playnite Link itself are excluded.",13,true);
            var actions=Actions(controls);setupCheckButton=Button(actions,setupChecking?"Checking…":"Check for changes",RefreshSetup);setupCheckButton.IsEnabled=setup.Options.Enabled&&!setupChecking;
            if(!setup.Options.Enabled){Label(panel,"Enable sharing to discover installed add-ons. Nothing is uploaded while this is off.");return;}
            var choices=new Dictionary<string,ComboBox>();
            foreach(var change in setupChanges)
            {
                var item=change;var sample=item.Heads[0].Data;
                var card=Card(panel,sample["Name"],sample["Target"]);
                var picker=new ComboBox{MinWidth=280,Margin=new Thickness(0,6,0,12)};
                StyleInput(picker);var itemStyle=new Style(typeof(ComboBoxItem));itemStyle.Setters.Add(new Setter(Control.BackgroundProperty,Surface));itemStyle.Setters.Add(new Setter(Control.ForegroundProperty,Ink));picker.ItemContainerStyle=itemStyle;
                foreach(var head in item.Heads)picker.Items.Add(new SetupChoice{Hash=head.Hash(),Label=(head.Data["Removed"]=="True"?"Remove installation":"Install "+head.Data["Version"])+" · device "+head.Device.Substring(0,8)});
                if(item.Heads.Count==1)picker.SelectedIndex=0;
                else Label(card,"Both devices changed this add-on. Choose the version to share, or leave it unresolved.",13,true);
                card.Children.Add(picker);choices[item.Key]=picker;
                Button(card,"Keep this add-on device-only",()=>{if(UpdateSetupOptions(()=>setup.Options.LocalOnly.Add(item.Key)))RebuildSetupPage();});
            }
            var local=setupLocalCache.OrderBy(p=>p.Value["Name"]).ToArray();
            var exclusions=new StackPanel();
            foreach(var pair in local)
            {
                var key=pair.Key;var toggle=Toggle(exclusions,pair.Value["Name"]+" — this device only",setup.Options.LocalOnly.Contains(key));
                toggle.Click+=(_,e)=>{if(!UpdateSetupOptions(()=>{if(toggle.IsChecked==true)setup.Options.LocalOnly.Add(key);else setup.Options.LocalOnly.Remove(key);})){toggle.IsChecked=setup.Options.LocalOnly.Contains(key);}};
            }
            Details(panel,"Device-only add-ons",exclusions);
            var footer=Actions(Footer(window));var apply=Button(footer,"Apply selected changes and restart",()=>ApplySetup(choices));StyleAction(apply,true);apply.IsEnabled=choices.Count>0&&syncing==0;
            Label(panel,"Changes are staged and verified first. Replaced installations are backed up; add-on settings and data are retained. Switching your active theme never changes another device's active theme.",13,true);
        }
        private async void ApplySetup(Dictionary<string,ComboBox> pickers)
        {
            if(setupRestartPending||!EnsureAccess())return;
            if(PlayniteApi.Database.Games.Any(g=>g.IsRunning||g.IsLaunching)){PlayniteApi.Dialogs.ShowMessage("Stop running games before applying add-on changes and restarting.","Playnite Link");return;}
            var selected=pickers.Where(p=>p.Value.SelectedItem is SetupChoice).ToDictionary(p=>p.Key,p=>((SetupChoice)p.Value.SelectedItem).Hash);
            if(selected.Count==0)return;
            if(PlayniteApi.Dialogs.ShowMessage("Apply "+selected.Count+" selected add-on changes and restart Playnite? Existing installations will be backed up. Settings and active themes stay local.","Apply add-ons",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            setupRestartPending=true;
            try
            {
                await Task.Run(async()=>{
                    await syncGate.WaitAsync();InterlockedSync(1);
                    try{if(!setup.Options.Enabled)throw new InvalidOperationException("Enable add-on sync first.");var transport=SetupBlobTransport();blobs.Fetch=names=>transport.PullFiles(blobs.DirectoryPath,names);setup.Stage(selected,repository.Snapshot().Device);}
                    finally{InterlockedSync(0);syncGate.Release();}
                });
                var script=Path.Combine(Path.GetDirectoryName(typeof(PlaytimePlugin).Assembly.Location),"SetupApply.ps1");if(!File.Exists(script))throw new FileNotFoundException("The setup helper is missing. Reinstall Playnite Link.");
                var process=Process.GetCurrentProcess();var exe=process.MainModule.FileName;
                var arguments="-NoProfile -ExecutionPolicy Bypass -File "+RcloneTransport.Quote(script)+" -Plan "+RcloneTransport.Quote(Path.Combine(setup.DirectoryPath,"pending.json"))+" -ParentId "+process.Id+" -RestartExe "+RcloneTransport.Quote(exe)+" -RestartProfile "+RcloneTransport.Quote(PlayniteApi.Paths.ConfigurationPath);
                if(Environment.GetCommandLineArgs().Contains("--masterinstance"))arguments+=" -RestartMaster";
                Process.Start(new ProcessStartInfo("powershell.exe",arguments){UseShellExecute=false,CreateNoWindow=true});
                Application.Current.Shutdown();
            }
            catch(Exception ex){setupRestartPending=false;NotifyError(ex);}
        }
        private void InterlockedSync(int value){System.Threading.Interlocked.Exchange(ref syncing,value);}
    }
}
