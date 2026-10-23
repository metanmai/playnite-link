using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlaytimeRclone;

class NativeHltbTests
{
    static int checks;
    static void Check(bool value){if(!value)throw new Exception("Assertion failed");checks++;}
    static string Page(JObject edit){return "<script type='application/json' id='__NEXT_DATA__'>"+new JObject{{"props",new JObject{{"pageProps",new JObject{{"editData",edit}}}}}}+"</script>";}
    static void Reject(Action action){try{action();}catch{checks++;return;}throw new Exception("Expected rejection");}
    static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(args[0]);
            var entry=JObject.Parse("{userId:1,gameId:2,submissionId:3,general:{progress:{hours:null,minutes:1,seconds:2},startDate:{year:'2020'}},lists:{completed:true},review:{notes:'preserve'},singlePlayer:{compMain:{time:{hours:7}}},unknown:{future:'keep'}}");
            JObject written=null;int writes=0;bool refuse=false;
            Func<string,JObject,Task<string>> request=(path,body)=>{
                if(body==null){Check(path=="/submit/edit/3");return Task.FromResult(Page(entry));}
                Check(path=="/api/submit");writes++;written=(JObject)body.DeepClone();if(!refuse)entry=(JObject)body.DeepClone();return Task.FromResult("{success:true}");
            };
            var api=new NativeHltbProgress(request,1,2,3);var bridge=new HltbProgress(args[0]);
            bridge.Update("native-account",3700,api,DateTime.UtcNow).GetAwaiter().GetResult();
            Check(writes==1&&api.Seconds(entry)==3700);Check((bool)written["lists"]["completed"]);Check((string)written["review"]["notes"]=="preserve");Check((string)written["unknown"]["future"]=="keep");Check((string)written["general"]["startDate"]["year"]=="2020");Check((int)written["singlePlayer"]["compMain"]["time"]["hours"]==7);
            bridge.Update("native-account",3700,api,DateTime.UtcNow).GetAwaiter().GetResult();Check(writes==1);
            bridge.Update("other-device",60,api,DateTime.UtcNow).GetAwaiter().GetResult();Check(writes==1);
            entry["userId"]=9;Reject(()=>api.Read().GetAwaiter().GetResult());entry["userId"]=1;
            entry["gameId"]=9;Reject(()=>api.Read().GetAwaiter().GetResult());entry["gameId"]=2;
            entry["submissionId"]=9;Reject(()=>api.Read().GetAwaiter().GetResult());entry["submissionId"]=3;
            refuse=true;Reject(()=>bridge.Update("not-verified",9999,api,DateTime.UtcNow).GetAwaiter().GetResult());
            Reject(()=>NativeHltbProgress.Edit("<html>Sign in</html>"));Reject(()=>NativeHltbProgress.Edit("<script id='__NEXT_DATA__'>{}</script>"));
            var matches=JArray.Parse("[{id:1,custom_title:'Bloodborne'},{id:2,custom_title:'Bloodborne Remastered'}]");
            Check((int)NativeHltbProgress.Match(matches,"bloodborne")["id"]==1);Check(NativeHltbProgress.Match(matches,"Blood")==null);matches.Add(matches[0].DeepClone());Check(NativeHltbProgress.Match(matches,"Bloodborne")==null);
            Console.WriteLine("PASS native HLTB without add-ons: "+checks+" checks");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
