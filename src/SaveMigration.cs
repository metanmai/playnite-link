using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;

namespace PlaytimeRclone
{
    public sealed class MigrationFile
    {public string Source,Copy,Sha256,SourceSha256;}
    public sealed class SaveMigrationReceipt
    {public string ConfigId,SourceConfigHash,CatalogHash,Game,Key;public bool UnavailableAcknowledged;public List<string> Unavailable=new List<string>();public List<string> ImportedBackups=new List<string>();public List<MigrationFile> Files=new List<MigrationFile>();}
    public static class SaveMigrationFiles
    {
        public static JObject BundleManifest(ZipArchive zip)
        {
            var own=zip.Entries.Where(e=>e.FullName=="playnite-link-save-bundle.json").ToList();
            if(own.Count>1)throw new IOException("Duplicate bundle manifest.");
            if(own.Count==1)
            {if(own[0].Length>16*1024*1024)throw new IOException("Oversized bundle manifest.");using(var reader=new StreamReader(own[0].Open()))return JsonSafety.Object(reader.ReadToEnd());}
            var configs=zip.Entries.Where(e=>e.FullName=="config.json").ToList();
            if(configs.Count!=1||configs[0].Length>16*1024*1024)throw new IOException("No supported Link / Save Manager bundle manifest.");
            JObject legacy;using(var reader=new StreamReader(configs[0].Open()))legacy=JsonSafety.Object(reader.ReadToEnd());
            var games=new JArray();var values=legacy["GameConfigs"] as JObject;if(values==null)throw new IOException("Unsupported Save Manager export.");
            foreach(var property in values.Properties())
            {
                var item=(JObject)property.Value.DeepClone();item["Game"]=(string)item["GameName"];var backups=new JArray();
                foreach(var record in (legacy["GameBackups"]==null?null:legacy["GameBackups"][property.Name]) as JArray??new JArray())
                {
                    var name=((string)record["BackupFilePath"]??"").Replace('\\','/');
                    var marker=name.IndexOf("/Backups/",StringComparison.OrdinalIgnoreCase);if(marker>=0)name=name.Substring(marker+1);
                    var matches=zip.Entries.Where(e=>e.FullName.Replace('\\','/')==name).ToList();
                    if(matches.Count==0){var missing=item["UnavailableBackups"] as JArray;if(missing==null){missing=new JArray();item["UnavailableBackups"]=missing;}missing.Add(record.DeepClone());continue;}
                    if(matches.Count!=1)throw new IOException("A Save Manager backup is ambiguous in this export: "+System.IO.Path.GetFileName(name));
                    backups.Add(new JObject{{"Path",matches[0].FullName},{"Automatic",(bool?)record["IsAutoBackup"]??false},{"Note",(string)record["Description"]??""},{"CreatedUtc",record["CreatedAt"]==null?null:record["CreatedAt"].DeepClone()}});
                }
                foreach(var extra in zip.Entries.Where(e=>e.FullName.Replace('\\','/').StartsWith("Backups/"+property.Name+"/",StringComparison.OrdinalIgnoreCase)&&e.FullName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)))if(!backups.Any(b=>(string)b["Path"]==extra.FullName))backups.Add(new JObject{{"Path",extra.FullName}});
                item["Backups"]=backups;games.Add(item);
            }
            var result=new JObject{{"Version",1},{"Games",games}};var settingsEntry=zip.GetEntry("settings.json");
            if(settingsEntry!=null)
            {if(settingsEntry.Length>1024*1024)throw new IOException("Oversized legacy settings.");JObject settings;using(var reader=new StreamReader(settingsEntry.Open()))settings=JsonSafety.Object(reader.ReadToEnd());result["LegacySettings"]=settings;result["Settings"]=new JObject{{"ShowSidebar",(bool?)settings["ShowSidebarButton"]??true},{"SyncOnStartup",(bool?)settings["SyncConfigOnApplicationStart"]??true},{"BackupRoot",(string)settings["CustomBackupPath"]}};}
            return result;
        }
        public static string Hash(string path){using(var input=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
        public static MigrationFile Copy(string source,string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));var expected=Hash(source);
            if(!File.Exists(destination))File.Copy(source,destination);
            if(Hash(destination)!=expected||Hash(source)!=expected)throw new IOException("Migration copy did not verify. Source data was kept.");
            return new MigrationFile{Source=source,Copy=destination,Sha256=expected,SourceSha256=expected};
        }
        public static void Verify(SaveMigrationReceipt receipt)
        {if(receipt==null||receipt.Files==null||receipt.Files.Count==0)throw new IOException("Migration is incomplete.");foreach(var file in receipt.Files){if(!File.Exists(file.Copy)||Hash(file.Copy)!=file.Sha256)throw new IOException("A migrated backup is missing or has changed. Save Manager was not retired.");if(File.Exists(file.Source)&&Hash(file.Source)!=(file.SourceSha256??file.Sha256))throw new IOException("Save Manager data changed since migration. Re-run migration before retirement.");}}
    }
    public sealed partial class PlaytimePlugin
    {
        private static readonly Guid SaveManagerId=new Guid("e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b");
        private string LegacySaveData{get{return Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,SaveManagerId.ToString());}}
        private string MigrationDirectory{get{return Path.Combine(Root,"save-manager-migration");}}
        private bool MigrationBlocksSync{get{return File.Exists(Path.Combine(MigrationDirectory,"resume-after-retirement.json"))&&PlayniteApi.Addons.Plugins.Any(p=>p.Id==SaveManagerId);}}
        private void PauseForMigration()
        {
            if(!PlayniteApi.Addons.Plugins.Any(p=>p.Id==SaveManagerId))return;
            ProtectRetiredSaveManager();
            var marker=Path.Combine(MigrationDirectory,"resume-after-retirement.json");if(!File.Exists(marker))Atomic.Write(marker,JsonConvert.SerializeObject(new{Resume=!IsSyncPaused}));
            onboarding.SyncPaused=true;SaveOnboarding();
        }
        private void ProtectRetiredSaveManager()
        {
            if(setup==null)return;
            setup.Options.LocalOnly.Add(Identity.Hash("Extensions:SaveManager"));
            setup.Options.LocalOnly.Add(Identity.Hash("Extensions:"+SaveManagerId));setup.SaveOptions();
        }
        private void CompleteMigrationRestart()
        {
            var marker=Path.Combine(MigrationDirectory,"resume-after-retirement.json");var result=Path.Combine(MigrationDirectory,"retirement-result.json");
            if(!File.Exists(marker)||!File.Exists(result)||PlayniteApi.Addons.Plugins.Any(p=>p.Id==SaveManagerId))return;
            if((bool?)JsonSafety.Object(File.ReadAllText(result))["Removed"]!=true)return;
            if((bool?)JsonSafety.Object(File.ReadAllText(marker))["Resume"]==true){onboarding.SyncPaused=false;SaveOnboarding();}
            File.Delete(marker);
        }
        private JObject LegacyConfiguration()
        {
            var path=Path.Combine(LegacySaveData,"config.json");if(!File.Exists(path))throw new IOException("No installed Save Manager configuration was found.");var result=JsonSafety.Object(File.ReadAllText(path));
            var remote=Path.Combine(MigrationDirectory,"legacy-cloud","config.json");
            if(File.Exists(remote))
            {
                var cloud=JsonSafety.Object(File.ReadAllText(remote));var cloudConfigs=cloud["GameConfigs"] as JObject;
                if(cloudConfigs!=null)foreach(var property in cloudConfigs.Properties())
                    if(result["GameConfigs"][property.Name]==null){result["GameConfigs"][property.Name]=property.Value.DeepClone();if(result["GameBackups"]==null)result["GameBackups"]=new JObject();result["GameBackups"][property.Name]=(cloud["GameBackups"]==null?new JArray():cloud["GameBackups"][property.Name]??new JArray()).DeepClone();}
            }
            return result;
        }
        private IEnumerable<JProperty> LegacyConfigs(JObject data)
        {var configs=data["GameConfigs"] as JObject;if(configs==null)throw new IOException("Unsupported legacy configuration layout. Export each game configuration from Save Manager, then import it in Link.");return configs.Properties();}
        private void ShowSaveBackups()
        {
            var window=Window("Save backups",900,680);var panel=Page(window,"Save backups","Backups run automatically after playing. Restore or export a copy whenever you need it.");
            var global=Card(panel,"Portable backup packages");
            Button(global,"Backup settings…",ShowBackupSettings);
            Button(global,"Open local backup data",()=>Process.Start(Path.Combine(Root,"save-manager")));
            var maintenance=new StackPanel();Button(maintenance,"Reset local save manager…",ResetLocalSaveManager);Button(maintenance,"Erase all cloud vault data…",EraseCloudVault);Details(panel,"Data maintenance",maintenance);
            Button(global,"Export all configurations & backups…",()=>{if(syncing!=0)throw new IOException("Wait for sync first.");var picker=new Microsoft.Win32.SaveFileDialog{Filter="Link backup package|*.zip",FileName="PlayniteLink-save-backups.zip"};if(picker.ShowDialog(window)==true)ExportSaveBundle(picker.FileName);});
            Button(global,"Import backup package…",()=>{var picker=new Microsoft.Win32.OpenFileDialog{Filter="Link / Save Manager package|*.zip",CheckFileExists=true};if(picker.ShowDialog(window)==true){window.Close();ShowBundleImport(picker.FileName);}});
            Button(Footer(window),"Close",()=>window.Close());window.ShowDialog();
        }
        private void MigrateLegacyGame(string id,JObject config,JArray backups,Game game,string key,SaveLink incoming)
        {
            var sourceConfig=Path.Combine(LegacySaveData,"config.json");var sourceHash=SaveMigrationFiles.Hash(sourceConfig);
            var receiptPath=Path.Combine(MigrationDirectory,id+".json");
            var unavailable=new List<string>();var candidates=LegacyBackupCandidates(id,backups,unavailable);var catalogHash=LegacyCatalogHash(candidates,unavailable);
            if(File.Exists(receiptPath)){var previous=JsonSafety.Deserialize<SaveMigrationReceipt>(File.ReadAllText(receiptPath));if(previous.SourceConfigHash==sourceHash&&previous.CatalogHash==catalogHash&&previous.Key==key){try{SaveMigrationFiles.Verify(previous);var persisted=new SaveManager.Services.BackupService(Path.Combine(Root,"save-manager"),logger,PlayniteApi,()=>false);if(previous.ImportedBackups.All(imported=>persisted.GetBackups(game.Id).Any(b=>b.Id==imported)))return;backupService=null;}catch(IOException){/* Revalidate originals rather than trusting an old receipt. */}}}
            PlanRecoveryBeforeEdit(game,key);var old=Link(key);incoming.Save=old.Save;incoming.Preferences=old.Preferences;
            var receipt=new SaveMigrationReceipt{ConfigId=id,Game=game.Id.ToString(),Key=key,SourceConfigHash=sourceHash,CatalogHash=catalogHash,Unavailable=unavailable};
            var archive=Path.Combine(MigrationDirectory,"originals",sourceHash);Directory.CreateDirectory(archive);
            receipt.Files.Add(SaveMigrationFiles.Copy(sourceConfig,Path.Combine(archive,"config.json")));
            var remoteConfig=Path.Combine(MigrationDirectory,"legacy-cloud","config.json");if(File.Exists(remoteConfig))receipt.Files.Add(SaveMigrationFiles.Copy(remoteConfig,Path.Combine(archive,"cloud-config-"+SaveMigrationFiles.Hash(remoteConfig)+".json")));
            var settingsPath=Path.Combine(LegacySaveData,"settings.json");
            if(File.Exists(settingsPath))
            {
                receipt.Files.Add(SaveMigrationFiles.Copy(settingsPath,Path.Combine(archive,"settings-"+SaveMigrationFiles.Hash(settingsPath)+".json")));
                if(!ManagerOptions.LegacySettingsImported)
                {
                    var settings=JsonSafety.Object(File.ReadAllText(settingsPath));var backupRoot=(string)settings["CustomBackupPath"];
                    if(!String.IsNullOrWhiteSpace(backupRoot))SaveFiles.Root(backupRoot);
                    ManagerOptions.ShowSidebar=(bool?)settings["ShowSidebarButton"]??true;ManagerOptions.SyncOnStartup=(bool?)settings["SyncConfigOnApplicationStart"]??true;ManagerOptions.BackupRoot=backupRoot;ManagerOptions.LegacySettingsImported=true;PersistManagerOptions();
                }
            }
            try
            {
                PauseForMigration();
                vault.State.Links[key]=incoming;vault.Save();ConfigureBackupCore(game,key);
                foreach(var source in candidates)
                {
                    SaveFiles.CheckAncestors(source);
                    var copy=SaveMigrationFiles.Copy(source,Path.Combine(archive,id,SaveMigrationFiles.Hash(source)+".zip"));receipt.Files.Add(copy);
                    var backup=Backups.GetBackups(game.Id).FirstOrDefault(b=>File.Exists(Backups.GetFullBackupPath(b.BackupFilePath))&&SaveMigrationFiles.Hash(Backups.GetFullBackupPath(b.BackupFilePath))==copy.Sha256)??ImportSaveBackup(game,key,copy.Copy);
                    var metadata=backups.FirstOrDefault(b=>String.Equals(Path.GetFileName((string)b["BackupFilePath"]),Path.GetFileName(source),StringComparison.OrdinalIgnoreCase));
                    if(metadata!=null)ApplyBackupMetadata(key,backup,new JObject{{"Automatic",(bool?)metadata["IsAutoBackup"]??false},{"Note",(string)metadata["Description"]??backup.Description},{"CreatedUtc",metadata["CreatedAt"]==null?null:metadata["CreatedAt"].DeepClone()}});
                    receipt.ImportedBackups.Add(backup.Id);
                    receipt.Files.Add(new MigrationFile{Source=source,Copy=Backups.GetFullBackupPath(backup.BackupFilePath),SourceSha256=copy.Sha256,Sha256=SaveMigrationFiles.Hash(Backups.GetFullBackupPath(backup.BackupFilePath))});
                }
                if(SaveMigrationFiles.Hash(sourceConfig)!=sourceHash)throw new IOException("Save Manager changed its configuration during migration. Close its background work and retry.");
                SaveMigrationFiles.Verify(receipt);Atomic.Write(receiptPath,JsonConvert.SerializeObject(receipt,Formatting.Indented));
            }
            catch{vault.State.Links[key]=old;vault.Save();throw;}
        }
        private void VerifyLegacyMigration(JObject displayed)
        {
            var current=LegacyConfiguration();if(current.ToString()!=displayed.ToString())throw new IOException("Save Manager changed since this window opened. Reopen migration and check again.");
            var currentHash=SaveMigrationFiles.Hash(Path.Combine(LegacySaveData,"config.json"));
            foreach(var property in LegacyConfigs(current))
            {
                var path=Path.Combine(MigrationDirectory,property.Name+".json");if(!File.Exists(path))throw new IOException("Migrate every configured game before removing Save Manager.");
                var receipt=JsonSafety.Deserialize<SaveMigrationReceipt>(File.ReadAllText(path));
                if(receipt.SourceConfigHash!=currentHash)throw new IOException("Save Manager has changed since migration. Re-run its migration before removal.");
                var unavailable=new List<string>();var candidates=LegacyBackupCandidates(property.Name,(current["GameBackups"]==null?null:current["GameBackups"][property.Name]) as JArray??new JArray(),unavailable);
                if(receipt.CatalogHash!=LegacyCatalogHash(candidates,unavailable))throw new IOException("Available legacy backups changed. Re-run migration before retirement.");
                SaveMigrationFiles.Verify(receipt);Mapping map;if(!repository.Snapshot().Mappings.TryGetValue(receipt.Game,out map)||map.Key!=receipt.Key)throw new IOException("A migrated game is no longer linked in Link.");
                var persisted=new SaveManager.Services.BackupService(Path.Combine(Root,"save-manager"),logger,PlayniteApi,()=>false);
                if(receipt.ImportedBackups.Any(id=>!persisted.GetBackups(new Guid(receipt.Game)).Any(b=>b.Id==id)))throw new IOException("The imported backup catalog did not persist. Re-run migration before retirement.");
                if(receipt.Unavailable.Count>0&&!receipt.UnavailableAcknowledged)throw new IOException(receipt.Unavailable.Count+" old backup records have no available ZIP. Recover the legacy cloud, re-run migration, and review unavailable records before retirement.");
            }
        }
        private string LegacyRemote()
        {
            var settings=Path.Combine(LegacySaveData,"settings.json");if(!File.Exists(settings))return "gdrive:PlayniteSaveManager";
            var value=(int?)JsonSafety.Object(File.ReadAllText(settings))["CloudProvider"]??0;
            return (value==1?"onedrive":value==2?"webdav":value==3?"cloudflare-r2":value==4?"pcloud":"gdrive")+":PlayniteSaveManager";
        }
        private List<string> LegacyBackupCandidates(string id,JArray records,List<string> unavailable)
        {
            var result=new List<string>();var cloud=Path.Combine(MigrationDirectory,"legacy-cloud");
            foreach(var record in records)
            {
                var value=(string)record["BackupFilePath"];if(String.IsNullOrWhiteSpace(value)){unavailable.Add("Record without a backup path");continue;}
                var local=Path.IsPathRooted(value)?value:Path.Combine(LegacySaveData,value);
                var remote=Path.Combine(cloud,"Backups",id,Path.GetFileName(value));
                if(File.Exists(local))result.Add(local);else if(File.Exists(remote))result.Add(remote);else unavailable.Add(value);
            }
            // Keep orphan archives too: old Save Manager's config can lag its uploads.
            foreach(var directory in new[]{Path.Combine(LegacySaveData,"Backups",id),Path.Combine(cloud,"Backups",id)})
                if(Directory.Exists(directory))result.AddRange(Directory.GetFiles(directory,"*.zip"));
            return result.Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(SaveMigrationFiles.Hash).Select(g=>g.First()).ToList();
        }
        private static string LegacyCatalogHash(IEnumerable<string> candidates,IEnumerable<string> missing)
        {return Identity.Hash(String.Join("\n",candidates.Select(SaveMigrationFiles.Hash).OrderBy(x=>x,StringComparer.Ordinal))+"\nmissing:\n"+String.Join("\n",missing.OrderBy(x=>x,StringComparer.Ordinal)));}
        private void ScheduleSaveManagerRetirement()
        {
            ProtectRetiredSaveManager();
            var plugin=PlayniteApi.Addons.Plugins.FirstOrDefault(p=>p.Id==SaveManagerId);if(plugin==null)throw new IOException("The separate Save Manager extension is not loaded.");
            var directory=Path.GetFullPath(Path.GetDirectoryName(plugin.GetType().Assembly.Location));
            var extensions=Path.GetFullPath(Path.Combine(PlayniteApi.Paths.ConfigurationPath,"Extensions")).TrimEnd('\\');
            if(Path.GetDirectoryName(directory)!=extensions||!File.Exists(Path.Combine(directory,"SaveManager.dll")))throw new IOException("Could not verify the separate Save Manager installation directory.");
            var destination=Path.Combine(MigrationDirectory,"retired-extension",Guid.NewGuid().ToString("N"));
            var argsPath=Path.Combine(MigrationDirectory,"retire-request.json");
            var files=Directory.GetFiles(MigrationDirectory,"*.json").Where(p=>Path.GetFileName(p)!="retire-request.json"&&Path.GetFileName(p)!="retirement-result.json").Select(p=>JsonSafety.Deserialize<SaveMigrationReceipt>(File.ReadAllText(p))).Where(r=>r!=null&&r.ConfigId!=null).SelectMany(r=>r.Files).ToList();
            Atomic.Write(argsPath,JsonConvert.SerializeObject(new{Process=Process.GetCurrentProcess().Id,Source=directory,Destination=destination,Extensions=extensions,Config=Path.Combine(LegacySaveData,"config.json"),ConfigHash=SaveMigrationFiles.Hash(Path.Combine(LegacySaveData,"config.json")),AssemblyHash=SaveMigrationFiles.Hash(Path.Combine(directory,"SaveManager.dll")),Files=files}));
            var script=Path.Combine(Path.GetDirectoryName(typeof(PlaytimePlugin).Assembly.Location),"RetireSaveManager.ps1");
            if(!File.Exists(script))throw new IOException("The retirement helper is missing. Reinstall the complete Link package.");
            Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""+script+"\" -Request \""+argsPath+"\""){UseShellExecute=false,CreateNoWindow=true});
        }
        private void ExportSaveBundle(string path)
        {
            if(!syncGate.Wait(0))throw new IOException("Wait for sync first.");
            var temporary=path+".pl-export-"+Guid.NewGuid().ToString("N");
            try
            {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write))using(var zip=new ZipArchive(file,ZipArchiveMode.Create))
                {
                    var manifest=new JArray();var state=repository.Snapshot();var exported=new HashSet<Guid>();
                    Action<JObject,IEnumerable<SaveManager.Models.SaveBackup>,string> add=(item,backups,id)=>{
                        var archives=new JArray();item["Backups"]=archives;
                        foreach(var backup in backups)
                        {
                            var source=Backups.GetFullBackupPath(backup.BackupFilePath);if(String.Equals(Path.GetFullPath(source),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))throw new IOException("Choose an export destination outside existing backup files.");
                            var name="backups/"+id+"/"+backup.Id+".zip";archives.Add(new JObject{{"Path",name},{"Sha256",SaveMigrationFiles.Hash(source)},{"Automatic",backup.IsAutoBackup},{"Note",backup.Description??""},{"CreatedUtc",backup.CreatedAt.ToUniversalTime().ToString("O")}});
                            using(var input=File.OpenRead(source))using(var output=zip.CreateEntry(name).Open())input.CopyTo(output);
                        }
                        manifest.Add(item);
                    };
                    foreach(var pair in state.Mappings)
                    {
                        var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));if(game==null||!Link(pair.Value.Key).HasSaves)continue;
                        var config=Backups.GetGameConfig(game.Id);if(config!=null)exported.Add(config.ConfigId);
                        add(JsonSafety.Object(ConfigurationJson(game,pair.Value.Key)),Backups.GetBackups(game.Id),pair.Value.Key);
                    }
                    foreach(var config in Backups.GetAllGameConfigs().Where(c=>!exported.Contains(c.ConfigId)))
                    {var item=JsonSafety.Object(JsonConvert.SerializeObject(config));item["Game"]=config.GameName;add(item,Backups.GetBackupsByConfigId(config.ConfigId),config.ConfigId.ToString("N"));}
                    using(var writer=new StreamWriter(zip.CreateEntry("playnite-link-save-bundle.json").Open()))writer.Write(new JObject{{"Version",1},{"Games",manifest},{"Settings",new JObject{{"ShowSidebar",ManagerOptions.ShowSidebar},{"SyncOnStartup",ManagerOptions.SyncOnStartup},{"BackupRoot",ManagerOptions.BackupRoot}}}}.ToString());
                }
                if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);syncGate.Release();}
        }
        private void ShowBundleImport(string path)
        {
            // Imported paths stay proposals until the user chooses a local game and
            // confirms the resolved destinations. ZIP entries never choose write paths.
            var staging=Path.Combine(Root,"scratch","bundle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
            var window=Window("Import save backup package",860,780);var panel=Page(window,"Import save backup package","Choose the local game for each configuration. Review destinations before importing.");
            using(var file=File.OpenRead(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
            {
                var manifest=SaveMigrationFiles.BundleManifest(zip);
                if((int?)manifest["Version"]!=1||!(manifest["Games"] is JArray))throw new IOException("Unsupported save package.");
                if(manifest["Settings"]!=null)Button(panel,"Review imported backup settings…",()=>ShowBackupSettings(JsonSafety.Deserialize<SaveManagerOptions>(manifest["Settings"].ToString())));
                foreach(var token in (JArray)manifest["Games"])
                {
                    var item=(JObject)token;var sources=new Dictionary<string,JObject>();
                    foreach(var backup in item["Backups"] as JArray??new JArray())
                    {
                        var archiveName=(string)backup["Path"];var found=zip.Entries.Where(e=>e.FullName==archiveName).ToList();if(found.Count!=1)throw new IOException("Missing or duplicate bundled backup.");
                        var local=Path.Combine(staging,Guid.NewGuid().ToString("N")+".zip");using(var output=File.Create(local))using(var input=found[0].Open())input.CopyTo(output);
                        if(backup["Sha256"]!=null&&SaveMigrationFiles.Hash(local)!=(string)backup["Sha256"])throw new IOException("Bundled backup checksum mismatch.");sources.Add(local,(JObject)backup.DeepClone());
                    }
                    var card=Card(panel,(string)item["Game"],sources.Count+" backups");var games=PlayniteApi.Database.Games.OrderBy(g=>g.Name).ToList();var choose=new ComboBox{ItemsSource=games,DisplayMemberPath="Name",MinHeight=36};StyleInput(choose);card.Children.Add(choose);
                    var state=repository.Snapshot();choose.SelectedItem=games.FirstOrDefault(g=>state.Mappings.ContainsKey(g.Id.ToString())&&state.Mappings[g.Id.ToString()].Key==(string)item["Key"]);
                    var feedback=Label(card,"",13);var import=Button(card,"Review and import…",()=>{});
                    import.Click+=async(_,e)=>{
                        try
                        {
                            var game=choose.SelectedItem as Game;if(game==null)throw new IOException("Choose a local game.");
                            Mapping map;if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map)){Enroll(game);if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map))return;}
                            if(syncing!=0||GameBusy(game.Id))throw new IOException("Stop the game and wait for sync first.");
                            var incoming=ParseSaveConfiguration(item.ToString());if(manifest["LegacySettings"] is JObject)SaveManagerOptions.ApplyLegacy(incoming,(JObject)manifest["LegacySettings"],(string)item["ConfigId"]??"");PreviewSaveConfiguration(game,map.Key,incoming);
                            // Cancellation leaves the original configuration reference in place.
                            if(!Object.ReferenceEquals(Link(map.Key),incoming))return;
                            import.IsEnabled=false;await SaveOperation(game,()=>{foreach(var source in sources){var backup=ImportSaveBackup(game,map.Key,source.Key);ApplyBackupMetadata(map.Key,backup,source.Value);}});feedback.Text="Configuration and backups imported. Live saves were not overwritten.";QueueSync();
                        }catch(Exception ex){feedback.Text=ex.Message;}finally{import.IsEnabled=true;}
                    };
                }
            }
            Button(Footer(window),"Close",()=>window.Close());window.ShowDialog();
        }
    }
}
