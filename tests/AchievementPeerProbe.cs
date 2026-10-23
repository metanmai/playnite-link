using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
    PlaytimePlugin subject;int checks;string root;
    object Field(string name){return typeof(PlaytimePlugin).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);}
    void Check(bool value,string reason){if(!value)throw new Exception(reason);checks++;}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(async()=>{try{await Run();}catch(Exception ex){Finish(new{passed=false,error=ex.ToString(),checks});}}));}
    void Finish(object result){File.WriteAllText(Path.Combine(root??PlayniteApi.Paths.ConfigurationPath,"host-result.json"),JsonConvert.SerializeObject(result,Formatting.Indented));System.Windows.Application.Current.Shutdown();}
    async Task Run()
    {
        root=PlayniteApi.Paths.ConfigurationPath;Check(File.Exists(Path.Combine(root,"PROBE-ONLY")),"Isolated profile marker missing");
        subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
        while(subject.IsSyncRunning)await Task.Delay(100);
        var engine=Field("achievementEngine");Check(engine!=null,"Built-in engine missing");Check(!PlayniteApi.Addons.Plugins.Any(p=>p.GetType().FullName==engine.GetType().FullName),"A separate achievement extension is installed");
        var phase=File.ReadAllText(Path.Combine(root,"achievement-peer-phase.txt")).Trim();var repo=(Repository)Field("repository");var id=new Guid(repo.Snapshot().Mappings.Keys.Single());var game=PlayniteApi.Database.Games.Get(id);if(game==null){game=new Game{Id=id,Name="Achievement peer fixture",Playtime=100,IsInstalled=true,InstallDirectory=root};PlayniteApi.Database.Games.Add(game);}
        var custom=PluginActivityStore.Member(engine,"GameCustomDataStore");var service=PluginActivityStore.Member(engine,"AchievementDataService");
        if(phase=="author")
        {
            var record=PluginActivityStore.Call(custom,"LoadOrDefault",game.Id);var raw=JObject.FromObject(record);
            raw["CustomAchievements"]=new JArray(new JObject{{"Id","c9b678d5bac34420ad8d48f3abfa0fd3"},{"DisplayName","Cross-device authored achievement"},{"Description","Portable metadata"},{"Category","Story"},{"Points",30},{"Unlocked",true},{"UnlockTimeUtc","2026-10-23T01:00:00Z"},{"ProgressNum",3},{"ProgressDenom",10}});PluginActivityStore.Call(custom,"Save",game.Id,raw.ToObject(record.GetType()));
            ((JArray)raw["CustomAchievements"]).Add(new JObject{{"Id","bbc89bac7fe840a696c4d57e74d676c8"},{"DisplayName","Still to earn"},{"Points",10},{"Unlocked",false}});PluginActivityStore.Call(custom,"Save",game.Id,raw.ToObject(record.GetType()));
            var data=JObject.FromObject(PluginActivityStore.Call(service,"GetGameAchievementData",game.Id.ToString()));var api=(string)((JArray)data["Achievements"]).Single(r=>(string)r["DisplayName"]=="Still to earn")["ApiName"];
            raw=JObject.FromObject(PluginActivityStore.Call(custom,"LoadOrDefault",game.Id));raw["GoalAchievementApiNames"]=new JArray(api);raw["AchievementNotes"]=new JObject{{api,"Written on the PC"}};PluginActivityStore.Call(custom,"Save",game.Id,raw.ToObject(record.GetType()));
        }
        await subject.SyncPersonalAsync(true,null);Check(!Convert.ToBoolean(Field("achievementSyncPending")),"Achievement sync remains pending: "+Field("achievementStatus"));
        var received=JObject.FromObject(PluginActivityStore.Call(custom,"LoadOrDefault",game.Id));var definitions=received["CustomAchievements"] as JArray;Check(definitions!=null&&definitions.Count==2,"Fresh peer did not receive the authored achievements");var item=definitions.Single(r=>(string)r["DisplayName"]=="Cross-device authored achievement");Check((int)item["Points"]==30&&(string)item["Category"]=="Story","Achievement metadata changed across peers");
        var goals=received["GoalAchievementApiNames"] as JArray;Check(goals!=null&&goals.Count==1,"Achievement goal did not transfer");Check((string)received["AchievementNotes"][(string)goals[0]]=="Written on the PC","Achievement note did not transfer");
        if(phase=="receive")
        {
            Check((bool)item["Unlocked"]&&(int)item["ProgressNum"]==3,"Unlock/progress did not arrive");
            var record=PluginActivityStore.Call(custom,"LoadOrDefault",game.Id);item["Unlocked"]=false;item["UnlockTimeUtc"]=null;item["ProgressNum"]=7;PluginActivityStore.Call(custom,"Save",game.Id,received.ToObject(record.GetType()));
            await subject.SyncPersonalAsync(true,null);Check(!Convert.ToBoolean(Field("achievementSyncPending")),"Peer edits did not upload");
        }
        if(phase=="verify")Check(!(bool)item["Unlocked"]&&(int)item["ProgressNum"]==7,"Returning peer lost the deliberate manual relock/progress edit");
        var merged=JObject.FromObject(PluginActivityStore.Call(service,"GetGameAchievementData",game.Id.ToString()));Check(((JArray)merged["Achievements"]).Count==2,"Built-in achievement view cannot see the received records");
        Finish(new{passed=true,phase,checks,localGameId=id,cloudGameKey=repo.Snapshot().Mappings[id.ToString()].Key,device=repo.Snapshot().Device,profile=root,engineBuiltIn=true});
    }
}
