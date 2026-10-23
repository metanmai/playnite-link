using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using PlaytimeRclone;
using Newtonsoft.Json.Linq;

class AchievementCloudProbe
{
    static int Main(string[] args)
    {
        try
        {
            var root=args[0];var exe=args[1];var config=args[2];var remote=args[3];if(!remote.Contains("/validation/achievement-"))throw new Exception("An isolated validation destination is required.");
            var source=Path.Combine(root,"source");var destination=Path.Combine(root,"fresh-client");Directory.CreateDirectory(source);Directory.CreateDirectory(destination);var key=Identity.Hash("achievement-cloud-fixture");var device=Guid.NewGuid().ToString("N");
            var store=new AchievementStore(source);store.Observe(new AchievementRecord{Game=key,Scope=Identity.Hash("custom-fixture"),Id="CLOUD_FIXTURE",Name="Synthetic cloud validation",Description="No personal game data",Device=device,Unlocked=true,UnlockedUtc="2026-10-23T01:00:00Z",Progress=7,Target=10});
            var blob=new BlobStore(Path.Combine(source,"blobs"));string reference;using(var bytes=new MemoryStream(Encoding.UTF8.GetBytes("private fixture round trip")))reference=blob.Put(bytes).ToString();
            var catalog=new AchievementCatalogStore(source);catalog.Observe(new AchievementCatalog{Game=key,Scope=Identity.Hash("custom-fixture"),CreatedUtc=DateTimeOffset.UtcNow.ToString("O"),Data=new JObject{{"ProviderKey","Custom"},{"AppId",0},{"Achievements",new JArray(new JObject{{"ApiName","CLOUD_FIXTURE"},{"DisplayName","Synthetic cloud validation"},{"Points",30},{"Hidden",false}})}}});
            var revisions=new Vault(Path.Combine(source,"customizations"));revisions.Add(new Revision{Version=2,Game=key,Kind="save",Device=device,Data=new SortedDictionary<string,string>{{"link-personal-state.json",reference}}});
            var blobTransport=new RcloneTransport(exe,config,remote,"playnite-link-v1/blobs","*.blob");blobTransport.PushFiles(blob.DirectoryPath,Directory.GetFiles(blob.DirectoryPath,"*.blob").Select(Path.GetFileName));
            new RcloneTransport(exe,config,remote,"playnite-link-v1/achievements/objects").PushFiles(store.Objects,Directory.GetFiles(store.Objects,"*.json").Select(Path.GetFileName));
            new RcloneTransport(exe,config,remote,"playnite-link-v1/achievement-catalogs/objects").PushFiles(catalog.Objects,Directory.GetFiles(catalog.Objects,"*.json").Select(Path.GetFileName));
            new RcloneTransport(exe,config,remote,"playnite-link-v1/achievement-customizations/objects").PushFiles(revisions.Objects,Directory.GetFiles(revisions.Objects,"*.json").Select(Path.GetFileName));
            var metadata=Path.Combine(destination,"metadata");new RcloneTransport(exe,config,remote,"playnite-link-v1").PullMetadata(metadata);var other=new AchievementStore(destination);other.Import(Path.Combine(metadata,"achievements","objects"));var schema=new AchievementCatalogStore(destination);schema.Import(Path.Combine(metadata,"achievement-catalogs","objects"));var history=new Vault(Path.Combine(destination,"customizations"));history.Import(Path.Combine(metadata,"achievement-customizations","objects"));
            var received=new BlobStore(Path.Combine(destination,"blobs"));received.Fetch=names=>blobTransport.PullFiles(received.DirectoryPath,names);received.Ensure(history.Heads(key,"save").Single().Data);
            if(!other.ForGame(key).Single().Unlocked||other.ForGame(key).Single().Progress!=7||(int)schema.Single(key).Data["Achievements"][0]["Points"]!=30||Encoding.UTF8.GetString(received.ReadSmall(reference))!="private fixture round trip")throw new Exception("Fresh cloud client did not preserve achievement metadata/state/blob hashes.");
            Console.WriteLine("PASS real cloud achievement records, catalog, customization ancestry and verified blob round trip to an initially empty client");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
