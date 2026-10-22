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
        public bool Capture(Repository repository,Guid game,IActivityStore store)
        {
            if(store.Busy(game))return false;
            var snapshot=repository.SnapshotGame(game.ToString());Mapping map;if(!snapshot.Mappings.TryGetValue(game.ToString(),out map))return true;
            var bindings=new Dictionary<string,ActivityBinding>(Bindings(game));var activities=store.Read(game);var entries=snapshot.Entries.Where(e=>e.Game==map.Key&&e.Kind!="baseline").ToArray();
            if(activities.GroupBy(a=>a.Key).Any(g=>g.Count()>1))throw new InvalidDataException("GameActivity contains ambiguous duplicate timestamps. Review its history; no sessions were imported.");
            var additions=new List<Entry>();
            foreach(var activity in activities)
            {
                var bound=bindings.FirstOrDefault(p=>p.Value.Date==activity.Key);
                if(bound.Key!=null){if(bound.Value.Seconds!=activity.Seconds)throw new InvalidDataException("A synchronized GameActivity session was edited locally. Review it before synchronization.");continue;}
                var near=entries.Where(e=>!bindings.ContainsKey(e.Id)&&
                    (DateTimeOffset.Parse(e.StartedUtc).UtcDateTime.Ticks==activity.Utc.ToUniversalTime().Ticks||
                    (e.Kind=="session"&&e.Device==snapshot.Device&&Math.Abs((DateTimeOffset.Parse(e.StartedUtc).UtcDateTime-activity.Utc.ToUniversalTime()).TotalSeconds)<=2))).ToArray();
                if(near.Length>1)throw new InvalidDataException("A GameActivity session matches multiple ledger sessions. Review the ambiguous history.");
                if(near.Length==1)
                {
                    if(near[0].Seconds!=activity.Seconds)throw new InvalidDataException("GameActivity and Playnite disagree on a session duration. No extra session was added; review the elapsed time.");
                    bindings[near[0].Id]=new ActivityBinding{Date=activity.Key,Seconds=activity.Seconds};continue;
                }
                if(entries.Any(e=>e.Kind=="session"&&e.Device!=snapshot.Device&&Math.Abs((DateTimeOffset.Parse(e.StartedUtc).UtcDateTime-activity.Utc.ToUniversalTime()).TotalSeconds)<=2))
                    throw new InvalidDataException("A copied GameActivity row may already belong to another device's session. Review its identity; no duplicate was imported.");
                var id=Identity.Hash("gameactivity-history-v1|"+map.Key+"|"+activity.Key).Substring(0,32);
                var date=activity.Utc.ToUniversalTime();
                var entry=new Entry{Version=2,Kind="history",Id=id,Game=map.Key,Device=Guid.Empty.ToString("N"),Seconds=activity.Seconds,StartedUtc=new DateTimeOffset(date).ToString("O"),EndedUtc=new DateTimeOffset(date.AddSeconds(activity.Seconds)).ToString("O")};
                entry.Validate();additions.Add(entry);bindings[id]=new ActivityBinding{Date=activity.Key,Seconds=activity.Seconds};
            }
            if(additions.Count>0)repository.Merge(additions);
            state.Games[game.ToString()]=bindings;
            Save();return true;
        }
        public bool Project(Repository repository,Guid game,IActivityStore store)
        {
            if(store.Busy(game))return false;
            var snapshot=repository.SnapshotGame(game.ToString());Mapping map;if(!snapshot.Mappings.TryGetValue(game.ToString(),out map))return true;
            var bindings=Bindings(game);var activities=store.Read(game);
            foreach(var entry in snapshot.Entries.Where(e=>e.Game==map.Key&&e.Kind!="baseline").OrderBy(e=>e.StartedUtc))
            {
                ActivityBinding binding;
                if(bindings.TryGetValue(entry.Id,out binding))
                {
                    var match=activities.Where(a=>a.Key==binding.Date).ToArray();
                    if(match.Length!=1||match[0].Seconds!=entry.Seconds)throw new InvalidDataException("A synchronized GameActivity session was removed or edited. Its cloud history was preserved; review before retrying.");
                    continue;
                }
                var activity=new ActivitySession{Utc=DateTimeOffset.Parse(entry.StartedUtc).UtcDateTime,Seconds=entry.Seconds};
                var existing=activities.Where(a=>a.Key==activity.Key).ToArray();
                if(existing.Length>1||(existing.Length==1&&existing[0].Seconds!=activity.Seconds))throw new InvalidDataException("GameActivity timestamp conflict. Both versions remain available for review.");
                if(existing.Length==0){store.Add(game,activity);activities.Add(activity);}
                bindings[entry.Id]=new ActivityBinding{Date=activity.Key,Seconds=activity.Seconds};
                Save(); // A crash before this receipt is recovered by exact timestamp/content adoption.
            }
            store.Refresh(game);return true;
        }
    }
}
