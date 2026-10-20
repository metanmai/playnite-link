using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PlaytimeRclone;

internal static class Tests
{
    private static int passed, failed;
    private static string root;
    private static string key = Identity.Hash("manual:dispatch");
    private static string NewDir() { var d = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0,12)); Directory.CreateDirectory(d); return d; }
    private static string EmptyConfig() { var p=Path.Combine(NewDir(),"rclone.conf");File.WriteAllText(p,"");return p; }
    private static Entry E(ulong seconds, string kind = "session")
    {
        return new Entry { Kind = kind, Id = Guid.NewGuid().ToString("N"), Game = key, Device = Guid.NewGuid().ToString("N"), Seconds = seconds,
            StartedUtc = "2026-01-01T00:00:00Z", EndedUtc = "2026-01-01T00:01:00Z" };
    }
    private static void Equal<T>(T expected, T actual) { if (!Object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private sealed class FolderTransport : ITransport
    {
        public readonly string Root = NewDir();
        public bool Offline, FailPush, PartialPull;
        public int Pushes;
        public void Pull(string destination)
        {
            if (Offline) throw new IOException("offline");
            foreach (var file in Directory.GetFiles(Root, "*.json")) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)),true);
            if (PartialPull) throw new IOException("interrupted pull");
        }
        public void Push(string source)
        {
            Pushes++;
            foreach (var file in Directory.GetFiles(source, "*.json"))
            {
                var dest = Path.Combine(Root, Path.GetFileName(file));
                if (File.Exists(dest)) Equal(File.ReadAllText(file), File.ReadAllText(dest)); else File.Copy(file, dest);
                if (FailPush) throw new IOException("interrupted upload");
            }
        }
    }
    private sealed class BarrierTransport : ITransport
    {
        private readonly ITransport inner;private readonly Barrier barrier;
        public BarrierTransport(ITransport inner,Barrier barrier){this.inner=inner;this.barrier=barrier;}
        public void Pull(string destination){inner.Pull(destination);if(!barrier.SignalAndWait(15000))throw new TimeoutException("Pull barrier");}
        public void Push(string source){inner.Push(source);}
    }
    private static ulong Total(Repository repo) { return new Ledger(repo.Snapshot().Entries).Total(key); }
    private static void Sync(Repository repo, ITransport transport) { new SyncEngine().Run(repo, transport, NewDir()); }
    private static Repository Enrolled(ulong original = 100, ulong baseline = 100)
    { var r = new Repository(NewDir()); r.Enroll("game", "manual:dispatch", original, baseline); return r; }
    public static int Main(string[] args)
    {
        root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "playtime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Test("additive baseline plus sessions", () => Equal(160UL, new Ledger(new[] { E(100,"baseline"), E(20), E(40) }).Total(key)));
        Test("overlapping history uses maximum, never sum", () => Equal(200UL, new Ledger(new[] { E(100,"baseline"), E(200,"baseline") }).Total(key)));
        Test("duplicate delivery is idempotent", () => { var e = E(20); Equal(20UL, new Ledger(new[] { e,e,e }).Total(key)); });
        Test("same logical ID differing contents fails closed", () => { var a = E(20); var b = JsonConvert.DeserializeObject<Entry>(a.Canonical()); b.Seconds++; Throws(() => new Ledger(new[] { a,b })); });
        Test("merge commutes and associates for 100 randomized orderings", () => {
            var rng = new Random(8); var es = Enumerable.Range(0,100).Select(i => E((ulong)i)).ToList();
            for (int i=0;i<100;i++) { var shuffled = es.Concat(es.Take(25)).OrderBy(_=>rng.Next()).ToList(); Equal(4950UL,new Ledger(shuffled).Total(key)); }
        });
        Test("separate identities never mix", () => { var a = E(20); var b = E(50); b.Game = Identity.Hash("another"); Equal(20UL,new Ledger(new[]{a,b}).Total(key)); });
        Test("clock skew never chooses a winning total", () => { var a=E(20);var b=E(30);b.StartedUtc="2050-01-01T00:00:00Z";b.EndedUtc="2050-01-01T00:01:00Z";Equal(50UL,new Ledger(new[]{b,a}).Total(key)); });
        Test("schema version rejected", () => { var a=E(1);a.Version=2;Throws(()=>new Ledger(new[]{a})); });
        Test("negative unsigned seconds rejected", () => Throws(()=>JsonConvert.DeserializeObject<Entry>("{\"Seconds\":-1}")));
        Test("invalid ID rejected", () => { var a=E(1);a.Id="../bad";Throws(()=>a.Validate()); });
        Test("invalid timestamp rejected", () => { var a=E(1);a.EndedUtc="bad";Throws(()=>a.Validate()); });
        Test("backwards timestamp rejected", () => { var a=E(1);a.EndedUtc="2020-01-01Z";Throws(()=>a.Validate()); });
        Test("absurd duration rejected", () => Throws(()=>new Ledger(new[]{E(UInt64.MaxValue)})));
        Test("manual identity normalized", () => Equal(Identity.Manual(" Dispatch "), Identity.Manual("dispatch")));
        Test("manual path injection rejected", () => Throws(()=>Identity.Manual("../dispatch")));
        Test("provider ID namespace prevents collisions", () => { var p=Guid.NewGuid();if(Identity.Provider(p,"1")==Identity.Provider(Guid.NewGuid(),"1"))throw new Exception(); });
        Test("provider opaque IDs preserved", () => { var p=Guid.NewGuid();if(Identity.Provider(p,"A")==Identity.Provider(p,"a"))throw new Exception(); });
        Test("manual games cannot implicitly match by name", () => Throws(()=>Identity.Provider(Guid.Empty,"Dispatch")));
        Test("duplicate local identity blocked", () => { using(var r=Enrolled()){Throws(()=>r.Enroll("other","manual:dispatch",100,100));} });
        Test("only explicitly enrolled manual games are captured", () => { using(var r=Enrolled()){Equal(false,r.Start("same-title-other-manual-game"));Equal(1,r.Snapshot().Mappings.Count);} });
        Test("removing local link preserves shared history", () => { using(var r=Enrolled()){r.Start("game");r.Stop("game",15);r.Unenroll("game");Equal(false,r.Start("game"));Equal(115UL,Total(r));Equal(0,r.Snapshot().Mappings.Count);} });
        Test("cannot remove link with unfinished session", () => { using(var r=Enrolled()){r.Start("game");Throws(()=>r.Unenroll("game"));Equal(1,r.Snapshot().Mappings.Count);} });
        Test("remapping an enrolled game blocked", () => { using(var r=Enrolled()){Throws(()=>r.Enroll("game","manual:other",100,100));} });
        Test("start-stop exact elapsed and duplicate stop ignored", () => { using(var r=Enrolled()){r.Start("game");Equal(true,r.Stop("game",30));Equal(false,r.Stop("game",30));Equal(130UL,Total(r));} });
        Test("unenrolled games ignored", () => { using(var r=Enrolled()){Equal(false,r.Start("unknown"));Equal(false,r.Stop("unknown",30));Equal(100UL,Total(r));} });
        Test("unfinished session prevents accidental new session", () => { using(var r=Enrolled()){r.Start("game");Throws(()=>r.Start("game"));Equal(100UL,Total(r));} });
        Test("simultaneous games keep distinct tokens", () => { using(var r=Enrolled()){r.Enroll("other","manual:other",0,0);r.Start("game");r.Start("other");r.Stop("other",70);r.Stop("game",5);Equal(105UL,Total(r));} });
        Test("restart retains offline sessions and device identity", () => {
            var d=NewDir();string device;using(var r=new Repository(d)){r.Enroll("game","manual:dispatch",100,100);r.Start("game");r.Stop("game",10);device=r.Snapshot().Device;}
            using(var r=new Repository(d)){Equal(device,r.Snapshot().Device);Equal(110UL,Total(r));}
        });
        Test("pending crash session retained without guessed duration", () => { var d=NewDir();using(var r=new Repository(d)){r.Enroll("game","manual:dispatch",100,100);r.Start("game");} using(var r=new Repository(d)){Equal(1,r.Snapshot().Active.Count);Equal(100UL,Total(r));} });
        Test("one writer per local profile", () => { var d=NewDir();using(var r=new Repository(d)){Throws(()=>{using(var other=new Repository(d)){};});} });
        Test("corrupted local state fails without reset", () => { var d=NewDir();File.WriteAllText(Path.Combine(d,"state.json"),"garbage");Throws(()=>new Repository(d));Equal("garbage",File.ReadAllText(Path.Combine(d,"state.json"))); });
        Test("atomic write retains previous backup", () => { var p=Path.Combine(NewDir(),"state");Atomic.Write(p,"old");Atomic.Write(p,"new");Equal("new",File.ReadAllText(p));Equal("old",File.ReadAllText(p+".bak")); });
        Test("failed disk commit does not mutate in-memory session state", () => {
            var d=NewDir();using(var r=new Repository(d)){r.Enroll("game","manual:dispatch",100,100);var file=Path.Combine(d,"state.json");File.SetAttributes(file,FileAttributes.ReadOnly);try{Throws(()=>r.Start("game"));Equal(0,r.Snapshot().Active.Count);}finally{File.SetAttributes(file,FileAttributes.Normal);}}
        });
        Test("projection uses exact session seconds without double counting", () => { using(var r=Enrolled()){r.Start("game");r.Stop("game",12);ulong displayed=112;r.AcceptProjection("game",displayed,v=>displayed=v);Equal(112UL,displayed);} });
        Test("projection skips an active game", () => { using(var r=Enrolled()){r.Start("game");r.AcceptProjection("game",100,_=>{throw new Exception("must not write");});} });
        Test("outside playtime edit is surfaced", () => { using(var r=Enrolled()){Throws(()=>r.AcceptProjection("game",999,_=>{}));Equal(100UL,Total(r));} });
        Test("projection crash after DB write recovers idempotently", () => {
            using(var r=Enrolled()){r.Merge(new[]{E(20)});ulong displayed=100;Throws(()=>r.AcceptProjection("game",displayed,v=>{displayed=v;throw new IOException("crash");}));Equal(120UL,displayed);r.AcceptProjection("game",displayed,v=>displayed=v);Equal(120UL,displayed);}
        });
        Test("projection failure before DB write retries safely", () => { using(var r=Enrolled()){r.Merge(new[]{E(20)});Throws(()=>r.AcceptProjection("game",100,v=>{throw new IOException();}));r.AcceptProjection("game",100,v=>Equal(120UL,v));} });
        Test("session after interrupted projection uses intended checkpoint", () => { using(var r=Enrolled()){r.Merge(new[]{E(20)});Throws(()=>r.AcceptProjection("game",100,v=>{throw new IOException();}));r.Start("game",120);r.Stop("game",5);r.AcceptProjection("game",125,v=>Equal(125UL,v));} });
        Test("reviewed migration does not sum device histories", () => { var t=new FolderTransport();using(var a=Enrolled(100,100))using(var b=Enrolled(200,200)){Sync(a,t);Sync(b,t);Sync(a,t);Equal(200UL,Total(a));Equal(200UL,Total(b));} });
        Test("two devices offline sessions converge after repeated sync", () => {
            var t=new FolderTransport();using(var a=Enrolled())using(var b=Enrolled()){a.Start("game");a.Stop("game",20);b.Start("game");b.Stop("game",30);t.Offline=true;Throws(()=>Sync(a,t));Throws(()=>Sync(b,t));t.Offline=false;for(int i=0;i<3;i++){Sync(a,t);Sync(b,t);}Equal(150UL,Total(a));Equal(150UL,Total(b));}
        });
        Test("partial upload retries without duplication", () => { var t=new FolderTransport();using(var r=Enrolled()){r.Start("game");r.Stop("game",20);t.FailPush=true;Throws(()=>Sync(r,t));t.FailPush=false;Sync(r,t);Sync(r,t);Equal(120UL,Total(r));Equal(2,Directory.GetFiles(t.Root,"*.json").Length);} });
        Test("partial failed pull does not merge", () => { var t=new FolderTransport();using(var a=Enrolled())using(var b=Enrolled()){a.Start("game");a.Stop("game",20);Sync(a,t);t.PartialPull=true;Throws(()=>Sync(b,t));Equal(100UL,Total(b));} });
        Test("hash mismatch rejects remote modification", () => { var d=NewDir();File.WriteAllText(Path.Combine(d,new string('a',64)+".json"),E(1).Canonical());Throws(()=>SyncEngine.ReadObjects(d)); });
        Test("truncated JSON rejected", () => { var d=NewDir();File.WriteAllText(Path.Combine(d,new string('a',64)+".json"),"{\"Kind\":");Throws(()=>SyncEngine.ReadObjects(d)); });
        Test("unknown remote fields rejected", () => { var e=E(1);var d=NewDir();File.WriteAllText(Path.Combine(d,e.Hash()+".json"),e.Canonical().TrimEnd('}')+",\"NewField\":1}");Throws(()=>SyncEngine.ReadObjects(d)); });
        Test("conflicting remote session never silently wins", () => { var t=new FolderTransport();using(var r=Enrolled()){var e=E(1);r.Merge(new[]{e});var other=JsonConvert.DeserializeObject<Entry>(e.Canonical());other.Seconds=10;File.WriteAllText(Path.Combine(t.Root,other.Hash()+".json"),other.Canonical());Throws(()=>Sync(r,t));Equal(101UL,Total(r));} });
        Test("warm persistent-cache sync makes no upload call; new sessions still publish",()=>{
            var t=new FolderTransport();var cache=NewDir();using(var r=Enrolled()){
                var engine=new SyncEngine();engine.Run(r,t,NewDir(),cache);Equal(1,t.Pushes);
                engine.Run(r,t,NewDir(),cache);Equal(1,t.Pushes);r.Start("game");r.Stop("game",17);
                engine.Run(r,t,NewDir(),cache);Equal(2,t.Pushes);Equal(117UL,new Ledger(SyncEngine.ReadObjects(t.Root)).Total(key));
            }
        });
        Test("shell metacharacters are quoted as argv", () => Equal("\"a & b\"",RcloneTransport.Quote("a & b")));
        Test("unconfigured cloud settings can be saved without enabling sync", () => { var saved=false;var settings=new CloudSettings(()=>new SyncConfig(),value=>saved=true);List<string> errors;Equal(true,settings.VerifySettings(out errors));settings.EndEdit();Equal(true,saved); });
        Test("configured cloud settings validate before saving", () => { var settings=new CloudSettings(()=>new SyncConfig { RemoteFolder="--bad-option" },value=>{});List<string> errors;Equal(false,settings.VerifySettings(out errors)); });
        Test("trailing slashes escaped for Windows argv", () => Equal("\"C:\\path\\\\\"",RcloneTransport.Quote("C:\\path\\")));
        if (args.Length > 1) Test("real Rclone local backend end-to-end two device convergence", () => {
            var transport=new RcloneTransport(Path.GetFullPath(args[1]),EmptyConfig(),NewDir());
            using(var a=Enrolled())using(var b=Enrolled()){a.Start("game");a.Stop("game",17);b.Start("game");b.Stop("game",23);Sync(a,transport);Sync(b,transport);Sync(a,transport);Equal(140UL,Total(a));Equal(140UL,Total(b));Sync(a,transport);Equal(140UL,Total(a));}
        });
        if (args.Length > 1) Test("real Rclone simultaneous offline publication loses no sessions", () => {
            var transport=new RcloneTransport(Path.GetFullPath(args[1]),EmptyConfig(),NewDir());
            using(var a=Enrolled())using(var b=Enrolled())using(var barrier=new Barrier(2)){
                a.Start("game");a.Stop("game",17);b.Start("game");b.Stop("game",23);
                Task.WaitAll(Task.Run(()=>Sync(a,new BarrierTransport(transport,barrier))),Task.Run(()=>Sync(b,new BarrierTransport(transport,barrier))));
                Sync(a,transport);Sync(b,transport);Equal(140UL,Total(a));Equal(140UL,Total(b));
            }
        });
        if(args.Length>1)Test("batched metadata pulls only the four permitted object areas",()=>{
            var source=NewDir();var destination=NewDir();foreach(var area in new[]{"devices","ledger","activity","revisions"}){var folder=Path.Combine(source,"playnite-link-v1",area,"objects");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,area+".json"),"metadata");}
            var excluded=Path.Combine(source,"playnite-link-v1","blobs");Directory.CreateDirectory(excluded);File.WriteAllText(Path.Combine(excluded,"private.json"),"must not download");
            var nested=Path.Combine(source,"playnite-link-v1","ledger","objects","nested");Directory.CreateDirectory(nested);File.WriteAllText(Path.Combine(nested,"private.json"),"must not download");
            new RcloneTransport(Path.GetFullPath(args[1]),EmptyConfig(),source,"playnite-link-v1").PullMetadata(destination);
            Equal(4,Directory.GetFiles(destination,"*.json",SearchOption.AllDirectories).Length);Equal(false,Directory.Exists(Path.Combine(destination,"blobs")));Equal(false,Directory.Exists(Path.Combine(destination,"ledger","objects","nested")));
        });
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }
}
