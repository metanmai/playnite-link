using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlaytimeRclone;
using SaveManager.Models;
using SaveManager.Services;

class SaveManagerReplacementTests
{
    static string root;static int passed,failed;
    static string Dir(){var p=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
    static void Check(bool value,string why="Assertion failed"){if(!value)throw new Exception(why);}
    static void Reject(Action action){try{action();}catch{return;}throw new Exception("Expected rejection");}
    static void Test(string title,Action action){try{action();passed++;Console.WriteLine("PASS "+title);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+title+": "+ex);}}
    class Proxy:RealProxy
    {
        readonly Game game;
        public Proxy(Type type,Game game):base(type){this.game=game;}
        public override IMessage Invoke(IMessage message)
        {
            var call=(IMethodCallMessage)message;var method=(MethodInfo)call.MethodBase;object value=null;
            if(method.Name=="Get"&&method.ReturnType==typeof(Game))value=game;
            else if(method.Name=="GetEnumerator")value=new[]{game}.AsEnumerable().GetEnumerator();
            else if(method.ReturnType.IsInterface)value=new Proxy(method.ReturnType,game).GetTransparentProxy();
            else if(method.ReturnType.IsValueType)value=Activator.CreateInstance(method.ReturnType);
            return new ReturnMessage(value,null,0,call.LogicalCallContext,call);
        }
    }
    class Log:ILogger
    {public void Info(string x){}public void Info(Exception e,string x){}public void Debug(string x){}public void Debug(Exception e,string x){}public void Warn(string x){}public void Warn(Exception e,string x){}public void Error(string x){}public void Error(Exception e,string x){}public void Trace(string x){}public void Trace(Exception e,string x){}}
    static BackupService Service(string dir,Game game){return new BackupService(dir,new Log(),(IPlayniteAPI)new Proxy(typeof(IPlayniteAPI),game).GetTransparentProxy(),()=>false);}
    static SavePlan Multi(string home,BlobStore blobs)
    {return new SavePlan(new SaveLink{Sources=new List<SaveSource>{new SaveSource{Id="main",Path=Path.Combine(home,"first")},new SaveSource{Id="source2",Path=Path.Combine(home,"second")},new SaveSource{Id="source3",Path=Path.Combine(home,"single.sav"),IsDirectory=false}}},null,blobs,Path.Combine(home,"transaction.json"));}
    static void WriteMulti(string home,string text)
    {Directory.CreateDirectory(Path.Combine(home,"first"));Directory.CreateDirectory(Path.Combine(home,"second"));File.WriteAllText(Path.Combine(home,"first","slot.sav"),text);File.WriteAllText(Path.Combine(home,"second","slot.sav"),text);File.WriteAllText(Path.Combine(home,"single.sav"),text);}
    static int Main(string[] args)
    {
        root=args[0];Directory.CreateDirectory(root);
        if(args.Length>1&&args[1]=="cloud")return LiveCloud(args[2],args[3],args[4]);
        if(args.Length>1&&args[1]=="installed")
        {
            var legacy=args[2];var data=JsonSafety.Object(File.ReadAllText(Path.Combine(legacy,"config.json")));int configs=0,archives=0;
            foreach(var property in ((JObject)data["GameConfigs"]).Properties())
            {
                var token=(JObject)property.Value;var definitions=JsonSafety.Deserialize<List<SavePath>>(token["SavePaths"].ToString());var home=Dir();
                var game=new Game{Id=Guid.NewGuid(),Name="Private migration compatibility fixture",InstallDirectory=home};var service=Service(Path.Combine(home,"core"),game);var config=service.GetOrCreateGameConfig(game.Id,game.Name);
                var sources=definitions.Select((p,i)=>new SaveSource{Id=i==0?"main":"source"+(i+1),Path=Path.Combine(home,"destination"+i),IsDirectory=p.IsDirectory}).ToList();
                var plan=new SavePlan(new SaveLink{Sources=sources},home,new BlobStore(Path.Combine(home,"blobs")),Path.Combine(home,"journal"));
                config.SavePaths=sources.Select(s=>new SavePath{Path=s.Path,IsDirectory=s.IsDirectory}).ToList();service.SaveGameConfig(config);
                foreach(var record in data["GameBackups"][property.Name])
                {
                    var path=(string)record["BackupFilePath"];if(!Path.IsPathRooted(path))path=Path.Combine(legacy,path);
                    var original=SaveMigrationFiles.Hash(path);var imported=service.ImportBackup(game.Id,game.Name,path);var copied=service.GetFullBackupPath(imported.BackupFilePath);
                    Check(SaveMigrationFiles.Hash(copied)==original&&SaveMigrationFiles.Hash(path)==original,"Legacy backup copy differs");var snapshot=SaveArchive.Read(copied,plan,new BlobStore(Path.Combine(home,"blobs")));plan.Restore(snapshot,Revision.ContentHashOf(plan.Capture()));Check(Revision.ContentHashOf(plan.Capture())==Revision.ContentHashOf(snapshot),"Isolated real-save archive restore differed");archives++;
                }
                configs++;
            }
            File.WriteAllText(Path.Combine(root,"installed-compatibility.json"),JsonConvert.SerializeObject(new{passed=true,configurations=configs,verifiedArchives=archives,isolatedRestoreRoundTrips=archives,originalFilesUnchanged=true,liveSavesWritten=false},Formatting.Indented));
            Console.WriteLine("PASS installed Save Manager compatibility: "+configs+" configurations, "+archives+" archives; source files unchanged, no live save writes");return 0;
        }
        if(args.Length>1&&args[1]=="child")
        {
            var blobs=new BlobStore(Path.Combine(root,"blobs"));var plan=Multi(root,blobs);var original=plan.Capture();var replacement=new SortedDictionary<string,string>(original,StringComparer.Ordinal);
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes("new"))){var blob=blobs.Put(stream).ToString();foreach(var key in replacement.Keys.Where(k=>!SavePlan.IsLayout(k)).ToList())replacement[key]=blob;}
            int boundary=0;typeof(BlobStore).GetField("RestoreBoundary",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,new Action<string>(point=>{if(point=="committed"&&++boundary==Int32.Parse(args[2])){File.WriteAllText(Path.Combine(root,"ready"),"ready");Thread.Sleep(Timeout.Infinite);}}));
            plan.Restore(replacement,Revision.ContentHashOf(original));return 2;
        }
        Test("Save Manager core creates, reloads, notes, imports and CRC-verifies ZIPs",()=>{
            var dir=Dir();var saves=Dir();File.WriteAllText(Path.Combine(saves,"slot.sav"),"progress");var game=new Game{Id=Guid.NewGuid(),Name="Real core fixture",InstallDirectory=dir};var service=Service(dir,game);
            var config=service.GetOrCreateGameConfig(game.Id,game.Name);config.SavePaths.Add(new SavePath{Path=saves,IsDirectory=true});service.SaveGameConfig(config);
            var backup=service.CreateBackup(game.Id,game.Name,"Before boss",true);var path=service.GetFullBackupPath(backup.BackupFilePath);Check(File.Exists(path));Check(service.ComputeCrc32(path)==backup.CRC);
            service.UpdateBackupDescription(backup,"Pinned checkpoint");Check(!backup.IsAutoBackup);service=Service(dir,game);Check(service.GetBackups(game.Id).Single().Description=="Pinned checkpoint");
            var imported=service.ImportBackup(game.Id,game.Name,path);Check(imported.CRC==backup.CRC);Check(service.GetBackups(game.Id).Count==2);
        });
        Test("same-second Save Manager backups never overwrite a previous ZIP",()=>{
            var dir=Dir();var saves=Dir();File.WriteAllText(Path.Combine(saves,"slot"),"1");var game=new Game{Id=Guid.NewGuid(),Name="Fixture"};var service=Service(dir,game);var config=service.GetOrCreateGameConfig(game.Id,game.Name);config.SavePaths.Add(new SavePath{Path=saves,IsDirectory=true});service.SaveGameConfig(config);
            var first=service.CreateBackup(game.Id,game.Name);var second=service.CreateBackup(game.Id,game.Name);Check(first.BackupFilePath!=second.BackupFilePath);Check(File.Exists(service.GetFullBackupPath(first.BackupFilePath)));
        });
        Test("retention keeps named backups and only prunes automatic ZIPs",()=>{
            var dir=Dir();var saves=Dir();File.WriteAllText(Path.Combine(saves,"slot"),"1");var game=new Game{Id=Guid.NewGuid(),Name="Fixture"};var service=Service(dir,game);var config=service.GetOrCreateGameConfig(game.Id,game.Name);config.SavePaths.Add(new SavePath{Path=saves,IsDirectory=true});service.SaveGameConfig(config);
            service.CreateBackup(game.Id,game.Name,"manual",false);for(int i=0;i<3;i++)service.CreateBackup(game.Id,game.Name,"auto",true);Check(service.CleanupOldAutoBackups(game.Id,1).Count==2);Check(service.GetBackups(game.Id).Count==2);Check(service.CleanupOldAutoBackups(game.Id,0).Count==0);
            foreach(var b in service.GetBackups(game.Id).ToList())service.DeleteBackup(b);Check(!Directory.Exists(service.GetGameBackupDirectory(config.ConfigId,game.Name)));
        });
        Test("two same-named folders and a single file survive core ZIP round trip",()=>{
            var dir=Dir();var a=Path.Combine(dir,"one","save");var b=Path.Combine(dir,"two","save");Directory.CreateDirectory(a);Directory.CreateDirectory(b);File.WriteAllText(Path.Combine(a,"slot"),"A");File.WriteAllText(Path.Combine(b,"slot"),"B");var single=Path.Combine(dir,"single");File.WriteAllText(single,"C");
            var game=new Game{Id=Guid.NewGuid(),Name="Fixture"};var service=Service(Path.Combine(dir,"core"),game);var config=service.GetOrCreateGameConfig(game.Id,game.Name);config.SavePaths=new List<SavePath>{new SavePath{Path=a,IsDirectory=true},new SavePath{Path=b,IsDirectory=true},new SavePath{Path=single,IsDirectory=false}};service.SaveGameConfig(config);
            var backup=service.CreateBackup(game.Id,game.Name);var blobs=new BlobStore(Dir());var link=new SaveLink{Sources=config.SavePaths.Select((p,i)=>new SaveSource{Id=i==0?"main":"source"+(i+1),Path=p.Path,IsDirectory=p.IsDirectory}).ToList()};var plan=new SavePlan(link,null,blobs,Path.Combine(dir,"journal.json"));var captured=plan.Capture();var read=SaveArchive.Read(service.GetFullBackupPath(backup.BackupFilePath),plan,blobs);Check(Revision.ContentHashOf(captured)==Revision.ContentHashOf(read));
        });
        Test("portable game paths follow a relocated installation",()=>{var a=Dir();var b=Dir();var template=SavePaths.Portable(Path.Combine(a,"Saves"),a,null);Check(template=="{GameDir}\\Saves");Check(SavePaths.Resolve(template,b,null)==Path.Combine(b,"Saves"));Reject(()=>SavePaths.Resolve("{EmulatorDir}\\saves",null,null));});
        Test("emulator and profile path variables preserve directory boundaries",()=>{var dir=Dir();var emulator=Path.Combine(dir,"emu");var neighbor=Path.Combine(dir,"emu2","save");var template=SavePaths.Portable(neighbor,null,emulator);Check(!template.StartsWith("{EmulatorDir}"));Check(SavePaths.Resolve(template,null,emulator)==neighbor);var save=Path.Combine(emulator,"save");Check(SavePaths.Resolve(SavePaths.Portable(save,null,emulator),null,emulator)==save);});
        Test("legacy backup preferences and per-game root overrides migrate",()=>{var link=new SaveLink();SaveManagerOptions.ApplyLegacy(link,new JObject{{"AutoBackupOnGameExit",false},{"ShowAutoBackupNotification",false},{"ConfirmBeforeBackup",false},{"MaxAutoBackupCount",0},{"RealtimeSyncEnabled",true},{"SyncConfigOnGameStart",false},{"RestoreOnGameReinstall",false},{"CustomBackupPath","C:\\Backups"},{"GameBackupPaths",new JObject{{"fixture","D:\\GameBackups"}}}},"fixture");Check(!link.AutoBackup&&!link.NotifyBackup&&!link.ConfirmBeforeBackup&&!link.RestoreOnInstall);Check(link.MaxAutoBackups==0&&link.CheckBeforeLaunch&&link.BackupRoot=="D:\\GameBackups");});
        foreach(var backend in new[]{"drive","dropbox","onedrive","pcloud","webdav","s3"})Test("provider configuration preserves backend and masked credential continuation / "+backend,()=>{
            var initial=backend=="webdav"?new Dictionary<string,string>{{"url","https://example.invalid/dav"},{"vendor","other"},{"user","fixture"},{"pass"," leading and trailing "}}:backend=="s3"?new Dictionary<string,string>{{"provider","Cloudflare"},{"endpoint","https://example.invalid"},{"access_key_id","fixture"},{"secret_access_key","fixture-secret"}}:null;
            int steps=0;CloudAccount.ConfigureProtocol(arguments=>{steps++;if(steps==1){Check(arguments[3]==backend);if(backend=="webdav"){Check(arguments.Contains("--obscure"));Check(arguments[arguments.IndexOf("pass")+1]==" leading and trailing ");}return "{\"State\":\"next\",\"Option\":{\"Name\":\"password\",\"IsPassword\":true,\"Required\":true}}";}Check(arguments.Contains("--continue")&&arguments.Last()=="secret-result");return "{\"State\":\"\"}";},"fixture.conf","fixture",backend,q=>{Check(q.Secret);return "secret-result";},(title,action)=>action(),initial);Check(steps==2);
        });
        var rclone=args.Length>1&&File.Exists(args[1])?args[1]:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Playnite","ExtensionsData","e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b","Tools","rclone.exe");
        foreach(var backend in new[]{"webdav","s3"})Test("real Rclone creates a separate native provider configuration / "+backend,()=>{
            Check(File.Exists(rclone),"Rclone runtime required");var file=Path.Combine(Dir(),"provider.conf");var values=backend=="webdav"?new Dictionary<string,string>{{"url","https://example.invalid/dav"},{"vendor","other"},{"user","fixture"},{"pass"," fixture secret "}}:new Dictionary<string,string>{{"provider","Cloudflare"},{"endpoint","https://example.invalid"},{"env_auth","false"},{"access_key_id","fixture-key"},{"secret_access_key","fixture-secret"},{"no_check_bucket","true"}};
            CloudAccount.Configure(rclone,file,"fixture",backend,q=>{throw new Exception("Unexpected provider question: "+q.Name);},(message,action)=>action(),values);var text=File.ReadAllText(file);Check(text.Contains("type = "+backend));if(backend=="webdav")Check(!text.Contains(" fixture secret "));
        });
        Test("explicit cloud erasure is confined to the chosen vault's protocol area",()=>{
            var home=Dir();var vault=Path.Combine(home,"vault");var protocol=Path.Combine(vault,"playnite-link-v1");Directory.CreateDirectory(protocol);File.WriteAllText(Path.Combine(protocol,"owned.json"),"fixture");File.WriteAllText(Path.Combine(vault,"keep.txt"),"outside protocol");var file=Path.Combine(Dir(),"alias.conf");File.WriteAllText(file,"[fixture]\ntype = alias\nremote = "+home.Replace('\\','/')+"\n");VaultMaintenance.Purge(new SyncConfig{RcloneExe=rclone,ConfigFile=file,RemoteFolder="fixture:vault"});Check(!Directory.Exists(protocol));Check(File.ReadAllText(Path.Combine(vault,"keep.txt"))=="outside protocol");
        });
        Test("erasure scope rejects empty and option-like destinations",()=>{Reject(()=>VaultMaintenance.ProtocolArea(""));Reject(()=>VaultMaintenance.ProtocolArea("--all"));Reject(()=>VaultMaintenance.ProtocolArea("remote:folder\nother"));Check(VaultMaintenance.ProtocolArea("remote:folder/")=="remote:folder/playnite-link-v1");});
        Test("failed core configuration commit preserves the last committed in-memory configuration",()=>{var home=Dir();var game=new Game{Id=Guid.NewGuid(),Name="Fixture"};var service=Service(home,game);var config=service.GetOrCreateGameConfig(game.Id,game.Name);var before=config.GameName;using(var locked=new FileStream(Path.Combine(home,"config.json"),FileMode.Open,FileAccess.Read,FileShare.None)){config.GameName="uncommitted change";Reject(()=>service.SaveGameConfig(config));Check(service.GetGameConfig(game.Id).GameName==before);}Check(Service(home,game).GetGameConfig(game.Id).GameName==before);});
        Test("partial and missing save sources can preserve a pre-restore backup",()=>{var home=Dir();var blobs=new BlobStore(Dir());Directory.CreateDirectory(Path.Combine(home,"first"));File.WriteAllText(Path.Combine(home,"first","slot.sav"),"keep");var plan=Multi(home,blobs);var data=plan.Capture(true);var archive=Path.Combine(Dir(),"partial.zip");SaveArchive.WriteSnapshot(archive,data,plan,blobs,"Before restore");Check(Revision.ContentHashOf(data)==Revision.ContentHashOf(SaveArchive.Read(archive,plan,blobs)));});
        Test("multi-root restore changes every source and handles file deletion",()=>{
            var home=Dir();var blobs=new BlobStore(Dir());WriteMulti(home,"old");var plan=Multi(home,blobs);var before=plan.Capture();var desired=new SortedDictionary<string,string>(before,StringComparer.Ordinal);desired.Remove("__pl_roots/source3/content");using(var stream=new MemoryStream(Encoding.UTF8.GetBytes("new")))desired["slot.sav"]=blobs.Put(stream).ToString();plan.Restore(desired,Revision.ContentHashOf(before));Check(File.ReadAllText(Path.Combine(home,"first","slot.sav"))=="new");Check(!File.Exists(Path.Combine(home,"single.sav")));Check(!File.Exists(Path.Combine(home,"transaction.json")));
        });
        Test("restore exclusions preserve settings and do not create repeated cloud conflicts",()=>{
            var folder=Dir();File.WriteAllText(Path.Combine(folder,"slot"),"old");File.WriteAllText(Path.Combine(folder,"config.ini"),"local settings");var blobs=new BlobStore(Dir());var link=new SaveLink{Folder=folder,RestoreExclusions=new List<string>{Path.Combine(folder,"config.ini")}};var plan=new SavePlan(link,null,blobs,Path.Combine(Dir(),"journal"));var all=plan.Capture(true);var local=plan.Capture();Check(all.Count==2&&local.Count==1);
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes("new progress")))all["slot"]=blobs.Put(stream).ToString();var vault=new Vault(Dir());var game=Identity.Hash("fixture");var device=Guid.NewGuid().ToString("N");vault.Add(new Revision{Version=2,Game=game,Device=device,Kind="save",Data=all});
            var checkpoint=new Checkpoint();var restores=0;for(int i=0;i<2;i++)vault.Reconcile(game,"save",device,plan.Capture(),checkpoint,c=>c.Heads[0].Hash(),d=>{restores++;plan.Restore(d,Revision.ContentHashOf(plan.Capture()));},plan.Filter);
            Check(restores==1);Check(File.ReadAllText(Path.Combine(folder,"config.ini"))=="local settings");Check(File.ReadAllText(Path.Combine(folder,"slot"))=="new progress");
        });
        Test("restore exclusions never delete a peer's unexcluded settings from published snapshots",()=>{
            var blobs=new BlobStore(Dir());var a=Dir();var b=Dir();File.WriteAllText(Path.Combine(a,"slot"),"one");File.WriteAllText(Path.Combine(a,"config.ini"),"desktop settings");
            var planA=new SavePlan(new SaveLink{Folder=a,RestoreExclusions=new List<string>{Path.Combine(a,"config.ini")}},null,blobs,Path.Combine(Dir(),"journal"));var planB=new SavePlan(new SaveLink{Folder=b},null,blobs,Path.Combine(Dir(),"journal"));var vault=new Vault(Dir());var game=Identity.Hash("fixture");var ca=new Checkpoint();var cb=new Checkpoint();var device=Guid.NewGuid().ToString("N");
            var all=planA.Capture(true);vault.Reconcile(game,"save",device,planA.Filter(all),ca,c=>"local",d=>{},planA.Filter,_=>all);Check(vault.Heads(game,"save").Single().Data.ContainsKey("config.ini"));
            var empty=planB.Capture();vault.Reconcile(game,"save",Guid.NewGuid().ToString("N"),empty,cb,c=>c.Heads[0].Hash(),d=>planB.Restore(d,Revision.ContentHashOf(empty)),planB.Filter);Check(File.ReadAllText(Path.Combine(b,"config.ini"))=="desktop settings");
        });
        Test("removing or changing a source cannot erase a peer's differently configured roots",()=>{
            var home=Dir();var blobs=new BlobStore(Dir());WriteMulti(home,"keep");var multi=Multi(home,blobs);var single=new SavePlan(new SaveLink{Folder=Path.Combine(home,"first")},null,blobs,Path.Combine(Dir(),"journal"));
            Reject(()=>multi.Filter(single.Capture()));Reject(()=>single.Restore(multi.Capture(),Revision.ContentHashOf(single.Capture())));Check(File.ReadAllText(Path.Combine(home,"second","slot.sav"))=="keep");Check(File.ReadAllText(Path.Combine(home,"single.sav"))=="keep");
        });
        Test("source declarations are validated and are never installed as real save files",()=>{
            var home=Dir();var blobs=new BlobStore(Dir());WriteMulti(home,"keep");var data=Multi(home,blobs).Capture();SavePlan.ValidateLayout(data);var target=Dir();var plan=Multi(target,blobs);plan.Restore(data,Revision.ContentHashOf(plan.Capture()));Check(Directory.GetFiles(target,".pl-source-*",SearchOption.AllDirectories).Length==0);var marker=data.Keys.First(SavePlan.IsLayout);data[marker]="blob:"+Identity.Hash("bad")+":0";Reject(()=>SavePlan.ValidateLayout(data));
        });
        Test("an empty or missing individual source requires review even when other sources have saves",()=>{
            var home=Dir();var blobs=new BlobStore(Dir());WriteMulti(home,"keep");var plan=Multi(home,blobs);var before=plan.Capture();File.Delete(Path.Combine(home,"single.sav"));var local=plan.Capture();Check(plan.NeedsSourceReview(before,local));var vault=new Vault(Dir());var game=Identity.Hash("fixture");var device=Guid.NewGuid().ToString("N");var revision=new Revision{Version=3,Game=game,Device=device,Kind="save",Data=before};vault.Add(revision);var checkpoint=new Checkpoint{Revision=revision.Hash(),Content=revision.ContentHash()};bool reviewed=false;var result=vault.Reconcile(game,"save",device,local,checkpoint,c=>{reviewed=c.MissingSource;return "cancel";},d=>{throw new Exception("Cancelled source replacement ran");},plan.Filter,_=>local,true);Check(reviewed&&result.Cancelled&&vault.Heads(game,"save").Single().Hash()==revision.Hash());
        });
        Test("missing excluded files remain local without deleting their previous shared bytes",()=>{
            var folder=Dir();var blobs=new BlobStore(Dir());File.WriteAllText(Path.Combine(folder,"slot"),"save");File.WriteAllText(Path.Combine(folder,"config.ini"),"shared settings");var plan=new SavePlan(new SaveLink{Folder=folder,RestoreExclusions=new List<string>{Path.Combine(folder,"config.ini")}},null,blobs,Path.Combine(Dir(),"journal"));var before=plan.Capture(true);File.Delete(Path.Combine(folder,"config.ini"));var current=plan.Capture(true);var publication=plan.ForPublication(current,before);Check(publication["config.ini"]==before["config.ini"]);Check(!plan.NeedsSourceReview(before,plan.Filter(current)));Check(!File.Exists(Path.Combine(folder,"config.ini")));
        });
        Test("folder exclusions preserve descendants without matching neighboring folder names",()=>{
            var folder=Dir();Directory.CreateDirectory(Path.Combine(folder,"settings"));Directory.CreateDirectory(Path.Combine(folder,"settings2"));File.WriteAllText(Path.Combine(folder,"settings","video.ini"),"keep local");File.WriteAllText(Path.Combine(folder,"settings2","video.ini"),"old");var blobs=new BlobStore(Dir());var plan=new SavePlan(new SaveLink{Folder=folder,RestoreExclusions=new List<string>{Path.Combine(folder,"settings")}},null,blobs,Path.Combine(Dir(),"journal"));var cloud=plan.Capture(true);using(var input=new MemoryStream(Encoding.UTF8.GetBytes("cloud"))){var value=blobs.Put(input).ToString();cloud["settings/video.ini"]=value;cloud["settings2/video.ini"]=value;}plan.Restore(cloud,Revision.ContentHashOf(plan.Capture()));Check(File.ReadAllText(Path.Combine(folder,"settings","video.ini"))=="keep local");Check(File.ReadAllText(Path.Combine(folder,"settings2","video.ini"))=="cloud");
        });
        Test("overlapping roots and unknown cloud sources fail before writes",()=>{
            var dir=Dir();var blobs=new BlobStore(Dir());Reject(()=>new SavePlan(new SaveLink{Sources=new List<SaveSource>{new SaveSource{Id="main",Path=dir},new SaveSource{Id="other",Path=Path.Combine(dir,"nested")}}},null,blobs,Path.Combine(Dir(),"journal")));
            var plan=new SavePlan(new SaveLink{Folder=dir},null,blobs,Path.Combine(Dir(),"journal"));Reject(()=>plan.Filter(new SortedDictionary<string,string>{{"__pl_roots/unconfigured/content",""}}));
        });
        Test("single save files directly in Documents can be selected without syncing other files",()=>{var dir=Dir();var file=Path.Combine(dir,"only.sav");File.WriteAllText(file,"x");var blobs=new BlobStore(Dir());var plan=new SavePlan(new SaveLink{Sources=new List<SaveSource>{new SaveSource{Id="main",Path=file,IsDirectory=false}}},null,blobs,Path.Combine(Dir(),"journal"));Check(SavePlan.FileCount(plan.Capture())==1);});
        Test("unsafe ZIP paths, duplicate names and undeclared source files are rejected",()=>{
            var folder=Dir();var blobs=new BlobStore(Dir());var plan=new SavePlan(new SaveLink{Folder=folder},null,blobs,Path.Combine(Dir(),"journal"));
            foreach(var entries in new[]{new[]{"../escape"},new[]{"slot","SLOT"}}){var path=Path.Combine(Dir(),"bad.zip");using(var file=File.Create(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Create))foreach(var entry in entries)using(var writer=new StreamWriter(zip.CreateEntry(entry).Open()))writer.Write("bad");Reject(()=>SaveArchive.Read(path,plan,blobs));}Check(!Directory.EnumerateFileSystemEntries(folder).Any());
        });
        Test("plain ZIP imports normalize into Save Manager's supported archive format",()=>{
            var path=Path.Combine(Dir(),"plain.zip");using(var file=File.Create(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Create)){using(var writer=new StreamWriter(zip.CreateEntry("slot.sav").Open()))writer.Write("progress");using(var writer=new StreamWriter(zip.CreateEntry("backup_info.json").Open()))writer.Write("ordinary game file, not metadata");}var blobs=new BlobStore(Dir());var plan=new SavePlan(new SaveLink{Folder=Dir()},null,blobs,Path.Combine(Dir(),"journal"));var before=SaveArchive.Read(path,plan,blobs);Check(before.ContainsKey("backup_info.json"));var normalized=SaveArchive.NormalizePlain(path,Path.Combine(Dir(),"normalized.zip"),plan);Check(Revision.ContentHashOf(before)==Revision.ContentHashOf(SaveArchive.Read(normalized,plan,blobs)));
        });
        Test("Save Manager full ZIP export maps configuration and nested backups",()=>{
            var path=Path.Combine(Dir(),"global.zip");var configId=Guid.NewGuid().ToString();var config=new JObject{{"Version",2},{"GameConfigs",new JObject{{configId,new JObject{{"GameName","Fixture"},{"SavePaths",new JArray(new JObject{{"Path","{GameDir}\\Saves"},{"IsDirectory",true}})}}}}},{"GameBackups",new JObject{{configId,new JArray(new JObject{{"BackupFilePath","Backups/"+configId+"/old.zip"}})}}}};
            using(var file=File.Create(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Create)){using(var writer=new StreamWriter(zip.CreateEntry("config.json").Open()))writer.Write(config);zip.CreateEntry("Backups/"+configId+"/old.zip");}
            using(var file=File.OpenRead(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Read)){var manifest=SaveMigrationFiles.BundleManifest(zip);Check(((JArray)manifest["Games"]).Count==1);Check((string)manifest["Games"][0]["Backups"][0]["Path"]=="Backups/"+configId+"/old.zip");}
        });
        Test("migration copies are hash verified and missing copies block retirement",()=>{var source=Path.Combine(Dir(),"save.zip");File.WriteAllText(source,"preserve");var copy=SaveMigrationFiles.Copy(source,Path.Combine(Dir(),"archive.zip"));var receipt=new SaveMigrationReceipt{Files=new List<MigrationFile>{copy}};SaveMigrationFiles.Verify(receipt);File.WriteAllText(copy.Copy,"tampered");Reject(()=>SaveMigrationFiles.Verify(receipt));Check(File.ReadAllText(source)=="preserve");});
        Test("sync receipts order by sequence, survive restart and reject duplicate sequence claims",()=>{var dir=Dir();var receipts=new GameSyncReceipts(dir);var device=Guid.NewGuid().ToString("N");var game=Identity.Hash("fixture");var r=Receipt(device,game);receipts.Publish(r);Check(new GameSyncReceipts(dir).Latest(device,game).Sequence==1);var second=Receipt(device,game);receipts.Publish(second);Check(receipts.Latest(device,game).Sequence==2);var conflict=Receipt(device,game);conflict.Sequence=2;conflict.CheckedUtc=DateTimeOffset.UtcNow.AddDays(-1).ToString("O");File.WriteAllText(Path.Combine(dir,conflict.Hash()+".json"),conflict.Canonical());Check(receipts.Latest(device,game)==null);});
        Test("stale, pending, playing and behind devices never claim current synchronization",()=>{var r=Receipt(Guid.NewGuid().ToString("N"),Identity.Hash("fixture"));var now=DateTimeOffset.UtcNow;Check(GameSyncReceipts.Describe(r,r.Revision,r.Content,r.Sessions,now,120).StartsWith("Up to date"));r.CheckedUtc=now.AddDays(-1).ToString("O");Check(GameSyncReceipts.Describe(r,r.Revision,r.Content,r.Sessions,now,120).Contains("unknown"));r.CheckedUtc=now.ToString("O");Check(GameSyncReceipts.Describe(r,Identity.Hash("other"),r.Content,r.Sessions,now,120).StartsWith("Behind"));r.State="playing";Check(GameSyncReceipts.Describe(r,r.Revision,r.Content,r.Sessions,now,120).StartsWith("Playing"));r.State="pending";Check(GameSyncReceipts.Describe(r,r.Revision,r.Content,r.Sessions,now,120).Contains("pending"));});
        Test("malformed receipt and modified receipt hash are rejected",()=>{var dir=Dir();var r=Receipt(Guid.NewGuid().ToString("N"),Identity.Hash("fixture"));r.Sequence=1;File.WriteAllText(Path.Combine(dir,new string('a',64)+".json"),r.Canonical());Reject(()=>new GameSyncReceipts(dir).Read());r.Revision="../unsafe";Reject(()=>r.Validate());});
        Test("large libraries report configured saves and one disable transition, not unrelated games",()=>{var prior=Receipt(Guid.NewGuid().ToString("N"),Identity.Hash("fixture"));Check(Enumerable.Range(0,250).Count(i=>GameSyncReceipt.NeedsReport(i<2,null))==2);Check(GameSyncReceipt.NeedsReport(false,prior));prior.State="disabled";Check(!GameSyncReceipt.NeedsReport(false,prior));Check(GameSyncReceipt.NeedsReport(true,prior));});
        Test("confirmation imports only uploaded receipts, never abandoned outbox history",()=>{var outbox=new GameSyncReceipts(Dir());var confirmed=new GameSyncReceipts(Dir());var abandoned=Receipt(Guid.NewGuid().ToString("N"),Identity.Hash("old"));var uploaded=Receipt(Guid.NewGuid().ToString("N"),Identity.Hash("new"));outbox.Publish(abandoned);outbox.Publish(uploaded);confirmed.ImportRecords(new[]{uploaded});Check(confirmed.Read().Count==1&&confirmed.Latest(abandoned.Device,abandoned.Game)==null&&confirmed.Latest(uploaded.Device,uploaded.Game).Hash()==uploaded.Hash());uploaded.Sequence=0;Reject(()=>confirmed.ImportRecords(new[]{uploaded}));Check(confirmed.Read().Count==1);});
        Test("multi-root format uses version 3 and older snapshots remain readable",()=>{var vault=new Vault(Dir());var data=Multi(Dir(),new BlobStore(Dir())).Capture();data["__pl_roots/source2/content"]=Convert.ToBase64String(new byte[]{1});vault.Reconcile(Identity.Hash("fixture"),"save",Guid.NewGuid().ToString("N"),data,new Checkpoint(),c=>"local",d=>{});Check(vault.Read(vault.Objects).Values.Single().Version==3);});
        Test("cloud backup catalog merges concurrent notes and explicit deletion without losing history",()=>{
            var a=new BackupCatalog(Dir());var b=new BackupCatalog(Dir());var game=Identity.Hash("fixture");var id=Guid.NewGuid().ToString("N");var device=Guid.NewGuid().ToString("N");var archive="blob:"+Identity.Hash("zip")+":3";
            var original=new BackupRecord{BackupId=id,Game=game,Device=device,Archive=archive,Note="Before boss",CreatedUtc=DateTimeOffset.UtcNow.ToString("O")};a.Add(original);b.Import(a.Objects);
            var first=new BackupRecord{BackupId=id,Game=game,Device=device,Archive=archive,Note="Desktop note",CreatedUtc=original.CreatedUtc,Parents=new List<string>{original.Hash()}};var second=new BackupRecord{BackupId=id,Game=game,Device=Guid.NewGuid().ToString("N"),Archive=archive,Note="Handheld note",CreatedUtc=original.CreatedUtc,Parents=new List<string>{original.Hash()}};a.Add(first);b.Add(second);a.Import(b.Objects);b.Import(a.Objects);Check(a.Heads(game,id).Count==2&&b.Heads(game,id).Count==2);
            a.Add(new BackupRecord{BackupId=id,Game=game,Device=device,Archive=archive,Note="Removed",CreatedUtc=original.CreatedUtc,Deleted=true,Parents=a.Heads(game,id).Select(r=>r.Hash()).OrderBy(h=>h,StringComparer.Ordinal).ToList()});b.Import(a.Objects);Check(b.Heads(game,id).Single().Deleted);Check(b.Read().Count==4);
        });
        Test("malformed cloud backup ancestry and altered archives metadata cannot import",()=>{
            var catalog=new BackupCatalog(Dir());var record=new BackupRecord{BackupId=Guid.NewGuid().ToString("N"),Game=Identity.Hash("fixture"),Device=Guid.NewGuid().ToString("N"),Archive="blob:"+Identity.Hash("zip")+":3",Note="Checkpoint",CreatedUtc=DateTimeOffset.UtcNow.ToString("O"),Parents=new List<string>{Identity.Hash("missing")}};catalog.Add(record);Reject(()=>catalog.Read());var empty=new BackupCatalog(Dir());Reject(()=>empty.Import(catalog.Objects));Check(empty.Read().Count==0);
        });
        Test("batched metadata includes validated backup and receipt records",()=>{
            var source=Dir();var receipt=Receipt(Guid.NewGuid().ToString("N"),Identity.Hash("fixture"));receipt.Sequence=1;var receipts=Path.Combine(source,"receipts","objects");Directory.CreateDirectory(receipts);File.WriteAllText(Path.Combine(receipts,receipt.Hash()+".json"),receipt.Canonical());
            var backup=new BackupRecord{BackupId=Guid.NewGuid().ToString("N"),Game=receipt.Game,Device=receipt.Device,Archive="blob:"+Identity.Hash("zip")+":3",Note="Checkpoint",CreatedUtc=receipt.CheckedUtc};var backups=Path.Combine(source,"backups","objects");Directory.CreateDirectory(backups);File.WriteAllText(Path.Combine(backups,backup.Hash()+".json"),backup.Canonical());Check(MetadataCache.Validate(source)==0);Check(MetadataCache.Seed(source,Dir())==2);
        });
        foreach(var boundary in new[]{1,2})Test("abrupt kill between save roots rolls back all roots on two restarts / "+boundary,()=>{
            var home=Dir();WriteMulti(home,"old");using(var child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"\""+home+"\" child "+boundary){UseShellExecute=false,CreateNoWindow=true}))
            {var until=DateTime.UtcNow.AddSeconds(30);while(!File.Exists(Path.Combine(home,"ready"))&&!child.HasExited&&DateTime.UtcNow<until)Thread.Sleep(20);if(!File.Exists(Path.Combine(home,"ready"))){if(!child.HasExited)child.Kill();throw new Exception("Crash boundary was not reached");}child.Kill();child.WaitForExit();}
            for(int i=0;i<2;i++)Multi(home,new BlobStore(Path.Combine(home,"blobs"))).Recover();Check(File.ReadAllText(Path.Combine(home,"first","slot.sav"))=="old");Check(File.ReadAllText(Path.Combine(home,"second","slot.sav"))=="old");Check(File.ReadAllText(Path.Combine(home,"single.sav"))=="old");
        });
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");return failed==0?0:1;
    }
    static GameSyncReceipt Receipt(string device,string game){return new GameSyncReceipt{Device=device,Game=game,State="confirmed",Revision=Identity.Hash("revision"),Content=Identity.Hash("content"),Sessions=Identity.Hash("sessions"),CheckedUtc=DateTimeOffset.UtcNow.ToString("O")};}
    static int LiveCloud(string exe,string config,string remote)
    {
        try
        {
            var a=Dir();var b=Dir();var deviceA=Guid.NewGuid().ToString("N");var deviceB=Guid.NewGuid().ToString("N");var game=Identity.Hash("manual:replacement-wan-fixture");
            var blobsA=new BlobStore(Path.Combine(a,"blobs"));var blobsB=new BlobStore(Path.Combine(b,"blobs"));WriteMulti(a,"alpha");var planA=Multi(a,blobsA);var planB=Multi(b,blobsB);var vaultA=new Vault(a);var vaultB=new Vault(b);var checkpointA=new Checkpoint();var checkpointB=new Checkpoint();
            var first=vaultA.Reconcile(game,"save",deviceA,planA.Capture(),checkpointA,c=>"local",d=>{});var zip=Path.Combine(a,"backup.zip");SaveArchive.WriteSnapshot(zip,planA.Capture(true),planA,blobsA,"Before boss");
            using(var file=new FileStream(zip,FileMode.Open,FileAccess.ReadWrite))using(var archive=new ZipArchive(file,ZipArchiveMode.Update)){var entry=archive.GetEntry("__save_paths__.json");JArray paths;using(var reader=new StreamReader(entry.Open()))paths=JsonSafety.Array(reader.ReadToEnd());for(int i=0;i<paths.Count;i++)paths[i]["OriginalPath"]="source"+i;entry.Delete();using(var writer=new StreamWriter(archive.CreateEntry("__save_paths__.json").Open()))writer.Write(paths.ToString());}
            string zipRef;using(var input=File.OpenRead(zip))zipRef=blobsA.Put(input).ToString();
            var catalogA=new BackupCatalog(Path.Combine(a,"backup-objects"));var backup=new BackupRecord{BackupId=Guid.NewGuid().ToString("N"),Game=game,Device=deviceA,Archive=zipRef,Note="Before boss",Automatic=false,CreatedUtc=DateTimeOffset.UtcNow.ToString("O")};catalogA.Add(backup);
            var blobRemote=new RcloneTransport(exe,config,remote,"playnite-link-v1/blobs",pattern:"*.blob",limitSeconds:300);blobRemote.PushFiles(blobsA.DirectoryPath,Directory.GetFiles(blobsA.DirectoryPath,"*.blob").Select(Path.GetFileName));new RcloneTransport(exe,config,remote,"playnite-link-v1/revisions/objects",limitSeconds:300).Push(vaultA.Objects);new RcloneTransport(exe,config,remote,"playnite-link-v1/backups/objects",limitSeconds:300).Push(catalogA.Objects);
            var cacheB=Path.Combine(b,"cache");new RcloneTransport(exe,config,remote,"playnite-link-v1",limitSeconds:300).PullMetadata(cacheB);vaultB.Import(Path.Combine(cacheB,"revisions","objects"));var catalogB=new BackupCatalog(Path.Combine(b,"backup-objects"));catalogB.Import(Path.Combine(cacheB,"backups","objects"));
            blobsB.Fetch=names=>blobRemote.PullFiles(blobsB.DirectoryPath,names);var empty=planB.Capture();vaultB.Reconcile(game,"save",deviceB,empty,checkpointB,c=>c.Heads[0].Hash(),d=>planB.Restore(d,Revision.ContentHashOf(empty)),planB.Filter);Check(Revision.ContentHashOf(planA.Capture())==Revision.ContentHashOf(planB.Capture()),"WAN multi-source restore differs");
            blobsB.Ensure(new SortedDictionary<string,string>{{"archive.zip",catalogB.Heads(game).Single().Archive}});var downloaded=Path.Combine(b,"backup.zip");using(var output=File.Create(downloaded))blobsB.CopyVerified(zipRef,output);Check(SaveMigrationFiles.Hash(downloaded)==SaveMigrationFiles.Hash(zip));Check(Revision.ContentHashOf(SaveArchive.Read(downloaded,planB,blobsB))==Revision.ContentHashOf(planB.Capture()));
            WriteMulti(a,"desktop offline progress");WriteMulti(b,"handheld offline progress");vaultA.Reconcile(game,"save",deviceA,planA.Capture(),checkpointA,c=>"local",d=>{});vaultB.Reconcile(game,"save",deviceB,planB.Capture(),checkpointB,c=>"local",d=>{});
            foreach(var pair in new[]{new[]{a,vaultA.Objects,blobsA.DirectoryPath},new[]{b,vaultB.Objects,blobsB.DirectoryPath}}){blobRemote.PushFiles(pair[2],Directory.GetFiles(pair[2],"*.blob").Select(Path.GetFileName));new RcloneTransport(exe,config,remote,"playnite-link-v1/revisions/objects",limitSeconds:300).Push(pair[1]);}
            var cacheA=Path.Combine(a,"cache");new RcloneTransport(exe,config,remote,"playnite-link-v1",limitSeconds:300).PullMetadata(cacheA);vaultA.Import(Path.Combine(cacheA,"revisions","objects"));Check(vaultA.Heads(game,"save").Count==2,"WAN divergence was lost");var cancelled=vaultA.Reconcile(game,"save",deviceA,planA.Capture(),checkpointA,c=>"cancel",d=>{throw new Exception("Cancelled restore executed");});Check(cancelled.Cancelled);vaultA.Reconcile(game,"save",deviceA,planA.Capture(),checkpointA,c=>"local",d=>{});new RcloneTransport(exe,config,remote,"playnite-link-v1/revisions/objects",limitSeconds:300).Push(vaultA.Objects);
            new RcloneTransport(exe,config,remote,"playnite-link-v1",limitSeconds:300).PullMetadata(cacheB);vaultB.Import(Path.Combine(cacheB,"revisions","objects"));var before=planB.Capture();vaultB.Reconcile(game,"save",deviceB,before,checkpointB,c=>c.Heads.Single().Hash(),d=>planB.Restore(d,Revision.ContentHashOf(before)),planB.Filter);Check(Revision.ContentHashOf(planA.Capture())==Revision.ContentHashOf(planB.Capture()),"WAN resolution did not converge");
            var entries=new[]{new Entry{Kind="session",Id=Guid.NewGuid().ToString("N"),Game=game,Device=deviceA,Seconds=42,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"),EndedUtc=DateTimeOffset.UtcNow.AddMinutes(-4).ToString("O")},new Entry{Kind="session",Id=Guid.NewGuid().ToString("N"),Game=game,Device=deviceB,Seconds=37,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-3).ToString("O"),EndedUtc=DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O")}};
            var ledgerDir=Path.Combine(a,"ledger");Directory.CreateDirectory(ledgerDir);foreach(var entry in entries)File.WriteAllText(Path.Combine(ledgerDir,entry.Hash()+".json"),entry.Canonical());new RcloneTransport(exe,config,remote,limitSeconds:300).Push(ledgerDir);
            var receiptA=new GameSyncReceipts(Path.Combine(a,"receipts"));var resolved=vaultA.Heads(game,"save").Single();foreach(var device in new[]{deviceA,deviceB})receiptA.Publish(new GameSyncReceipt{Device=device,Game=game,State="confirmed",Revision=resolved.Hash(),Content=resolved.ContentHash(),Sessions=GameSyncReceipt.SessionDigest(entries,game),CheckedUtc=DateTimeOffset.UtcNow.ToString("O")});new RcloneTransport(exe,config,remote,"playnite-link-v1/receipts/objects",limitSeconds:300).Push(receiptA.DirectoryPath);
            new RcloneTransport(exe,config,remote,"playnite-link-v1",limitSeconds:300).PullMetadata(cacheB);var receivedEntries=SyncEngine.ReadObjects(Path.Combine(cacheB,"ledger","objects"));Check(new Ledger(receivedEntries).Total(game)==79);var receiptB=new GameSyncReceipts(Path.Combine(b,"receipts"));receiptB.Import(Path.Combine(cacheB,"receipts","objects"));Check(GameSyncReceipts.Describe(receiptB.Latest(deviceA,game),resolved.Hash(),resolved.ContentHash(),GameSyncReceipt.SessionDigest(receivedEntries,game),DateTimeOffset.UtcNow,600).StartsWith("Up to date"));
            var tally=new{passed=true,multiSourceCloudRestore=true,namedZipSha256RoundTrip=true,offlineConflictPreserved=true,cancelPreservesSaves=true,explicitResolutionConverges=true,sessionSeconds=79,deviceReceiptsConverge=true,metadataNamespaces=MetadataCache.Areas.Length,liveGameFilesWritten=false};File.WriteAllText(Path.Combine(root,"wan-result.json"),JsonConvert.SerializeObject(tally,Formatting.Indented));Console.WriteLine(JsonConvert.SerializeObject(tally));return 0;
        }catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}



