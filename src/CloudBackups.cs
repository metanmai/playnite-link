using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using SaveManager.Models;

namespace PlaytimeRclone
{
    public sealed class BackupRecord
    {
        public int Version=1;
        public string Id=Guid.NewGuid().ToString("N"),BackupId,Game,Device,Archive,Note,CreatedUtc;
        public bool Automatic,Deleted;
        public List<string> Parents=new List<string>();
        public string Canonical(){return JsonConvert.SerializeObject(this,Formatting.None);}
        public string Hash(){return Identity.Hash(Canonical());}
        public void Validate()
        {
            Guid id;DateTimeOffset date;
            if(Version!=1||!Guid.TryParseExact(Id,"N",out id)||!Guid.TryParseExact(BackupId,"N",out id)||!Guid.TryParseExact(Device,"N",out id)||!Regex.IsMatch(Game??"","^[a-f0-9]{64}$")||!DateTimeOffset.TryParse(CreatedUtc,out date)||Note==null||Note.Length>100000||Parents==null||Parents.Count>256||Parents.Distinct().Count()!=Parents.Count||Parents.Any(p=>!Regex.IsMatch(p??"","^[a-f0-9]{64}$")))throw new InvalidDataException("Invalid cloud backup record.");
            BlobReference.Parse(Archive);
        }
    }
    public sealed class BackupCatalog
    {
        public readonly string Objects;
        public BackupCatalog(string root){Objects=root;Directory.CreateDirectory(root);}
        public Dictionary<string,BackupRecord> Read()
        {
            var all=new Dictionary<string,BackupRecord>();
            foreach(var file in Directory.GetFiles(Objects,"*.json"))
            {if(new FileInfo(file).Length>1024*1024)throw new IOException("Oversized backup metadata.");var r=JsonSafety.Deserialize<BackupRecord>(File.ReadAllText(file),new JsonSerializerSettings{MissingMemberHandling=MissingMemberHandling.Error});if(r==null)throw new IOException("Null backup metadata.");r.Validate();if(Path.GetFileNameWithoutExtension(file)!=r.Hash())throw new IOException("Backup metadata hash mismatch.");all.Add(r.Hash(),r);}
            if(all.Values.GroupBy(r=>r.Id).Any(g=>g.Count()>1))throw new IOException("Conflicting backup event identity.");
            foreach(var item in all)foreach(var parent in item.Value.Parents)if(!all.ContainsKey(parent)||all[parent].BackupId!=item.Value.BackupId||all[parent].Game!=item.Value.Game)throw new IOException("Incomplete / cross-game backup ancestry.");
            var remaining=all.ToDictionary(p=>p.Key,p=>p.Value.Parents.Count);var children=all.Keys.ToDictionary(k=>k,k=>new List<string>());
            foreach(var item in all)foreach(var parent in item.Value.Parents)children[parent].Add(item.Key);
            var ready=new Queue<string>(remaining.Where(p=>p.Value==0).Select(p=>p.Key));int seen=0;while(ready.Count>0){var id=ready.Dequeue();seen++;foreach(var child in children[id])if(--remaining[child]==0)ready.Enqueue(child);}if(seen!=all.Count)throw new IOException("Backup revision cycle.");
            return all;
        }
        public List<BackupRecord> Heads(string game,string backupId=null)
        {var all=Read().Values.Where(r=>r.Game==game&&(backupId==null||r.BackupId==backupId)).ToList();var parents=new HashSet<string>(all.SelectMany(r=>r.Parents));return all.Where(r=>!parents.Contains(r.Hash())).ToList();}
        public void Add(BackupRecord record){record.Validate();Atomic.Write(Path.Combine(Objects,record.Hash()+".json"),record.Canonical());}
        public void Import(string directory)
        {
            var incoming=new BackupCatalog(directory).Read();var staging=Path.Combine(Path.GetDirectoryName(Objects),"backup-merge-"+Guid.NewGuid().ToString("N"));
            try{Directory.CreateDirectory(staging);foreach(var file in Directory.GetFiles(Objects,"*.json"))File.Copy(file,Path.Combine(staging,Path.GetFileName(file)));foreach(var pair in incoming)File.WriteAllText(Path.Combine(staging,pair.Key+".json"),pair.Value.Canonical());new BackupCatalog(staging).Read();foreach(var pair in incoming)if(!File.Exists(Path.Combine(Objects,pair.Key+".json")))Add(pair.Value);}
            finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
        }
    }
    public sealed partial class PlaytimePlugin
    {
        BackupCatalog CloudBackups{get{return new BackupCatalog(Path.Combine(Root,"backup-objects"));}}
        Dictionary<string,string> BackupBindings()
        {var path=Path.Combine(Root,"backup-bindings.json");return File.Exists(path)?JsonSafety.Deserialize<Dictionary<string,string>>(File.ReadAllText(path)):new Dictionary<string,string>();}
        void BindBackup(string local,string cloud)
        {var bindings=BackupBindings();bindings[local]=cloud;Atomic.Write(Path.Combine(Root,"backup-bindings.json"),JsonConvert.SerializeObject(bindings));}
        void RecordBackup(string key,SaveBackup backup,bool deleted=false)
        {
            var bindings=BackupBindings();string id;if(!bindings.TryGetValue(backup.Id,out id))id=backup.Id;
            var catalog=CloudBackups;var heads=catalog.Heads(key,id);string archive;
            if(deleted){if(heads.Count==0)return;archive=heads[0].Archive;}
            else
            {
                // The cloud copy uses anonymous source labels rather than sharing
                // absolute local destination paths inside upstream ZIP metadata.
                var temporary=Path.Combine(Root,"scratch","cloud-archive-"+Guid.NewGuid().ToString("N")+".zip");Directory.CreateDirectory(Path.GetDirectoryName(temporary));
                try
                {
                    File.Copy(Backups.GetFullBackupPath(backup.BackupFilePath),temporary);
                    using(var file=new FileStream(temporary,FileMode.Open,FileAccess.ReadWrite))using(var zip=new ZipArchive(file,ZipArchiveMode.Update))
                    {
                        var entry=zip.GetEntry("__save_paths__.json");if(entry==null)throw new IOException("Backup mapping missing.");JArray mapping;using(var reader=new StreamReader(entry.Open()))mapping=JsonSafety.Array(reader.ReadToEnd());
                        for(int i=0;i<mapping.Count;i++)mapping[i]["OriginalPath"]="source"+i;
                        entry.Delete();using(var writer=new StreamWriter(zip.CreateEntry("__save_paths__.json").Open()))writer.Write(mapping.ToString());
                    }
                    using(var input=File.OpenRead(temporary))archive=blobs.Put(input).ToString();
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
            catalog.Add(new BackupRecord{BackupId=id,Game=key,Device=repository.Snapshot().Device,Archive=archive,Note=backup.Description??"",Automatic=backup.IsAutoBackup,Deleted=deleted,CreatedUtc=backup.CreatedAt.ToUniversalTime().ToString("O"),Parents=heads.Select(r=>r.Hash()).OrderBy(h=>h,StringComparer.Ordinal).ToList()});
            BindBackup(backup.Id,id);
        }
        void SeedLocalBackupCatalog(List<SyncGame> games)
        {
            var bindings=BackupBindings();
            foreach(var item in games)foreach(var backup in Backups.GetBackups(item.Game.Id))if(!bindings.ContainsKey(backup.Id))RecordBackup(item.Key,backup);
        }
        void PublishCloudBackups(RcloneTransport blobTransport,string cache)
        {
            var catalog=CloudBackups;var pending=catalog.Read().Where(p=>!File.Exists(Path.Combine(cache,p.Key+".json"))).ToList();
            if(pending.Count==0)return;
            blobTransport.PushFiles(blobs.DirectoryPath,pending.Select(p=>BlobReference.Parse(p.Value.Archive).Hash+".blob"));
            new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/backups/objects",cancelled:()=>closing||TransferStopped).PushFiles(catalog.Objects,pending.Select(p=>p.Key+".json"));
        }
        void DownloadCloudBackup(Game game,string key,BackupRecord record)
        {
            var data=new SortedDictionary<string,string>{{"archive.zip",record.Archive}};blobs.Ensure(data);
            var temporary=Path.Combine(Root,"scratch","download-"+Guid.NewGuid().ToString("N")+".zip");Directory.CreateDirectory(Path.GetDirectoryName(temporary));
            try
            {
                using(var output=File.Create(temporary))blobs.CopyVerified(record.Archive,output);
                ConfigureBackupCore(game,key);SaveArchive.Read(temporary,SaveArchive.UnconfiguredPlan(temporary,blobs,Path.Combine(Root,"scratch","unconfigured-"+key)),blobs);
                var imported=Backups.ImportBackup(game.Id,game.Name,temporary);BindBackup(imported.Id,record.BackupId);
                imported.Description=record.Note;
                imported.IsAutoBackup=record.Automatic;
                imported.CreatedAt=DateTimeOffset.Parse(record.CreatedUtc,System.Globalization.CultureInfo.InvariantCulture).LocalDateTime;
                Backups.UpdateOrAddBackup(imported);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        void BuildCloudBackupLibrary(System.Windows.Controls.Panel panel,Game game,string key,Action refresh)
        {
            var card=Card(panel,"Cloud backup library","Named ZIP backups sync with your vault. Downloading adds a local backup; choose Restore separately to replace saves.");
            if(syncing!=0){Label(card,"Sync is running. Reopen after it finishes.");return;}
            var heads=CloudBackups.Heads(key);
            if(heads.All(r=>r.Deleted))Label(card,"No shared ZIP backups yet. Choose Check cloud now after creating or migrating backups.");
            foreach(var record in heads.Where(r=>!r.Deleted).OrderByDescending(r=>r.CreatedUtc))
            {
                var captured=record;var group=heads.Where(r=>r.BackupId==record.BackupId).ToList();
                var item=Card(card,record.Note.Length==0?"Save backup":record.Note,DateTimeOffset.Parse(record.CreatedUtc).ToLocalTime().ToString("g")+(group.Count>1?" · conflicting backup versions":""));
                Button(item,"Download to this device",async()=>{try{await SaveOperation(game,()=>DownloadCloudBackup(game,key,captured));refresh();QueueSync();}catch(Exception ex){Label(item,ex.Message);}});
                if(group.Count>1)Button(item,"Keep this backup version",()=>{if(syncing!=0)throw new IOException("Wait for sync first.");captured=new BackupRecord{BackupId=record.BackupId,Game=key,Device=repository.Snapshot().Device,Archive=record.Archive,Note=record.Note,Automatic=record.Automatic,CreatedUtc=record.CreatedUtc,Parents=group.Select(r=>r.Hash()).OrderBy(h=>h,StringComparer.Ordinal).ToList()};CloudBackups.Add(captured);refresh();QueueSync();});
            }
        }
    }
}
