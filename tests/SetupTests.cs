using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PlaytimeRclone;
using Newtonsoft.Json;

internal static class SetupTests
{
    static int passed,failed;static string root,exe,script,config,remote;
    static void True(bool value){if(!value)throw new Exception("Assertion failed");}
    static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex);}}
    static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected rejection");}
    sealed class Peer
    {
        public string Profile,Data,Device=Guid.NewGuid().ToString("N");public BlobStore Blobs;public SetupProfile Setup;
        public Peer(string name){Profile=Path.Combine(root,name);Data=Path.Combine(Profile,"ExtensionsData","fixture-link");Directory.CreateDirectory(Path.Combine(Profile,"Extensions"));Directory.CreateDirectory(Path.Combine(Profile,"Themes","Desktop"));Blobs=new BlobStore(Path.Combine(Data,"blobs"));Reload();}
        public void Reload(){Setup=new SetupProfile(Data,Profile,Blobs);}
        public void Sync(){Setup.Capture(Device);var incoming=Path.Combine(Data,"incoming");Directory.CreateDirectory(incoming);var transport=new RcloneTransport(exe,config,remote,"playnite-link-v1/setup/objects");transport.Pull(incoming);Setup.History.Import(incoming);Setup.MergeEquivalent(Device);var all=Setup.History.Read(Setup.History.Objects);var blobs=new RcloneTransport(exe,config,remote,"playnite-link-v1/blobs","*.blob");blobs.PushFiles(Blobs.DirectoryPath,all.Values.SelectMany(r=>SetupComponent.Files(r.Data).Values).Select(v=>BlobReference.Parse(v).Hash+".blob").Where(n=>File.Exists(Path.Combine(Blobs.DirectoryPath,n))));transport.PushFiles(Setup.History.Objects,all.Keys.Select(n=>n+".json"));}
        public SetupPlan Stage(Dictionary<string,string> choices=null){var transport=new RcloneTransport(exe,config,remote,"playnite-link-v1/blobs","*.blob");Blobs.Fetch=names=>transport.PullFiles(Blobs.DirectoryPath,names);return Setup.Stage(choices??Setup.Changes().Where(c=>c.Heads.Count==1).ToDictionary(c=>c.Key,c=>c.Heads[0].Hash()),Device);}
        public int Apply(string helper=null){var start=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File "+RcloneTransport.Quote(helper??script)+" -Plan "+RcloneTransport.Quote(Path.Combine(Setup.DirectoryPath,"pending.json"))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};using(var process=Process.Start(start)){var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(30000)){process.Kill();throw new Exception("Apply timeout");}if(process.ExitCode!=0)Console.WriteLine("Apply diagnostic: "+error.Result+" "+output.Result);return process.ExitCode;}}
        public void Install(){if(Setup.Changes().Count==0)return;Stage();True(Apply()==0);Reload();Setup.Capture(Device);}
    }
    static string Package(Peer peer,string id,bool theme=false,string text="v1")
    {var path=Path.Combine(peer.Profile,theme?"Themes/Desktop":"Extensions",id);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,theme?"theme.yaml":"extension.yaml"),"Id: "+id+"\nName: Fixture "+id+"\nVersion: 1.0.0\n");File.WriteAllText(Path.Combine(path,"asset.txt"),text);return path;}
    static void Main(string[] args)
    {
        root=args[0];exe=args[1];script=args[2];Directory.CreateDirectory(root);var cloud=Path.Combine(root,"cloud");Directory.CreateDirectory(Path.Combine(cloud,"playnite-link-v1","setup","objects"));Directory.CreateDirectory(Path.Combine(cloud,"playnite-link-v1","blobs"));config=Path.Combine(root,"rclone.conf");File.WriteAllText(config,"[vault]\ntype = alias\nremote = "+cloud+"\n");remote="vault:";
        var a=new Peer("A");var b=new Peer("B");string aPackage=null;var themeKey=Identity.Hash("Themes/Desktop:ThemeFixture");
        Test("add-on sync defaults off on every device",()=>True(!a.Setup.Options.Enabled&&!b.Setup.Options.Enabled));
        Test("capture includes add-ons and themes, excluding settings, credentials and sync extension",()=>{
            aPackage=Package(a,"ExtensionFixture");Package(a,"ThemeFixture",true);var own=Package(a,"PlayniteLink");File.WriteAllText(Path.Combine(own,"extension.yaml"),"Id: PlayniteLink_8ca18482-3ebe-4bf7-b229-a259b2094767\n");
            File.WriteAllText(Path.Combine(a.Profile,"config.json"),"{\"StartInFullscreen\":false,\"Theme\":\"DesktopTheme\"}");File.WriteAllText(Path.Combine(a.Data,"credentials.json"),"private-source-token");a.Setup.Capture(a.Device);True(a.Setup.State.Local.Count==2);True(a.Setup.History.Read(a.Setup.History.Objects).Values.All(r=>r.Kind=="setup"&&!r.Canonical().Contains("private-source-token")));
        });
        Test("unchanged package metadata avoids rehashing assets",()=>{var count=a.Blobs.CaptureCount;a.Setup.Capture(a.Device);True(a.Blobs.CaptureCount==count);});
        Test("new device discovers peer packages without publishing deletions",()=>{a.Sync();b.Sync();True(b.Setup.Changes().Count==2);True(b.Setup.History.Read(b.Setup.History.Objects).Count==2);});
        Test("real restart helper installs verified packages and preserves local settings",()=>{
            File.WriteAllText(Path.Combine(b.Profile,"config.json"),"{\"StartInFullscreen\":true,\"StartOnBoot\":false,\"Theme\":\"AllyTheme\"}");File.WriteAllText(Path.Combine(b.Data,"credentials.json"),"private-ally-token");b.Install();True(File.ReadAllText(Path.Combine(b.Profile,"Extensions","ExtensionFixture","asset.txt"))=="v1");True(File.ReadAllText(Path.Combine(b.Profile,"config.json")).Contains("AllyTheme"));True(File.ReadAllText(Path.Combine(b.Data,"credentials.json"))=="private-ally-token");True(b.Setup.Changes().Count==0);
        });
        Test("independent installs on both peers merge without a primary device",()=>{Package(a,"FromA");Package(b,"FromB");a.Sync();b.Sync();a.Sync();True(a.Setup.Changes().Single().Heads[0].Data["Name"]=="Fixture FromB");True(b.Setup.Changes().Single().Heads[0].Data["Name"]=="Fixture FromA");a.Install();b.Install();True(Directory.Exists(Path.Combine(a.Profile,"Extensions","FromB"))&&Directory.Exists(Path.Combine(b.Profile,"Extensions","FromA")));});
        Test("device-only theme changes neither publish nor receive changes",()=>{a.Setup.Options.LocalOnly.Add(themeKey);a.Setup.SaveOptions();var before=a.Setup.History.Read(a.Setup.History.Objects).Count;File.WriteAllText(Path.Combine(a.Profile,"Themes","Desktop","ThemeFixture","asset.txt"),"local-theme");a.Setup.Capture(a.Device);True(a.Setup.History.Read(a.Setup.History.Objects).Count==before);File.WriteAllText(Path.Combine(b.Profile,"Themes","Desktop","ThemeFixture","asset.txt"),"other-theme");b.Sync();a.Sync();True(a.Setup.Changes().All(c=>c.Key!=themeKey));True(File.ReadAllText(Path.Combine(a.Profile,"Themes","Desktop","ThemeFixture","asset.txt"))=="local-theme");});
        Test("simultaneous edits preserve both branches for explicit resolution",()=>{File.WriteAllText(Path.Combine(aPackage,"asset.txt"),"branch-a");File.WriteAllText(Path.Combine(b.Profile,"Extensions","ExtensionFixture","asset.txt"),"branch-b");a.Setup.Capture(a.Device);b.Setup.Capture(b.Device);a.Sync();b.Sync();a.Sync();var key=Identity.Hash("Extensions:ExtensionFixture");var change=a.Setup.Changes().Single(c=>c.Key==key);True(change.Heads.Count==2);var selected=change.Heads.Single(r=>r.Device==b.Device);a.Stage(new Dictionary<string,string>{{key,selected.Hash()}});True(a.Apply()==0);a.Reload();a.Sync();b.Sync();b.Install();True(File.ReadAllText(Path.Combine(aPackage,"asset.txt"))=="branch-b");True(b.Setup.History.Heads(key,"setup").Count==1);});
        Test("shared removal backs up code and retains extension data",()=>{var path=Path.Combine(a.Profile,"Extensions","FromA");Directory.Move(path,Path.Combine(root,"removed-from-a"));a.Sync();b.Sync();var change=b.Setup.Changes().Single(c=>c.Key==Identity.Hash("Extensions:FromA"));True(change.Heads.Single().Data["Removed"]=="True");b.Install();True(!Directory.Exists(Path.Combine(b.Profile,"Extensions","FromA")));True(Directory.GetFiles(Path.Combine(b.Setup.DirectoryPath,"backups"),"asset.txt",SearchOption.AllDirectories).Length>0);True(File.Exists(Path.Combine(b.Data,"credentials.json")));});
        Test("corrupt staged files fail verification before modifying installations",()=>{Package(a,"CorruptionFixture");a.Sync();b.Sync();var plan=b.Stage();var install=plan.Installs.Single(i=>i.Target.EndsWith("CorruptionFixture"));File.WriteAllText(Path.Combine(install.Stage,"asset.txt"),"corrupt");True(b.Apply()!=0);True(!Directory.Exists(Path.Combine(b.Profile,"Extensions","CorruptionFixture")));True(File.Exists(Path.Combine(b.Setup.DirectoryPath,"apply-error.txt")));});
        Test("unsafe and protected package destinations are rejected",()=>{var data=new SortedDictionary<string,string>(a.Setup.State.Local.First().Value);var key=Identity.Hash(data["Identity"]);data["Target"]="Extensions/../escape";Throws(()=>SetupComponent.Validate(key,data));data["Target"]="Extensions/PlayniteLink";Throws(()=>SetupComponent.Validate(key,data));data["Target"]="Extensions/Fixture_Builtin";Throws(()=>SetupComponent.Validate(key,data));});
        Test("identical independent installs merge without a conflict",()=>{
            Package(a,"Equivalent");Package(b,"Equivalent");a.Setup.Capture(a.Device);b.Setup.Capture(b.Device);a.Sync();b.Sync();a.Sync();
            var key=Identity.Hash("Extensions:Equivalent");True(a.Setup.History.Heads(key,"setup").Count==1);True(a.Setup.Changes().All(c=>c.Key!=key));True(b.Setup.Changes().All(c=>c.Key!=key));
        });
        Test("changes after preview abort staging without replacing local code",()=>{
            var key=Identity.Hash("Extensions:CorruptionFixture");
            File.WriteAllText(Path.Combine(b.Profile,"Extensions","Equivalent","asset.txt"),"local-edit-after-preview");
            File.WriteAllText(Path.Combine(a.Profile,"Extensions","Equivalent","asset.txt"),"cloud-edit");a.Sync();b.Setup.History.Import(Path.Combine(a.Setup.DirectoryPath,"vault-objects"));
            key=Identity.Hash("Extensions:Equivalent");var head=b.Setup.History.Heads(key,"setup").Single(r=>r.Device==a.Device);
            Throws(()=>b.Stage(new Dictionary<string,string>{{key,head.Hash()}}));
            True(File.ReadAllText(Path.Combine(b.Profile,"Extensions","Equivalent","asset.txt"))=="local-edit-after-preview");
        });
        Test("mid-install failure rolls back earlier packages and retry succeeds",()=>{
            foreach(var id in new[]{"RollbackFirst","RollbackSecond"}){Package(a,id);Package(b,id);}
            a.Setup.Capture(a.Device);b.Setup.Capture(b.Device);a.Sync();b.Sync();a.Sync();a.Setup.Changes();b.Setup.Changes();
            foreach(var id in new[]{"RollbackFirst","RollbackSecond"})File.WriteAllText(Path.Combine(a.Profile,"Extensions",id,"asset.txt"),"updated");
            a.Sync();b.Sync();
            var keys=new[]{"RollbackFirst","RollbackSecond"}.Select(id=>Identity.Hash("Extensions:"+id)).ToArray();
            var choices=b.Setup.Changes().Where(c=>keys.Contains(c.Key)).ToDictionary(c=>c.Key,c=>c.Heads.Single().Hash());
            var plan=b.Stage(choices);var second=plan.Installs[1];var locked=Path.Combine(b.Profile,second.Target.Replace('/',Path.DirectorySeparatorChar),"asset.txt");
            var injected=Path.Combine(root,"SetupApply-fault.ps1");
            var helperText=File.ReadAllText(script);True(helperText.Contains("$record.Installed=$true}"));
            File.WriteAllText(injected,helperText.Replace("$record.Installed=$true}","$record.Installed=$true;if($moved.Count -eq 1){throw 'Injected test failure'}}"));
            True(b.Apply(injected)!=0);
            foreach(var id in new[]{"RollbackFirst","RollbackSecond"})True(File.ReadAllText(Path.Combine(b.Profile,"Extensions",id,"asset.txt"))=="v1");
            b.Stage(choices);True(b.Apply()==0);b.Reload();b.Setup.Capture(b.Device);
            foreach(var id in new[]{"RollbackFirst","RollbackSecond"})True(File.ReadAllText(Path.Combine(b.Profile,"Extensions",id,"asset.txt"))=="updated");
        });
        Test("device-only choices survive restart, including not-yet-installed packages",()=>{
            var key=Identity.Hash("Extensions:CorruptionFixture");b.Setup.Options.LocalOnly.Add(key);b.Setup.SaveOptions();b.Reload();True(b.Setup.Options.LocalOnly.Contains(key));True(b.Setup.Changes().All(c=>c.Key!=key));
        });
        Test("conflict replacement preserved the discarded branch in backup",()=>{
            True(Directory.GetFiles(Path.Combine(a.Setup.DirectoryPath,"backups"),"asset.txt",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p)=="branch-a"));
        });
        Test("shared removal cannot remove this device's active theme",()=>{
            var c=new Peer("ThemeSafety");Package(c,"ActiveTheme",true);c.Setup.Capture(c.Device);
            File.WriteAllText(Path.Combine(c.Profile,"config.json"),JsonConvert.SerializeObject(new{Theme="ActiveTheme"}));
            var key=Identity.Hash("Themes/Desktop:ActiveTheme");var previous=c.Setup.History.Heads(key,"setup").Single();
            var data=new SortedDictionary<string,string>(previous.Data);data["Removed"]="True";data["Files"]="{}";
            var removed=new Revision{Game=key,Kind="setup",Device=Guid.NewGuid().ToString("N"),Data=data,Parents=new List<string>{previous.Hash()}};c.Setup.History.Add(removed);
            Throws(()=>c.Stage(new Dictionary<string,string>{{key,removed.Hash()}}));
            True(Directory.Exists(Path.Combine(c.Profile,"Themes","Desktop","ActiveTheme")));
        });
        Test("theme folder parentheses survive capture, sync and real helper install",()=>{
            var previousRemote=remote;remote="vault:parentheses";
            Directory.CreateDirectory(Path.Combine(root,"cloud","parentheses","playnite-link-v1","setup","objects"));
            Directory.CreateDirectory(Path.Combine(root,"cloud","parentheses","playnite-link-v1","blobs"));
            try{var c=new Peer("ParenA");var d=new Peer("ParenB");var package=Package(c,"ParenTheme",true);var renamed=Path.Combine(Path.GetDirectoryName(package),"(u)biquity_fixture");Directory.Move(package,renamed);c.Sync();d.Sync();d.Install();True(File.ReadAllText(Path.Combine(d.Profile,"Themes","Desktop","(u)biquity_fixture","asset.txt"))=="v1");}finally{remote=previousRemote;}
        });
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");Environment.ExitCode=failed==0?0:1;
    }
}
