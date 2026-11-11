using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    // No game names, install paths, save paths or credentials enter the ledger.
    public sealed class Entry
    {
        public int Version = 1;
        public string Kind;
        public string Id;
        public string Game;
        public string Device;
        public ulong Seconds;
        public string StartedUtc;
        public string EndedUtc;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ulong? Count;
        public string Canonical() { return JsonConvert.SerializeObject(this, Formatting.None); }
        public string Hash() { return Identity.Hash(Canonical()); }
        public void Validate()
        {
            Guid id, device;
            DateTimeOffset start, end;
            if ((Version != 1&&Version!=2) || (Version==2&&Kind!="history"&&!Count.HasValue) || (Kind != "session" && Kind != "baseline" && Kind != "history") ||
                !Guid.TryParseExact(Id, "N", out id) || !Guid.TryParseExact(Device, "N", out device) ||
                !Regex.IsMatch(Game ?? "", "^[a-f0-9]{64}$") || Seconds > 3155760000UL ||
                !DateTimeOffset.TryParse(StartedUtc, out start) || !DateTimeOffset.TryParse(EndedUtc, out end) || end < start || (Kind!="baseline"&&Count.HasValue))
                throw new InvalidDataException("Unsupported or invalid ledger record.");
        }
    }
    public static class Identity
    {
        public static string Hash(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        public static string Provider(Guid plugin, string gameId)
        {
            if (plugin == Guid.Empty || String.IsNullOrWhiteSpace(gameId))
                throw new InvalidOperationException("Manual games need an explicit shared key.");
            // Preserve provider ID case and whitespace: opaque provider IDs must not be guessed.
            return "provider:" + plugin.ToString("N") + ":" + gameId.Length + ":" + gameId;
        }
        public static string Manual(string key)
        {
            key = (key ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(key, "^[a-z0-9][a-z0-9._-]{2,127}$"))
                throw new ArgumentException("Shared key must be 3-128 letters, digits, dots, underscores or hyphens.");
            return "manual:" + key;
        }
    }
    public sealed class PlaytimeProjection
    {
        internal ulong BaselineSeconds,BaselineCount,HistorySeconds,HistoryCount,SessionSeconds,NativeCount;
        internal bool HasBaselineCount;
        public ulong Total {get{return checked(Math.Max(BaselineSeconds,HistorySeconds)+SessionSeconds);}}
        public ulong Count {get{return checked(Math.Max(BaselineCount,HistoryCount)+NativeCount);}}
        public DateTime? LastActivityUtc {get;internal set;}
    }
    public sealed class Ledger
    {
        private readonly Dictionary<string, Entry> items = new Dictionary<string, Entry>();
        private readonly Lazy<Dictionary<string,PlaytimeProjection>> projection;
        private static readonly PlaytimeProjection Empty=new PlaytimeProjection();
        public Ledger(IEnumerable<Entry> entries)
        {
            foreach (var entry in entries)
            {
                entry.Validate();
                Entry old;
                if (items.TryGetValue(entry.Id, out old) && old.Canonical() != entry.Canonical())
                    throw new InvalidDataException("Conflicting content for session/baseline " + entry.Id + ". Sync stopped; inspect both records.");
                items[entry.Id] = entry;
            }
            projection=new Lazy<Dictionary<string,PlaytimeProjection>>(BuildProjection);
        }
        public List<Entry> Entries { get { return items.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(); } }
        internal void PrepareProjection(){var view=projection.Value;}
        private Dictionary<string,PlaytimeProjection> BuildProjection()
        {
            var result=new Dictionary<string,PlaytimeProjection>(StringComparer.Ordinal);
            foreach(var item in items.Values)
            {
                PlaytimeProjection game;
                if(!result.TryGetValue(item.Game,out game)){game=new PlaytimeProjection();result.Add(item.Game,game);}
                if(item.Kind=="baseline")
                {
                    game.BaselineSeconds=Math.Max(game.BaselineSeconds,item.Seconds);
                    game.BaselineCount=Math.Max(game.BaselineCount,item.Count??0UL);
                    game.HasBaselineCount|=item.Count.HasValue;
                    continue;
                }
                if(item.Kind=="history"){game.HistorySeconds=checked(game.HistorySeconds+item.Seconds);game.HistoryCount++;}
                else{game.SessionSeconds=checked(game.SessionSeconds+item.Seconds);game.NativeCount++;}
                var started=DateTimeOffset.Parse(item.StartedUtc).UtcDateTime;
                if(!game.LastActivityUtc.HasValue||game.LastActivityUtc.Value<started)game.LastActivityUtc=started;
            }
            return result;
        }
        internal PlaytimeProjection ForGame(string game)
        {
            PlaytimeProjection found;
            return projection.Value.TryGetValue(game,out found)?found:Empty;
        }
        public ulong Total(string game){return ForGame(game).Total;}
        public ulong SessionCount(string game){return ForGame(game).Count;}
    }
    public sealed class Mapping
    {
        public string Key;
        public string Source;
        public ulong Expected;
        public ulong? Intended;
        public ulong Original;
    }
    public sealed class Active
    {
        public string Id;
        public string StartedUtc;
    }
    public sealed class Enrollment
    {
        public string Local,Source,SharedKey;
        public ulong Original,Baseline;
        public ulong? Count;
    }
    public sealed class State
    {
        public int Version = 1;
        public string Device = Guid.NewGuid().ToString("N");
        public List<Entry> Entries = new List<Entry>();
        public Dictionary<string, Mapping> Mappings = new Dictionary<string, Mapping>();
        public Dictionary<string, Active> Active = new Dictionary<string, Active>();
        public Dictionary<string, string> PendingGameSyncs = new Dictionary<string, string>();
    }
    public static class Atomic
    {
        public static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = Path.Combine(Path.GetDirectoryName(path), "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(content);
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public sealed class Repository : IDisposable
    {
        public readonly object Gate = new object();
        private readonly string path;
        private readonly FileStream lease;
        // Commit publishes a new snapshot after its durable write.
        private volatile State state;
        private Ledger ledger;
        public long LedgerValidationCount {get;private set;}
        public Repository(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "state.json");
            lease = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                state = File.Exists(path) ? JsonSafety.Deserialize<State>(File.ReadAllText(path)) : new State();
                ledger=Validate(state);
                if (!File.Exists(path)) Commit(state,true);
            }
            catch { lease.Dispose(); throw; }
        }
        private Ledger Validate(State value,Ledger existing=null)
        {
            Guid device;
            if (value == null || value.Version != 1 || !Guid.TryParseExact(value.Device, "N", out device) ||
                value.Entries == null || value.Mappings == null || value.Active == null || value.PendingGameSyncs == null)
                throw new InvalidDataException("Local state is damaged or newer than this extension. Restore a reviewed backup; no automatic reset.");
            var validated=existing;
            if(validated==null){validated=new Ledger(value.Entries);LedgerValidationCount++;}
            if (value.Mappings.Values.Any(x => x == null || !Regex.IsMatch(x.Key ?? "", "^[a-f0-9]{64}$") || String.IsNullOrEmpty(x.Source)) ||
                value.Mappings.Values.GroupBy(x => x.Key).Any(x => x.Count() > 1))
                throw new InvalidDataException("Invalid or ambiguous local mappings.");
            foreach (var active in value.Active)
            {
                Guid id; DateTimeOffset started;
                if (!value.Mappings.ContainsKey(active.Key) || active.Value == null || !Guid.TryParseExact(active.Value.Id, "N", out id) ||
                    !DateTimeOffset.TryParse(active.Value.StartedUtc, out started)) throw new InvalidDataException("Invalid pending session.");
            }
            foreach(var pending in value.PendingGameSyncs)
                if(!value.Mappings.ContainsKey(pending.Key)||!Guid.TryParseExact(pending.Value,"N",out device))
                    throw new InvalidDataException("Invalid pending game sync.");
            return validated;
        }
        private State Clone() { return JsonSafety.Deserialize<State>(JsonConvert.SerializeObject(state)); }
        private void Commit(State next,bool entriesUnchanged=false)
        {
            var validated=Validate(next,entriesUnchanged?ledger:null);
            Atomic.Write(path, JsonConvert.SerializeObject(next, Formatting.Indented));
            state=next;ledger=validated;
        }
        public State Snapshot() { lock (Gate) return Clone(); }
        private static Mapping CopyMapping(Mapping value)
        {return new Mapping{Key=value.Key,Source=value.Source,Expected=value.Expected,Intended=value.Intended,Original=value.Original};}
        public string DeviceId {get{lock(Gate)return state.Device;}}
        public bool IsActive(string local){lock(Gate)return state.Active.ContainsKey(local);}
        public Mapping ReadMapping(string local)
        {lock(Gate){Mapping mapping;return state.Mappings.TryGetValue(local,out mapping)?CopyMapping(mapping):null;}}
        public KeyValuePair<string,Mapping>[] Mappings()
        {lock(Gate)return state.Mappings.Select(p=>new KeyValuePair<string,Mapping>(p.Key,CopyMapping(p.Value))).ToArray();}
        public KeyValuePair<string,Mapping>[] ProjectionMappings()
        {
            PrepareProjection();
            return Mappings();
        }
        public void PrepareProjection()
        {
            Ledger current;
            lock(Gate)current=ledger;
            current.PrepareProjection();
        }
        public PlaytimeProjection ReadProjection(string local)
        {
            lock(Gate)return ledger.ForGame(state.Mappings[local].Key);
        }
        public State SnapshotGame(string local)
        {
            lock(Gate)
            {
                Mapping map;var result=new State{Device=state.Device};
                if(!state.Mappings.TryGetValue(local,out map))return result;
                result.Mappings.Add(local,JsonSafety.Deserialize<Mapping>(JsonConvert.SerializeObject(map)));
                result.Entries=JsonSafety.Deserialize<List<Entry>>(JsonConvert.SerializeObject(state.Entries.Where(e=>e.Game==map.Key)));
                return result;
            }
        }
        public void Merge(IEnumerable<Entry> entries)
        {
            lock (Gate) { var merged=new Ledger(state.Entries.Concat(entries)).Entries;if(merged.Count==state.Entries.Count)return;var next=Clone();next.Entries=merged;Commit(next); }
        }
        public void Enroll(string local, string source, ulong original, ulong baseline,ulong? count=null)
        {EnrollBatch(new[]{new Enrollment{Local=local,Source=source,Original=original,Baseline=baseline,Count=count}});}
        public void EnrollShared(string local,string key,ulong original)
        {if(!Regex.IsMatch(key??"","^[a-f0-9]{64}$"))throw new InvalidDataException("Invalid selected cloud game.");EnrollBatch(new[]{new Enrollment{Local=local,Source="manual:linked:"+key,SharedKey=key,Original=original,Baseline=0,Count=0}});}
        public void EnrollBatch(IEnumerable<Enrollment> requests)
        {
            lock (Gate)
            {
                var next = Clone();var added=false;
                foreach(var request in requests)
                {
                var local=request.Local;var source=request.Source;var original=request.Original;var baseline=request.Baseline;var count=request.Count;var key = request.SharedKey??Identity.Hash(source);
                if(request.SharedKey!=null&&(!Regex.IsMatch(key??"","^[a-f0-9]{64}$")||source!="manual:linked:"+key))throw new InvalidDataException("Invalid selected cloud game.");
                if (next.Mappings.ContainsKey(local) || next.Mappings.Values.Any(x => x.Key == key))
                    throw new InvalidOperationException("Already enrolled or another local game uses this shared identity. Mapping changes require a reviewed migration.");
                next.Mappings.Add(local, new Mapping { Key = key, Source = source, Original = original, Expected = original });
                var now = DateTimeOffset.UtcNow.ToString("O");
                next.Entries.Add(new Entry { Kind = "baseline", Id = Guid.NewGuid().ToString("N"), Game = key, Device = next.Device, Seconds = baseline, StartedUtc = now, EndedUtc = now });
                if(count.HasValue)next.Entries.Add(new Entry{Version=2,Kind="baseline",Id=Guid.NewGuid().ToString("N"),Game=key,Device=next.Device,Seconds=baseline,StartedUtc=now,EndedUtc=now,Count=count});
                added=true;
                }
                if(added)Commit(next);
            }
        }
        public bool Start(string local, ulong? observed = null)
        {
            lock (Gate)
            {
                if (!state.Mappings.ContainsKey(local)) return false;
                if (state.Active.ContainsKey(local)) throw new InvalidOperationException("A pending session already exists. Recover it before starting another session.");
                var next = Clone();
                var map = next.Mappings[local];
                if (observed.HasValue && map.Intended.HasValue && observed.Value == map.Intended.Value)
                { map.Expected = observed.Value; map.Intended = null; }
                next.Active.Add(local, new Active { Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow.ToString("O") });
                Commit(next,true); return true;
            }
        }
        public void EnsureBaselineCount(string local,ulong observed)
        {
            lock(Gate)
            {
                var map=state.Mappings[local];var projection=ledger.ForGame(map.Key);
                if(projection.HasBaselineCount)return;
                var sessions=projection.NativeCount;
                var next=Clone();var date=DateTimeOffset.UtcNow.ToString("O");
                next.Entries.Add(new Entry{Version=2,Kind="baseline",Id=Guid.NewGuid().ToString("N"),Game=map.Key,Device=state.Device,Seconds=projection.BaselineSeconds,Count=observed>sessions?observed-sessions:0,StartedUtc=date,EndedUtc=date});
                Commit(next);
            }
        }
        public Dictionary<string,string> PendingGameSyncs()
        {
            lock(Gate)return new Dictionary<string,string>(state.PendingGameSyncs);
        }
        public void ReadGameSyncState(string local,out bool active,out bool pending)
        {
            var snapshot=state;
            active=snapshot.Active.ContainsKey(local);
            pending=snapshot.PendingGameSyncs.ContainsKey(local);
        }
        public void QueueGameSync(string local){QueueGameSyncs(new[]{local});}
        public void QueueGameSyncs(IEnumerable<string> games)
        {
            lock(Gate)
            {
                var mapped=games.Distinct().Where(state.Mappings.ContainsKey).ToArray();
                if(mapped.Length==0)return;
                var next=Clone();
                foreach(var game in mapped)next.PendingGameSyncs[game]=Guid.NewGuid().ToString("N");
                Commit(next,true);
            }
        }
        public void CompleteGameSyncs(IEnumerable<KeyValuePair<string,string>> completed)
        {
            lock(Gate)
            {
                var matching=completed.Where(pair=>{
                    string token;
                    return state.PendingGameSyncs.TryGetValue(pair.Key,out token)&&token==pair.Value;
                }).ToArray();
                if(matching.Length==0)return;
                var next=Clone();
                foreach(var pair in matching)next.PendingGameSyncs.Remove(pair.Key);
                Commit(next,true);
            }
        }
        public bool Stop(string local, ulong seconds, bool queueGameSync=false)
        {
            lock (Gate)
            {
                if (!state.Active.ContainsKey(local)) return false; // duplicate callback
                var next = Clone(); var active = next.Active[local]; var map = next.Mappings[local];
                var now = DateTimeOffset.UtcNow;
                var start = DateTimeOffset.Parse(active.StartedUtc);
                next.Entries.Add(new Entry { Kind = "session", Id = active.Id, Game = map.Key, Device = next.Device,
                    Seconds = seconds, StartedUtc = active.StartedUtc, EndedUtc = (now < start ? start : now).ToString("O") });
                map.Expected = checked(map.Expected + seconds);
                map.Intended = null;
                next.Active.Remove(local);
                if(queueGameSync)next.PendingGameSyncs[local]=active.Id;
                Commit(next); return true;
            }
        }
        public void AcceptProjection(string local, ulong observed, Action<ulong> write)
        {
            lock (Gate)
            {
                if (state.Active.ContainsKey(local)) return;
                var map = state.Mappings[local];
                if (observed != map.Expected && observed != map.Intended)
                    throw new InvalidOperationException("Playtime changed outside this extension. Resolve the local edit before sync can project a total. Expected " + map.Expected + ", found " + observed + ".");
                var desired = ledger.Total(map.Key);
                if(observed==desired&&map.Expected==desired&&!map.Intended.HasValue)return;
                var next = Clone(); next.Mappings[local].Intended = desired;
                Commit(next,true); // journal before touching Playnite; either expected or intended is valid after crash
                write(desired);
                next = Clone(); next.Mappings[local].Expected = desired; next.Mappings[local].Intended = null;
                Commit(next,true);
            }
        }
        public void AcknowledgeLocalValue(string local, ulong observed)
        {
            lock (Gate)
            {
                var next = Clone(); next.Mappings[local].Expected = observed; next.Mappings[local].Intended = null; Commit(next,true);
            }
        }
        public void Unenroll(string local)
        {
            lock (Gate)
            {
                if (state.Active.ContainsKey(local)) throw new InvalidOperationException("Recover the unfinished session first.");
                var next = Clone(); next.Mappings.Remove(local); next.PendingGameSyncs.Remove(local); Commit(next,true);
            }
        }
        public void Dispose() { lease.Dispose(); }
    }
}
