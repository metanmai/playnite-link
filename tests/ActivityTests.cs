using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlaytimeRclone;

static class ActivityTests
{
    static string root;static int passed,failed;static Guid game=Guid.NewGuid();static string key=Identity.Hash("manual:session-demo");
    static string Dir(){var d=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(d);return d;}
    static void Equal<T>(T expected,T actual){if(!Object.Equals(expected,actual))throw new Exception("Expected "+expected+", got "+actual);}
    static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected rejection");}
    static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex);}}
    sealed class Store:IActivityStore
    {
        public bool Running;public int Writes;public bool FailWrite;public List<ActivitySession> Items=new List<ActivitySession>();
        public bool Busy(Guid id){return Running;}
        public List<ActivitySession> Read(Guid id){return Items.ToList();}
        public void Add(Guid id,ActivitySession session){if(FailWrite)throw new IOException("fixture failure");Items.Add(session);Writes++;}
        public void Refresh(Guid id){}
    }
    static Store History(){return new Store{Items=new List<ActivitySession>{new ActivitySession{Utc=new DateTime(2026,1,1,12,0,0,DateTimeKind.Utc),Seconds=20},new ActivitySession{Utc=new DateTime(2026,1,2,12,0,0,DateTimeKind.Utc),Seconds=40}}};}
    sealed class Folder:ITransport
    {
        public string Root=Dir();
        public void Pull(string destination){foreach(var p in Directory.GetFiles(Root))File.Copy(p,Path.Combine(destination,Path.GetFileName(p)),true);}
        public void Push(string source){foreach(var p in Directory.GetFiles(source))File.Copy(p,Path.Combine(Root,Path.GetFileName(p)),true);}
    }
    static Repository Repo(ulong time=60,ulong? count=2){var r=new Repository(Dir());r.Enroll(game.ToString(),"manual:session-demo",time,time,count);return r;}
    static int Main(string[] args)
    {
        root=args[0];Directory.CreateDirectory(root);
        Test("existing detailed history is adopted without adding baseline time twice",()=>{using(var r=Repo()){var s=History();var b=new ActivityBridge(Dir());b.Capture(r,game,s);Equal(60UL,new Ledger(r.Snapshot().Entries).Total(key));Equal(2UL,new Ledger(r.Snapshot().Entries).SessionCount(key));Equal(0,s.Writes);}});
        Test("cloned old history on two devices has one stable identity",()=>{using(var a=Repo())using(var b=Repo()){new ActivityBridge(Dir()).Capture(a,game,History());new ActivityBridge(Dir()).Capture(b,game,History());a.Merge(b.Snapshot().Entries);Equal(2,a.Snapshot().Entries.Count(e=>e.Kind=="history"));Equal(60UL,new Ledger(a.Snapshot().Entries).Total(key));}});
        Test("independent old histories merge without losing disjoint sessions",()=>{using(var a=Repo())using(var b=Repo(30,1)){new ActivityBridge(Dir()).Capture(a,game,History());var s=new Store();s.Items.Add(new ActivitySession{Utc=new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc),Seconds=30});new ActivityBridge(Dir()).Capture(b,game,s);a.Merge(b.Snapshot().Entries);Equal(90UL,new Ledger(a.Snapshot().Entries).Total(key));Equal(3UL,new Ledger(a.Snapshot().Entries).SessionCount(key));}});
        Test("native local session is bound instead of imported twice",()=>{using(var r=Repo()){var store=History();var bridge=new ActivityBridge(Dir());bridge.Capture(r,game,store);r.Start(game.ToString());var active=r.Snapshot().Active[game.ToString()];r.Stop(game.ToString(),17);store.Items.Add(new ActivitySession{Utc=DateTimeOffset.Parse(active.StartedUtc).UtcDateTime.AddMilliseconds(60),Seconds=17});bridge.Capture(r,game,store);bridge.Project(r,game,store);Equal(3,store.Items.Count);Equal(77UL,new Ledger(r.Snapshot().Entries).Total(key));Equal(3UL,new Ledger(r.Snapshot().Entries).SessionCount(key));}});
        Test("ten repeated sync projections never create duplicate sessions",()=>{using(var a=Repo())using(var b=Repo()){var sa=History();var sb=History();var ba=new ActivityBridge(Dir());var bb=new ActivityBridge(Dir());ba.Capture(a,game,sa);bb.Capture(b,game,sb);a.Start(game.ToString());a.Stop(game.ToString(),17);for(int i=0;i<10;i++){b.Merge(a.Snapshot().Entries);bb.Capture(b,game,sb);bb.Project(b,game,sb);a.Merge(b.Snapshot().Entries);}Equal(3,sb.Items.Count);Equal(1,sb.Writes);Equal(77UL,new Ledger(b.Snapshot().Entries).Total(key));}});
        Test("restart retains binding receipts",()=>{using(var r=Repo()){var path=Dir();var s=History();new ActivityBridge(path).Capture(r,game,s);r.Start(game.ToString());r.Stop(game.ToString(),17);new ActivityBridge(path).Project(r,game,s);new ActivityBridge(path).Capture(r,game,s);new ActivityBridge(path).Project(r,game,s);Equal(3,s.Items.Count);Equal(1,s.Writes);}});
        Test("busy native writer is deferred without touching data",()=>{using(var r=Repo()){var s=History();s.Running=true;var b=new ActivityBridge(Dir());Equal(false,b.Capture(r,game,s));Equal(false,b.Project(r,game,s));Equal(0,r.Snapshot().Entries.Count(e=>e.Kind=="history"));Equal(0,s.Writes);}});
        Test("conflicting historical duration fails closed across peers",()=>{using(var a=Repo())using(var b=Repo()){var sa=History();var sb=History();sb.Items[0].Seconds=25;new ActivityBridge(Dir()).Capture(a,game,sa);new ActivityBridge(Dir()).Capture(b,game,sb);Throws(()=>a.Merge(b.Snapshot().Entries));Equal(60UL,new Ledger(a.Snapshot().Entries).Total(key));}});
        Test("native and ledger duration disagreement cannot become another session",()=>{using(var r=Repo()){var s=History();var b=new ActivityBridge(Dir());b.Capture(r,game,s);r.Start(game.ToString());var active=r.Snapshot().Active[game.ToString()];r.Stop(game.ToString(),17);s.Items.Add(new ActivitySession{Utc=DateTimeOffset.Parse(active.StartedUtc).UtcDateTime,Seconds=12});Throws(()=>b.Capture(r,game,s));Equal(77UL,new Ledger(r.Snapshot().Entries).Total(key));Equal(2,r.Snapshot().Entries.Count(e=>e.Kind=="history"));}});
        Test("externally edited imported session is preserved for review",()=>{using(var r=Repo()){var s=History();var b=new ActivityBridge(Dir());b.Capture(r,game,s);s.Items[0].Seconds=99;Throws(()=>b.Capture(r,game,s));Equal(99UL,s.Items[0].Seconds);}});
        Test("externally removed imported session is not silently recreated",()=>{using(var r=Repo()){var s=History();var b=new ActivityBridge(Dir());b.Capture(r,game,s);s.Items.RemoveAt(0);Throws(()=>b.Project(r,game,s));Equal(1,s.Items.Count);}});
        Test("ambiguous duplicate timestamps require review",()=>{using(var r=Repo()){var s=History();s.Items.Add(new ActivitySession{Utc=s.Items[0].Utc,Seconds=20});Throws(()=>new ActivityBridge(Dir()).Capture(r,game,s));Equal(0,r.Snapshot().Entries.Count(e=>e.Kind=="history"));}});
        Test("failed destination write can retry exactly once",()=>{using(var r=Repo()){var s=History();var b=new ActivityBridge(Dir());b.Capture(r,game,s);r.Start(game.ToString());r.Stop(game.ToString(),17);s.FailWrite=true;Throws(()=>b.Project(r,game,s));s.FailWrite=false;b.Project(r,game,s);b.Project(r,game,s);Equal(1,s.Writes);Equal(3,s.Items.Count);}});
        Test("missing receipt after a crash adopts exact existing session",()=>{using(var r=Repo()){var s=History();new ActivityBridge(Dir()).Capture(r,game,s);r.Start(game.ToString());r.Stop(game.ToString(),17);var entry=r.Snapshot().Entries.Single(e=>e.Kind=="session");s.Items.Add(new ActivitySession{Utc=DateTimeOffset.Parse(entry.StartedUtc).UtcDateTime,Seconds=17});var b=new ActivityBridge(Dir());b.Capture(r,game,s);b.Project(r,game,s);Equal(0,s.Writes);Equal(3,s.Items.Count);}});
        Test("old ledger canonical serialization remains byte compatible",()=>{var e=new Entry{Kind="baseline",Id=Guid.NewGuid().ToString("N"),Game=key,Device=Guid.NewGuid().ToString("N"),Seconds=60,StartedUtc="2026-01-01T00:00:00Z",EndedUtc="2026-01-01T00:00:00Z"};Equal(false,e.Canonical().Contains("Count"));});
        Test("new history stays out of the legacy ledger namespace",()=>{using(var r=Repo()){new ActivityBridge(Dir()).Capture(r,game,History());var legacy=new Folder();var activity=new Folder();var engine=new SyncEngine();engine.Run(r,legacy,Dir());engine.Run(r,activity,Dir(),null,true);var old=SyncEngine.ReadObjects(legacy.Root);Equal(1,old.Count);Equal(true,old.All(e=>e.Version==1&&e.Kind=="baseline"&&!e.Count.HasValue));Equal(3,SyncEngine.ReadObjects(activity.Root).Count);}});
        Test("separate metadata areas converge counts and history repeatedly",()=>{using(var a=Repo())using(var b=Repo()){var sa=History();var sb=History();var ba=new ActivityBridge(Dir());var bb=new ActivityBridge(Dir());ba.Capture(a,game,sa);bb.Capture(b,game,sb);a.Start(game.ToString());a.Stop(game.ToString(),17);var legacy=new Folder();var activity=new Folder();var engine=new SyncEngine();for(int i=0;i<5;i++){engine.Run(a,legacy,Dir());engine.Run(a,activity,Dir(),null,true);engine.Run(b,legacy,Dir());engine.Run(b,activity,Dir(),null,true);bb.Capture(b,game,sb);bb.Project(b,game,sb);}Equal(77UL,new Ledger(b.Snapshot().Entries).Total(key));Equal(3UL,new Ledger(b.Snapshot().Entries).SessionCount(key));Equal(3,sb.Items.Count);}});
        Test("copied foreign native row with lost receipt cannot become a duplicate",()=>{using(var a=Repo())using(var b=Repo()){var store=History();var bridge=new ActivityBridge(Dir());bridge.Capture(b,game,store);a.Start(game.ToString());a.Stop(game.ToString(),17);b.Merge(a.Snapshot().Entries);var session=a.Snapshot().Entries.Single(e=>e.Kind=="session");store.Items.Add(new ActivitySession{Utc=DateTimeOffset.Parse(session.StartedUtc).UtcDateTime.AddMilliseconds(500),Seconds=17});Throws(()=>bridge.Capture(b,game,store));Equal(2,b.Snapshot().Entries.Count(e=>e.Kind=="history"));Equal(77UL,new Ledger(b.Snapshot().Entries).Total(key));}});
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");return failed==0?0:1;
    }
}
