using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class AchievementRecord
    {
        public int Version=1;
        public string Game,Scope,Id,Name,Description,Device,UnlockedUtc;
        public bool Unlocked;
        public bool Hidden;
        public double? Progress,Target;
        public string Canonical(){return JsonConvert.SerializeObject(this,Formatting.None);}
        public string Hash(){return Identity.Hash(Canonical());}
        public void Validate()
        {
            Guid device;DateTimeOffset date;
            if(Version!=1||!Regex.IsMatch(Game??"","^[a-f0-9]{64}$")||!Regex.IsMatch(Scope??"","^[a-f0-9]{64}$")||!Guid.TryParseExact(Device,"N",out device)||String.IsNullOrWhiteSpace(Id)||Id.Length>512||Name==null||Name.Length>1000||Description==null||Description.Length>8000||Id.Any(c=>c<32))throw new InvalidDataException("Invalid achievement identity or metadata.");
            if(UnlockedUtc!=null&&(!Unlocked||!DateTimeOffset.TryParse(UnlockedUtc,out date)||date.Year<1983))throw new InvalidDataException("Invalid achievement unlock date.");
            if(Progress.HasValue!=Target.HasValue||Progress.HasValue&&(Double.IsNaN(Progress.Value)||Double.IsInfinity(Progress.Value)||Double.IsNaN(Target.Value)||Double.IsInfinity(Target.Value)||Progress<0||Target<=0||Progress>Target))throw new InvalidDataException("Invalid achievement progress.");
        }
    }
    public sealed class AchievementStore
    {
        public readonly string Objects;
        Dictionary<string,AchievementRecord> cache;
        public AchievementStore(string root){Objects=Path.Combine(root,"achievement-objects");Directory.CreateDirectory(Objects);}
        public static AchievementRecord Read(string file)
        {
            if(new FileInfo(file).Length>65536)throw new InvalidDataException("Oversized achievement record.");
            var record=JsonSafety.Deserialize<AchievementRecord>(File.ReadAllText(file),new JsonSerializerSettings{MissingMemberHandling=MissingMemberHandling.Error});
            if(record==null)throw new InvalidDataException("Empty achievement record.");record.Validate();
            if(Path.GetFileNameWithoutExtension(file)!=record.Hash())throw new InvalidDataException("Achievement record hash mismatch.");return record;
        }
        public List<AchievementRecord> ReadAll(){if(cache==null)cache=Directory.GetFiles(Objects,"*.json").Select(Read).ToDictionary(r=>r.Hash(),StringComparer.Ordinal);return cache.Values.ToList();}
        public void Import(string directory)
        {
            if(!Directory.Exists(directory))return;
            // Validate the complete batch before admitting any record.
            var records=Directory.GetFiles(directory,"*.json").Select(Read).ToArray();
            foreach(var record in records)Put(record);
        }
        void Put(AchievementRecord record){record.Validate();var hash=record.Hash();var path=Path.Combine(Objects,hash+".json");if(!File.Exists(path))Atomic.Write(path,record.Canonical());if(cache!=null)cache[hash]=record;}
        public bool Observe(AchievementRecord record)
        {
            record.Validate();var old=Merge(ReadAll().Where(r=>r.Game==record.Game&&r.Scope==record.Scope&&r.Id==record.Id)).SingleOrDefault();
            if(old!=null&&(!record.Unlocked||old.Unlocked)&&(record.UnlockedUtc==null||old.UnlockedUtc!=null&&String.CompareOrdinal(old.UnlockedUtc,NormalizeDate(record.UnlockedUtc))<=0)&&(!record.Progress.HasValue||old.Target==record.Target&&old.Progress>=record.Progress))return false;
            Put(record);return true;
        }
        public void ObserveMany(IEnumerable<AchievementRecord> values)
        {
            var batch=values.ToArray();foreach(var record in batch)record.Validate();
            var existing=ReadAll();
            foreach(var record in batch)
            {
                var matching=existing.Where(r=>r.Game==record.Game&&r.Scope==record.Scope&&r.Id==record.Id).ToArray();
                var before=Merge(matching).SingleOrDefault();var after=Merge(matching.Concat(new[]{record})).Single();
                if(before!=null&&before.Unlocked==after.Unlocked&&before.UnlockedUtc==after.UnlockedUtc&&before.Progress==after.Progress&&before.Target==after.Target)continue;
                Put(record);existing.Add(record);
            }
        }
        static string NormalizeDate(string value){return DateTimeOffset.Parse(value).ToUniversalTime().ToString("O");}
        public static List<AchievementRecord> Merge(IEnumerable<AchievementRecord> records)
        {
            var all=records.ToArray();foreach(var record in all)record.Validate();
            return all.GroupBy(r=>r.Game+":"+r.Scope+":"+r.Id,StringComparer.Ordinal).Select(group=>{
                var first=group.OrderBy(r=>r.Hash(),StringComparer.Ordinal).First();var result=JsonSafety.Deserialize<AchievementRecord>(first.Canonical());
                result.Hidden=group.Any(r=>r.Hidden);result.Unlocked=group.Any(r=>r.Unlocked);result.UnlockedUtc=group.Where(r=>r.UnlockedUtc!=null).Select(r=>NormalizeDate(r.UnlockedUtc)).OrderBy(s=>s,StringComparer.Ordinal).FirstOrDefault();
                var targets=group.Where(r=>r.Target.HasValue).Select(r=>r.Target.Value).Distinct().ToArray();
                // Different denominators may represent a changed provider schema.
                // Keep raw observations, but do not fabricate combined progress.
                result.Target=targets.Length==1?(double?)targets[0]:null;result.Progress=targets.Length==1?group.Where(r=>r.Target==targets[0]).Max(r=>r.Progress):null;
                return result;
            }).OrderBy(r=>r.Game,StringComparer.Ordinal).ThenBy(r=>r.Scope,StringComparer.Ordinal).ThenBy(r=>r.Id,StringComparer.Ordinal).ToList();
        }
        public List<AchievementRecord> ForGame(string key){return Merge(ReadAll().Where(r=>r.Game==key));}
    }
}
