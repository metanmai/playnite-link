using System;
using System.IO;
using System.Threading.Tasks;
using System.Reflection;
using PlaytimeRclone;
static class HltbTests
{
    static int pass,fail;static string root;static DateTime now=DateTime.UtcNow;
    static void Assert(bool ok){if(!ok)throw new Exception("Assertion failed");}
    static void Test(string name,Action action){try{action();pass++;Console.WriteLine("PASS "+name);}catch(Exception ex){fail++;Console.WriteLine("FAIL "+name+": "+ex);}}
    sealed class Api:IHltbProgress
    {
        public ulong Total;public int Reads,Writes;public bool Fail,TimeoutAfterCommit,Missing;public string Status="completed";
        public Task<object> Read(){Reads++;return Task.FromResult(Missing?null:(object)Total);}
        public ulong Seconds(object value){return (ulong)value;}
        public Task<bool> Write(object edit,ulong value){Writes++;if(Fail)return Task.FromResult(false);Total=value;if(TimeoutAfterCommit){TimeoutAfterCommit=false;throw new IOException("timeout after commit");}return Task.FromResult(true);}
    }
    public sealed class Edit{public int SubmissionId{get;set;}public int GameId{get;set;}}
    public sealed class NativeApi{public Edit Value;public Task<Edit> GetEditData(string name,string id){return Task.FromResult(Value);}}
    static IHltbProgress Adapter(int submission,int game)
    {
        var type=typeof(PlaytimePlugin).GetNestedType("OnlineProgress",BindingFlags.NonPublic);var adapter=Activator.CreateInstance(type,true);
        type.GetField("Api").SetValue(adapter,new NativeApi{Value=new Edit{SubmissionId=submission,GameId=game}});
        type.GetField("Game").SetValue(adapter,new Playnite.SDK.Models.Game{Name="Fixture"});type.GetField("Submission").SetValue(adapter,"123");type.GetField("HltbId").SetValue(adapter,"456");return (IHltbProgress)adapter;
    }
    static string Dir(){var p=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p;}
    static void Update(HltbProgress bridge,Api api,ulong total,DateTime? when=null){bridge.Update("account:game",total,api,when??now).GetAwaiter().GetResult();}
    static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected error");}
    static int Main(string[] args)
    {
        root=args[0];Directory.CreateDirectory(root);
        Test("absolute total updates once across repeated syncs",()=>{var api=new Api{Total=60};var bridge=new HltbProgress(Dir());for(int i=0;i<10;i++)Update(bridge,api,76);Assert(api.Total==76&&api.Writes==1&&api.Reads==2&&api.Status=="completed");});
        Test("two devices converge without adding totals",()=>{var api=new Api{Total=60};var a=new HltbProgress(Dir());var b=new HltbProgress(Dir());Update(a,api,68);Update(b,api,76);for(int i=0;i<5;i++){Update(a,api,76);Update(b,api,76);}Assert(api.Total==76&&api.Writes==2);});
        Test("stale device cannot reduce greater online progress",()=>{var api=new Api{Total=100};Update(new HltbProgress(Dir()),api,76);Assert(api.Total==100&&api.Writes==0);});
        Test("restart uses durable success receipt",()=>{var path=Dir();var api=new Api{Total=60};Update(new HltbProgress(path),api,76);Update(new HltbProgress(path),api,76);Assert(api.Writes==1&&api.Reads==2);});
        Test("failed upload retains intent for retry",()=>{var api=new Api{Total=60,Fail=true};var b=new HltbProgress(Dir());Throws(()=>Update(b,api,76));api.Fail=false;Update(b,api,76);Assert(api.Total==76&&api.Writes==2);});
        Test("timeout after server commit cannot double progress",()=>{var api=new Api{Total=60,TimeoutAfterCommit=true};var path=Dir();Throws(()=>Update(new HltbProgress(path),api,76));Update(new HltbProgress(path),api,76);Assert(api.Total==76&&api.Writes==1);});
        Test("missing authenticated profile never creates another entry",()=>{var api=new Api{Missing=true};Throws(()=>Update(new HltbProgress(Dir()),api,76));Assert(api.Writes==0);});
        Test("periodic fresh read repairs external stale overwrite",()=>{var api=new Api{Total=60};var b=new HltbProgress(Dir());Update(b,api,76);api.Total=68;Update(b,api,76,now.AddMinutes(11));Assert(api.Total==76&&api.Writes==2);});
        Test("native adapter rejects a different online submission",()=>Throws(()=>Adapter(999,456).Read().GetAwaiter().GetResult()));
        Test("native adapter rejects stale mapping to another HLTB game",()=>Throws(()=>Adapter(123,999).Read().GetAwaiter().GetResult()));
        Test("native adapter accepts the intended submission and game",()=>Assert(Adapter(123,456).Read().GetAwaiter().GetResult()!=null));
        Console.WriteLine("RESULT "+pass+" passed, "+fail+" failed");return fail==0?0:1;
    }
}
