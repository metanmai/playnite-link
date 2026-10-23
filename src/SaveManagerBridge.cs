using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using SaveManager.Models;
using SaveManager.Services;

namespace PlaytimeRclone
{
    public static class SaveArchive
    {
        public static SavePlan UnconfiguredPlan(string path,BlobStore blobs,string root)
        {
            var sources=new List<SaveSource>();
            using(var file=File.OpenRead(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
            {
                var entry=zip.GetEntry("__save_paths__.json");
                if(entry!=null)
                {
                    if(entry.Length>1024*1024)throw new IOException("Oversized archive mapping.");List<BackupService.PathMapping> mapping;
                    using(var reader=new StreamReader(entry.Open()))mapping=JsonSafety.Deserialize<List<BackupService.PathMapping>>(reader.ReadToEnd());
                    if(mapping==null||mapping.Count==0)throw new IOException("Empty archive source mapping.");
                    for(int i=0;i<mapping.Count;i++)sources.Add(new SaveSource{Id=i==0?"main":"source"+(i+1),Path=Path.Combine(root,"source"+i),IsDirectory=mapping[i].IsDirectory});
                }
                else sources.Add(new SaveSource{Id="main",Path=Path.Combine(root,"source0")});
            }
            return new SavePlan(new SaveLink{Sources=sources},null,blobs,Path.Combine(root,"unused-journal.json"));
        }
        public static void WriteSnapshot(string path,SortedDictionary<string,string> data,SavePlan plan,BlobStore blobs,string note)
        {
            SaveFiles.Validate(data);blobs.Ensure(data);
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))using(var zip=new ZipArchive(file,ZipArchiveMode.Create))
            {
                var mapping=plan.Sources.Select((s,i)=>new BackupService.PathMapping{OriginalPath=s.Path,IsDirectory=s.IsDirectory,EntryName="source"+i.ToString("D4")}).ToList();
                foreach(var pair in data)
                {
                    if(SavePlan.IsLayout(pair.Key))continue;
                    var target=plan.Target(pair.Key);var index=plan.Sources.FindIndex(s=>s.IsDirectory?SavePaths.Contains(s.Path,target):String.Equals(s.Path,target,StringComparison.OrdinalIgnoreCase));
                    if(index<0)throw new IOException("Unmapped save snapshot entry.");var source=plan.Sources[index];var name=mapping[index].EntryName+(source.IsDirectory?"/"+target.Substring(source.Path.Length+1).Replace('\\','/'):"");
                    using(var output=zip.CreateEntry(name).Open())blobs.CopyVerified(pair.Value,output);
                }
                using(var writer=new StreamWriter(zip.CreateEntry("__save_paths__.json").Open()))writer.Write(JsonConvert.SerializeObject(mapping));
                using(var writer=new StreamWriter(zip.CreateEntry("backup_info.json").Open()))writer.Write(JsonConvert.SerializeObject(new BackupInfo{Description=note,CreatedAt=DateTime.Now,GameName="Preserved local saves"}));
            }
        }
        public static SortedDictionary<string,string> Read(string path,SavePlan plan,BlobStore blobs)
        {
            var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
            {
                var hasMapping=zip.GetEntry("__save_paths__.json")!=null;
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in zip.Entries)
                {
                    var name=entry.FullName.Replace('\\','/').TrimEnd('/');
                    SaveFiles.Validate(new SortedDictionary<string,string>{{name,""}});
                    if(!names.Add(name))throw new InvalidDataException("Duplicate ZIP entry: "+name);
                    if(((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("ZIP symlinks are unsupported.");
                    if(hasMapping&&(entry.FullName=="backup_info.json"||entry.FullName=="__save_paths__.json")&&entry.Length>1024*1024)throw new InvalidDataException("Oversized ZIP metadata.");
                }
                var mapping=zip.GetEntry("__save_paths__.json");
                var information=zip.GetEntry("backup_info.json");
                if(mapping!=null&&information!=null)
                {using(var reader=new StreamReader(information.Open())){var info=JsonSafety.Object(reader.ReadToEnd());if(((string)info["Description"]??"").Length>100000)throw new IOException("Backup notes can contain up to 100,000 characters. The original archive was kept.");}}
                List<BackupService.PathMapping> paths=null;
                if(mapping!=null)
                {
                    if(mapping.Length>1024*1024)throw new InvalidDataException("Oversized ZIP path mapping.");
                    using(var reader=new StreamReader(mapping.Open()))paths=JsonSafety.Deserialize<List<BackupService.PathMapping>>(reader.ReadToEnd());
                    if(paths==null||paths.Count!=plan.Sources.Count||paths.Any(p=>p==null||String.IsNullOrWhiteSpace(p.EntryName))||paths.Select(p=>p.EntryName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count)throw new InvalidDataException("Backup source layout differs. Import/configure its save paths first.");
                    for(int i=0;i<paths.Count;i++)
                    {
                        SaveFiles.Validate(new SortedDictionary<string,string>{{paths[i].EntryName.Replace('\\','/'),""}});if(paths[i].IsDirectory!=plan.Sources[i].IsDirectory)throw new InvalidDataException("Backup source types differ from this game's configured paths.");
                        var name=paths[i].EntryName.Replace('\\','/').TrimEnd('/');
                        if(paths.Where((p,j)=>j!=i).Any(p=>p.EntryName.Replace('\\','/').StartsWith(name+"/",StringComparison.OrdinalIgnoreCase)||name.StartsWith(p.EntryName.Replace('\\','/').TrimEnd('/')+"/",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Overlapping archive source mappings are ambiguous.");
                    }
                }
                foreach(var entry in zip.Entries.Where(e=>!e.FullName.EndsWith("/")))
                {
                    if(mapping!=null&&(entry.FullName=="backup_info.json"||entry.FullName=="__save_paths__.json"))continue;
                    var name=entry.FullName.Replace('\\','/');string key=null;
                    if(paths!=null)
                    {
                        for(int i=0;i<paths.Count;i++)
                        {
                            var p=paths[i];var prefix=p.EntryName.Replace('\\','/').TrimEnd('/');var source=plan.Sources[i];
                            if(p.IsDirectory&&name.StartsWith(prefix+"/",StringComparison.Ordinal))key=source.Prefix+name.Substring(prefix.Length+1);
                            else if(!p.IsDirectory&&name==prefix)key=source.Prefix+"content";
                        }
                        if(key==null)throw new InvalidDataException("ZIP file is outside its declared save sources: "+name);
                    }
                    else
                    {
                        if(plan.Sources.Count!=1||!plan.Sources[0].IsDirectory)throw new InvalidDataException("Plain ZIP imports require one folder source.");
                        key=plan.Sources[0].Prefix+name;
                    }
                    plan.Target(key);using(var input=entry.Open())result.Add(key,blobs.Put(input).ToString());
                }
            }
            plan.AddLayout(result);SaveFiles.Validate(result);return result;
        }
        public static string NormalizePlain(string path,string destination,SavePlan plan)
        {
            using(var file=File.OpenRead(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
            {
                if(zip.GetEntry("__save_paths__.json")!=null)return path;
                if(plan.Sources.Count!=1||!plan.Sources[0].IsDirectory)throw new InvalidDataException("Plain ZIPs need one folder source.");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
                using(var output=File.Create(destination))using(var normalized=new ZipArchive(output,ZipArchiveMode.Create))
                {
                    foreach(var entry in zip.Entries.Where(e=>!e.FullName.EndsWith("/")))
                        using(var input=entry.Open())using(var target=normalized.CreateEntry("source0000/"+entry.FullName.Replace('\\','/')).Open())input.CopyTo(target);
                    using(var writer=new StreamWriter(normalized.CreateEntry("__save_paths__.json").Open()))writer.Write(JsonConvert.SerializeObject(new[]{new BackupService.PathMapping{OriginalPath=plan.Sources[0].Path,IsDirectory=true,EntryName="source0000"}}));
                }
            }
            return destination;
        }
    }
    public sealed partial class PlaytimePlugin
    {
        private BackupService backupService;
        private BackupService Backups
        {
            get {return backupService??(backupService=new BackupService(Path.Combine(Root,"save-manager"),logger,PlayniteApi,()=>false,()=>ManagerOptions.BackupRoot,BackupRootForConfig));}
        }
        private SavePlan Plan(Game game,string key)
        {return new SavePlan(Link(key),game.InstallDirectory,blobs,Path.Combine(Root,"save-transactions",key+".json"));}
        private void ValidatePlan(Game game,SaveLink link)
        {
            if(!link.HasSaves)return;
            var candidate=new SavePlan(link,game.InstallDirectory,blobs,Path.Combine(Root,"save-transactions","validation.json"));
            foreach(var mapping in repository.Snapshot().Mappings.Where(p=>p.Key!=game.Id.ToString()))
            {
                SaveLink other;var peer=PlayniteApi.Database.Games.Get(new Guid(mapping.Key));if(peer==null||!vault.State.Links.TryGetValue(mapping.Value.Key,out other)||!other.HasSaves)continue;
                var peerPlan=Plan(peer,mapping.Value.Key);
                if(candidate.Sources.Any(a=>peerPlan.Sources.Any(b=>SavePaths.Contains(a.Path,b.Path)||SavePaths.Contains(b.Path,a.Path))))throw new IOException("Save paths for different games must not overlap: "+peer.Name);
            }
            if(link.MaxAutoBackups<0||link.MaxAutoBackups>100000)throw new IOException("Use 0 for unlimited automatic backups, or a positive limit.");
            var backupRoot=String.IsNullOrWhiteSpace(link.BackupRoot)?ManagerOptions.BackupRoot:link.BackupRoot;
            if(!String.IsNullOrWhiteSpace(backupRoot))
            {backupRoot=SaveFiles.Root(backupRoot);if(candidate.Sources.Any(s=>SavePaths.Contains(s.Path,backupRoot)||SavePaths.Contains(backupRoot,s.Path)))throw new IOException("The backup root must be separate from live save sources.");}
        }
        private void ConfigureBackupCore(Game game,string key)
        {
            if(!Link(key).HasSaves){var existing=Backups.GetGameConfig(game.Id);if(existing!=null){existing.SavePaths.Clear();existing.RestoreExcludePaths.Clear();Backups.SaveGameConfig(existing);}return;}
            var plan=Plan(game,key);var config=Backups.GetOrCreateGameConfig(game.Id,game.Name);
            config.SavePaths=plan.Sources.Select(s=>new SavePath{Path=s.Path,IsDirectory=s.IsDirectory}).ToList();
            config.RestoreExcludePaths=(Link(key).RestoreExclusions??new List<string>()).Select(p=>new SavePath{Path=SavePaths.Resolve(p,game.InstallDirectory,Link(key).EmulatorDirectory),IsDirectory=Directory.Exists(SavePaths.Resolve(p,game.InstallDirectory,Link(key).EmulatorDirectory))}).ToList();
            Backups.SaveGameConfig(config);
        }
        private SaveBackup CreateBackup(Game game,string key,string note,bool automatic)
        {
            if((note??"").Length>100000)throw new IOException("Backup notes can contain up to 100,000 characters.");
            if(UI(()=>GameBusy(game.Id)))throw new IOException("Stop the game before creating a backup.");
            var captured=Plan(game,key).Capture(true); // Recover and validate before upstream ZIP traversal.
            ConfigureBackupCore(game,key);
            var backup=Backups.CreateBackup(game.Id,game.Name,note,automatic);
            // Read and hash every byte through the same verified import path before retention.
            try
            {
                var archived=SaveArchive.Read(Backups.GetFullBackupPath(backup.BackupFilePath),Plan(game,key),blobs);
                if(Revision.ContentHashOf(captured)!=Revision.ContentHashOf(archived)||Revision.ContentHashOf(Plan(game,key).Capture(true))!=Revision.ContentHashOf(captured))throw new IOException("Save files changed during backup.");
            }
            catch(Exception ex)
            {
                var path=Backups.GetFullBackupPath(backup.BackupFilePath);var preserved=path+".unverified-"+Guid.NewGuid().ToString("N");
                if(File.Exists(path))File.Move(path,preserved);Backups.DeleteBackup(backup);
                throw new IOException("Backup verification failed. The ZIP was kept for review at "+preserved+". It will not be shared or counted for retention.",ex);
            }
            RecordBackup(key,backup);
            if(automatic)foreach(var pruned in Backups.CleanupOldAutoBackups(game.Id,Link(key).MaxAutoBackups))RecordBackup(key,pruned,true);
            if(automatic&&Link(key).NotifyBackup){PlayniteApi.Notifications.Add(new Playnite.SDK.NotificationMessage("PlayniteLink.Backup."+game.Id,"Save backup created for "+game.Name,Playnite.SDK.NotificationType.Info));SaveManager.Services.ToastNotificationService.ShowBackupSuccess(game.Name,backup.Description);}
            return backup;
        }
        private string ConfigurationJson(Game game,string key)
        {
            var link=Link(key);
            var copy=new SaveLink{Folder=link.Folder,Sources=link.Sources,RestoreExclusions=link.RestoreExclusions,AutoBackup=link.AutoBackup,NotifyBackup=link.NotifyBackup,CheckBeforeLaunch=link.CheckBeforeLaunch,MaxAutoBackups=link.MaxAutoBackups,EmulatorDirectory=link.EmulatorDirectory,BackupRoot=link.BackupRoot,ConfirmBeforeBackup=link.ConfirmBeforeBackup,RestoreOnInstall=link.RestoreOnInstall,NextSourceId=link.NextSourceId};
            return JsonConvert.SerializeObject(new {Format="PlayniteLink.SaveConfiguration",Version=1,Game=game.Name,Key=key,Configuration=copy},Formatting.Indented);
        }
        private SaveLink ParseSaveConfiguration(string json)
        {
            var obj=JsonSafety.Object(json);var own=obj["Configuration"];
            if((string)obj["Format"]=="PlayniteLink.SaveConfiguration")
            {
                if((int?)obj["Version"]!=1||own==null)throw new InvalidDataException("Unsupported save configuration.");
                var link=JsonSafety.Deserialize<SaveLink>(own.ToString());link.Save=new Checkpoint();link.Preferences=new Checkpoint();return link;
            }
            if(obj["SavePaths"]==null)throw new InvalidDataException("Select a Link or Save Manager game configuration.");
            var paths=JsonSafety.Deserialize<List<SavePath>>(obj["SavePaths"].ToString());
            if(paths==null)throw new InvalidDataException("Invalid save paths in this configuration.");
            return new SaveLink{Sources=paths.Select((p,i)=>new SaveSource{Id=i==0?"main":"source"+(i+1),Path=p.Path,IsDirectory=p.IsDirectory}).ToList(),RestoreExclusions=obj["RestoreExcludePaths"]==null?new List<string>():JsonSafety.Deserialize<List<SavePath>>(obj["RestoreExcludePaths"].ToString()).Select(p=>p.Path).ToList()};
        }
        private SaveBackup ImportSaveBackup(Game game,string key,string path)
        {
            var plan=SaveArchive.UnconfiguredPlan(path,blobs,Path.Combine(Root,"scratch","unconfigured-"+key));var incoming=SaveArchive.Read(path,plan,blobs);
            var receiptPath=Path.Combine(Root,"backup-imports.json");var receipts=File.Exists(receiptPath)?JsonSafety.Deserialize<Dictionary<string,string>>(File.ReadAllText(receiptPath)):new Dictionary<string,string>();
            var identity=key+":"+SaveMigrationFiles.Hash(path);string previous;
            if(receipts.TryGetValue(identity,out previous))
            {
                var existing=Backups.GetBackups(game.Id).FirstOrDefault(b=>b.Id==previous);
                if(existing!=null&&File.Exists(Backups.GetFullBackupPath(existing.BackupFilePath)))
                {try{if(Revision.ContentHashOf(SaveArchive.Read(Backups.GetFullBackupPath(existing.BackupFilePath),plan,blobs))==Revision.ContentHashOf(incoming))return existing;}catch(IOException){/* Keep the damaged archive and import a verified replacement. */}}
            }
            var temporary=Path.Combine(Root,"scratch","zip-import",Guid.NewGuid().ToString("N")+".zip");
            var normalized=SaveArchive.NormalizePlain(path,temporary,plan);
            try{ConfigureBackupCore(game,key);var imported=Backups.ImportBackup(game.Id,game.Name,normalized);SaveArchive.Read(Backups.GetFullBackupPath(imported.BackupFilePath),plan,blobs);RecordBackup(key,imported);receipts[identity]=imported.Id;Atomic.Write(receiptPath,JsonConvert.SerializeObject(receipts));return imported;}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        private void VerifyGameCloud(Game game,string key)
        {
            if(String.IsNullOrWhiteSpace(config.RemoteFolder))throw new IOException("Connect cloud storage first.");CheckActiveGameVault(config);
            var data=new SortedDictionary<string,string>(StringComparer.Ordinal);int index=0;
            foreach(var head in vault.Heads(key,"save"))foreach(var reference in head.Data.Values.Where(v=>v.StartsWith("blob:",StringComparison.Ordinal)))data[(++index).ToString()]=reference;
            foreach(var backup in CloudBackups.Heads(key).Where(b=>!b.Deleted))data[(++index).ToString()]=backup.Archive;
            if(data.Count==0)throw new IOException("No cloud save or backup manifests are available. Sync first.");
            var directory=Path.Combine(Root,"scratch","verify-cloud-"+Guid.NewGuid().ToString("N"));
            var verification=new BlobStore(directory);
            verification.Fetch=names=>new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/blobs",pattern:"*.blob",limitSeconds:86400,cancelled:()=>closing||TransferStopped).PullFiles(directory,names);
            try{verification.Ensure(data);foreach(var value in data.Values.Distinct())verification.CopyVerified(value,Stream.Null);}
            finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
        }
        private void ApplyBackupMetadata(string key,SaveBackup backup,JObject metadata)
        {
            if(((string)metadata["Note"]??"").Length>100000)throw new IOException("Backup notes can contain up to 100,000 characters.");
            if(metadata["Note"]!=null&&backup.Description!=(string)metadata["Note"])Backups.UpdateBackupDescription(backup,(string)metadata["Note"]??"");
            if(metadata["Automatic"]!=null)backup.IsAutoBackup=(bool)metadata["Automatic"];
            DateTimeOffset created;if(metadata["CreatedUtc"]!=null&&DateTimeOffset.TryParse((string)metadata["CreatedUtc"],out created))backup.CreatedAt=created.LocalDateTime;
            Backups.UpdateOrAddBackup(backup);RecordBackup(key,backup);
        }
    }
}
