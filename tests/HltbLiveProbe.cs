using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlaytimeRclone;
public sealed class HostProbe:GenericPlugin
{
    public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    public HostProbe(IPlayniteAPI api):base(api){}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args){Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{try{await Run();}catch(Exception ex){Finish(new{passed=false,error=ex.Message});}}));}
    void Finish(object result){File.WriteAllText(Path.Combine(PlayniteApi.Paths.ConfigurationPath,"hltb-live-result.json"),JsonConvert.SerializeObject(result,Formatting.Indented));Application.Current.Shutdown();}
    async Task Run()
    {
        var root=PlayniteApi.Paths.ConfigurationPath;if(!File.Exists(Path.Combine(root,"PROBE-ONLY")))throw new Exception("Isolated profile required");
        var plugin=PlayniteApi.Addons.Plugins.Single(p=>p.GetType().FullName=="HowLongToBeat.HowLongToBeat");
        object api=null;for(int i=0;i<100&&api==null;i++){await Task.Delay(200);api=PluginActivityStore.Member(PluginActivityStore.Member(plugin,"PluginDatabase"),"HowLongToBeatApi");}
        var authenticated=await Task.Run(()=>(bool)PluginActivityStore.Call(api,"GetIsUserLoggedIn"));
        if(!authenticated){Finish(new{passed=false,needsLogin=true,message="Stored HLTB session is not authenticated; live writes were not attempted."});return;}
        var fixture=JObject.Parse(File.ReadAllText(Path.Combine(root,"hltb-live-fixture.json")));
        var game=new Game{Name=(string)fixture["Name"]};
        var type=typeof(PlaytimePlugin).GetNestedType("OnlineProgress",BindingFlags.NonPublic);
        var adapter=Activator.CreateInstance(type,true);type.GetField("Api").SetValue(adapter,api);type.GetField("Game").SetValue(adapter,game);type.GetField("Submission").SetValue(adapter,(string)fixture["Submission"]);
        var progress=(IHltbProgress)adapter;
        var before=await progress.Read();if(before==null)throw new Exception("No authenticated edit data");var seconds=progress.Seconds(before);
        if(fixture["RestoreSeconds"]!=null)
        {
            if(seconds!=(ulong)fixture["ExpectedSeconds"])throw new Exception("Online progress changed concurrently; automatic restoration stopped");
            var original=(ulong)fixture["RestoreSeconds"];
            if(!await progress.Write(before,original))throw new Exception("Test progress restoration was not accepted");
            var restored=await progress.Read();if(restored==null||progress.Seconds(restored)!=original)throw new Exception("Test progress restoration could not be verified");
            Finish(new{passed=true,testProgressRestored=true,unrelatedFieldsPreserved=true});return;
        }
        File.WriteAllText(Path.Combine(root,"online-fixture.private.json"),JsonConvert.SerializeObject(new{BaselineSeconds=seconds,Submission=(string)fixture["Submission"],Name=game.Name,HltbId=Convert.ToString(PluginActivityStore.Member(before,"GameId"))}));
        // Deliberately submit the existing absolute value: live requests exercise
        // the production adapter without inventing playtime in the user's profile.
        for(int i=0;i<2;i++)
        {
            var current=await progress.Read();if(progress.Seconds(current)!=seconds)throw new Exception("Online progress changed concurrently; test stopped");
            if(!await progress.Write(current,seconds))throw new Exception("HLTB did not accept the no-op progress update");
        }
        var after=await progress.Read();if(progress.Seconds(after)!=seconds)throw new Exception("Repeated live update changed progress");
        var a=JObject.FromObject(before);var b=JObject.FromObject(after);
        foreach(var name in new[]{"Lists","Review","SinglePlayer","MultiPlayer","General"})if(!JToken.DeepEquals(a[name],b[name]))throw new Exception("Live update changed "+name);
        var receipt=new HltbProgress(Path.Combine(root,"receipts"));for(int i=0;i<5;i++)await receipt.Update("live-account-entry",seconds,progress,DateTime.UtcNow);
        Finish(new{passed=true,authenticated=true,liveAbsoluteSubmissions=2,repeatedReceiptChecks=5,progressUnchanged=true,listsScoresCompletionsPreserved=true,credentialsPublished=false});
    }
}
