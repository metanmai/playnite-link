using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlaytimeRclone;

internal static class StreamingTests
{
    static string root;static int passed,failed;static string game=Identity.Hash("manual:streaming-demo");static string aId=Guid.NewGuid().ToString("N"),bId=Guid.NewGuid().ToString("N");
    static string Dir(){var path=Path.Combine(root,Guid.NewGuid().ToString("N").Substring(0,10));Directory.CreateDirectory(path);return path;}
    static void Equal<T>(T a,T b){if(!Object.Equals(a,b))throw new Exception("Expected "+a+", got "+b);}
    static void True(bool value){if(!value)throw new Exception("Assertion failed");}
    static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected rejection");}
    static void Test(string title,Action action){try{action();passed++;Console.WriteLine("PASS "+title);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+title+": "+ex);}}
    static void LargeFile(string path,long bytes)
    {var buffer=new byte[1024*1024];new Random(17).NextBytes(buffer);using(var file=File.Create(path))while(bytes>0){var count=(int)Math.Min(buffer.Length,bytes);file.Write(buffer,0,count);bytes-=count;}}
    static string HashFile(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
    static Revision Save(BlobStore blobs,string folder,string device,params string[] parents){return new Revision{Version=2,Game=game,Kind="save",Device=device,Data=blobs.Capture(folder),Parents=parents.ToList()};}
    static void CopyDirectoryObjects(string from,string to){Directory.CreateDirectory(to);foreach(var p in Directory.GetFiles(from,"*.json"))File.Copy(p,Path.Combine(to,Path.GetFileName(p)),true);}
    static void Main(string[] args)
    {
        root=args[0];Directory.CreateDirectory(root);
        if(args.Length>1&&args[1]=="--ledger-cloud"){LiveLedger(args[2]);return;}
        if(args.Length>1&&args[1]=="--cloud"){LiveCloud(args[2]);return;}
        Test("stream and restore a 64 MiB save without a save-size cap",()=>{
            var store=new BlobStore(Dir());var saves=Dir();var original=Path.Combine(saves,"large.sav");LargeFile(original,64L*1024*1024+13);var sha=HashFile(original);var data=store.Capture(saves);
            Equal(64L*1024*1024+13,BlobReference.Parse(data["large.sav"]).Length);var destination=Dir();store.Restore(destination,data,Revision.ContentHashOf(store.Capture(destination)));Equal(sha,HashFile(Path.Combine(destination,"large.sav")));
        });
        Test("unchanged files deduplicate across paths and revisions",()=>{var store=new BlobStore(Dir());var saves=Dir();File.WriteAllText(Path.Combine(saves,"a"),"same");File.WriteAllText(Path.Combine(saves,"b"),"same");var first=store.Capture(saves);var second=store.Capture(saves);Equal(first["a"],first["b"]);Equal(Revision.ContentHashOf(first),Revision.ContentHashOf(second));Equal(1,Directory.GetFiles(store.DirectoryPath,"*.blob").Length);});
        Test("capture more than the old 4096 file limit",()=>{var store=new BlobStore(Dir());var saves=Dir();for(int i=0;i<4100;i++)File.WriteAllText(Path.Combine(saves,i+".sav"),"");Equal(4100,store.Capture(saves).Count);Equal(1,Directory.GetFiles(store.DirectoryPath,"*.blob").Length);});
        Test("blob manifests reject path traversal and negative sizes",()=>{Throws(()=>BlobReference.Parse("blob:"+new string('a',64)+":-1"));Throws(()=>BlobReference.Parse("blob:../escape:5"));});
        Test("missing blobs cannot modify existing saves",()=>{var store=new BlobStore(Dir());var saves=Dir();File.WriteAllText(Path.Combine(saves,"safe"),"keep");var data=new SortedDictionary<string,string>{{"a","blob:"+new string('a',64)+":4"}};Throws(()=>store.Restore(saves,data,Revision.ContentHashOf(store.Capture(saves))));Equal("keep",File.ReadAllText(Path.Combine(saves,"safe")));});
        Test("same-length corruption fails SHA-256 before swapping folders",()=>{var store=new BlobStore(Dir());var saves=Dir();File.WriteAllText(Path.Combine(saves,"progress"),"original");var data=store.Capture(saves);var reference=BlobReference.Parse(data["progress"]);File.WriteAllBytes(store.PathFor(reference.Hash),Encoding.UTF8.GetBytes("tampered"));var target=Dir();File.WriteAllText(Path.Combine(target,"safe"),"keep");Throws(()=>store.Restore(target,data,Revision.ContentHashOf(store.Capture(target))));Equal("keep",File.ReadAllText(Path.Combine(target,"safe")));});
        Test("legacy inline snapshots migrate with common-version ancestry intact",()=>{var store=new BlobStore(Dir());var vault=new Vault(Dir());var old=new Revision{Game=game,Kind="save",Device=aId,Data=new SortedDictionary<string,string>{{"progress",Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy"))}}};vault.Add(old);vault.State.Links[game]=new SaveLink{Save=new Checkpoint{Revision=old.Hash(),Content=old.ContentHash()}};vault.Save();vault.UpgradeLegacySaves(store,aId);var head=vault.Heads(game,"save").Single();Equal(2,head.Version);Equal(old.Hash(),head.Parents.Single());Equal(head.ContentHash(),vault.State.Links[game].Save.Content);Equal("legacy",Encoding.UTF8.GetString(store.ReadSmall(head.Data["progress"])));});
        Test("migrating before a remote legacy descendant arrives does not invent a conflict",()=>{var store=new BlobStore(Dir());var vault=new Vault(Dir());var original=new Revision{Game=game,Kind="save",Device=aId,Data=new SortedDictionary<string,string>{{"progress",Convert.ToBase64String(Encoding.UTF8.GetBytes("base"))}}};vault.Add(original);vault.UpgradeLegacySaves(store,aId);var remote=new Revision{Game=game,Kind="save",Device=bId,Parents=new List<string>{original.Hash()},Data=new SortedDictionary<string,string>{{"progress",Convert.ToBase64String(Encoding.UTF8.GetBytes("new"))}}};vault.Add(remote);vault.UpgradeLegacySaves(store,aId);Equal(1,vault.Heads(game,"save").Count);Equal("new",Encoding.UTF8.GetString(store.ReadSmall(vault.Heads(game,"save").Single().Data["progress"])));});
        Test("cancelled streaming capture cleans staging without publishing partial blobs",()=>{var store=new BlobStore(Dir());var saves=Dir();LargeFile(Path.Combine(saves,"save"),2L*1024*1024);int reads=0;store.CancelRequested=()=>++reads>2;Throws(()=>store.Capture(saves));Equal(0,Directory.GetFiles(store.DirectoryPath).Length);});
        Test("two equivalent migration branches converge without a false conflict",()=>{var store=new BlobStore(Dir());var vault=new Vault(Dir());var saves=Dir();File.WriteAllText(Path.Combine(saves,"progress"),"same");var data=store.Capture(saves);vault.Add(new Revision{Version=2,Game=game,Kind="save",Device=aId,Data=data});vault.Add(new Revision{Version=2,Game=game,Kind="save",Device=bId,Data=data});var result=vault.Reconcile(game,"save",aId,data,new Checkpoint(),c=>{throw new Exception("False conflict");},d=>{});True(!result.Cancelled);Equal(1,vault.Heads(game,"save").Count);Equal(2,vault.Heads(game,"save").Single().Parents.Count);});
        Test("device profiles round trip without cloud credentials",()=>{var a=new DeviceDirectory(Dir());a.PublishProfile(aId,new DevicePreferences{Name="Desktop"},new List<DeviceGame>{new DeviceGame{Key=game,Name="Demo",SavesEnabled=true}});var b=new DeviceDirectory(Dir());b.Import(a.Objects);var view=b.Views().Single();Equal("Desktop",view.Name);Equal(1,view.Games.Count);True(!File.ReadAllText(Directory.GetFiles(a.Objects).Single()).Contains("token"));});
        Test("remote per-device preferences propagate and resolve concurrent edits",()=>{var a=new DeviceDirectory(Dir());var b=new DeviceDirectory(Dir());a.SetSettings(aId,bId,new DevicePreferences{Name="Handheld",IntervalSeconds=45,BandwidthKiB=512});b.Import(a.Objects);Equal(45,b.Heads(bId,"settings").Single().Settings.IntervalSeconds);a.SetSettings(aId,bId,new DevicePreferences{Name="A"});b.SetSettings(bId,bId,new DevicePreferences{Name="B"});a.Import(b.Objects);True(a.Views().Single().SettingsConflict);a.SetSettings(aId,bId,new DevicePreferences{Name="Resolved"});Equal(1,a.Heads(bId,"settings").Count);Equal("Resolved",a.Views().Single().Name);});
        Test("device pause remove and explicit re-enable converge",()=>{var a=new DeviceDirectory(Dir());var b=new DeviceDirectory(Dir());a.SetState(aId,bId,"paused");b.Import(a.Objects);Equal("paused",b.EffectiveState(bId));a.SetState(aId,bId,"removed");b.Import(a.Objects);Equal("removed",b.EffectiveState(bId));b.SetState(bId,bId,"active");a.Import(b.Objects);Equal("active",a.EffectiveState(bId));});
        Test("concurrent remove wins over an unseen resume",()=>{var a=new DeviceDirectory(Dir());var b=new DeviceDirectory(Dir());a.SetState(aId,bId,"active");b.Import(a.Objects);a.SetState(aId,bId,"removed");b.SetState(bId,bId,"active");a.Import(b.Objects);Equal("removed",a.EffectiveState(bId));a.SetState(aId,bId,"active");Equal("active",a.EffectiveState(bId));});
        Test("invalid remote settings and cross-device parents are rejected",()=>{Throws(()=>new DevicePreferences{IntervalSeconds=0}.Validate());var directory=new DeviceDirectory(Dir());var original=new DeviceEvent{Author=aId,Target=aId,Kind="status",State="active"};directory.Add(original);directory.Add(new DeviceEvent{Author=bId,Target=bId,Kind="status",State="paused",Parents=new List<string>{original.Hash()}});Throws(()=>directory.Views());});
        Test("launch during background staging prevents save replacement",()=>{
            var store=new BlobStore(Dir());var cloud=Dir();File.WriteAllText(Path.Combine(cloud,"save"),"remote");var data=store.Capture(cloud);var local=Dir();File.WriteAllText(Path.Combine(local,"save"),"local");var expected=Revision.ContentHashOf(store.Capture(local));var called=false;
            Throws(()=>store.Restore(local,data,expected,()=>{called=true;True(Directory.GetDirectories(Path.GetDirectoryName(local),Path.GetFileName(local)+".pl-stage-*").Length==1);throw new IOException("Game started while the remote files staged");}));
            True(called);Equal("local",File.ReadAllText(Path.Combine(local,"save")));Equal(0,Directory.GetDirectories(Path.GetDirectoryName(local),Path.GetFileName(local)+".pl-backup-*").Length);Equal(0,Directory.GetDirectories(Path.GetDirectoryName(local),Path.GetFileName(local)+".pl-stage-*").Length);
        });
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");Environment.ExitCode=failed==0?0:1;
    }
    static void LiveLedger(string settingsPath)
    {
        var config=JObject.Parse(File.ReadAllText(settingsPath));var remote=(string)config["Remote"]+"/ledger-completion";
        Test("real Google Drive additive playtime convergence and retry",()=>{
            using(var a=new Repository(Path.Combine(root,"a")))using(var b=new Repository(Path.Combine(root,"b")))
            {
                if(a.Snapshot().Mappings.Count==0){a.Enroll("demo","manual:streaming-demo",100,100);a.Start("demo");a.Stop("demo",17);}
                if(b.Snapshot().Mappings.Count==0){b.Enroll("demo","manual:streaming-demo",100,100);b.Start("demo");b.Stop("demo",23);}
                var transport=new RcloneTransport((string)config["Exe"],(string)config["Config"],remote,limitSeconds:180);var engine=new SyncEngine();
                foreach(var repo in new[]{a,b,a,b})
                {
                    bool done=false;for(int attempt=0;attempt<6&&!done;attempt++)
                    {try{engine.Run(repo,transport,Dir());done=true;}catch(IOException ex){Console.WriteLine("Provider retry "+(attempt+1)+": "+ex.Message);}if(!done)System.Threading.Thread.Sleep(30000);}
                    if(!done)throw new IOException("Provider did not permit ledger completion.");Console.WriteLine("Cloud ledger exchange completed.");
                }
                Equal(140UL,new Ledger(a.Snapshot().Entries).Total(game));Equal(140UL,new Ledger(b.Snapshot().Entries).Total(game));
            }
        });
        File.WriteAllText(Path.Combine(root,"live-ledger-result.json"),JsonConvert.SerializeObject(new{passed=failed==0,provider="Existing Rclone Google Drive",expectedSeconds=140,duplicateRetryVerified=failed==0},Formatting.Indented));
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");Environment.ExitCode=failed==0?0:1;
    }
    static void LiveCloud(string settingsPath)
    {
        var config=JObject.Parse(File.ReadAllText(settingsPath));var exe=(string)config["Exe"];var cfg=(string)config["Config"];var shared=(string)config["Remote"]+"/streaming-vault";var remote=shared+"/run-"+Guid.NewGuid().ToString("N").Substring(0,8);
        var log=new Dictionary<string,object>();var watch=System.Diagnostics.Stopwatch.StartNew();
        RcloneTransport.FailureDiagnostic=message=>File.WriteAllText(Path.Combine(root,"rclone-failure.private.log"),message);
        Test("real Google Drive >32 MiB save, conflict, playtime and device management",()=>{
            var a=new Vault(Dir());var b=new Vault(Dir());var ba=new BlobStore(Dir());var bb=new BlobStore(Dir());var saveA=Dir();var saveB=Dir();
            LargeFile(Path.Combine(saveA,"world.dat"),40L*1024*1024+13);File.Copy(Path.Combine(saveA,"world.dat"),Path.Combine(saveA,"world-copy.dat"));File.WriteAllText(Path.Combine(saveA,"progress.dat"),"base");
            var blobCloud=new RcloneTransport(exe,cfg,shared,"playnite-link-v1/blobs","*.blob",1800);var metadata=new RcloneTransport(exe,cfg,remote,"playnite-link-v1/revisions/objects",limitSeconds:600);
            var deviceCloud=new RcloneTransport(exe,cfg,remote,"playnite-link-v1/devices/objects",limitSeconds:600);bb.Fetch=names=>blobCloud.PullFiles(bb.DirectoryPath,names);
            var baseline=Save(ba,saveA,aId);a.Add(baseline);blobCloud.Push(ba.DirectoryPath);metadata.Push(a.Objects);Console.WriteLine("Cloud: streamed large files and published manifest");
            var incoming=Dir();metadata.Pull(incoming);b.Import(incoming);var cpB=new Checkpoint();var empty=bb.Capture(saveB);b.Reconcile(game,"save",bId,empty,cpB,c=>"cancel",data=>bb.Restore(saveB,data,Revision.ContentHashOf(empty)));
            Equal(HashFile(Path.Combine(saveA,"world.dat")),HashFile(Path.Combine(saveB,"world.dat")));Equal(HashFile(Path.Combine(saveA,"world.dat")),HashFile(Path.Combine(saveB,"world-copy.dat")));Console.WriteLine("Cloud: second device restored and verified large save");
            File.WriteAllText(Path.Combine(saveA,"progress.dat"),"desktop-offline");File.WriteAllText(Path.Combine(saveB,"progress.dat"),"handheld-offline");var left=Save(ba,saveA,aId,baseline.Hash());var right=Save(bb,saveB,bId,baseline.Hash());a.Add(left);b.Add(right);blobCloud.Push(ba.DirectoryPath);blobCloud.Push(bb.DirectoryPath);Task.WaitAll(Task.Run(()=>metadata.Push(a.Objects)),Task.Run(()=>metadata.Push(b.Objects)));
            incoming=Dir();metadata.Pull(incoming);a.Import(incoming);b.Import(incoming);Equal(2,a.Heads(game,"save").Count);ba.Fetch=names=>blobCloud.PullFiles(ba.DirectoryPath,names);
            var cpA=new Checkpoint{Revision=left.Hash(),Content=left.ContentHash()};string backup=null;var local=ba.Capture(saveA);
            a.Reconcile(game,"save",aId,local,cpA,c=>right.Hash(),data=>backup=ba.Restore(saveA,data,Revision.ContentHashOf(local)));
            Equal("desktop-offline",File.ReadAllText(Path.Combine(backup,"progress.dat")));Equal("handheld-offline",File.ReadAllText(Path.Combine(saveA,"progress.dat")));metadata.Push(a.Objects);
            incoming=Dir();metadata.Pull(incoming);b.Import(incoming);Equal(1,b.Heads(game,"save").Count);Equal(cpA.Content,b.Heads(game,"save").Single().ContentHash());Console.WriteLine("Cloud: concurrent branches resolved with preserved backup");
            var da=new DeviceDirectory(Dir());var db=new DeviceDirectory(Dir());da.PublishProfile(aId,new DevicePreferences{Name="Cloud test desktop"},new List<DeviceGame>{new DeviceGame{Key=game,Name="Cloud Trail >32 MiB demo",SavesEnabled=true}});deviceCloud.Push(da.Objects);incoming=Dir();deviceCloud.Pull(incoming);db.Import(incoming);db.PublishProfile(bId,new DevicePreferences{Name="Cloud test handheld"},new List<DeviceGame>());deviceCloud.Push(db.Objects);incoming=Dir();deviceCloud.Pull(incoming);da.Import(incoming);Equal(2,da.Views().Count);
            da.SetSettings(aId,bId,new DevicePreferences{Name="Cloud test handheld",IntervalSeconds=45,BandwidthKiB=1024});da.SetState(aId,bId,"paused");deviceCloud.Push(da.Objects);incoming=Dir();deviceCloud.Pull(incoming);db.Import(incoming);Equal("paused",db.EffectiveState(bId));Equal(45,db.Heads(bId,"settings").Single().Settings.IntervalSeconds);
            db.SetState(bId,bId,"active");deviceCloud.Push(db.Objects);incoming=Dir();deviceCloud.Pull(incoming);da.Import(incoming);Equal("active",da.EffectiveState(bId));da.SetState(aId,bId,"removed");deviceCloud.Push(da.Objects);incoming=Dir();deviceCloud.Pull(incoming);db.Import(incoming);Equal("removed",db.EffectiveState(bId));Console.WriteLine("Cloud: device roster, remote settings, pause/resume/remove verified");
            using(var ra=new Repository(Dir()))using(var rb=new Repository(Dir()))
            {
                ra.Enroll("demo","manual:streaming-demo",100,100);rb.Enroll("demo","manual:streaming-demo",100,100);ra.Start("demo");ra.Stop("demo",17);rb.Start("demo");rb.Stop("demo",23);
                var ledgerCloud=new RcloneTransport(exe,cfg,remote,limitSeconds:600);var engine=new SyncEngine();engine.Run(ra,ledgerCloud,Dir());engine.Run(rb,ledgerCloud,Dir());engine.Run(ra,ledgerCloud,Dir());Equal(140UL,new Ledger(ra.Snapshot().Entries).Total(game));Equal(140UL,new Ledger(rb.Snapshot().Entries).Total(game));engine.Run(ra,ledgerCloud,Dir());Equal(140UL,new Ledger(ra.Snapshot().Entries).Total(game));
            }
            log["provider"]="Existing Rclone Google Drive remote";log["remote"]=remote;log["largeFileBytes"]=40L*1024*1024+13;log["logicalSnapshotBytes"]=80L*1024*1024+26;log["largeFileHashVerified"]=true;log["duplicateFilesShareBlob"]=baseline.Data["world.dat"]==baseline.Data["world-copy.dat"];log["conflictResolved"]=true;log["backupPreserved"]=true;log["connectedDevices"]=2;log["remoteDeviceConfigApplied"]=true;log["pauseResumeRemoveVerified"]=true;log["playtimeSeconds"]=140;log["noUserSavesUsed"]=true;
        });
        log["passed"]=failed==0;log["elapsedSeconds"]=watch.Elapsed.TotalSeconds;File.WriteAllText(Path.Combine(root,"live-cloud-result.json"),JsonConvert.SerializeObject(log,Formatting.Indented));Console.WriteLine(JsonConvert.SerializeObject(log,Formatting.Indented));Environment.ExitCode=failed==0?0:1;
    }
}
