using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK.Models;
using Newtonsoft.Json.Linq;

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
        {
            var identity=modern?"pa:"+Convert.ToString(Get(record,"EffectiveProviderKey")??Get(record,"ProviderKey"))+":"+Convert.ToString(Get(record,"AppId")):"successstory:"+(game.PluginId==Guid.Empty?"manual":game.PluginId.ToString());
            var registry=modern?Get(plugin,"ProviderRegistry"):null;
            if(registry!=null)
            {
                var provider=Convert.ToString(Get(record,"ProviderKey"));
                if(!new[]{"Custom","Manual","ShadPS4","RPCS3","Xenia","Local"}.Contains(provider,StringComparer.OrdinalIgnoreCase))
                {
                    var live=registry.GetType().GetMethod("GetLiveSettings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
                    if(live==null)throw new NotSupportedException("The built-in achievement account interface changed; synchronization is deferred.");
                    var settings=live.Invoke(registry,new object[]{provider});
                    foreach(var field in new[]{"SteamUserId","RaUsername","AccountId","UserId","Username","Xuid","BattleNetAccountId","AccountName","CharacterId","Uid","PlayerId"})
                    {var value=Convert.ToString(Get(settings,field));if(!String.IsNullOrWhiteSpace(value)){identity+=":account:"+Identity.Hash(field+":"+value.Trim().ToLowerInvariant());break;}}
                }
            }
            return Identity.Hash(identity);
        }
        public void Capture(AchievementStore store,Game game,string key,string device)
        {
            var record=Load(game.Id);if(modern&&Get(plugin,"AchievementDataService")!=null)record=PluginActivityStore.Call(Get(plugin,"AchievementDataService"),"GetGameAchievementData",game.Id.ToString());if(record==null)return;
            if(Convert.ToBoolean(Get(record,modern?"ExcludedByUser":"IsIgnored")??false))return;
            var items=Get(record,modern?"Achievements":"Items") as IEnumerable;if(items==null)return;
            var scope=Scope(game,record);var seen=new HashSet<string>(StringComparer.Ordinal);
            if(modern)
            {
                var metadata=new JObject{{"ProviderKey",Convert.ToString(Get(record,"ProviderKey"))},{"ProviderPlatformKey",Get(record,"ProviderPlatformKey")==null?null:JToken.FromObject(Get(record,"ProviderPlatformKey"))},{"AppId",Convert.ToInt32(Get(record,"AppId")??0)}};var rows=new JArray();
                foreach(var item in items){var id=Convert.ToString(Get(item,"ApiName"));if(String.IsNullOrWhiteSpace(id))continue;var row=new JObject();foreach(var field in AchievementCatalog.Fields){var value=Get(item,field);if(field.EndsWith("IconPath",StringComparison.Ordinal)&&value!=null){Uri uri;if(File.Exists(Convert.ToString(value))&&new FileInfo(Convert.ToString(value)).Length<=8*1024*1024){using(var image=File.OpenRead(Convert.ToString(value)))value=new BlobStore(Path.Combine(Path.GetDirectoryName(store.Objects),"blobs")).Put(image).ToString();}else if(!Uri.TryCreate(Convert.ToString(value),UriKind.Absolute,out uri)||uri.Scheme!="https"||!String.IsNullOrEmpty(uri.UserInfo))value=null;}row[field]=value==null?JValue.CreateNull():JToken.FromObject(value);}rows.Add(row);}metadata["Achievements"]=rows;
                new AchievementCatalogStore(Path.GetDirectoryName(store.Objects)).Observe(new AchievementCatalog{Game=key,Scope=scope,CreatedUtc=DateTimeOffset.UtcNow.ToString("O"),Data=metadata});
            }
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
            var record=Load(game.Id);
            bool catalogChanged=false;
            if(modern)
            {
                var catalogType=plugin.GetType().Assembly.GetType("PlayniteAchievements.Models.Achievements.GameAchievementData");
                var matches=catalogType==null?new AchievementCatalog[0]:new AchievementCatalogStore(Path.GetDirectoryName(store.Objects)).ReadAll().Where(c=>c.Game==key&&c.Scope==Scope(game,c.Data.ToObject(catalogType))&&(record==null||c.Scope==Scope(game,record))).GroupBy(c=>c.Scope).Select(g=>g.OrderByDescending(c=>DateTimeOffset.Parse(c.CreatedUtc)).ThenBy(c=>c.Hash(),StringComparer.Ordinal).First()).ToArray();
                if(matches.Length==1&&(record==null||!Convert.ToBoolean(Get(record,"ExcludedByUser")??false)))
                {
                    var payload=record==null?(JObject)matches[0].Data.DeepClone():JObject.FromObject(record);
                    var rows=(JArray)payload["Achievements"];
                    if(record!=null){var known=new HashSet<string>(rows.Select(r=>(string)r["ApiName"]),StringComparer.Ordinal);foreach(var row in ((JArray)matches[0].Data["Achievements"]).OfType<JObject>())if(known.Add((string)row["ApiName"])){rows.Add(row.DeepClone());catalogChanged=true;}}
                    else catalogChanged=true;
                    if(catalogChanged){foreach(var row in rows.OfType<JObject>())foreach(var field in new[]{"UnlockedIconPath","LockedIconPath"}){var reference=(string)row[field];if(reference!=null&&reference.StartsWith("blob:",StringComparison.Ordinal))row[field]=MaterializeIcon(Path.GetDirectoryName(store.Objects),reference);}record=payload.ToObject(catalogType);}
                }
            }
            if(record==null||Convert.ToBoolean(Get(record,modern?"ExcludedByUser":"IsIgnored")??false))return;
            var items=Get(record,modern?"Achievements":"Items") as IEnumerable;if(items==null)return;
            var scope=Scope(game,record);var merged=store.ForGame(key).Where(r=>r.Scope==scope).ToDictionary(r=>r.Id,StringComparer.Ordinal);bool changed=catalogChanged;
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
        static string MaterializeIcon(string root,string reference)
        {
            var blob=BlobReference.Parse(reference);var folder=Path.Combine(root,"achievement-icons");Directory.CreateDirectory(folder);SaveFiles.CheckAncestors(folder);
            var file=Path.Combine(folder,blob.Hash+".png");SaveFiles.CheckAncestors(file);
            if(File.Exists(file)&&new FileInfo(file).Length==blob.Length&&SaveMigrationFiles.Hash(file)==blob.Hash)return file;
            var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var output=File.Create(temporary))new BlobStore(Path.Combine(root,"blobs")).CopyVerified(reference,output);if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);return file;}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    public sealed partial class PlaytimePlugin
    {
        AchievementStore AchievementData{get{return new AchievementStore(Root);}}
        string achievementStatus="Achievement history is stored locally. Sync to check the cloud.";
        bool achievementSyncPending;
        AchievementAdapter[] AchievementAdapters()
        {return PlayniteApi.Addons.Plugins.Concat(achievementEngine==null?new Playnite.SDK.Plugins.Plugin[0]:new Playnite.SDK.Plugins.Plugin[]{achievementEngine}).Distinct().Where(p=>p.GetType().FullName=="SuccessStory.SuccessStory"||p.GetType().FullName=="PlayniteAchievements.PlayniteAchievementsPlugin").Select(p=>new AchievementAdapter(p,p.GetType().FullName=="PlayniteAchievements.PlayniteAchievementsPlugin")).ToArray();}
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
            var catalogs=new AchievementCatalogStore(Root);var catalogCache=Path.Combine(metadataCache,"achievement-catalogs","objects");catalogs.Import(catalogCache);var schemas=catalogs.ReadAll();var pendingSchemas=schemas.Where(c=>!File.Exists(Path.Combine(catalogCache,c.Hash()+".json"))).ToArray();new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,()=>closing||TransferStopped).PushFiles(blobs.DirectoryPath,pendingSchemas.SelectMany(c=>c.IconReferences()).Select(r=>BlobReference.Parse(r).Hash+".blob"));new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/achievement-catalogs/objects",cancelled:()=>closing||TransferStopped).PushFiles(catalogs.Objects,pendingSchemas.Select(c=>c.Hash()+".json"));var iconData=new SortedDictionary<string,string>(StringComparer.Ordinal);var iconIndex=0;foreach(var reference in schemas.Where(c=>repository.Snapshot().Mappings.Values.Any(m=>m.Key==c.Game)).SelectMany(c=>c.IconReferences()).Distinct())iconData.Add("icon"+(iconIndex++),reference);blobs.Ensure(iconData);
            var transport=new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/achievements/objects",cancelled:()=>closing||TransferStopped);
            transport.PushFiles(store.Objects,Directory.GetFiles(store.Objects,"*.json").Where(p=>!File.Exists(Path.Combine(incoming,Path.GetFileName(p)))).Select(Path.GetFileName));
            achievementStatus="Achievements synchronized with your personal vault.";
            var adapters=UI(()=>AchievementAdapters());foreach(var pair in repository.Snapshot().Mappings)
            {
                CheckTransfer();UI(()=>{var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));if(game==null||GameBusy(game.Id))return;foreach(var adapter in adapters)try{adapter.Project(store,game,pair.Value.Key);}catch(Exception ex){achievementSyncPending=true;achievementStatus="Achievement history synced; an optional add-on display update is pending.";logger.Warn(ex,"Achievement display update deferred.");}});await Task.Delay(1);
            }
        }
        private Task SyncAchievementRecords(string metadataCache)
        {
            if(!onboarding.SyncAchievements)return Task.FromResult(0);
            var store=AchievementData;var incoming=Path.Combine(metadataCache,"achievements","objects");store.Import(incoming);
            new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/achievements/objects",cancelled:()=>closing||TransferStopped).PushFiles(store.Objects,Directory.GetFiles(store.Objects,"*.json").Where(p=>!File.Exists(Path.Combine(incoming,Path.GetFileName(p)))).Select(Path.GetFileName));return Task.FromResult(0);
        }
        async Task SyncAchievementDetails(string metadataCache,bool interactive)
        {
            try{await SyncAchievementCustomizations(metadataCache,interactive);}catch(OperationCanceledException){throw;}catch(Exception ex){achievementSyncPending=true;achievementStatus="Achievement customizations are queued for retry. Local records are preserved.";logger.Warn(ex,"Achievement customization transfer deferred; sessions continue.");}
            try{await SyncAchievements(metadataCache);}catch(OperationCanceledException){throw;}catch(Exception ex){achievementSyncPending=true;achievementStatus="Achievement sync is queued for retry. Local records are preserved.";logger.Warn(ex,"Achievement transfer deferred; sessions continue.");}
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
