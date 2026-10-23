using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class SaveSource
    {
        public string Id, Path;
        public bool IsDirectory = true;
    }
    public sealed class ResolvedSaveSource
    {
        public string Id, Path, Prefix;
        public bool IsDirectory;
    }
    // Save Manager supplies portable-path conversion. Link adds strict validation
    // and its journaled, content-addressed restore protocol around that core.
    public static class SavePaths
    {
        public static string Resolve(string template,string gameDir,string emulatorDir)
        {
            if(String.IsNullOrWhiteSpace(template))throw new ArgumentException("A save path is required.");
            var path=template;
            if(path.Contains("{GameDir}")){if(String.IsNullOrWhiteSpace(gameDir))throw new IOException("The game's installation directory is unavailable.");path=path.Replace("{GameDir}",gameDir);}
            if(path.Contains("{EmulatorDir}")){if(String.IsNullOrWhiteSpace(emulatorDir))throw new IOException("Choose this game's emulator directory first.");path=path.Replace("{EmulatorDir}",emulatorDir);}
            path=Environment.ExpandEnvironmentVariables(path);
            if(path.Contains("{")||path.Contains("}")||Regex.IsMatch(path,"%[^%]+%"))throw new IOException("Unresolved variable in save path: "+template);
            if(!System.IO.Path.IsPathRooted(path))throw new IOException("Use an absolute path or a supported path variable.");
            path=SaveManager.Services.PathHelper.ResolvePath(template,gameDir,emulatorDir).TrimEnd('\\','/');SaveFiles.CheckAncestors(path);return path;
        }
        public static bool Contains(string parent,string child)
        {return String.Equals(parent,child,StringComparison.OrdinalIgnoreCase)||child.StartsWith(parent.TrimEnd('\\','/')+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
        public static string Portable(string path,string gameDir,string emulatorDir)
        {
            return SaveManager.Services.PathHelper.ConvertToStoragePath(path,gameDir,emulatorDir,!String.IsNullOrWhiteSpace(gameDir)&&Contains(gameDir.TrimEnd('\\','/'),System.IO.Path.GetFullPath(path)));
        }
    }
    public sealed class SavePlan
    {
        public const string Mounts="__pl_roots/";
        public static bool IsLayout(string key){return Regex.IsMatch(key??"",@"^__pl_roots/[a-z0-9_-]{1,40}/\.pl-source-(directory|file)$");}
        public static bool HasFiles(SortedDictionary<string,string> data){return data.Keys.Any(k=>!IsLayout(k));}
        public static int FileCount(SortedDictionary<string,string> data){return data.Keys.Count(k=>!IsLayout(k));}
        public static void ValidateLayout(SortedDictionary<string,string> data)
        {
            var layout=data.Keys.Where(IsLayout).ToList();if(layout.Count==0)throw new InvalidDataException("A multi-source snapshot needs source declarations.");
            var types=new Dictionary<string,bool>(StringComparer.Ordinal);
            foreach(var key in layout)
            {
                var parts=key.Split('/');if(types.ContainsKey(parts[1]))throw new InvalidDataException("Conflicting save source declarations.");types.Add(parts[1],parts[2]==".pl-source-directory");
                var marker=BlobReference.Parse(data[key]);if(marker.Length!=0||marker.Hash!="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")throw new InvalidDataException("Invalid save source declaration content.");
            }
            foreach(var key in data.Keys.Where(k=>!IsLayout(k)))
            {
                if(key.StartsWith(Mounts,StringComparison.Ordinal))
                {var parts=key.Split('/');bool directory;if(parts.Length<3||!types.TryGetValue(parts[1],out directory)||(!directory&&(parts.Length!=3||parts[2]!="content")))throw new InvalidDataException("Undeclared or invalid save source contents.");}
                else {bool directory;if(!types.TryGetValue("main",out directory)||!directory)throw new InvalidDataException("Missing primary folder declaration.");}
            }
        }
        public readonly List<ResolvedSaveSource> Sources=new List<ResolvedSaveSource>();
        readonly List<string> exclusions=new List<string>();
        readonly BlobStore blobs;
        readonly string journal;
        public SavePlan(SaveLink link,string gameDir,BlobStore blobs,string journalPath)
        {
            this.blobs=blobs;journal=journalPath;
            var definitions=link.Sources!=null&&link.Sources.Count>0?link.Sources:new List<SaveSource>{new SaveSource{Id="main",Path=link.Folder}};
            foreach(var source in definitions)
            {
                if(source==null||!Regex.IsMatch(source.Id??"","^[a-z0-9_-]{1,40}$")||Sources.Any(s=>s.Id==source.Id))throw new InvalidDataException("Save source IDs must be unique letters, numbers, underscores or hyphens.");
                var path=SavePaths.Resolve(source.Path,gameDir,link.EmulatorDirectory);
                if(source.IsDirectory)SaveFiles.Root(path);
                else {SaveFiles.CheckAncestors(path);if(Directory.Exists(path)||String.IsNullOrEmpty(System.IO.Path.GetFileName(path)))throw new IOException("A save-file source must name a file.");SaveFiles.Validate(new SortedDictionary<string,string>{{System.IO.Path.GetFileName(path),""}});}
                if(Sources.Any(s=>SavePaths.Contains(s.Path,path)||SavePaths.Contains(path,s.Path)))throw new IOException("Save sources must not overlap.");
                Sources.Add(new ResolvedSaveSource{Id=source.Id,Path=path,IsDirectory=source.IsDirectory,Prefix=source.Id=="main"&&source.IsDirectory?"":Mounts+source.Id+"/"});
            }
            if(Sources.Count==0)throw new IOException("Choose at least one save path.");
            foreach(var value in link.RestoreExclusions??new List<string>())
            {
                var path=SavePaths.Resolve(value,gameDir,link.EmulatorDirectory);
                if(!Sources.Any(s=>s.IsDirectory?SavePaths.Contains(s.Path,path):String.Equals(s.Path,path,StringComparison.OrdinalIgnoreCase)))throw new IOException("A restore exclusion must be inside a configured save source.");
                exclusions.Add(path);
            }
        }
        public bool Protected(string key)
        {
            if(IsLayout(key))return false;
            var target=Target(key);return exclusions.Any(e=>SavePaths.Contains(e,target));
        }
        bool HasLayout{get{return Sources.Count!=1||Sources[0].Id!="main"||!Sources[0].IsDirectory;}}
        public void AddLayout(SortedDictionary<string,string> data)
        {
            if(!HasLayout)return;string empty;using(var stream=new MemoryStream(new byte[0]))empty=blobs.Put(stream).ToString();
            foreach(var source in Sources)data.Add(Mounts+source.Id+"/.pl-source-"+(source.IsDirectory?"directory":"file"),empty);
        }
        public string Target(string key)
        {
            var source=Sources.FirstOrDefault(s=>s.Prefix.Length>0&&key.StartsWith(s.Prefix,StringComparison.Ordinal));
            if(source==null)
            {
                if(key.StartsWith(Mounts,StringComparison.OrdinalIgnoreCase))throw new IOException("This backup includes an unconfigured save source. Import its configuration or configure matching source IDs first.");
                source=Sources.FirstOrDefault(s=>s.Prefix=="");
            }
            if(source==null)throw new IOException("The backup's primary save source is not configured.");
            var relative=key.Substring(source.Prefix.Length);
            if(!source.IsDirectory){if(relative!="content")throw new IOException("Invalid single-file save entry.");return source.Path;}
            return System.IO.Path.Combine(source.Path,relative.Replace('/',System.IO.Path.DirectorySeparatorChar));
        }
        public SortedDictionary<string,string> Filter(SortedDictionary<string,string> data)
        {
            SaveFiles.Validate(data);var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
            var layout=data.Keys.Where(IsLayout).OrderBy(x=>x,StringComparer.Ordinal).ToArray();var expected=HasLayout?Sources.Select(s=>Mounts+s.Id+"/.pl-source-"+(s.IsDirectory?"directory":"file")).OrderBy(x=>x,StringComparer.Ordinal).ToArray():new string[0];
            if(!layout.SequenceEqual(expected))throw new IOException("This snapshot uses a different save-source layout. Configure matching folders/files before syncing or restoring. Existing saves were kept.");
            if(layout.Length>0)ValidateLayout(data);
            foreach(var pair in data){if(IsLayout(pair.Key)){result.Add(pair.Key,pair.Value);continue;}Target(pair.Key);if(!Protected(pair.Key))result.Add(pair.Key,pair.Value);}return result;
        }
        public bool NeedsSourceReview(SortedDictionary<string,string> previous,SortedDictionary<string,string> current)
        {
            var comparable=new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in previous.Where(p=>!IsLayout(p.Key)))
            {try{Target(pair.Key);if(!Protected(pair.Key))comparable.Add(pair.Key,pair.Value);}catch(IOException){/* Source intentionally removed from this configuration. */}}
            foreach(var source in Sources)
            {
                Func<string,bool> belongs=key=>source.Prefix==""?!key.StartsWith(Mounts,StringComparison.Ordinal):key.StartsWith(source.Prefix,StringComparison.Ordinal);
                if(comparable.Keys.Any(belongs)&&!current.Keys.Where(k=>!IsLayout(k)).Any(belongs))return true;
            }
            return false;
        }
        public SortedDictionary<string,string> ForPublication(SortedDictionary<string,string> full,SortedDictionary<string,string> previous)
        {
            var result=new SortedDictionary<string,string>(full,StringComparer.Ordinal);
            foreach(var pair in previous.Where(p=>!IsLayout(p.Key)&&!result.ContainsKey(p.Key)))
            {try{if(Protected(pair.Key))result.Add(pair.Key,pair.Value);}catch(IOException){/* An old, explicitly removed source is not published again. */}}
            return result;
        }
        public SortedDictionary<string,string> Capture(bool includeProtected=false)
        {
            Recover();return CaptureRaw(includeProtected);
        }
        SortedDictionary<string,string> CaptureRaw(bool includeProtected)
        {
            var result=new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach(var source in Sources)
            {
                if(source.IsDirectory)
                {
                    foreach(var pair in blobs.Capture(source.Path))
                    {
                        if(source.Prefix==""&&pair.Key.StartsWith(Mounts,StringComparison.OrdinalIgnoreCase))throw new IOException("The save folder uses Link's reserved __pl_roots directory name.");
                        var key=source.Prefix+pair.Key;if(includeProtected||!Protected(key))result.Add(key,pair.Value);
                    }
                }
                else if(File.Exists(source.Path))
                {
                    var key=source.Prefix+"content";if(includeProtected||!Protected(key))using(var input=new FileStream(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read))result.Add(key,blobs.Put(input).ToString());
                }
            }
            AddLayout(result);SaveFiles.Validate(result);return result;
        }
        sealed class Recovery
        {
            public int Version=1;
            public List<ResolvedSaveSource> Sources;
            public SortedDictionary<string,string> Before;
            public List<string> Absent;
        }
        public void Recover()
        {
            foreach(var source in Sources.Where(s=>s.IsDirectory))BlobStore.RecoverRestore(source.Path);
            if(!File.Exists(journal))return;
            var state=JsonSafety.Deserialize<Recovery>(File.ReadAllText(journal));
            if(state==null||state.Version!=1||state.Sources==null||state.Before==null||state.Absent==null||JsonConvert.SerializeObject(state.Sources)!=JsonConvert.SerializeObject(Sources))throw new IOException("An interrupted save replacement needs its original path configuration. Restore that configuration before launching.");
            SaveFiles.Validate(state.Before);foreach(var pair in state.Before)if(!IsLayout(pair.Key))Target(pair.Key);blobs.Ensure(state.Before);
            var cancellation=blobs.CancelRequested;
            try{blobs.CancelRequested=null;Apply(state.Before,null);}finally{blobs.CancelRequested=cancellation;}
            foreach(var source in Sources.Where(s=>state.Absent.Contains(s.Id)))
            {if(source.IsDirectory&&Directory.Exists(source.Path)&&!Directory.EnumerateFileSystemEntries(source.Path).Any())Directory.Delete(source.Path);}
            File.Delete(journal);
        }
        public string Restore(SortedDictionary<string,string> data,string expected,Action beforeReplace=null)
        {
            Recover();var incoming=Filter(data);blobs.Ensure(incoming);foreach(var value in incoming.Values)blobs.CopyVerified(value,Stream.Null);
            if(Revision.ContentHashOf(CaptureRaw(false))!=expected)throw new IOException("Local saves changed; restore deferred.");
            var before=CaptureRaw(true);var merged=new SortedDictionary<string,string>(incoming,StringComparer.Ordinal);
            foreach(var pair in before)if(Protected(pair.Key))merged[pair.Key]=pair.Value;
            if(beforeReplace!=null)beforeReplace();
            if(Revision.ContentHashOf(CaptureRaw(true))!=Revision.ContentHashOf(before))throw new IOException("Local saves changed while restore was preparing.");
            // Preserve the existing single-folder crash protocol and backups.
            if(Sources.Count==1&&Sources[0].IsDirectory&&Sources[0].Prefix=="")return blobs.Restore(Sources[0].Path,merged,Revision.ContentHashOf(before));
            var receipt=new Recovery{Sources=Sources,Before=before,Absent=Sources.Where(s=>!File.Exists(s.Path)&&!Directory.Exists(s.Path)).Select(s=>s.Id).ToList()};
            Atomic.Write(journal,JsonConvert.SerializeObject(receipt));
            try{Apply(merged,before);File.Delete(journal);}catch{try{Recover();}catch{/* Durable rollback record remains; next launch fails closed. */}throw;}
            return journal+".bak";
        }
        void Apply(SortedDictionary<string,string> data,SortedDictionary<string,string> expected)
        {
            foreach(var source in Sources)
            {
                SaveFiles.CheckAncestors(source.Path);
                var part=new SortedDictionary<string,string>(StringComparer.Ordinal);
                foreach(var pair in data)
                {
                    if(IsLayout(pair.Key))continue;
                    var target=Target(pair.Key);
                    if(source.IsDirectory&&SavePaths.Contains(source.Path,target))part.Add(target.Substring(source.Path.Length+1).Replace('\\','/'),pair.Value);
                }
                var current=source.IsDirectory?blobs.Capture(source.Path):new SortedDictionary<string,string>(StringComparer.Ordinal);
                if(!source.IsDirectory&&File.Exists(source.Path))using(var input=File.OpenRead(source.Path))current.Add("content",blobs.Put(input).ToString());
                if(expected!=null)
                {
                    var prior=new SortedDictionary<string,string>(StringComparer.Ordinal);
                    foreach(var pair in expected)
                    {
                        if(IsLayout(pair.Key))continue;
                        var target=Target(pair.Key);
                        if(source.IsDirectory&&SavePaths.Contains(source.Path,target))prior.Add(target.Substring(source.Path.Length+1).Replace('\\','/'),pair.Value);
                        else if(!source.IsDirectory&&pair.Key==source.Prefix+"content")prior.Add("content",pair.Value);
                    }
                    if(Revision.ContentHashOf(current)!=Revision.ContentHashOf(prior))throw new IOException("A save source changed during restore. The transaction was rolled back.");
                }
                if(source.IsDirectory){blobs.Restore(source.Path,part,Revision.ContentHashOf(current));continue;}
                string value;
                if(data.TryGetValue(source.Prefix+"content",out value))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(source.Path));var temp=source.Path+".pl-stage-"+Guid.NewGuid().ToString("N");
                    try{using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){blobs.CopyVerified(value,output);output.Flush(true);}SaveFiles.CheckAncestors(source.Path);if(File.Exists(source.Path))File.Replace(temp,source.Path,source.Path+".pl-backup-"+Guid.NewGuid().ToString("N"));else File.Move(temp,source.Path);}
                    finally{if(File.Exists(temp))File.Delete(temp);}
                }
                else if(File.Exists(source.Path))File.Move(source.Path,source.Path+".pl-backup-"+Guid.NewGuid().ToString("N"));
            }
        }
    }
}
