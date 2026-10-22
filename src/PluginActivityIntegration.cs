using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlaytimeRclone
{
    public sealed class PluginActivityStore : IActivityStore
    {
        private readonly object plugin;
        private readonly Func<Guid,Game> getGame;
        public PluginActivityStore(object plugin,Func<Guid,Game> getGame){this.plugin=plugin;this.getGame=getGame;}
        public static object Member(object target,string name)
        {
            if(target==null)return null;
            for(var type=target.GetType();type!=null;type=type.BaseType)
            {
                var property=type.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly);
                if(property!=null)return property.GetValue(property.GetGetMethod(true).IsStatic?null:target,null);
            }
            return null;
        }
        public static object Call(object target,string name,params object[] args)
        {
            var methods=target.GetType().GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.Name==name&&m.GetParameters().Length==args.Length&&m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(x=>x)).ToArray();
            if(methods.Length!=1)throw new NotSupportedException("Unsupported extension API: "+name+". Activity data was not modified.");
            try{return methods[0].Invoke(target,args);}catch(TargetInvocationException ex){throw new IOException("Activity extension could not finish "+name+".",ex.InnerException??ex);}
        }
        private object Database{get{var db=Member(plugin,"PluginDatabase");if(db==null)throw new IOException("GameActivity is still initializing.");return db;}}
        private object Record(Guid game){return Call(Database,"Get",game,true,false);}
        public bool Busy(Guid game)
        {
            if(Member(Database,"IsLoaded") is bool&&!(bool)Member(Database,"IsLoaded"))return true;
            var monitor=Member(plugin,"GameActivityMonitoring");
            if(monitor==null)throw new NotSupportedException("Unsupported GameActivity session monitor. Update GameActivity before synchronizing sessions.");
            return Call(monitor,"GetRunningActivity",game)!=null;
        }
        public List<ActivitySession> Read(Guid game)
        {
            var record=Record(game);if(record==null)throw new IOException("GameActivity history is not ready.");
            var items=Member(record,"Items") as IEnumerable;if(items==null)throw new NotSupportedException("Unsupported GameActivity history schema.");
            var result=new List<ActivitySession>();
            foreach(var item in items)
            {
                var date=Member(item,"DateSession");var seconds=Member(item,"ElapsedSeconds");
                if(!(date is DateTime)||!(seconds is ulong))throw new NotSupportedException("Unsupported GameActivity session schema.");
                var utc=(DateTime)date;
                // This extension serializes DateSession in UTC. Older JSON readers may
                // return Unspecified; never reinterpret those ticks in the local zone.
                if(utc.Kind==DateTimeKind.Unspecified)utc=DateTime.SpecifyKind(utc,DateTimeKind.Utc);
                result.Add(new ActivitySession{Utc=utc.ToUniversalTime(),Seconds=(ulong)seconds});
            }
            return result;
        }
        public void Add(Guid game,ActivitySession session)
        {
            if(Busy(game))throw new IOException("GameActivity is completing a local session; retrying later.");
            var record=Record(game);var items=Member(record,"Items") as IList;
            if(items==null)throw new NotSupportedException("Unsupported GameActivity collection.");
            var type=plugin.GetType().Assembly.GetType("GameActivity.Models.Activity",true);
            var item=Activator.CreateInstance(type);var local=getGame(game);
            type.GetProperty("DateSession").SetValue(item,session.Utc,null);
            type.GetProperty("ElapsedSeconds").SetValue(item,session.Seconds,null);
            type.GetProperty("IdConfiguration").SetValue(item,-1,null);
            type.GetProperty("SourceID").SetValue(item,local.SourceId,null);
            type.GetProperty("PlatformIDs").SetValue(item,local.PlatformIds??new List<Guid>(),null);
            type.GetProperty("GameActionName").SetValue(item,"Synchronized session",null);
            items.Add(item);
            try{Call(Database,"Update",record);}catch{items.Remove(item);throw;}
        }
        public void Refresh(Guid game){var context=Member(Database,"GameContext") as Game;if(context!=null&&context.Id==game)Call(Database,"SetThemesResources",getGame(game));}
    }
    public sealed partial class PlaytimePlugin
    {
        private HltbProgress hltbProgress;
        private readonly HashSet<Guid> activityReview=new HashSet<Guid>();
        private DateTime hltbRetryAfter;
        private sealed class OnlineProgress:IHltbProgress
        {
            public object Api;public Game Game;public string Submission;public string HltbId;
            public static async Task<object> Await(object value){var task=value as Task;if(task==null)throw new NotSupportedException("Unsupported HLTB asynchronous API.");await task;return PluginActivityStore.Member(task,"Result");}
            public async Task<object> Read()
            {
                var edit=await Await(PluginActivityStore.Call(Api,"GetEditData",Game.Name,Submission));
                if(edit!=null&&Convert.ToString(PluginActivityStore.Member(edit,"SubmissionId"))!=Submission)throw new IOException("HLTB returned a different profile entry; upload stopped.");
                if(edit!=null&&!String.IsNullOrWhiteSpace(HltbId)&&Convert.ToString(PluginActivityStore.Member(edit,"GameId"))!=HltbId)throw new IOException("HLTB profile entry belongs to a different game; upload stopped.");
                return edit;
            }
            static object Progress(object edit){return PluginActivityStore.Member(PluginActivityStore.Member(edit,"General"),"Progress");}
            public ulong Seconds(object edit)
            {
                var progress=Progress(edit);if(progress==null)throw new NotSupportedException("Unsupported HLTB progress schema.");
                return checked(Convert.ToUInt64(PluginActivityStore.Member(progress,"Hours")??0)*3600+Convert.ToUInt64(PluginActivityStore.Member(progress,"Minutes")??0)*60+Convert.ToUInt64(PluginActivityStore.Member(progress,"Seconds")??0));
            }
            public async Task<bool> Write(object edit,ulong seconds)
            {
                var progress=Progress(edit);if(seconds/3600>Int32.MaxValue)throw new InvalidDataException("HLTB progress exceeds its supported range.");
                progress.GetType().GetProperty("Hours").SetValue(progress,(int)(seconds/3600),null);
                progress.GetType().GetProperty("Minutes").SetValue(progress,(int)(seconds/60%60),null);
                progress.GetType().GetProperty("Seconds").SetValue(progress,(int)(seconds%60),null);
                // Submit the freshly loaded edit object with only progress changed.
                // Never call OnGameStopped or alter lists, reviews or completions.
                return (bool)await Await(PluginActivityStore.Call(Api,"ApiSubmitData",Game,edit));
            }
        }
        private async Task SyncOnlineHltb()
        {
            if(onboarding==null||!onboarding.OnlineHltb||!devicePreferences.SyncPlaytime||DateTime.UtcNow<hltbRetryAfter)return;
            try
            {
                var plugin=UI(()=>PlayniteApi.Addons.Plugins.FirstOrDefault(p=>p.GetType().FullName=="HowLongToBeat.HowLongToBeat"));if(plugin==null)return;
                var db=PluginActivityStore.Member(plugin,"PluginDatabase");var api=PluginActivityStore.Member(db,"HowLongToBeatApi");
                if(api==null||!(bool)PluginActivityStore.Call(api,"GetIsUserLoggedIn")){activityStatus="Sign in through HowLongToBeat to upload merged progress. Cloud sessions remain synchronized.";hltbRetryAfter=DateTime.UtcNow.AddMinutes(5);return;}
                if(hltbProgress==null)hltbProgress=new HltbProgress(Root);
                var pending=0;
                foreach(var map in repository.Snapshot().Mappings)
                {
                    if(closing||transferCancelled)return;
                    var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(map.Key)));
                    if(game==null||game.IsRunning||game.IsLaunching||UI(()=>activityReview.Contains(game.Id)))continue;
                    var snapshot=repository.SnapshotGame(map.Key);var mapping=snapshot.Mappings[map.Key];
                    if(game.Playtime!=mapping.Expected||game.Playtime!=new Ledger(snapshot.Entries).Total(mapping.Key)){pending++;continue;}
                    var record=UI(()=>PluginActivityStore.Call(db,"Get",game.Id,true,false));
                    var data=record==null?null:PluginActivityStore.Call(record,"GetData");
                    var id=Convert.ToString(PluginActivityStore.Member(data,"Id"));if(String.IsNullOrWhiteSpace(id)||Convert.ToBoolean(PluginActivityStore.Member(data,"IsVndb")??false)){pending++;continue;}
                    var submission=Convert.ToString(PluginActivityStore.Member(record,"UserGameId"));
                    if(String.IsNullOrWhiteSpace(submission)||submission=="0")submission=Convert.ToString(PluginActivityStore.Call(api,"FindIdExisting",id));
                    if(String.IsNullOrWhiteSpace(submission)||submission=="0"){pending++;continue;}
                    var account=Convert.ToString(PluginActivityStore.Member(PluginActivityStore.Member(db,"PluginSettings"),"UserLogin"));
                    await hltbProgress.Update(Identity.Hash(account+":"+id+":"+submission),game.Playtime,new OnlineProgress{Api=api,Game=game,Submission=submission,HltbId=id},DateTime.UtcNow);
                }
                if(activityReview.Count==0)activityStatus="GameActivity sessions and existing online HLTB progress are up to date."+(pending>0?" "+pending+" game(s) need an HLTB profile entry or playtime review.":"");
            }
            catch(Exception ex){hltbRetryAfter=DateTime.UtcNow.AddMinutes(2);activityStatus="HLTB progress is queued: "+ex.Message;logger.Error(ex,"Online HLTB progress deferred.");}
        }
        private ActivityBridge activityBridge;
        private string activityStatus="Session integration detects GameActivity and HowLongToBeat automatically.";
        private TextBlock activityStatusText;
        private void ShowSessionHistory(Game game)
        {
            var window=Window("Session history — "+game.Name,820,680);
            var snapshot=repository.Snapshot();Mapping map;if(!snapshot.Mappings.TryGetValue(game.Id.ToString(),out map))return;
            var ledger=new Ledger(snapshot.Entries);var panel=Page(window,"Session history",game.Name);
            var summary=Card(panel,ledger.SessionCount(map.Key)+" sessions · "+ledger.Total(map.Key)+" seconds","Shared playtime is set to the merged total. Receiving history does not create game-start or game-stop events.");
            var store=ActivityStore();
            if(store!=null){var sessions=store.Read(game.Id);Label(summary,"GameActivity: "+sessions.Count+" recorded sessions · "+sessions.Sum(s=>(long)s.Seconds)+" seconds.");}
            Label(summary,"HowLongToBeat's Playnite display uses this game's merged total. Online progress updates use its own sign-in and the option in Playnite Link's Overview.",13,true);
            var records=snapshot.Entries.Where(e=>e.Game==map.Key&&e.Kind!="baseline").OrderByDescending(e=>e.StartedUtc).ToArray();
            var list=new ListBox{ItemsSource=records.Select(e=>DateTimeOffset.Parse(e.StartedUtc).ToLocalTime().ToString("g")+"  ·  "+e.Seconds+" seconds  ·  "+(e.Kind=="history"?"Earlier history":e.Device==snapshot.Device?"This device":"Another device")).ToArray(),MinHeight=160,MaxHeight=320};StyleInput(list);panel.Children.Add(list);
            if(ledger.SessionCount(map.Key)>(ulong)records.Length)Label(panel,"Some older plays are known only as a total/count. Their individual dates are unavailable; no artificial sessions were created.",13,true);
            Label(panel,activityStatus,13,true);Button(Footer(window),"Close",()=>window.Close());window.ShowDialog();
        }
        private PluginActivityStore ActivityStore()
        {
            var plugin=PlayniteApi.Addons.Plugins.FirstOrDefault(p=>p.Id==new Guid("afbb1a0d-04a1-4d0c-9afa-c6e42ca855b4"));
            return plugin==null?null:new PluginActivityStore(plugin,id=>PlayniteApi.Database.Games.Get(id));
        }
        private async System.Threading.Tasks.Task CaptureActivityHistoryAsync()
        {
            var store=UI(()=>ActivityStore());if(store==null)return;
            UI(()=>{if(activityBridge==null)activityBridge=new ActivityBridge(Root);activityReview.Clear();});
            var mappings=UI(()=>repository.Snapshot().Mappings.ToArray());
            for(var i=0;i<mappings.Length;i+=4)
            {
                CheckTransfer();ReportStatus("Reading existing GameActivity sessions without adding duplicate plays.",SyncDisplayState.Working,"Reading session history",Math.Min(i+4,mappings.Length),mappings.Length);
                var batch=mappings.Skip(i).Take(4).ToArray();
                UI(()=>{foreach(var map in batch){var game=PlayniteApi.Database.Games.Get(new Guid(map.Key));if(game==null||game.IsRunning||game.IsLaunching)continue;
                    try{if(!activityBridge.Capture(repository,game.Id,store))activityStatus="Waiting for GameActivity to finish a local session.";}
                    catch(Exception ex){activityReview.Add(game.Id);activityStatus=activityReview.Count+" game(s) need session-history review. "+game.Name+": "+ex.Message;NotifyError(ex);}
                }});await System.Threading.Tasks.Task.Delay(1);
            }
        }
        private bool ProjectActivity(Game game)
        {
            if(activityReview.Contains(game.Id))return false;
            var store=ActivityStore();
            if(store!=null)
            {
                if(activityBridge==null)activityBridge=new ActivityBridge(Root);
                if(!activityBridge.Capture(repository,game.Id,store)||!activityBridge.Project(repository,game.Id,store))return false;
                if(activityReview.Count==0)activityStatus="GameActivity session history is synchronized without replaying game events.";
            }
            return true;
        }
        private void SetActivityCounters(Game game)
        {
            var snapshot=repository.SnapshotGame(game.Id.ToString());var mapping=snapshot.Mappings[game.Id.ToString()];
            game.PlayCount=new Ledger(snapshot.Entries).SessionCount(mapping.Key);
            var latest=snapshot.Entries.Where(e=>e.Game==mapping.Key&&e.Kind!="baseline").Select(e=>DateTimeOffset.Parse(e.StartedUtc).UtcDateTime).OrderByDescending(d=>d).FirstOrDefault();
            if(latest!=default(DateTime)&&(!game.LastActivity.HasValue||game.LastActivity.Value.ToUniversalTime()<latest))game.LastActivity=latest;
        }
        private void RefreshActivityDisplays(Game game)
        {
            var store=ActivityStore();if(store!=null)store.Refresh(game.Id);
            // Use the actual runtime type, since published HLTB plugin ids predate
            // the stable manifest id and differ between forks.
            var hltb=PlayniteApi.Addons.Plugins.FirstOrDefault(p=>p.GetType().FullName=="HowLongToBeat.HowLongToBeat");
            if(hltb!=null){var db=PluginActivityStore.Member(hltb,"PluginDatabase");var context=PluginActivityStore.Member(db,"GameContext") as Game;if(context!=null&&context.Id==game.Id)PluginActivityStore.Call(db,"SetThemesResources",game);}
        }
        private async void RetryActivityAfterStop()
        {
            try{await Task.Delay(2500);while(!closing&&IsSyncRunning)await Task.Delay(250);if(!closing&&activityStatus.StartsWith("Waiting for GameActivity",StringComparison.Ordinal))QueuePersonalSync();}catch(Exception ex){logger.Error(ex,"Session integration retry deferred.");}
        }
    }
}
