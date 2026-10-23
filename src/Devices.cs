using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class DevicePreferences
    {
        public string Name=Environment.MachineName;
        public int IntervalSeconds=120;
        public int BandwidthKiB=0;
        public bool AutoSync=true;
        public bool SyncSaves=true;
        public bool SyncPlaytime=true;
        public bool SyncPreferences=true;
        public bool SharePaths=false;
        public void Validate()
        {
            if(String.IsNullOrWhiteSpace(Name)||Name.Length>80||Name.Any(c=>c<32)||IntervalSeconds<30||IntervalSeconds>86400||BandwidthKiB<0||BandwidthKiB>1048576)
                throw new InvalidDataException("Invalid device settings: name 1–80 characters, interval 30–86400 seconds, bandwidth 0–1048576 KiB/s.");
        }
        public DevicePreferences Clone(){return JsonSafety.Deserialize<DevicePreferences>(JsonConvert.SerializeObject(this));}
        public DevicePreferences ApplyRequested(DeviceEvent request,string localDevice)
        {
            var next=request.Settings.Clone();next.Validate();
            // Other devices may withdraw sharing, but cannot grant local path consent.
            if(request.Author!=localDevice&&!SharePaths)next.SharePaths=false;
            return next;
        }
    }
    public sealed class DeviceGame
    {
        public string Key;
        public string Name;
        public string SaveFolder;
        public bool SavesEnabled;
    }
    public sealed class DeviceEvent
    {
        public int Version=1;
        public string Id=Guid.NewGuid().ToString("N");
        public string Author,Target,Kind;
        public string CreatedUtc=DateTimeOffset.UtcNow.ToString("O");
        public List<string> Parents=new List<string>();
        public DevicePreferences Settings;
        public string State;
        public string AppVersion="0.6.1";
        public List<DeviceGame> Games;
        public string Canonical(){return JsonConvert.SerializeObject(this,Formatting.None);}
        public string Hash(){return Identity.Hash(Canonical());}
        public void Validate()
        {
            Guid guid;DateTimeOffset date;
            if(Version!=1||!Guid.TryParseExact(Id,"N",out guid)||!Guid.TryParseExact(Author,"N",out guid)||!Guid.TryParseExact(Target,"N",out guid)||
                (Kind!="profile"&&Kind!="settings"&&Kind!="status")||!DateTimeOffset.TryParse(CreatedUtc,out date)||Parents==null||Parents.Count>256||Parents.Distinct().Count()!=Parents.Count||Parents.Any(p=>!Regex.IsMatch(p??"","^[a-f0-9]{64}$")))
                throw new InvalidDataException("Invalid device record.");
            if(Kind=="profile")
            {
                if(Author!=Target||Games==null||Games.Count>100000||Settings==null)throw new InvalidDataException("Invalid device profile.");Settings.Validate();
                if(Games.Any(g=>g==null||!Regex.IsMatch(g.Key??"","^[a-f0-9]{64}$")||g.Name==null||g.Name.Length>1000||(g.SaveFolder!=null&&g.SaveFolder.Length>1000)))throw new InvalidDataException("Invalid device game configuration.");
            }
            if(Kind=="settings"){if(Settings==null)throw new InvalidDataException("Missing device settings.");Settings.Validate();}
            if(Kind=="status"&&State!="active"&&State!="paused"&&State!="removed")throw new InvalidDataException("Unknown device status.");
        }
    }
    public sealed class DeviceView
    {
        public string Id,Name,LastSeen,State;
        public bool SettingsConflict;
        public DevicePreferences Settings;
        public List<DeviceGame> Games;
    }
    public sealed class DeviceDirectory
    {
        public readonly string Objects;
        public DeviceDirectory(string root){Objects=Path.Combine(root,"device-objects");Directory.CreateDirectory(Objects);}
        public Dictionary<string,DeviceEvent> Read(string directory)
        {
            var all=new Dictionary<string,DeviceEvent>();
            foreach(var path in Directory.GetFiles(directory,"*.json"))
            {
                if(new FileInfo(path).Length>16L*1024*1024)throw new InvalidDataException("Oversized device metadata.");
                var item=JsonSafety.Deserialize<DeviceEvent>(File.ReadAllText(path),new JsonSerializerSettings{MissingMemberHandling=MissingMemberHandling.Error});
                if(item==null)throw new InvalidDataException("Null device metadata.");item.Validate();
                if(Path.GetFileNameWithoutExtension(path)!=item.Hash())throw new InvalidDataException("Device record hash mismatch.");all.Add(item.Hash(),item);
            }
            return all;
        }
        public static void ValidateGraph(Dictionary<string,DeviceEvent> all)
        {
            if(all.Values.GroupBy(e=>e.Id).Any(g=>g.Count()>1))throw new InvalidDataException("Conflicting device event ID.");
            foreach(var item in all.Values)foreach(var parent in item.Parents)
                if(!all.ContainsKey(parent)||all[parent].Target!=item.Target||all[parent].Kind!=item.Kind)throw new InvalidDataException("Incomplete or cross-device event ancestry.");
        }
        public void Import(string path)
        {
            var all=Read(Objects);var incoming=Read(path);foreach(var pair in incoming)all[pair.Key]=pair.Value;ValidateGraph(all);
            foreach(var pair in incoming)if(!File.Exists(Path.Combine(Objects,pair.Key+".json")))Add(pair.Value);
        }
        public void Add(DeviceEvent item){item.Validate();var path=Path.Combine(Objects,item.Hash()+".json");if(!File.Exists(path))Atomic.Write(path,item.Canonical());}
        public List<DeviceEvent> Heads(string target,string kind)
        {
            var all=Read(Objects);ValidateGraph(all);return HeadsFrom(all,target,kind);
        }
        private static List<DeviceEvent> HeadsFrom(Dictionary<string,DeviceEvent> all,string target,string kind)
        {
            var items=all.Where(p=>p.Value.Target==target&&p.Value.Kind==kind).ToList();var parents=new HashSet<string>(items.SelectMany(p=>p.Value.Parents));
            return items.Where(p=>!parents.Contains(p.Key)).OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Value).ToList();
        }
        public string EffectiveState(string target)
        {
            var heads=Heads(target,"status");if(heads.Any(h=>h.State=="removed"))return "removed";if(heads.Any(h=>h.State=="paused"))return "paused";return "active";
        }
        public void SetState(string author,string target,string state)
        {Add(new DeviceEvent{Author=author,Target=target,Kind="status",State=state,Parents=Heads(target,"status").Select(e=>e.Hash()).ToList()});}
        public void SetSettings(string author,string target,DevicePreferences settings)
        {settings.Validate();Add(new DeviceEvent{Author=author,Target=target,Kind="settings",Settings=settings.Clone(),Parents=Heads(target,"settings").Select(e=>e.Hash()).ToList()});}
        public void PublishProfile(string device,DevicePreferences settings,List<DeviceGame> games,bool force=false)
        {
            var heads=Heads(device,"profile");
            // Keep-alive at most every 15 minutes unless settings/game mappings changed.
            if(!force&&heads.Count==1&&JsonConvert.SerializeObject(heads[0].Settings)==JsonConvert.SerializeObject(settings)&&JsonConvert.SerializeObject(heads[0].Games)==JsonConvert.SerializeObject(games)&&
                DateTimeOffset.UtcNow-DateTimeOffset.Parse(heads[0].CreatedUtc)<TimeSpan.FromMinutes(15))return;
            Add(new DeviceEvent{Author=device,Target=device,Kind="profile",Settings=settings.Clone(),Games=games,Parents=heads.Select(e=>e.Hash()).ToList()});
        }
        public List<DeviceView> Views(bool includeRemoved=false)
        {
            var all=Read(Objects);ValidateGraph(all);var result=new List<DeviceView>();
            foreach(var target in all.Values.Select(e=>e.Target).Distinct())
            {
                var profiles=HeadsFrom(all,target,"profile");var profile=profiles.OrderByDescending(e=>DateTimeOffset.Parse(e.CreatedUtc)).FirstOrDefault();var settings=HeadsFrom(all,target,"settings");
                var statuses=HeadsFrom(all,target,"status");var state=statuses.Any(h=>h.State=="removed")?"removed":statuses.Any(h=>h.State=="paused")?"paused":"active";
                if(state=="removed"&&!includeRemoved)continue;
                var effective=settings.Count==1?settings[0].Settings:profile==null?new DevicePreferences{Name="Unknown device"}:profile.Settings;
                result.Add(new DeviceView{Id=target,Name=effective.Name,LastSeen=profile==null?null:profile.CreatedUtc,State=state,SettingsConflict=settings.Count>1,
                    Settings=effective.Clone(),Games=profile==null?new List<DeviceGame>():profile.Games});
            }
            return result.OrderBy(v=>v.Name,StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}

