using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK.Models;

namespace PlaytimeRclone
{
    // Only current-user public cache APIs are used. No credential/configuration
    // database, friend records, provider unlock API or game save files are touched.
    public sealed class AchievementAdapter
    {
        readonly object plugin;readonly bool modern;
        public AchievementAdapter(object plugin,bool modern){this.plugin=plugin;this.modern=modern;}
        static object Get(object obj,string name){return PluginActivityStore.Member(obj,name);}
        static void Set(object obj,string name,object value){var property=obj.GetType().GetProperty(name);if(property==null||!property.CanWrite)throw new NotSupportedException("Achievement add-on format changed.");property.SetValue(obj,value,null);}
        object Database{get{return Get(plugin,modern?"CacheManager":"PluginDatabase");}}
        object Load(Guid id){var db=Database;if(db==null)return null;return modern?PluginActivityStore.Call(db,"LoadGameData",id.ToString()):PluginActivityStore.Call(db,"Get",id,true,false);}
        static double? Number(object obj,string name){var value=Get(obj,name);return value==null?null:(double?)Convert.ToDouble(value);}
        string Scope(Game game,object record)
        {return Identity.Hash(modern?"pa:"+Convert.ToString(Get(record,"EffectiveProviderKey")??Get(record,"ProviderKey"))+":"+Convert.ToString(Get(record,"AppId")):"successstory:"+(game.PluginId==Guid.Empty?"manual":game.PluginId.ToString()));}
        public void Capture(AchievementStore store,Game game,string key,string device)
        {
            var record=Load(game.Id);if(record==null)return;
            if(Convert.ToBoolean(Get(record,modern?"ExcludedByUser":"IsIgnored")??false))return;
            var items=Get(record,modern?"Achievements":"Items") as IEnumerable;if(items==null)return;
            var scope=Scope(game,record);var seen=new HashSet<string>(StringComparer.Ordinal);
            var batch=new List<AchievementRecord>();
            foreach(var item in items)
            {
                var id=Convert.ToString(Get(item,"ApiName"));if(String.IsNullOrWhiteSpace(id))continue; // Localized titles are not identities.
                if(!seen.Add(id))throw new IOException("Ambiguous duplicate achievement API identifiers.");
                var date=Get(item,modern?"UnlockTimeUtc":"DateUnlocked") as DateTime?;
                var unlocked=Convert.ToBoolean(Get(item,modern?"Unlocked":"IsUnlock")??false);
                string utc=null;if(date.HasValue&&date.Value.Year>=1983)utc=DateTime.SpecifyKind(date.Value,date.Value.Kind==DateTimeKind.Unspecified?DateTimeKind.Utc:date.Value.Kind).ToUniversalTime().ToString("O");
                var progress=modern?Number(item,"ProgressNum"):Number(Get(item,"Progression"),"Value");var target=modern?Number(item,"ProgressDenom"):Number(Get(item,"Progression"),"Max");
                if(!progress.HasValue||!target.HasValue||target<=0){progress=null;target=null;}
                var value=new AchievementRecord{Game=key,Scope=scope,Id=id,Device=device,Name=Convert.ToString(Get(item,modern?"DisplayName":"Name")),Description=Convert.ToString(Get(item,"Description")),Hidden=Convert.ToBoolean(Get(item,"IsHidden")??false),Unlocked=unlocked,UnlockedUtc=unlocked?utc:null,Progress=progress,Target=target};value.Validate();batch.Add(value);
            }
            store.ObserveMany(batch);
        }
        public void Project(AchievementStore store,Game game,string key)
        {
            var record=Load(game.Id);if(record==null||Convert.ToBoolean(Get(record,modern?"ExcludedByUser":"IsIgnored")??false))return;
            var items=Get(record,modern?"Achievements":"Items") as IEnumerable;if(items==null)return;
            var scope=Scope(game,record);var merged=store.ForGame(key).Where(r=>r.Scope==scope).ToDictionary(r=>r.Id,StringComparer.Ordinal);bool changed=false;
            foreach(var item in items)
            {
                AchievementRecord value;if(!merged.TryGetValue(Convert.ToString(Get(item,"ApiName")),out value))continue;
                var old=Get(item,modern?"UnlockTimeUtc":"DateUnlocked") as DateTime?;
                if(value.Unlocked&&value.UnlockedUtc!=null&&(!old.HasValue||old.Value.Year<1983||DateTimeOffset.Parse(value.UnlockedUtc).UtcDateTime<old.Value.ToUniversalTime())){Set(item,modern?"UnlockTimeUtc":"DateUnlocked",DateTimeOffset.Parse(value.UnlockedUtc).UtcDateTime);changed=true;}
                if(modern&&value.Unlocked&&!Convert.ToBoolean(Get(item,"Unlocked")??false)){Set(item,"Unlocked",true);changed=true;}
                // SuccessStory's documented sentinel means unlocked with no known
                // date. It is never exported as a real date into Link's history.
                if(!modern&&value.Unlocked&&value.UnlockedUtc==null&&!Convert.ToBoolean(Get(item,"IsUnlock")??false)){Set(item,"DateUnlocked",new DateTime(1982,1,1,0,0,0,DateTimeKind.Local));changed=true;}
                var progression=modern?item:Get(item,"Progression");var target=Number(progression,modern?"ProgressDenom":"Max");var current=Number(progression,modern?"ProgressNum":"Value");
                if(value.Progress.HasValue&&value.Target==target&&(!current.HasValue||current<value.Progress))
                {Set(progression,modern?"ProgressNum":"Value",modern?(object)checked((int)value.Progress.Value):(object)value.Progress.Value);changed=true;}
            }
            if(!changed)return;
            if(modern){var result=PluginActivityStore.Call(Database,"SaveGameData",game.Id.ToString(),record);if(!Convert.ToBoolean(Get(result,"Success")??false))throw new IOException("Playnite Achievements cache update deferred.");}
            else PluginActivityStore.Call(Database,"Update",record);
        }
    }
    public sealed partial class PlaytimePlugin
    {
        AchievementStore AchievementData{get{return new AchievementStore(Root);}}
        string achievementStatus="Achievement history is stored locally. Sync to check the cloud.";
        bool achievementSyncPending;
        AchievementAdapter[] AchievementAdapters()
        {return PlayniteApi.Addons.Plugins.Where(p=>p.GetType().FullName=="SuccessStory.SuccessStory"||p.GetType().FullName=="PlayniteAchievements.PlayniteAchievementsPlugin").Select(p=>new AchievementAdapter(p,p.GetType().FullName=="PlayniteAchievements.PlayniteAchievementsPlugin")).ToArray();}
        private async Task CaptureAchievements()
        {
            achievementSyncPending=false;achievementStatus="Achievement sync is pending.";
            if(!onboarding.SyncAchievements)return;
            var state=repository.Snapshot();var adapters=UI(()=>AchievementAdapters());if(adapters.Length==0)return;var store=AchievementData;
            foreach(var pair in state.Mappings)
            {
                CheckTransfer();UI(()=>{var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));if(game==null||GameBusy(game.Id))return;foreach(var adapter in adapters)try{adapter.Capture(store,game,pair.Value.Key,state.Device);}catch(Exception ex){achievementSyncPending=true;achievementStatus="Some local achievement records need attention; synced history is preserved.";logger.Warn(ex,"Achievement capture deferred.");}});
                await Task.Delay(1);
            }
        }
        private async Task SyncAchievements(string metadataCache)
        {
            if(!onboarding.SyncAchievements)return;
            var store=AchievementData;var incoming=Path.Combine(metadataCache,"achievements","objects");store.Import(incoming);
            var transport=new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/achievements/objects",cancelled:()=>closing||TransferStopped);
            transport.PushFiles(store.Objects,Directory.GetFiles(store.Objects,"*.json").Where(p=>!File.Exists(Path.Combine(incoming,Path.GetFileName(p)))).Select(Path.GetFileName));
            achievementStatus="Achievements synchronized with your personal vault.";
            var adapters=UI(()=>AchievementAdapters());foreach(var pair in repository.Snapshot().Mappings)
            {
                CheckTransfer();UI(()=>{var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));if(game==null||GameBusy(game.Id))return;foreach(var adapter in adapters)try{adapter.Project(store,game,pair.Value.Key);}catch(Exception ex){achievementSyncPending=true;achievementStatus="Achievement history synced; an optional add-on display update is pending.";logger.Warn(ex,"Achievement display update deferred.");}});await Task.Delay(1);
            }
        }
        private void ShowAchievements(Game game)
        {
            Mapping map;if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map))return;
            var records=AchievementData.ForGame(map.Key);var window=Window("Achievements — "+game.Name,860,700);var panel=Page(window,"Achievements",game.Name);
            Label(panel,records.Count(r=>r.Unlocked)+" / "+records.Count+" unlocked · "+achievementStatus);
            Label(panel,"Merged Playnite records. Platform accounts remain authoritative for platform unlocks. Separate provider sets are kept separate.",13,true);
            var list=new ListBox{ItemsSource=records.OrderByDescending(r=>r.Unlocked).ThenBy(r=>r.Name).Select(r=>(r.Unlocked?"Unlocked":"Locked")+" · "+(r.Hidden&&!r.Unlocked?"Hidden achievement":r.Name)+(r.Progress.HasValue?" · "+r.Progress+" / "+r.Target:"")+(r.UnlockedUtc==null?"":" · "+DateTimeOffset.Parse(r.UnlockedUtc).ToLocalTime().ToString("g"))+" · set "+r.Scope.Substring(0,8)).ToArray(),MinHeight=200,MaxHeight=430};StyleInput(list);panel.Children.Add(list);
            if(records.Count==0)Label(panel,"No achievement records yet. Link automatically captures cached achievements from Playnite Achievements or SuccessStory when available.");
            Button(Footer(window),"Close",()=>window.Close());window.ShowDialog();
        }
    }
}
