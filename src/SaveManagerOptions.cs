using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlaytimeRclone
{
    public sealed class SaveManagerOptions
    {
        public bool ShowSidebar=true,SyncOnStartup=true;
        public bool LegacySettingsImported;
        public string BackupRoot;
        public Dictionary<string,bool> Installed=new Dictionary<string,bool>();
        public static void ApplyLegacy(SaveLink link,JObject settings,string configId)
        {
            link.AutoBackup=(bool?)settings["AutoBackupOnGameExit"]??false;
            link.NotifyBackup=(bool?)settings["ShowAutoBackupNotification"]??true;
            link.ConfirmBeforeBackup=(bool?)settings["ConfirmBeforeBackup"]??true;
            link.MaxAutoBackups=(int?)settings["MaxAutoBackupCount"]??0;
            link.CheckBeforeLaunch=((bool?)settings["SyncConfigOnGameStart"]??false)||((bool?)settings["RealtimeSyncEnabled"]??true);
            link.RestoreOnInstall=(bool?)settings["RestoreOnGameReinstall"]??true;
            link.BackupRoot=(settings["GameBackupPaths"]==null?null:(string)settings["GameBackupPaths"][configId])??(string)settings["CustomBackupPath"];
        }
    }
    public sealed partial class PlaytimePlugin
    {
        SaveManagerOptions managerOptions;
        SaveManagerOptions ManagerOptions
        {get{if(managerOptions==null){var path=Path.Combine(Root,"save-manager-options.json");managerOptions=File.Exists(path)?JsonSafety.Deserialize<SaveManagerOptions>(File.ReadAllText(path)):new SaveManagerOptions();if(managerOptions==null||managerOptions.Installed==null)throw new IOException("Invalid save manager settings.");}return managerOptions;}}
        void PersistManagerOptions(){Atomic.Write(Path.Combine(Root,"save-manager-options.json"),JsonConvert.SerializeObject(ManagerOptions,Formatting.Indented));}
        void RelocateBackupDirectory(Guid configId,string root)
        {
            var from=Backups.GetGameBackupDirectory(configId);var to=Backups.GetGameBackupDirectoryAtRoot(configId,root);
            if(String.Equals(from,to,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(from))return;
            SaveFiles.CheckAncestors(from);SaveFiles.CheckAncestors(to);
            foreach(var file in Directory.GetFiles(from,"*",SearchOption.AllDirectories))
            {
                SaveFiles.CheckAncestors(file);var relative=file.Substring(from.Length+1);var target=Path.Combine(to,relative);SaveFiles.CheckAncestors(target);
                SaveMigrationFiles.Copy(file,target);
            }
            Backups.UpdateBackupPathsAfterMigration(configId,from,to);
            // Original files remain recoverable after interrupted settings writes.
        }
        void ShowBackupSettings(){ShowBackupSettings(null);}
        void ShowBackupSettings(SaveManagerOptions imported)
        {
            var draft=imported??ManagerOptions;
            var window=Window("Save manager settings",760,620);var panel=Page(window,"Save manager settings","These settings apply to this device.");
            var sidebar=Toggle(panel,"Show the saves button in Playnite's sidebar",draft.ShowSidebar);
            var startup=Toggle(panel,"Check cloud history when Playnite starts",draft.SyncOnStartup);
            var path=EntryField(panel,"Global backup root (blank = plugin data)",draft.BackupRoot??"");
            Label(panel,"Changing the root copies and verifies existing backups before updating their paths. Original copies remain available for recovery. Per-game overrides stay in effect.",13,true);
            var feedback=Label(panel,"",13);
            Button(Footer(window),"Save",()=>{
                if(!syncGate.Wait(0)){feedback.Text="Wait for sync first.";return;}
                try
                {
                    var next=path.Text.Trim();if(next.Length>0)SaveFiles.Root(next);
                    foreach(var mapping in repository.Snapshot().Mappings)
                    {
                        var game=PlayniteApi.Database.Games.Get(new Guid(mapping.Key));var link=Link(mapping.Value.Key);if(game==null||!link.HasSaves||!String.IsNullOrWhiteSpace(link.BackupRoot))continue;
                        var candidate=JsonSafety.Deserialize<SaveLink>(JsonConvert.SerializeObject(link));candidate.BackupRoot=next;ValidatePlan(game,candidate);
                    }
                    if(!String.Equals(next,ManagerOptions.BackupRoot??"",StringComparison.OrdinalIgnoreCase))
                        Network("Copying and verifying backup files…",()=>{foreach(var config in Backups.GetAllGameConfigs())if(String.IsNullOrWhiteSpace(BackupRootForConfig(config.ConfigId)))RelocateBackupDirectory(config.ConfigId,next);});
                    ManagerOptions.BackupRoot=next;ManagerOptions.ShowSidebar=sidebar.IsChecked==true;ManagerOptions.SyncOnStartup=startup.IsChecked==true;PersistManagerOptions();window.Close();
                }catch(Exception ex){feedback.Text=ex.Message;}
                finally{syncGate.Release();}
            });
            window.ShowDialog();
        }
        string BackupRootForConfig(Guid configId)
        {
            if(backupService==null)return null;var config=backupService.GetConfigByConfigId(configId);if(config==null)return null;var state=repository.Snapshot();
            foreach(var gameId in config.GameIds){Mapping map;SaveLink link;if(state.Mappings.TryGetValue(gameId.ToString(),out map)&&vault.State.Links.TryGetValue(map.Key,out link)&&!String.IsNullOrWhiteSpace(link.BackupRoot))return link.BackupRoot;}
            return null;
        }
        void CheckInstalledSaves()
        {
            foreach(var mapping in repository.Snapshot().Mappings)
            {
                var game=PlayniteApi.Database.Games.Get(new Guid(mapping.Key));if(game==null)continue;bool previous;
                if(ManagerOptions.Installed.TryGetValue(mapping.Key,out previous)&&!previous&&game.IsInstalled)OfferReinstallRestore(game);
                ManagerOptions.Installed[mapping.Key]=game.IsInstalled;
            }
            PersistManagerOptions();
        }
        void OfferReinstallRestore(Game game)
        {
            Mapping map;SaveLink link;if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map)||!vault.State.Links.TryGetValue(map.Key,out link)||!link.RestoreOnInstall||!link.HasSaves)return;
            PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteLink.Reinstalled."+game.Id,"Game reinstalled: review saved backups for "+game.Name,NotificationType.Info,()=>Guard(()=>ShowSaveManager(game,map.Key))));
        }
        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {if(repository==null)return;UI(()=>{OfferReinstallRestore(args.Game);ManagerOptions.Installed[args.Game.Id.ToString()]=true;PersistManagerOptions();});}
        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {if(repository==null)return;UI(()=>{ManagerOptions.Installed[args.Game.Id.ToString()]=false;PersistManagerOptions();});}
        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            if(repository==null||!ManagerOptions.ShowSidebar)return new SidebarItem[0];
            return new[]{new SidebarItem{Title="Playnite Link saves",Icon="\uE8B7",Type=SiderbarItemType.Button,Activated=()=>Guard(()=>{
                var selected=PlayniteApi.MainView.SelectedGames.ToList();if(selected.Count==1){Mapping map;if(repository.Snapshot().Mappings.TryGetValue(selected[0].Id.ToString(),out map))ShowSaveManager(selected[0],map.Key);else Enroll(selected[0]);}else ManageGames();
            })}};
        }
        async void QuickBackup(IEnumerable<Game> games)
        {
            foreach(var game in games)
            {
                try
                {
                    Mapping map;if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map)||!Link(map.Key).HasSaves)throw new IOException("Configure save paths for "+game.Name+" first.");
                    if(Link(map.Key).ConfirmBeforeBackup&&PlayniteApi.Dialogs.ShowMessage("Create a save backup for "+game.Name+"?","Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)continue;
                    await SaveOperation(game,()=>CreateBackup(game,map.Key,"Quick backup",false));
                }catch(Exception ex){NotifyError(ex);}
            }
            QueueSync();
        }
    }
}

