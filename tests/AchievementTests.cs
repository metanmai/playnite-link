using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlaytimeRclone;
using Playnite.SDK.Models;

class AchievementTests
{
    static int checks;static string key=Identity.Hash("achievement-game");
    static void Check(bool ok){if(!ok)throw new Exception("Achievement assertion failed at "+checks);checks++;}
    static void Reject(Action action){try{action();}catch{checks++;return;}throw new Exception("Expected rejection");}
    static AchievementRecord Record(bool unlocked=false,double progress=0){return new AchievementRecord{Game=key,Scope=Identity.Hash("platform"),Id="A",Device=new string('a',32),Name="Achievement",Description="Description",Unlocked=unlocked,Progress=progress,Target=10};}
    public sealed class Item{public string ApiName{get;set;}public string DisplayName{get;set;}public string Description{get;set;}public bool Unlocked{get;set;}public DateTime? UnlockTimeUtc{get;set;}public int? ProgressNum{get;set;}public int? ProgressDenom{get;set;}}
    public sealed class Data{public string ProviderKey{get;set;}public int AppId{get;set;}public bool ExcludedByUser{get;set;}public List<Item> Achievements{get;set;}}
    public sealed class Result{public bool Success{get;set;}}
    public sealed class Cache{public Data Data;public int Writes;public Data LoadGameData(string id){return Data;}public Result SaveGameData(string id,Data data){Data=data;Writes++;return new Result{Success=true};}}
    public sealed class Plugin{public Cache CacheManager{get;set;}}
    public sealed class LegacyItem{public string ApiName{get;set;}public string Name{get;set;}public string Description{get;set;}public DateTime? DateUnlocked{get;set;}public bool IsUnlock{get{return DateUnlocked.HasValue;}}}
    public sealed class LegacyData{public bool IsIgnored{get;set;}public List<LegacyItem> Items{get;set;}}
    public sealed class LegacyDb{public LegacyData Data;public int Writes;public LegacyData Get(Guid id,bool load,bool refresh){return Data;}public void Update(LegacyData data){Writes++;Data=data;}}
    public sealed class LegacyPlugin{public LegacyDb PluginDatabase{get;set;}}
    static int Main(string[] args)
    {
        try{
            var root=args[0];Directory.CreateDirectory(root);var a=new AchievementStore(Path.Combine(root,"a"));var b=new AchievementStore(Path.Combine(root,"b"));
            var locked=Record();var unlocked=Record(true,5);unlocked.UnlockedUtc="2026-10-23T10:00:00Z";var progress=Record(false,8);progress.Device=new string('b',32);
            Check(a.Observe(locked));Check(a.Observe(unlocked));Check(b.Observe(progress));a.Import(b.Objects);b.Import(a.Objects);
            var merged=a.ForGame(key).Single();Check(merged.Unlocked&&merged.Progress==8);Check(b.ForGame(key).Single().Canonical()==merged.Canonical());
            var count=a.ReadAll().Count;a.ObserveMany(new[]{locked,unlocked,progress});Check(a.ReadAll().Count==count);
            var earlier=Record(true,1);earlier.UnlockedUtc="2026-10-23T10:30:00+01:00";a.Observe(earlier);Check(a.ForGame(key).Single().UnlockedUtc.StartsWith("2026-10-23T09:30"));
            var other=Record();other.Scope=Identity.Hash("other-platform");a.Observe(other);Check(a.ForGame(key).Count==2);
            var different=Record();different.Game=Identity.Hash("different-game");a.Observe(different);Check(a.ForGame(key).Count==2);
            var changed=Record(false,100);changed.Target=200;a.ObserveMany(new[]{changed});Check(a.ForGame(key).Single(r=>r.Scope==locked.Scope).Progress==null);
            Check(new AchievementStore(Path.Combine(root,"a")).ForGame(key).Any(r=>r.Unlocked));
            var invalid=Record();invalid.Progress=Double.NaN;Reject(()=>invalid.Validate());invalid=Record();invalid.Progress=11;Reject(()=>invalid.Validate());invalid=Record();invalid.UnlockedUtc="2026-10-23T10:00:00Z";Reject(()=>invalid.Validate());
            var corrupt=Path.Combine(root,"bad");Directory.CreateDirectory(corrupt);File.WriteAllText(Path.Combine(corrupt,new string('a',64)+".json"),locked.Canonical());var n=b.ReadAll().Count;Reject(()=>b.Import(corrupt));Check(b.ReadAll().Count==n);
            var cache=new Cache{Data=new Data{ProviderKey="Steam",AppId=1,Achievements=new List<Item>{new Item{ApiName="A",DisplayName="Name",Description="Keep",Unlocked=true,UnlockTimeUtc=new DateTime(2026,10,23,1,0,0,DateTimeKind.Utc),ProgressNum=5,ProgressDenom=10}}}};
            var adapter=new AchievementAdapter(new Plugin{CacheManager=cache},true);var game=new Game{Id=Guid.NewGuid(),Name="Fixture"};var store=new AchievementStore(Path.Combine(root,"adapter"));adapter.Capture(store,game,key,new string('a',32));Check(store.ForGame(key).Single().Unlocked);
            cache.Data.Achievements[0].Unlocked=false;cache.Data.Achievements[0].UnlockTimeUtc=null;cache.Data.Achievements[0].ProgressNum=0;adapter.Capture(store,game,key,new string('b',32));adapter.Project(store,game,key);Check(cache.Writes==1&&cache.Data.Achievements[0].Unlocked&&cache.Data.Achievements[0].ProgressNum==5);Check(cache.Data.Achievements[0].Description=="Keep");
            adapter.Project(store,game,key);Check(cache.Writes==1);cache.Data.ProviderKey="Xbox";cache.Data.Achievements[0].Unlocked=false;cache.Data.Achievements[0].UnlockTimeUtc=null;adapter.Project(store,game,key);Check(!cache.Data.Achievements[0].Unlocked);
            var db=new LegacyDb{Data=new LegacyData{Items=new List<LegacyItem>{new LegacyItem{ApiName="UNKNOWN_DATE",Name="Earlier unlock",Description="Keep",DateUnlocked=new DateTime(1982,1,1)}}}};var legacy=new AchievementAdapter(new LegacyPlugin{PluginDatabase=db},false);legacy.Capture(store,game,key,new string('a',32));Check(store.ForGame(key).Any(r=>r.Id=="UNKNOWN_DATE"&&r.Unlocked&&r.UnlockedUtc==null));db.Data.Items[0].DateUnlocked=null;legacy.Project(store,game,key);Check(db.Writes==1&&db.Data.Items[0].DateUnlocked.Value.Year==1982);legacy.Project(store,game,key);Check(db.Writes==1);
            if(args.Length>1){var cloud=Path.Combine(root,"cloud");Directory.CreateDirectory(cloud);var transport=new RcloneTransport(args[1],"",cloud,"playnite-link-v1/achievements/objects");transport.PushFiles(store.Objects,Directory.GetFiles(store.Objects,"*.json").Select(Path.GetFileName));var pulled=Path.Combine(root,"pulled");new RcloneTransport(args[1],"",cloud,"playnite-link-v1").PullMetadata(pulled);var third=new AchievementStore(Path.Combine(root,"third"));third.Import(Path.Combine(pulled,"achievements","objects"));Check(third.ForGame(key).Count==2&&third.ForGame(key).All(r=>r.Unlocked));Check(MetadataCache.Validate(pulled)==0);}
            Console.WriteLine("PASS achievement merge, adapters, retries and real Rclone: "+checks+" checks");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
