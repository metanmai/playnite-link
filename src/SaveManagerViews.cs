using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Playnite.SDK.Models;
using SaveManager.Models;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private async void QuickRestore(Game game,string key,SaveBackup backup)
        {
            try{if(PlayniteApi.Dialogs.ShowMessage("Restore "+backup.Description+" from "+backup.CreatedAt.ToString("g")+"? Current saves will be backed up first. Exclusions remain local.","Restore backup",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await SaveOperation(game,()=>RestoreArchive(game,key,Backups.GetFullBackupPath(backup.BackupFilePath)));QueueSync();}catch(Exception ex){NotifyError(ex);}
        }
        private Task SaveOperation(Game game,Action action)
        {
            return Task.Run(async()=>{
                await syncGate.WaitAsync();
                try
                {
                    if(closing||vaultSwitchPending||UI(()=>GameBusy(game.Id)))throw new IOException("Stop the game and finish any vault switch before managing its saves.");
                    Interlocked.Exchange(ref syncing,1);Volatile.Write(ref activeTransferGeneration,Volatile.Read(ref transferGeneration));transferCancelled=false;
                    ReportStatus("Managing save backups for "+game.Name,SyncDisplayState.Working,"Save manager");
                    action();ReportStatus("Save operation finished. Cloud status is confirmed by the next successful sync.",SyncDisplayState.Ready,"Saved locally");
                }
                finally{Interlocked.Exchange(ref syncing,0);syncGate.Release();if(!closing)ScheduleIdle(true);}
            });
        }
        private async void BackupThenSync(Game game)
        {
            try
            {
                await Task.Delay(750);if(closing)return;
                Mapping map;var state=repository.Snapshot();
                if(state.Mappings.TryGetValue(game.Id.ToString(),out map))
                {
                    SaveLink link;if(vault.State.Links.TryGetValue(map.Key,out link)&&link.HasSaves&&link.AutoBackup)
                        await SaveOperation(game,()=>CreateBackup(game,map.Key,"After playing",true));
                }
            }
            catch(Exception ex){UI(()=>{NotifyError(ex);PlayniteApi.Notifications.Add(new Playnite.SDK.NotificationMessage("PlayniteLink.BackupError."+game.Id,"Automatic save backup failed for "+game.Name+": "+ex.Message,Playnite.SDK.NotificationType.Error));SaveManager.Services.ToastNotificationService.ShowBackupError(game.Name,ex.Message);});}
            finally{if(!closing)QueueSync();}
        }
        private void ShowSavePaths(Game game,string key)
        {
            if(syncing!=0)throw new IOException("Wait for the current sync before editing save paths.");
            if(GameBusy(game.Id))throw new IOException("Stop the game before editing save paths.");
            PlanRecoveryBeforeEdit(game,key);
            var draft=JsonSafety.Deserialize<SaveLink>(JsonConvert.SerializeObject(Link(key)));
            if(draft.Sources==null)draft.Sources=new List<SaveSource>();
            if(draft.Sources.Count==0&&!String.IsNullOrWhiteSpace(draft.Folder))draft.Sources.Add(new SaveSource{Id="main",Path=SavePaths.Portable(draft.Folder,game.InstallDirectory,draft.EmulatorDirectory)});
            draft.NextSourceId=Math.Max(draft.NextSourceId,draft.Sources.Select(s=>{int number;return s.Id!=null&&s.Id.StartsWith("source",StringComparison.Ordinal)&&Int32.TryParse(s.Id.Substring(6),out number)?number:1;}).DefaultIfEmpty(1).Max()+1);
            if(draft.RestoreExclusions==null)draft.RestoreExclusions=new List<string>();
            var window=Window("Save paths & options — "+game.Name,820,780);Action render=null;
            render=()=>{
                var panel=Page(window,"Save paths & options",game.Name);
                Label(panel,"Folders include their contents recursively. Files include only that file. Portable paths follow the game/emulator installation directory when it moves.",13,true);
                foreach(var source in draft.Sources.ToList())
                {
                    var item=source;var card=Card(panel,item.IsDirectory?"Save folder":"Save file",item.Path);
                    Label(card,"Source: "+item.Id,12,true);
                    Button(card,"Edit path…",()=>{var value=Ask("Save path. Supports {GameDir}, {EmulatorDir} and %USERPROFILE%.",item.Path);if(value!=null){item.Path=value.Trim();render();}});
                    Button(card,"Remove path",()=>{draft.Sources.Remove(item);render();});
                    Button(card,"Open location",()=>{var resolved=SavePaths.Resolve(item.Path,game.InstallDirectory,draft.EmulatorDirectory);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.IsDirectory?resolved:Path.GetDirectoryName(resolved)){UseShellExecute=true});});
                }
                Action<string,bool> add=(path,isDirectory)=>{
                    if(String.IsNullOrWhiteSpace(path))return;
                    var id=draft.Sources.Count==0?"main":"source"+(draft.NextSourceId++);while(draft.Sources.Any(s=>s.Id==id))id="source"+(draft.NextSourceId++);
                    draft.Sources.Add(new SaveSource{Id=id,Path=SavePaths.Portable(path,game.InstallDirectory,draft.EmulatorDirectory),IsDirectory=isDirectory});render();
                };
                var addActions=Actions(panel);
                Button(addActions,"Add folder…",()=>add(PlayniteApi.Dialogs.SelectFolder(),true));
                Button(addActions,"Add file…",()=>{var picker=new Microsoft.Win32.OpenFileDialog{Title="Choose a game save file",CheckFileExists=true};if(picker.ShowDialog(window)==true)add(picker.FileName,false);});
                Button(addActions,"Add path with variables…",()=>{var path=Ask("Folder path. Supports {GameDir}, {EmulatorDir} and environment variables.","{GameDir}\\Saves");if(path!=null)add(path,true);});
                var exclusions=Card(panel,"Keep local files when restoring","Excluded files/folders stay local and do not participate in cloud save comparisons. Backups still contain them.");
                foreach(var exclusion in draft.RestoreExclusions.ToList())
                {var captured=exclusion;Label(exclusions,exclusion,13,true);Button(exclusions,"Remove exclusion",()=>{draft.RestoreExclusions.Remove(captured);render();});}
                Button(exclusions,"Exclude file…",()=>{var picker=new Microsoft.Win32.OpenFileDialog{Title="Keep this file local",CheckFileExists=true};if(picker.ShowDialog(window)==true){draft.RestoreExclusions.Add(SavePaths.Portable(picker.FileName,game.InstallDirectory,draft.EmulatorDirectory));render();}});
                Button(exclusions,"Exclude folder…",()=>{var path=PlayniteApi.Dialogs.SelectFolder();if(!String.IsNullOrWhiteSpace(path)){draft.RestoreExclusions.Add(SavePaths.Portable(path,game.InstallDirectory,draft.EmulatorDirectory));render();}});
                var options=Card(panel,"Backup & launch options");
                var confirm=Toggle(options,"Confirm before quick backups",draft.ConfirmBeforeBackup);confirm.Click+=(_,e)=>draft.ConfirmBeforeBackup=confirm.IsChecked==true;
                var auto=Toggle(options,"Create a backup after playing",draft.AutoBackup);auto.Click+=(_,e)=>draft.AutoBackup=auto.IsChecked==true;
                var notify=Toggle(options,"Notify me after automatic backups",draft.NotifyBackup);notify.Click+=(_,e)=>draft.NotifyBackup=notify.IsChecked==true;
                var preflight=Toggle(options,"Check and sync cloud saves before launching",draft.CheckBeforeLaunch);preflight.Click+=(_,e)=>draft.CheckBeforeLaunch=preflight.IsChecked==true;
                var reinstall=Toggle(options,"Offer save recovery after reinstalling this game",draft.RestoreOnInstall);reinstall.Click+=(_,e)=>draft.RestoreOnInstall=reinstall.IsChecked==true;
                Label(options,"If cloud access fails, you can explicitly choose to play offline. Sync must finish before another device can receive the latest saves.",13,true);
                var maximum=EntryField(options,"Automatic backups to keep (0 = unlimited)",draft.MaxAutoBackups.ToString());maximum.TextChanged+=(_,e)=>{int value;draft.MaxAutoBackups=Int32.TryParse(maximum.Text,out value)?value:-1;};
                var emulator=EntryField(options,"Emulator directory (for {EmulatorDir})",draft.EmulatorDirectory??"");emulator.TextChanged+=(_,e)=>draft.EmulatorDirectory=emulator.Text.Trim();
                var backupRoot=EntryField(options,"Custom backup root (blank = global default)",draft.BackupRoot??"");backupRoot.TextChanged+=(_,e)=>draft.BackupRoot=backupRoot.Text.Trim();
                var feedback=Label(panel,"",13);
                var actions=Actions(Footer(window));
                Button(actions,"Save",()=>{
                    if(!syncGate.Wait(0)){feedback.Text="Wait for sync to finish.";return;}
                    try
                    {
                        if(syncing!=0||GameBusy(game.Id))throw new IOException("Stop the game and wait for sync before saving.");
                        draft.Folder=draft.Sources.Count==1&&draft.Sources[0].IsDirectory?SavePaths.Resolve(draft.Sources[0].Path,game.InstallDirectory,draft.EmulatorDirectory):null;
                        if(!draft.HasSaves&&PlayniteApi.Dialogs.ShowMessage("Stop including save files for this game? Existing local saves, backups and cloud history remain available.","Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                        ValidatePlan(game,draft);var old=Link(key);draft.Save=old.Save;draft.Preferences=old.Preferences;
                        if(!String.Equals(old.BackupRoot??"",draft.BackupRoot??"",StringComparison.OrdinalIgnoreCase))
                        {var config=Backups.GetGameConfig(game.Id);if(config!=null)Network("Copying and verifying this game's backups…",()=>RelocateBackupDirectory(config.ConfigId,String.IsNullOrWhiteSpace(draft.BackupRoot)?ManagerOptions.BackupRoot:draft.BackupRoot));}
                        if(GameBusy(game.Id))throw new IOException("The game started while paths were being reviewed. Stop it and retry.");
                        vault.State.Links[key]=draft;vault.Save();
                        ConfigureBackupCore(game,key);window.Close();
                    }catch(Exception ex){feedback.Text=ex.Message;}
                    finally{syncGate.Release();}
                });
                Button(actions,"Cancel",()=>window.Close());
            };
            render();window.ShowDialog();
        }
        private void PlanRecoveryBeforeEdit(Game game,string key)
        {if(Link(key).HasSaves)Plan(game,key).Recover();}
        private void ShowSaveManager(Game game,string key)
        {
            var window=Window("Save manager — "+game.Name,860,780);Action render=null;
            render=()=>{
                var panel=Page(window,"Save manager",game.Name);
                var link=Link(key);var paths=Card(panel,"Save configuration",link.HasSaves?"Multiple folders/files and restore exclusions are supported.":"Choose save paths before creating or restoring backups.");
                Button(paths,"Save paths & options…",()=>{ShowSavePaths(game,key);render();});
                Button(paths,"Export game configuration…",()=>{var picker=new Microsoft.Win32.SaveFileDialog{Filter="Save configuration|*.json",FileName="save-configuration.json"};if(picker.ShowDialog(window)==true)File.WriteAllText(picker.FileName,ConfigurationJson(game,key));});
                Button(paths,"Import game configuration…",()=>{
                    if(syncing!=0||GameBusy(game.Id))throw new IOException("Stop the game and wait for sync first.");
                    var picker=new Microsoft.Win32.OpenFileDialog{Filter="Save configuration|*.json",CheckFileExists=true};if(picker.ShowDialog(window)!=true)return;
                    var incoming=ParseSaveConfiguration(File.ReadAllText(picker.FileName));PreviewSaveConfiguration(game,key,incoming);render();
                });
                var statusCard=Card(panel,"Saves & sessions");SyncStatusView(statusCard);Button(statusCard,"Sessions & device sync status…",()=>ShowGameSyncHistory(game));
                if(syncing!=0)Label(panel,"A save/sync operation is running. Reopen this window after it finishes to update the backup list.");
                var list=new ListBox{MinHeight=130,MaxHeight=280};StyleInput(list);panel.Children.Add(list);
                var backups=syncing==0?Backups.GetBackups(game.Id).OrderByDescending(b=>b.CreatedAt).ToList():new List<SaveBackup>();
                foreach(var backup in backups)list.Items.Add(new ListBoxItem{Content=backup.CreatedAt.ToString("g")+" · "+(backup.IsAutoBackup?"Automatic":"Manual")+" · "+backup.Description+" · "+backup.FormattedSize,Tag=backup});
                Func<SaveBackup> selected=()=>{var item=list.SelectedItem as ListBoxItem;if(item==null)throw new IOException("Select a backup first.");return (SaveBackup)item.Tag;};
                var feedback=Label(panel,"",13);var actions=Actions(panel);
                Action<Action> operation=async action=>{
                    panel.IsEnabled=false;
                    try{await SaveOperation(game,action);if(window.IsVisible)render();QueueSync();}
                    catch(Exception ex){feedback.Text=ex.Message;}
                    finally{panel.IsEnabled=true;}
                };
                Button(actions,"Create backup…",()=>{var note=Ask("Backup note (for example, Before boss).","");if(note!=null)operation(()=>CreateBackup(game,key,note,false));});
                Button(actions,"Restore selected…",()=>{
                    var backup=selected();
                    if(PlayniteApi.Dialogs.ShowMessage("Restore this backup? Current saves will be backed up first. Restore exclusions remain local.","Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    operation(()=>RestoreArchive(game,key,Backups.GetFullBackupPath(backup.BackupFilePath)));
                });
                Button(actions,"Edit note…",()=>{var backup=selected();var note=Ask("Backup note",backup.Description);if(note!=null){if(note.Length>100000)throw new IOException("Backup notes can contain up to 100,000 characters.");operation(()=>{Backups.UpdateBackupDescription(backup,note);RecordBackup(key,backup);});}});
                Button(actions,"Restore including excluded files…",()=>{var backup=selected();if(PlayniteApi.Dialogs.ShowMessage("Replace excluded settings too? Current saves will be backed up first. This restore ignores exclusions once; your exclusion settings are kept.","Restore all files",MessageBoxButton.YesNo)==MessageBoxResult.Yes)operation(()=>RestoreArchiveIncludingExcluded(game,key,Backups.GetFullBackupPath(backup.BackupFilePath)));});
                Button(actions,"Re-upload selected backup",()=>{var backup=selected();operation(()=>RecordBackup(key,backup));});
                Button(actions,"Open backup folder",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(Backups.GetFullBackupPath(selected().BackupFilePath))){UseShellExecute=true}));
                Button(actions,"Export selected ZIP…",()=>{var backup=selected();var picker=new Microsoft.Win32.SaveFileDialog{Filter="Save backup|*.zip",FileName=backup.Name+".zip"};if(picker.ShowDialog(window)==true)operation(()=>File.Copy(Backups.GetFullBackupPath(backup.BackupFilePath),picker.FileName,true));});
                Button(actions,"Import ZIP…",()=>{
                    var picker=new Microsoft.Win32.OpenFileDialog{Filter="Save backup|*.zip",CheckFileExists=true};if(picker.ShowDialog(window)!=true)return;
                    if(PlayniteApi.Dialogs.ShowMessage("Import into "+game.Name+"? Save Manager ZIP sources map to configured save paths in their listed order. Check Save paths & options first. Plain ZIPs require one folder. Importing adds history; it does not overwrite saves.","Playnite Link",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    operation(()=>ImportSaveBackup(game,key,picker.FileName));
                });
                Button(actions,"Delete selected backup…",()=>{var backup=selected();if(PlayniteApi.Dialogs.ShowMessage("Remove this backup from the shared backup library and delete its local ZIP? Immutable cloud history is retained.","Playnite Link",MessageBoxButton.YesNo)==MessageBoxResult.Yes)operation(()=>{RecordBackup(key,backup,true);Backups.DeleteBackup(backup);});});
                Button(actions,"Cloud revision history…",()=>ShowHistory(game,key));
                Button(actions,"Verify cloud save & backup files",async()=>{panel.IsEnabled=false;feedback.Text="Downloading and verifying current cloud saves and shared backup files…";try{await SaveOperation(game,()=>VerifyGameCloud(game,key));feedback.Text="Cloud files verified against their SHA-256 hashes at "+DateTime.Now.ToString("T")+".";}catch(Exception ex){feedback.Text="Cloud verification failed: "+ex.Message;}finally{panel.IsEnabled=true;}});
                BuildCloudBackupLibrary(panel,game,key,render);
                Button(Footer(window),"Done",()=>window.Close());
            };
            render();window.ShowDialog();
        }
        private void PreviewSaveConfiguration(Game game,string key,SaveLink incoming)
        {
            PlanRecoveryBeforeEdit(game,key);ValidatePlan(game,incoming);
            var preview=incoming.HasSaves?String.Join("\n",new SavePlan(incoming,game.InstallDirectory,blobs,Path.Combine(Root,"validation.json")).Sources.Select(s=>(s.IsDirectory?"Folder: ":"File: ")+s.Path)):"No live save paths. Backups remain available; configure paths before restoring.";
            if(PlayniteApi.Dialogs.ShowMessage("Use these paths for "+game.Name+"?\n\n"+preview+"\n\nReview every destination. Importing configuration does not restore saves.","Import save configuration",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            if(!syncGate.Wait(0))throw new IOException("A sync started while reviewing paths. Try again when it finishes.");
            try{if(GameBusy(game.Id))throw new IOException("Stop the game before importing save paths.");var old=Link(key);incoming.Save=old.Save;incoming.Preferences=old.Preferences;vault.State.Links[key]=incoming;vault.Save();ConfigureBackupCore(game,key);}finally{syncGate.Release();}
        }
        private void RestoreArchive(Game game,string key,string path)
        {RestoreArchiveCore(game,key,path,false);}
        private void RestoreArchiveIncludingExcluded(Game game,string key,string path)
        {RestoreArchiveCore(game,key,path,true);}
        private void RestoreArchiveCore(Game game,string key,string path,bool includeExcluded)
        {
            if(MigrationBlocksSync)throw new IOException("Finish migration and restart Playnite before restoring with Link.");
            var plan=Plan(game,key);
            if(includeExcluded){var all=JsonSafety.Deserialize<SaveLink>(JsonConvert.SerializeObject(Link(key)));all.RestoreExclusions=new List<string>();plan=new SavePlan(all,game.InstallDirectory,blobs,Path.Combine(Root,"save-transactions",key+".json"));}
            var data=SaveArchive.Read(path,plan,blobs);var local=plan.Capture();
            if(plan.Sources.All(s=>s.IsDirectory?Directory.Exists(s.Path):File.Exists(s.Path)))CreateBackup(game,key,"Before restoring backup",false);
            else if(SavePlan.HasFiles(plan.Capture(true)))
            {
                var preserved=Path.Combine(Root,"scratch","before-restore-"+Guid.NewGuid().ToString("N")+".zip");Directory.CreateDirectory(Path.GetDirectoryName(preserved));
                try{SaveArchive.WriteSnapshot(preserved,plan.Capture(true),plan,blobs,"Before restoring backup (some paths absent)");ImportSaveBackup(game,key,preserved);}finally{if(File.Exists(preserved))File.Delete(preserved);}
            }
            try
            {
                LastBackup=plan.Restore(data,Revision.ContentHashOf(local),()=>UI(()=>{if(GameBusy(game.Id))throw new IOException("The game is starting; restore deferred.");restoringSaves.Add(game.Id);}));
                data=plan.Capture(true);var next=new Revision{Version=data.Keys.Any(k=>k.StartsWith(SavePlan.Mounts,StringComparison.Ordinal))?3:2,Game=key,Kind="save",Device=repository.Snapshot().Device,Data=data,Parents=vault.Heads(key,"save").Select(h=>h.Hash()).OrderBy(h=>h,StringComparer.Ordinal).ToList()};
                vault.Add(next);var link=Link(key);link.Save.Revision=next.Hash();link.Save.Content=Revision.ContentHashOf(Plan(game,key).Filter(data));vault.Save();
            }
            finally{UI(()=>restoringSaves.Remove(game.Id));}
        }
    }
}
