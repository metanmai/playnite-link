using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
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
    public sealed class Account{public string AccountId{get;set;}}
    public sealed class Registry{public Account Account;public Account GetLiveSettings(string key){return Account;}}
    public sealed class Plugin{public Cache CacheManager{get;set;}public Registry ProviderRegistry{get;set;}}
    public sealed class LegacyItem{public string ApiName{get;set;}public string Name{get;set;}public string Description{get;set;}public DateTime? DateUnlocked{get;set;}public bool IsUnlock{get{return DateUnlocked.HasValue;}}}
    public sealed class LegacyData{public bool IsIgnored{get;set;}public List<LegacyItem> Items{get;set;}}
    public sealed class LegacyDb{public LegacyData Data;public int Writes;public LegacyData Get(Guid id,bool load,bool refresh){return Data;}public void Update(LegacyData data){Writes++;Data=data;}}
    public sealed class LegacyPlugin{public LegacyDb PluginDatabase{get;set;}}
    static string StoreText(BlobStore store,string value)
    {using(var input=new MemoryStream(Encoding.UTF8.GetBytes(value)))return store.Put(input).ToString();}

    static void CustomizationPayload(string root,string rclone)
    {
        var path=Path.Combine(root,"custom");Directory.CreateDirectory(path);
        var blobs=new BlobStore(Path.Combine(path,"b"));
        var manifest=AchievementCustomizationPayload.Manifest;
        var personal=AchievementCustomizationPayload.PersonalState;
        var package=Path.Combine(path,"valid.pa");
        var nested=Enumerable.Repeat("{\"KindStyles\":{\"Achievement\":",80);
        var deep="{\"NotificationAppearanceOverride\":{\"Style\":"+String.Concat(nested)+"{}"+new string('}',162);
        var deepData=new SortedDictionary<string,string>{{manifest,StoreText(blobs,deep)}};
        Reject(()=>AchievementCustomizationPayload.Prepare(deepData,blobs,null,package));Check(!File.Exists(package));

        var notes="{\"AchievementNotes\":{\"test\":\"portable note\"},\"NotificationAppearanceOverride\":{\"ToastUseThemeStyling\":false}}";
        var valid=new SortedDictionary<string,string>{{manifest,StoreText(blobs,notes)},{personal,StoreText(blobs,"{\"GoalAchievementApiNames\":[\"test\"]}")}};
        var result=AchievementCustomizationPayload.Prepare(valid,blobs,null,package);
        Check((string)result["GoalAchievementApiNames"][0]=="test");
        using(var file=File.OpenRead(package))using(var zip=new ZipArchive(file,ZipArchiveMode.Read))
        using(var reader=new StreamReader(zip.GetEntry(manifest).Open()))
        {
            var restored=JObject.Parse(reader.ReadToEnd());
            Check((string)restored["AchievementNotes"]["test"]=="portable note");
        }

        Func<long,string> reference=bytes=>new BlobReference{Hash=new string('b',64),Length=bytes}.ToString();
        int fetches=0;Action<BlobReference,Stream> countFetch=(r,s)=>fetches++;
        var fake=new SortedDictionary<string,string>{{manifest,reference(AchievementCustomizationPayload.ManifestBytes+1)}};
        Reject(()=>AchievementCustomizationPayload.Prepare(fake,blobs,countFetch,package));
        fake[manifest]=reference(AchievementCustomizationPayload.ManifestBytes);
        fake[personal]=reference(AchievementCustomizationPayload.PersonalStateBytes+1);
        Reject(()=>AchievementCustomizationPayload.Prepare(fake,blobs,countFetch,package));
        fake[personal]=reference(AchievementCustomizationPayload.PersonalStateBytes);
        fake["images/1.png"]=reference(AchievementCustomizationPayload.ImageBytes+1L);
        Reject(()=>AchievementCustomizationPayload.Prepare(fake,blobs,countFetch,package));
        fake["images/1.png"]=reference(AchievementCustomizationPayload.ImageBytes);
        fake["images/2.png"]=reference(AchievementCustomizationPayload.ImageBytes);
        fake["images/3.png"]=reference(AchievementCustomizationPayload.ImageBytes);
        fake["images/4.png"]=reference(32*1024*1024+1);
        Reject(()=>AchievementCustomizationPayload.Prepare(fake,blobs,countFetch,package));Check(fetches==0);
        fake["images/4.png"]=reference(32*1024*1024);
        AchievementCustomizationPayload.Validate(fake);Check(fetches==0);

        if(rclone==null)return;
        var remote=new BlobStore(Path.Combine(path,"remote","blobs"));
        var transport=new RcloneTransport(rclone,"",Path.Combine(path,"remote"),"blobs","*.blob");
        var download=new SortedDictionary<string,string>{{personal,StoreText(remote,"{\"GoalAchievementApiNames\":[\"remote\"]}")}};
        var downloaded=BlobReference.Parse(download[personal]);var filename=remote.PathFor(downloaded.Hash);
        var validText=File.ReadAllBytes(filename);
        using(var extra=new FileStream(filename,FileMode.Append))extra.WriteByte(32);
        Reject(()=>AchievementCustomizationPayload.Prepare(download,blobs,transport.FetchBoundedBlob,package));
        Check(!File.Exists(blobs.PathFor(downloaded.Hash))&&!Directory.GetFiles(blobs.DirectoryPath,"*.tmp").Any());
        File.WriteAllBytes(filename,validText);
        var read=AchievementCustomizationPayload.Prepare(download,blobs,transport.FetchBoundedBlob,package);
        Check((string)read["GoalAchievementApiNames"][0]=="remote");

        // The actual manifest boundary succeeds without a fixture-sized
        // allocation to generate it. An extra on-disk byte fails the next
        // streamed download even when the content address and declared
        // length still describe the original boundary-sized file.
        var boundaryFile=Path.Combine(path,"boundary.json");
        var header=Encoding.UTF8.GetBytes("{\"AchievementNotes\":{\"test\":\"boundary\u0020fixture\"}}");
        var padding=Enumerable.Repeat((byte)' ',8192).ToArray();
        using(var output=File.Create(boundaryFile))
        {
            output.Write(header,0,header.Length);
            for(var remaining=AchievementCustomizationPayload.ManifestBytes-header.Length;remaining>0;)
            {var count=Math.Min(remaining,padding.Length);output.Write(padding,0,count);remaining-=count;}
        }
        var boundary=new SortedDictionary<string,string>();
        using(var input=File.OpenRead(boundaryFile))boundary.Add(manifest,remote.Put(input).ToString());
        var edgePackage=Path.Combine(path,"boundary.pa");
        AchievementCustomizationPayload.Prepare(boundary,blobs,transport.FetchBoundedBlob,edgePackage);
        using(var input=File.OpenRead(edgePackage))using(var zip=new ZipArchive(input,ZipArchiveMode.Read))
            Check(zip.GetEntry(manifest).Length==AchievementCustomizationPayload.ManifestBytes);
        var edgeReference=BlobReference.Parse(boundary[manifest]);
        File.Delete(blobs.PathFor(edgeReference.Hash));
        using(var output=new FileStream(remote.PathFor(edgeReference.Hash),FileMode.Append))output.WriteByte(32);
        Reject(()=>AchievementCustomizationPayload.Prepare(boundary,blobs,transport.FetchBoundedBlob,Path.Combine(path,"too-large.pa")));
        Check(!File.Exists(blobs.PathFor(edgeReference.Hash))&&!Directory.GetFiles(blobs.DirectoryPath,"*.tmp").Any());
    }

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
            var registry=new Registry{Account=new Account{AccountId="account-a"}};var scoped=new AchievementAdapter(new Plugin{CacheManager=cache,ProviderRegistry=registry},true);cache.Data.ProviderKey="Steam";cache.Data.Achievements[0].Unlocked=true;scoped.Capture(store,game,key,new string('a',32));registry.Account.AccountId="account-b";cache.Data.Achievements[0].Unlocked=false;cache.Data.Achievements[0].UnlockTimeUtc=null;var writes=cache.Writes;scoped.Project(store,game,key);Check(!cache.Data.Achievements[0].Unlocked&&cache.Writes==writes);registry.Account.AccountId="account-a";scoped.Project(store,game,key);Check(cache.Data.Achievements[0].Unlocked);
            var db=new LegacyDb{Data=new LegacyData{Items=new List<LegacyItem>{new LegacyItem{ApiName="UNKNOWN_DATE",Name="Earlier unlock",Description="Keep",DateUnlocked=new DateTime(1982,1,1)}}}};var legacy=new AchievementAdapter(new LegacyPlugin{PluginDatabase=db},false);legacy.Capture(store,game,key,new string('a',32));Check(store.ForGame(key).Any(r=>r.Id=="UNKNOWN_DATE"&&r.Unlocked&&r.UnlockedUtc==null));db.Data.Items[0].DateUnlocked=null;legacy.Project(store,game,key);Check(db.Writes==1&&db.Data.Items[0].DateUnlocked.Value.Year==1982);legacy.Project(store,game,key);Check(db.Writes==1);
            var fixture=Path.Combine(root,"indexes");var indexed=new AchievementStore(fixture);
            var batch=Enumerable.Range(0,180).Select(i=>{var value=Record();value.Game=Identity.Hash("indexed-"+i);return value;}).ToArray();
            indexed.ObserveMany(batch);var candidates=indexed.ObservationCandidates;indexed.ObserveMany(batch);
            Check(indexed.ObservationCandidates-candidates==180);Check(indexed.ReadAll().Count==180);
            var schemas=new AchievementCatalogStore(fixture);
            foreach(var record in batch)schemas.Observe(new AchievementCatalog
            {
                Game=record.Game,Scope=record.Scope,CreatedUtc=DateTimeOffset.UtcNow.ToString("O"),
                Data=new Newtonsoft.Json.Linq.JObject{{"ProviderKey","Custom"},{"AppId",0},{"Achievements",new Newtonsoft.Json.Linq.JArray()}}
            });
            var reopened=new AchievementCatalogStore(fixture);
            foreach(var record in batch)Check(reopened.ForGame(record.Game).Count==1);
            Check(reopened.CatalogReads==180);
            Console.WriteLine("180 unchanged observations examined 180 identity candidates; 180 catalog lookups read 180 files once.");
            if(args.Length>1){var cloud=Path.Combine(root,"cloud");Directory.CreateDirectory(cloud);var transport=new RcloneTransport(args[1],"",cloud,"playnite-link-v1/achievements/objects");transport.PushFiles(store.Objects,Directory.GetFiles(store.Objects,"*.json").Select(Path.GetFileName));var pulled=Path.Combine(root,"pulled");new RcloneTransport(args[1],"",cloud,"playnite-link-v1").PullMetadata(pulled);var third=new AchievementStore(Path.Combine(root,"third"));third.Import(Path.Combine(pulled,"achievements","objects"));Check(third.ForGame(key).Count==store.ForGame(key).Count&&third.ForGame(key).All(r=>r.Unlocked));Check(MetadataCache.Validate(pulled)==0);}
            CustomizationPayload(root,args.Length>1?args[1]:null);
            Console.WriteLine("PASS achievement merge, adapters, retries and real Rclone: "+checks+" checks");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
