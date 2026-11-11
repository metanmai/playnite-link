using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class ActivitySession
    {
        public DateTime Utc;
        public ulong Seconds;
        public string Key {get{return Utc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);}}
    }
    public interface IActivityStore
    {
        bool Busy(Guid game);
        List<ActivitySession> Read(Guid game);
        void Add(Guid game,ActivitySession session);
        void Refresh(Guid game);
    }
    public sealed class ActivityBinding
    {
        public string Date;
        public ulong Seconds;
    }
    public sealed class ActivityBridgeState
    {
        public int Version=1;
        public Dictionary<string,Dictionary<string,ActivityBinding>> Games=new Dictionary<string,Dictionary<string,ActivityBinding>>();
    }
    // Only the extension's logical sessions cross devices. The adapter never copies
    // a third-party database, machine configuration, credentials, or hardware logs.
    public sealed class ActivityBridge
    {
        private readonly string path;
        private ActivityBridgeState state;
        private string serialized;
        public ActivityBridge(string directory)
        {
            path=Path.Combine(directory,"activity-bindings.json");
            state=File.Exists(path)?JsonSafety.Deserialize<ActivityBridgeState>(File.ReadAllText(path)):new ActivityBridgeState();
            if(state==null||state.Version!=1||state.Games==null)throw new InvalidDataException("Invalid activity integration state; restore its backup before continuing.");
            serialized=JsonConvert.SerializeObject(state,Formatting.Indented);
        }
        private Dictionary<string,ActivityBinding> Bindings(Guid game)
        {Dictionary<string,ActivityBinding> bindings;if(!state.Games.TryGetValue(game.ToString(),out bindings)){bindings=new Dictionary<string,ActivityBinding>();state.Games.Add(game.ToString(),bindings);}return bindings;}
        private void Save(){var json=JsonConvert.SerializeObject(state,Formatting.Indented);if(json!=serialized){Atomic.Write(path,json);serialized=json;}}
        private sealed class DatedEntry
        {
            public Entry Entry;
            public long Ticks;
        }
        private static int LowerBound(DatedEntry[] entries,long ticks)
        {
            int start=0,end=entries.Length;
            while(start<end)
            {
                int middle=start+(end-start)/2;
                if(entries[middle].Ticks<ticks)start=middle+1;else end=middle;
            }
            return start;
        }
        public bool Capture(Repository repository,Guid game,IActivityStore store)
        {
            if(store.Busy(game))return false;
            var snapshot=repository.SnapshotGame(game.ToString());Mapping map;if(!snapshot.Mappings.TryGetValue(game.ToString(),out map))return true;
            var bindings=new Dictionary<string,ActivityBinding>(Bindings(game));var activities=store.Read(game);
            var byDate=bindings.Values.ToLookup(b=>b.Date,StringComparer.Ordinal);
            var entries=snapshot.Entries.Where(e=>e.Game==map.Key&&e.Kind!="baseline")
                .Select(e=>new DatedEntry{Entry=e,Ticks=DateTimeOffset.Parse(e.StartedUtc).UtcDateTime.Ticks}).OrderBy(e=>e.Ticks).ToArray();
            if(activities.GroupBy(a=>a.Key).Any(g=>g.Count()>1))throw new InvalidDataException("GameActivity contains ambiguous duplicate timestamps. Review its history; no sessions were imported.");
            var additions=new List<Entry>();
            foreach(var activity in activities)
            {
                var key=activity.Key;
                var bound=byDate[key].FirstOrDefault();
                if(bound!=null){if(bound.Seconds!=activity.Seconds)throw new InvalidDataException("A synchronized GameActivity session was edited locally. Review it before synchronization.");continue;}
                var ticks=activity.Utc.ToUniversalTime().Ticks;
                const long tolerance=2*TimeSpan.TicksPerSecond;
                Entry match=null;
                bool remoteNearby=false;
                for(int i=LowerBound(entries,ticks-tolerance);i<entries.Length&&entries[i].Ticks<=ticks+tolerance;i++)
                {
                    var candidate=entries[i].Entry;
                    var remote=candidate.Kind=="session"&&candidate.Device!=snapshot.Device;
                    remoteNearby|=remote;
                    if(bindings.ContainsKey(candidate.Id)||(entries[i].Ticks!=ticks&&(candidate.Kind!="session"||remote)))continue;
                    if(match!=null)throw new InvalidDataException("A GameActivity session matches multiple ledger sessions. Review the ambiguous history.");
                    match=candidate;
                }
                if(match!=null)
                {
                    if(match.Seconds!=activity.Seconds)throw new InvalidDataException("GameActivity and Playnite disagree on a session duration. No extra session was added; review the elapsed time.");
                    bindings[match.Id]=new ActivityBinding{Date=key,Seconds=activity.Seconds};continue;
                }
                if(remoteNearby)throw new InvalidDataException("A copied GameActivity row may already belong to another device's session. Review its identity; no duplicate was imported.");
                var id=Identity.Hash("gameactivity-history-v1|"+map.Key+"|"+key).Substring(0,32);
                var date=activity.Utc.ToUniversalTime();
                var entry=new Entry{Version=2,Kind="history",Id=id,Game=map.Key,Device=Guid.Empty.ToString("N"),Seconds=activity.Seconds,StartedUtc=new DateTimeOffset(date).ToString("O"),EndedUtc=new DateTimeOffset(date.AddSeconds(activity.Seconds)).ToString("O")};
                entry.Validate();additions.Add(entry);bindings[id]=new ActivityBinding{Date=key,Seconds=activity.Seconds};
            }
            if(additions.Count>0)repository.Merge(additions);
            state.Games[game.ToString()]=bindings;
            Save();return true;
        }
        public bool Project(Repository repository,Guid game,IActivityStore store)
        {
            if(store.Busy(game))return false;
            var snapshot=repository.SnapshotGame(game.ToString());Mapping map;if(!snapshot.Mappings.TryGetValue(game.ToString(),out map))return true;
            var bindings=Bindings(game);var activities=store.Read(game).GroupBy(a=>a.Key).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.Ordinal);
            foreach(var entry in snapshot.Entries.Where(e=>e.Game==map.Key&&e.Kind!="baseline").OrderBy(e=>e.StartedUtc))
            {
                ActivityBinding binding;
                if(bindings.TryGetValue(entry.Id,out binding))
                {
                    List<ActivitySession> match;
                    if(!activities.TryGetValue(binding.Date,out match)||match.Count!=1||match[0].Seconds!=entry.Seconds)throw new InvalidDataException("A synchronized GameActivity session was removed or edited. Its cloud history was preserved; review before retrying.");
                    continue;
                }
                var activity=new ActivitySession{Utc=DateTimeOffset.Parse(entry.StartedUtc).UtcDateTime,Seconds=entry.Seconds};
                var key=activity.Key;List<ActivitySession> existing;
                if(activities.TryGetValue(key,out existing))
                {
                    if(existing.Count!=1||existing[0].Seconds!=activity.Seconds)throw new InvalidDataException("GameActivity timestamp conflict. Both versions remain available for review.");
                }
                else{store.Add(game,activity);activities.Add(key,new List<ActivitySession>{activity});}
                bindings[entry.Id]=new ActivityBinding{Date=key,Seconds=activity.Seconds};
                Save(); // A crash before this receipt is recovered by exact timestamp/content adoption.
            }
            store.Refresh(game);return true;
        }
    }
}
