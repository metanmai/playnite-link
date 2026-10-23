using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class Revision
    {
        public int Version = 1;
        public string Id = Guid.NewGuid().ToString("N");
        public string Game;
        public string Kind;
        public string Device;
        public string CreatedUtc = DateTimeOffset.UtcNow.ToString("O");
        [JsonProperty(NullValueHandling=NullValueHandling.Ignore)] public string MigrationOf;
        public List<string> Parents = new List<string>();
        public SortedDictionary<string, string> Data = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string Canonical() { return JsonConvert.SerializeObject(this, Formatting.None); }
        public string Hash() { return Identity.Hash(Canonical()); }
        public string ContentHash() { return ContentHashOf(Data); }
        public static string ContentHashOf(SortedDictionary<string,string> data) { return Identity.Hash(JsonConvert.SerializeObject(data, Formatting.None)); }
        public void Validate()
        {
            Guid id, device; DateTimeOffset date;
            if ((Version != 1 && Version != 2 && Version != 3) || (Version==3&&Kind!="save") || !Guid.TryParseExact(Id, "N", out id) || !Guid.TryParseExact(Device, "N", out device) ||
                !Regex.IsMatch(Game ?? "", "^[a-f0-9]{64}$") || (Kind != "save" && Kind != "preferences" && Kind != "setup") ||
                !DateTimeOffset.TryParse(CreatedUtc, out date) || Parents == null || Parents.Count > 256 ||
                Parents.Distinct().Count() != Parents.Count || Parents.Any(p => !Regex.IsMatch(p ?? "", "^[a-f0-9]{64}$")) || Data == null)
                throw new InvalidDataException("Invalid vault revision.");
            if (Kind == "save") {SaveFiles.Validate(Data);if(Version==3)SavePlan.ValidateLayout(Data);}
            else if(Kind=="setup")SetupComponent.Validate(Game,Data);
            else if (Data.Count != 3 || !Data.ContainsKey("Favorite") || !Data.ContainsKey("Hidden") || !Data.ContainsKey("Notes") ||
                (Data["Favorite"] != "True" && Data["Favorite"] != "False") || (Data["Hidden"] != "True" && Data["Hidden"] != "False") ||
                Data["Notes"] == null || Data["Notes"].Length > 100000)
                throw new InvalidDataException("Invalid portable preferences.");
            if(MigrationOf!=null&&(Kind!="save"||Version!=2||!Parents.Contains(MigrationOf)||!Regex.IsMatch(MigrationOf,"^[a-f0-9]{64}$")))throw new InvalidDataException("Invalid save migration.");
        }
    }

    public static class SaveFiles
    {
        public static void Validate(SortedDictionary<string,string> data)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in data)
            {
                var parts = pair.Key.Split('/');
                if (pair.Key.Length > 220 || !names.Add(pair.Key) || parts.Any(p => p.Length == 0 || p == "." || p == ".." ||
                    p.EndsWith(" ") || p.EndsWith(".") || p.Any(c => c < 32 || "<>:\\|?*\"".Contains(c)) ||
                    Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\.|$)", RegexOptions.IgnoreCase)))
                    throw new InvalidDataException("Unsafe or colliding save path: " + pair.Key);
                if (pair.Value == null) throw new InvalidDataException("Invalid save content.");
                if(pair.Value.StartsWith("blob:",StringComparison.Ordinal))BlobReference.Parse(pair.Value);
                else Convert.FromBase64String(pair.Value); // Legacy v1 snapshots remain readable.
            }
            foreach (var name in names)
            {
                var parts = name.Split('/'); string prefix = "";
                for (int i = 0; i < parts.Length-1; i++) { prefix += (i == 0 ? "" : "/") + parts[i]; if (names.Contains(prefix)) throw new InvalidDataException("File/directory collision."); }
            }
        }
        public static string Root(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new ArgumentException("Choose an absolute, dedicated game save directory.");
            var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var forbidden = new[] { Path.GetPathRoot(path).TrimEnd('\\','/'), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
            if (forbidden.Any(p => String.Equals(path, p, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Select the game's own subfolder, not a drive or shared system folder.");
            CheckAncestors(path); return path;
        }
        public static void CheckAncestors(string path)
        {
            for (var current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Junctions/symlinks are not supported in save paths: " + current);
        }
        private static void Walk(string root, string path, SortedDictionary<string,string> data, ref long bytes)
        {
            foreach (var file in Directory.GetFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Save folder contains a junction or symlink.");
                if ((attributes & FileAttributes.Directory) != 0) { Walk(root, file, data, ref bytes); continue; }
                var info = new FileInfo(file); bytes += info.Length;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var memory = new MemoryStream())
                { stream.CopyTo(memory); data.Add(file.Substring(root.Length+1).Replace('\\','/'), Convert.ToBase64String(memory.ToArray())); }
            }
        }
        public static SortedDictionary<string,string> Capture(string folder)
        {
            var root = Root(folder); var data = new SortedDictionary<string,string>(StringComparer.Ordinal); long bytes = 0;
            if (Directory.Exists(root)) Walk(root, root, data, ref bytes);
            Validate(data); return data;
        }
        public static string Restore(string folder, SortedDictionary<string,string> data, string expected)
        {
            Validate(data); var root = Root(folder);
            if (Revision.ContentHashOf(Capture(root)) != expected) throw new IOException("Local saves changed during sync. Retry after the writer stops.");
            var suffix = Guid.NewGuid().ToString("N"); var stage = root + ".pl-stage-" + suffix; var backup = root + ".pl-backup-" + suffix;
            Directory.CreateDirectory(stage);
            try
            {
                foreach (var pair in data)
                {
                    var path = Path.Combine(stage, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, Convert.FromBase64String(pair.Value));
                }
                CheckAncestors(root);
                if (Revision.ContentHashOf(Capture(root)) != expected) throw new IOException("Local saves changed while restore was staging.");
                if (Directory.Exists(root)) Directory.Move(root, backup);
                try { Directory.Move(stage, root); }
                catch { if (Directory.Exists(backup) && !Directory.Exists(root)) Directory.Move(backup, root); throw; }
                return Directory.Exists(backup) ? backup : null;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
    }

    public sealed class Checkpoint { public string Revision; public string Content; }
    public sealed class SaveLink
    {
        public string Folder;
        public List<SaveSource> Sources = new List<SaveSource>();
        public List<string> RestoreExclusions = new List<string>();
        public bool AutoBackup = true, NotifyBackup = true, CheckBeforeLaunch = true;
        public bool ConfirmBeforeBackup = true, RestoreOnInstall = true;
        public string BackupRoot;
        public int MaxAutoBackups = 10;
        public int NextSourceId = 2;
        public string EmulatorDirectory;
        public Checkpoint Save = new Checkpoint(); public Checkpoint Preferences = new Checkpoint();
        public bool HasSaves { get { return !String.IsNullOrWhiteSpace(Folder)||(Sources!=null&&Sources.Count>0); } }
    }
    public sealed class VaultState
    {
        public int Version = 1;
        public string Remote = "";
        public string ConnectionIdentity = "";
        public Dictionary<string, SaveLink> Links = new Dictionary<string,SaveLink>();
    }
    public sealed class Conflict
    {
        public string Kind;
        public string Game;
        public string LocalHash;
        public List<Revision> Heads;
        public bool EmptyLocal;
        public bool MissingSource;
    }
    public sealed class ReconcileResult { public bool Cancelled; public bool Changed; public SortedDictionary<string,string> Data; public string Revision; }
    public sealed class Vault
    {
        public readonly string Objects;
        private readonly string statePath;
        private sealed class CachedObject { public long Length; public DateTime Modified; public Revision Revision; }
        private readonly Dictionary<string,CachedObject> readCache = new Dictionary<string,CachedObject>(StringComparer.OrdinalIgnoreCase);
        public VaultState State { get; private set; }
        public Vault(string root)
        {
            Objects = Path.Combine(root, "vault-objects"); Directory.CreateDirectory(Objects); statePath = Path.Combine(root, "vault-state.json");
            State = File.Exists(statePath) ? JsonSafety.Deserialize<VaultState>(File.ReadAllText(statePath)) : new VaultState();
            if (State == null || State.Version != 1 || State.Links == null || State.Remote == null ||
                State.Links.Any(p => !Regex.IsMatch(p.Key,"^[a-f0-9]{64}$") || p.Value == null || p.Value.Save == null || p.Value.Preferences == null))
                throw new InvalidDataException("Damaged vault state. Restore a reviewed .bak file.");
        }
        public void Save() { Atomic.Write(statePath, JsonConvert.SerializeObject(State,Formatting.Indented)); }
        public void Pin(string remote)
        {
            remote = remote.TrimEnd('/');
            if (State.Remote.Length > 0 && State.Remote != remote) throw new InvalidOperationException("This profile is pinned to another personal vault. Use a separate Playnite profile to switch accounts.");
            State.Remote = remote; Save();
        }
        public void PinVerified(string remote,string identity,string previousIdentity)
        {
            remote=remote.TrimEnd('/');
            var expected=String.IsNullOrWhiteSpace(State.ConnectionIdentity)?previousIdentity:State.ConnectionIdentity;
            if(State.Remote.Length>0&&(remote!=State.Remote||!String.IsNullOrEmpty(expected)||!String.IsNullOrEmpty(identity)))
                if(String.IsNullOrEmpty(expected)||String.IsNullOrEmpty(identity)||expected!=identity)
                    throw new InvalidOperationException("This is a different cloud vault. Select the original account and folder to keep your existing history together. Your connection was not changed.");
            var oldRemote=State.Remote;var oldIdentity=State.ConnectionIdentity;
            try{State.Remote=remote;State.ConnectionIdentity=identity??"";Save();}
            catch{State.Remote=oldRemote;State.ConnectionIdentity=oldIdentity;throw;}
        }
        public void Add(Revision revision)
        { revision.Validate(); Atomic.Write(Path.Combine(Objects,revision.Hash()+".json"),revision.Canonical()); }
        public void UpgradeLegacySaves(BlobStore blobs,string device)
        {
            var all=Read(Objects);
            foreach(var link in State.Links.Values)
            {
                Revision previous;
                if(link.Save.Revision!=null&&all.TryGetValue(link.Save.Revision,out previous)&&previous.Version==1&&previous.Kind=="save"&&link.Save.Content==previous.ContentHash())
                    link.Save.Content=Revision.ContentHashOf(blobs.ImportLegacy(previous.Data));
            }
            var parents=new HashSet<string>(all.Values.SelectMany(r=>r.Parents));
            foreach(var pair in all.Where(p=>p.Value.Kind=="save"&&p.Value.Version==1&&p.Value.Data.Count>0&&!parents.Contains(p.Key)).ToList())
            {
                var ancestry=new HashSet<string>();var pending=new Stack<string>();pending.Push(pair.Key);
                while(pending.Count>0){var key=pending.Pop();if(!ancestry.Add(key))continue;foreach(var parent in all[key].Parents)pending.Push(parent);}
                var migrationParents=new List<string>{pair.Key};
                migrationParents.AddRange(all.Where(p=>!parents.Contains(p.Key)&&p.Value.MigrationOf!=null&&ancestry.Contains(p.Value.MigrationOf)).Select(p=>p.Key));
                Add(new Revision{Version=2,Game=pair.Value.Game,Kind="save",Device=device,MigrationOf=pair.Key,Data=blobs.ImportLegacy(pair.Value.Data),Parents=migrationParents.Distinct().OrderBy(p=>p,StringComparer.Ordinal).ToList()});
            }
            Save();
        }
        public Dictionary<string,Revision> Read(string directory)
        {
            var result = new Dictionary<string,Revision>(); long bytes = 0;
            foreach (var path in Directory.GetFiles(directory,"*.json"))
            {
                var info = new FileInfo(path);var length = info.Length; bytes += length;
                if (length > 64L*1024*1024) throw new InvalidDataException("Oversized metadata object. Save contents belong in streamed blobs.");
                CachedObject cached;
                if(readCache.TryGetValue(path,out cached)&&cached.Length==length&&cached.Modified==info.LastWriteTimeUtc)
                { result.Add(Path.GetFileNameWithoutExtension(path),cached.Revision);continue; }
                var revision = JsonSafety.Deserialize<Revision>(File.ReadAllText(path),new JsonSerializerSettings { MissingMemberHandling=MissingMemberHandling.Error });
                if (revision == null) throw new InvalidDataException("Null vault object."); revision.Validate();
                if (Path.GetFileNameWithoutExtension(path) != revision.Hash()) throw new InvalidDataException("Vault hash mismatch.");
                readCache[path]=new CachedObject{Length=length,Modified=info.LastWriteTimeUtc,Revision=revision};
                result.Add(Path.GetFileNameWithoutExtension(path),revision);
            }
            return result;
        }
        public void Import(string directory)
        {
            var all = Read(Objects);var existing=new HashSet<string>(all.Keys);var incoming=Read(directory);foreach(var pair in incoming) all[pair.Key]=pair.Value;
            ValidateGraph(all); // Validate the entire pull before changing local history.
            foreach(var pair in incoming)if(!existing.Contains(pair.Key))Add(pair.Value);
        }
        public static void ValidateGraph(Dictionary<string,Revision> all)
        {
            if(all.Values.GroupBy(r=>r.Id).Any(g=>g.Count()>1)) throw new InvalidDataException("A revision ID has conflicting content.");
            foreach(var pair in all) foreach(var parent in pair.Value.Parents)
                if(!all.ContainsKey(parent) || all[parent].Game!=pair.Value.Game || all[parent].Kind!=pair.Value.Kind)
                    throw new InvalidDataException("Incomplete or cross-game revision ancestry. Retry the pull.");
            var counts=all.ToDictionary(p=>p.Key,p=>p.Value.Parents.Count);var children=all.Keys.ToDictionary(k=>k,k=>new List<string>());
            foreach(var pair in all)foreach(var parent in pair.Value.Parents)children[parent].Add(pair.Key);
            var ready=new Queue<string>(counts.Where(p=>p.Value==0).Select(p=>p.Key));int visited=0;
            while(ready.Count>0){var key=ready.Dequeue();visited++;foreach(var child in children[key])if(--counts[child]==0)ready.Enqueue(child);}
            if(visited!=all.Count)throw new InvalidDataException("Revision cycle.");
        }
        public List<Revision> Heads(string game,string kind)
        {
            var all = Read(Objects); ValidateGraph(all); var revisions=all.Where(p=>p.Value.Game==game&&p.Value.Kind==kind).ToList();
            var parents=new HashSet<string>(revisions.SelectMany(p=>p.Value.Parents));
            return revisions.Where(p=>!parents.Contains(p.Key)).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Value).ToList();
        }
        // choice: local, cancel, or a head hash. No implicit timestamp winner.
        public ReconcileResult Reconcile(string game,string kind,string device,SortedDictionary<string,string> local,Checkpoint checkpoint,Func<Conflict,string> choose,Action<SortedDictionary<string,string>> apply,Func<SortedDictionary<string,string>,SortedDictionary<string,string>> project=null,Func<SortedDictionary<string,string>,SortedDictionary<string,string>> publishLocal=null,bool requireSourceReview=false)
        {
            if(project==null)project=d=>d;
            var heads=Heads(game,kind); var hash=Revision.ContentHashOf(local); string choice="local";
            if(heads.Count>1&&heads.All(h=>Revision.ContentHashOf(project(h.Data))==hash))choice="local";
            if(heads.Count==1 && Revision.ContentHashOf(project(heads[0].Data))==hash)
            { checkpoint.Revision=heads[0].Hash();checkpoint.Content=hash;Save();return new ReconcileResult { Data=local,Revision=checkpoint.Revision }; }
            if(heads.Count==1 && checkpoint.Content==hash && checkpoint.Revision!=null)
            {
                // Restore only if the checkpoint is an ancestor of the remote head.
                var all=Read(Objects); var todo=new Stack<string>();var seen=new HashSet<string>();todo.Push(heads[0].Hash());
                while(todo.Count>0){var id=todo.Pop();if(!seen.Add(id))continue;foreach(var p in all[id].Parents)todo.Push(p);}
                choice=seen.Contains(checkpoint.Revision) ? heads[0].Hash() : null;
            }
            else if(heads.Count==1 && heads[0].Hash()==checkpoint.Revision) choice="local";
            else if(heads.Count==1 && checkpoint.Revision==null && !SavePlan.HasFiles(local)) choice=heads[0].Hash();
            else if(heads.Count>0 && !(heads.Count>1&&heads.All(h=>Revision.ContentHashOf(project(h.Data))==hash))) choice=null;
            if(requireSourceReview||(choice=="local" && kind=="save" && !SavePlan.HasFiles(local) && heads.Any(h=>SavePlan.HasFiles(project(h.Data)))) || choice==null)
                choice=choose(new Conflict { Game=game,Kind=kind,LocalHash=hash,Heads=heads,EmptyLocal=!SavePlan.HasFiles(local),MissingSource=requireSourceReview });
            if(choice==null || choice=="cancel")return new ReconcileResult { Cancelled=true,Data=local };
            var selected=choice=="local" ? local : project(heads.Single(h=>h.Hash()==choice).Data);
            string revisionId;
            if(heads.Count==1 && choice==heads[0].Hash()) revisionId=heads[0].Hash();
            else
            {
                var publication=choice=="local"?(publishLocal==null?selected:publishLocal(selected)):heads.Single(h=>h.Hash()==choice).Data;
                var revision=new Revision { Version=kind=="save"&&publication.Keys.Any(k=>k.StartsWith(SavePlan.Mounts,StringComparison.Ordinal))?3:kind=="save"&&(publication.Count==0||publication.Values.Any(v=>v.StartsWith("blob:",StringComparison.Ordinal)))?2:1,Game=game,Kind=kind,Device=device,Parents=heads.Select(h=>h.Hash()).OrderBy(x=>x,StringComparer.Ordinal).ToList(),Data=publication };
                Add(revision); revisionId=revision.Hash();
            }
            if(Revision.ContentHashOf(selected)!=hash)apply(selected);
            checkpoint.Revision=revisionId;checkpoint.Content=Revision.ContentHashOf(selected);Save();
            return new ReconcileResult { Data=selected,Changed=true,Revision=revisionId };
        }
    }
}
