using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlaytimeRclone
{
    // Package history is separate from game history. No Playnite or add-on settings
    // directories, sign-ins, passcodes or startup preferences enter this protocol.
    public static class SetupComponent
    {
        public static string Target(SortedDictionary<string,string> data){return data["Target"];}
        public static SortedDictionary<string,string> Files(SortedDictionary<string,string> data)
        {return JsonSafety.Deserialize<SortedDictionary<string,string>>(data["Files"]);}
        public static void Validate(string key,SortedDictionary<string,string> data)
        {
            if(data.Count!=6||!new[]{"Identity","Target","Name","Version","Removed","Files"}.All(data.ContainsKey))throw new InvalidDataException("Invalid add-on package metadata.");
            if(String.IsNullOrWhiteSpace(data["Identity"])||Identity.Hash(data["Identity"])!=key||data["Identity"].Length>300)throw new InvalidDataException("Invalid add-on identity.");
            var parts=data["Target"].Split('/');
            if(!((parts.Length==2&&parts[0]=="Extensions")||(parts.Length==3&&parts[0]=="Themes"&&(parts[1]=="Desktop"||parts[1]=="Fullscreen"))))throw new InvalidDataException("Invalid add-on destination.");
            var folder=parts.Last();
            if(!Regex.IsMatch(folder,"^[A-Za-z0-9_(][A-Za-z0-9_. ()-]{0,179}$")||Regex.IsMatch(folder,@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)",RegexOptions.IgnoreCase)||folder.EndsWith(".")||folder.EndsWith(" ")||folder.EndsWith("_Builtin",StringComparison.OrdinalIgnoreCase)||folder.IndexOf("PlayniteLink",StringComparison.OrdinalIgnoreCase)>=0||data["Identity"].IndexOf("8ca18482-3ebe-4bf7-b229-a259b2094767",StringComparison.OrdinalIgnoreCase)>=0)throw new InvalidDataException("Protected or unsafe add-on destination.");
            if(data["Name"].Length>300||data["Version"].Length>80||(data["Removed"]!="True"&&data["Removed"]!="False"))throw new InvalidDataException("Invalid add-on description.");
            var files=Files(data);if(files==null)throw new InvalidDataException("Missing package contents.");SaveFiles.Validate(files);
            if(files.Values.Any(v=>!v.StartsWith("blob:",StringComparison.Ordinal)))throw new InvalidDataException("Packages require verified streamed blobs.");
            var manifest=parts[0]=="Extensions"?"extension.yaml":"theme.yaml";
            if(data["Removed"]=="True"?files.Count!=0:!files.ContainsKey(manifest))throw new InvalidDataException("Missing package manifest or invalid removal.");
        }
    }
    public sealed class SetupOptions
    {
        public bool Enabled;
        public HashSet<string> LocalOnly=new HashSet<string>(StringComparer.Ordinal);
    }
    public sealed class SetupState
    {
        public Dictionary<string,Checkpoint> Checkpoints=new Dictionary<string,Checkpoint>();
        public Dictionary<string,SortedDictionary<string,string>> Local=new Dictionary<string,SortedDictionary<string,string>>();
        public Dictionary<string,string> Fingerprints=new Dictionary<string,string>();
    }
    public sealed class SetupChange
    {
        public string Key;
        public SortedDictionary<string,string> Local;
        public List<Revision> Heads;
    }
    public sealed class SetupInstall
    {
        public string Key,Target,Stage;
        public bool Removed;
        public SortedDictionary<string,string> Expected;
        public SortedDictionary<string,string> Desired;
        public Checkpoint Checkpoint;
    }
    public sealed class SetupPlan
    {
        public string Profile,DataDirectory;
        public List<SetupInstall> Installs=new List<SetupInstall>();
    }
    public sealed class SetupProfile
    {
        public readonly Vault History;
        public readonly string DirectoryPath;
        public SetupState State;
        public SetupOptions Options;
        public bool RecoveryRequired{get{return File.Exists(Path.Combine(DirectoryPath,"apply-journal.json"));}}
        private readonly string profile;
        private readonly BlobStore blobs;
        public SetupProfile(string root,string profile,BlobStore blobs)
        {
            DirectoryPath=Path.Combine(root,"setup");Directory.CreateDirectory(DirectoryPath);
            this.profile=Path.GetFullPath(profile);this.blobs=blobs;History=new Vault(DirectoryPath);
            State=Read<SetupState>("state.json")??new SetupState();Options=Read<SetupOptions>("options.json")??new SetupOptions();
            if(State.Checkpoints==null||State.Local==null||State.Fingerprints==null||Options.LocalOnly==null)throw new InvalidDataException("Damaged setup sync state.");
            var receipt=RecoveryRequired?null:Read<SetupPlan>("applied.json");
            if(receipt!=null){foreach(var item in receipt.Installs)State.Checkpoints[item.Key]=item.Checkpoint;Save();File.Move(Path.Combine(DirectoryPath,"applied.json"),Path.Combine(DirectoryPath,"applied-"+Guid.NewGuid().ToString("N")+".json"));}
        }
        private T Read<T>(string name) where T:class
        {var path=Path.Combine(DirectoryPath,name);return File.Exists(path)?JsonSafety.Deserialize<T>(File.ReadAllText(path)):null;}
        public void Save(){Atomic.Write(Path.Combine(DirectoryPath,"state.json"),JsonConvert.SerializeObject(State));}
        public void SaveOptions(){Atomic.Write(Path.Combine(DirectoryPath,"options.json"),JsonConvert.SerializeObject(Options));}
        public static string ManifestField(string path,string field)
        {
            foreach(var line in File.ReadLines(path)){var match=Regex.Match(line,"^"+Regex.Escape(field)+@"\s*:\s*(.*?)\s*$");if(match.Success)return match.Groups[1].Value.Trim('"','\'');}
            return "";
        }
        public void Capture(string device,bool force=false)
        {
            if(RecoveryRequired)throw new IOException("An interrupted add-on installation needs recovery. Open Add-ons and recover before sharing changes.");
            var seen=new HashSet<string>();
            foreach(var area in new[]{"Extensions","Themes/Desktop","Themes/Fullscreen"})
            {
                var directory=Path.Combine(profile,area.Replace('/',Path.DirectorySeparatorChar));if(!System.IO.Directory.Exists(directory))continue;
                SaveFiles.CheckAncestors(directory);
                foreach(var folder in System.IO.Directory.GetDirectories(directory))
                {
                    if(Path.GetFileName(folder).EndsWith("_Builtin",StringComparison.OrdinalIgnoreCase))continue;
                    var manifest=Path.Combine(folder,area=="Extensions"?"extension.yaml":"theme.yaml");if(!File.Exists(manifest))continue;
                    SaveFiles.CheckAncestors(folder);
                    var id=ManifestField(manifest,"Id");if(String.IsNullOrWhiteSpace(id)||id.StartsWith("Playnite_builtin_",StringComparison.OrdinalIgnoreCase)||id.IndexOf("8ca18482-3ebe-4bf7-b229-a259b2094767",StringComparison.OrdinalIgnoreCase)>=0)continue;
                    var key=Identity.Hash(area+":"+id);if(!seen.Add(key))throw new InvalidDataException("Duplicate installed add-on identity: "+id);
                    if(Options.LocalOnly.Contains(key))continue;
                    var stamp=Fingerprint(folder);string old;
                    if(!force&&State.Fingerprints.TryGetValue(key,out old)&&old==stamp)continue;
                    var data=new SortedDictionary<string,string>(StringComparer.Ordinal){{"Identity",area+":"+id},{"Target",area+"/"+Path.GetFileName(folder)},{"Name",ManifestField(manifest,"Name")},{"Version",ManifestField(manifest,"Version")},{"Removed","False"},{"Files",JsonConvert.SerializeObject(blobs.Capture(folder))}};
                    Observe(key,data,device);State.Fingerprints[key]=stamp;
                }
            }
            foreach(var pair in State.Local.ToArray())
                if(!seen.Contains(pair.Key)&&!Options.LocalOnly.Contains(pair.Key)&&pair.Value["Removed"]!="True")
                {
                    // A missing package is a removal only if its parent area exists.
                    var parent=Path.Combine(profile,Path.GetDirectoryName(pair.Value["Target"].Replace('/',Path.DirectorySeparatorChar)));
                    if(!System.IO.Directory.Exists(parent))continue;
                    // A missing/rewritten manifest during an update is not an uninstall.
                    var installed=Path.Combine(profile,pair.Value["Target"].Replace('/',Path.DirectorySeparatorChar));
                    if(System.IO.Directory.Exists(installed)||File.Exists(installed))continue;
                    var removed=new SortedDictionary<string,string>(pair.Value,StringComparer.Ordinal);removed["Removed"]="True";removed["Files"]="{}";Observe(pair.Key,removed,device);State.Fingerprints.Remove(pair.Key);
                }
            Save();
        }
        private static string Fingerprint(string root)
        {
            var items=new List<string>();var pending=new Stack<string>();pending.Push(root);
            while(pending.Count!=0){var directory=pending.Pop();SaveFiles.CheckAncestors(directory);foreach(var file in System.IO.Directory.GetFiles(directory)){SaveFiles.CheckAncestors(file);var info=new FileInfo(file);items.Add(file.Substring(root.Length)+":"+info.Length+":"+info.LastWriteTimeUtc.Ticks);}foreach(var child in System.IO.Directory.GetDirectories(directory))pending.Push(child);}
            return Identity.Hash(String.Join("\n",items.OrderBy(x=>x,StringComparer.Ordinal)));
        }
        private void Observe(string key,SortedDictionary<string,string> data,string device)
        {
            SetupComponent.Validate(key,data);Checkpoint checkpoint;
            if(State.Checkpoints.TryGetValue(key,out checkpoint)&&checkpoint.Content==Revision.ContentHashOf(data)){State.Local[key]=data;return;}
            var revision=new Revision{Game=key,Kind="setup",Device=device,Data=data};
            if(checkpoint!=null&&checkpoint.Revision!=null)revision.Parents.Add(checkpoint.Revision);
            History.Add(revision);State.Local[key]=data;State.Checkpoints[key]=new Checkpoint{Revision=revision.Hash(),Content=revision.ContentHash()};Save();
        }
        public List<SetupChange> Changes()
        {
            var all=History.Read(History.Objects);Vault.ValidateGraph(all);var result=new List<SetupChange>();
            var acknowledged=false;
            foreach(var key in all.Values.Select(r=>r.Game).Distinct().OrderBy(x=>x,StringComparer.Ordinal))
            {
                if(Options.LocalOnly.Contains(key))continue;
                var heads=History.Heads(key,"setup");Checkpoint checkpoint;State.Checkpoints.TryGetValue(key,out checkpoint);
                if(heads.Count==1&&checkpoint!=null&&checkpoint.Content==heads[0].ContentHash())
                {if(checkpoint.Revision!=heads[0].Hash()){checkpoint.Revision=heads[0].Hash();acknowledged=true;}continue;}
                SortedDictionary<string,string> local;State.Local.TryGetValue(key,out local);
                if(local==null&&heads.All(r=>r.Data["Removed"]=="True"))continue;
                result.Add(new SetupChange{Key=key,Local=local,Heads=heads});
            }
            if(acknowledged)Save();return result;
        }
        public void MergeEquivalent(string device)
        {
            foreach(var key in History.Read(History.Objects).Values.Select(r=>r.Game).Distinct().ToArray())
            {
                if(Options.LocalOnly.Contains(key))continue;
                var heads=History.Heads(key,"setup");
                if(heads.Count>1&&heads.Select(r=>r.ContentHash()).Distinct().Count()==1)
                    History.Add(new Revision{Game=key,Kind="setup",Device=device,Data=heads[0].Data,Parents=heads.Select(r=>r.Hash()).OrderBy(x=>x,StringComparer.Ordinal).ToList()});
            }
        }
        // Choices are component-specific; resolving one package cannot replace another.
        public SetupPlan Stage(IDictionary<string,string> choices,string device)
        {
            if(RecoveryRequired)throw new IOException("Recover the interrupted installation before staging another update.");
            var plan=new SetupPlan{Profile=profile,DataDirectory=DirectoryPath};
            var stageRoot=Path.Combine(DirectoryPath,"staged",Guid.NewGuid().ToString("N").Substring(0,12));System.IO.Directory.CreateDirectory(stageRoot);
            foreach(var change in Changes())
            {
                string selected;if(!choices.TryGetValue(change.Key,out selected))continue;
                var revision=change.Heads.Single(r=>r.Hash()==selected);var data=revision.Data;SetupComponent.Validate(change.Key,data);
                if(data["Removed"]=="True"&&data["Target"].StartsWith("Themes/",StringComparison.Ordinal))
                {
                    var settingsPath=Path.Combine(profile,data["Target"].StartsWith("Themes/Fullscreen/",StringComparison.Ordinal)?"fullscreenConfig.json":"config.json");
                    if(File.Exists(settingsPath)){
                        var active=(string)JsonSafety.Object(File.ReadAllText(settingsPath))["Theme"];
                        var identity=data["Identity"];var id=identity.Substring(identity.IndexOf(':')+1);
                        if(String.Equals(active,id,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("This theme is active on this device. Select another local theme or keep this add-on device-only before removing it.");
                    }
                }
                if(change.Heads.Count>1){revision=new Revision{Game=change.Key,Kind="setup",Device=device,Data=data,Parents=change.Heads.Select(r=>r.Hash()).OrderBy(x=>x,StringComparer.Ordinal).ToList()};History.Add(revision);}
                var target=Path.Combine(profile,data["Target"].Replace('/',Path.DirectorySeparatorChar));SaveFiles.CheckAncestors(target);
                var expected=System.IO.Directory.Exists(target)?blobs.Capture(target):new SortedDictionary<string,string>(StringComparer.Ordinal);
                if(change.Local==null?expected.Count!=0:Revision.ContentHashOf(expected)!=Revision.ContentHashOf(SetupComponent.Files(change.Local)))throw new IOException("This installation changed after the preview. Check for changes and review it again.");
                if(change.Local!=null&&change.Local["Target"]!=data["Target"])throw new InvalidOperationException("The add-on uses a different folder on this device. Keep it device-only or reinstall it in the shared folder first.");
                var stage=Path.Combine(stageRoot,plan.Installs.Count.ToString());
                if(data["Removed"]!="True"){System.IO.Directory.CreateDirectory(stage);blobs.Restore(stage,SetupComponent.Files(data),Revision.ContentHashOf(new SortedDictionary<string,string>(StringComparer.Ordinal)));}
                plan.Installs.Add(new SetupInstall{Key=change.Key,Target=data["Target"],Stage=stage,Removed=data["Removed"]=="True",Expected=expected,Desired=SetupComponent.Files(data),Checkpoint=new Checkpoint{Revision=revision.Hash(),Content=revision.ContentHash()}});
            }
            if(plan.Installs.Count==0)throw new InvalidOperationException("Choose at least one package to apply.");
            Atomic.Write(Path.Combine(DirectoryPath,"pending.json"),JsonConvert.SerializeObject(plan,Formatting.Indented));return plan;
        }
    }
}

