using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PlaytimeRclone;

internal static class AuditTests
{
    static int failed;
    static void Test(string name, Action action)
    { try { action(); Console.WriteLine("PASS " + name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
    static void Require(bool value) { if(!value)throw new Exception("Assertion failed"); }
    static int Main(string[] args)
    {
        var root=args[0];Directory.CreateDirectory(root);
        Test("capture rejects same-length corrupted cached blobs before publication",()=>{
            var store=new BlobStore(Path.Combine(root,"blobs"));
            var bytes=Encoding.UTF8.GetBytes("original");string hash;
            using(var input=new MemoryStream(bytes))hash=store.Put(input).Hash;
            File.WriteAllText(store.PathFor(hash),"tampered");
            bool rejected=false;
            try{using(var input=new MemoryStream(bytes))store.Put(input);}catch(InvalidDataException){rejected=true;}
            Require(rejected);
            Require(Directory.GetFiles(store.DirectoryPath,"*.tmp").Length==0);
        });
        Test("an existing package with a missing manifest is not a shared removal",()=>{
            var profile=Path.Combine(root,"profile");var package=Path.Combine(profile,"Extensions","Fixture");
            Directory.CreateDirectory(package);var manifest=Path.Combine(package,"extension.yaml");
            File.WriteAllText(manifest,"Id: Fixture\nName: Fixture\nVersion: 1.0\n");
            File.WriteAllText(Path.Combine(package,"asset.txt"),"keep");
            var data=Path.Combine(profile,"ExtensionsData","link");
            var setup=new SetupProfile(data,profile,new BlobStore(Path.Combine(data,"blobs")));
            var device=Guid.NewGuid().ToString("N");setup.Capture(device);
            var key=Identity.Hash("Extensions:Fixture");var previous=setup.History.Heads(key,"setup").Single().Hash();
            File.Move(manifest,manifest+".updating");setup.Capture(device);
            Require(setup.History.Heads(key,"setup").Single().Hash()==previous);
            File.Move(manifest+".updating",manifest);setup.Capture(device);
            Require(setup.History.Heads(key,"setup").Single().Hash()==previous);
            Directory.Move(package,Path.Combine(root,"removed-package"));setup.Capture(device);
            Require(setup.History.Heads(key,"setup").Single().Data["Removed"]=="True");
        });
        Test("queued corrupted blobs are rejected before starting an upload",()=>{
            var directory=Path.Combine(root,"pending");Directory.CreateDirectory(directory);
            var name=new string('a',64)+".blob";File.WriteAllText(Path.Combine(directory,name),"bad");
            bool rejected=false;
            try{new RcloneTransport("must-not-start.exe","","fixture:").PushFiles(directory,new[]{name});}
            catch(InvalidDataException){rejected=true;}
            Require(rejected);Require(Directory.GetFiles(directory,"*.tmp").Length==0);
        });
        Test("deep JSON fails at the extension boundary without changing host settings",()=>{
            var original=Newtonsoft.Json.JsonConvert.DefaultSettings;
            var deep=String.Concat(Enumerable.Repeat("{\"a\":",100))+"0"+new string('}',100);
            foreach(var parse in new Action[]{()=>JsonSafety.Deserialize<object>(deep),()=>CloudAccount.Parse(deep),()=>JsonSafety.Array("["+deep+"]")})
            {bool rejected=false;try{parse();}catch(Newtonsoft.Json.JsonReaderException){rejected=true;}Require(rejected);}
            Require(Object.ReferenceEquals(original,Newtonsoft.Json.JsonConvert.DefaultSettings));
        });
        Test("JSON boundary preserves valid input and rejects additional documents",()=>{
            Require((string)CloudAccount.Parse("{\"State\":\"ready\"}")["State"]=="ready");
            bool rejected=false;try{JsonSafety.Object("{} {}");}catch(Newtonsoft.Json.JsonException){rejected=true;}Require(rejected);
        });
        Test("disabled initialization does not crash the game menu",()=>{
            var plugin=(PlaytimePlugin)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PlaytimePlugin));
            Require(!plugin.GetGameMenuItems(new Playnite.SDK.Plugins.GetGameMenuItemsArgs()).Any());
        });
        Test("remote settings cannot grant another device's path-sharing consent",()=>{
            var local=Guid.NewGuid().ToString("N");var other=Guid.NewGuid().ToString("N");
            var current=new DevicePreferences{SharePaths=false};
            var request=new DeviceEvent{Author=other,Target=local,Kind="settings",Settings=new DevicePreferences{SharePaths=true,IntervalSeconds=60}};
            var result=current.ApplyRequested(request,local);Require(!result.SharePaths&&result.IntervalSeconds==60&&!current.SharePaths);
            request.Author=local;Require(current.ApplyRequested(request,local).SharePaths);
            request.Author=other;current.SharePaths=true;Require(current.ApplyRequested(request,local).SharePaths);
            request.Settings.SharePaths=false;Require(!current.ApplyRequested(request,local).SharePaths);
        });
        Test("interrupted add-on install blocks capture, restaging and premature receipt consumption",()=>{
            var profile=Path.Combine(root,"interrupted-setup");var data=Path.Combine(profile,"ExtensionsData","fixture");
            var blobs=new BlobStore(Path.Combine(data,"blobs"));var setup=new SetupProfile(data,profile,blobs);
            File.WriteAllText(Path.Combine(setup.DirectoryPath,"apply-journal.json"),"{}");
            File.WriteAllText(Path.Combine(setup.DirectoryPath,"applied.json"),"{\"Installs\":[]}");
            setup=new SetupProfile(data,profile,blobs);Require(setup.RecoveryRequired);
            foreach(var action in new Action[]{()=>setup.Capture(Guid.NewGuid().ToString("N")),()=>setup.Stage(new Dictionary<string,string>(),Guid.NewGuid().ToString("N"))})
            {bool rejected=false;try{action();}catch(IOException){rejected=true;}Require(rejected);}
            Require(File.Exists(Path.Combine(setup.DirectoryPath,"applied.json")));
        });
        Console.WriteLine("RESULT " + (8-failed) + " passed, " + failed + " failed");return failed==0?0:1;
    }
}
