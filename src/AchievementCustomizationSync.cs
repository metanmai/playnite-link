using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        Vault achievementCustomVault;
        readonly Dictionary<string,SortedDictionary<string,string>> capturedAchievementCustomizations=new Dictionary<string,SortedDictionary<string,string>>();
        Vault AchievementCustomVault{get{return achievementCustomVault??(achievementCustomVault=new Vault(Path.Combine(Root,"achievement-customization-vault")));}}
        object EngineCustomData{get{return achievementEngine==null?null:PluginActivityStore.Member(achievementEngine,"GameCustomDataStore");}}
        Checkpoint AchievementCustomCheckpoint(string key)
        {
            SaveLink link;if(!AchievementCustomVault.State.Links.TryGetValue(key,out link)){link=new SaveLink();AchievementCustomVault.State.Links.Add(key,link);AchievementCustomVault.Save();}return link.Save;
        }
        static JObject PersonalAchievementState(object record)
        {
            var raw=JObject.FromObject(record);var state=new JObject();
            state["GoalAchievementApiNames"]=raw["GoalAchievementApiNames"] as JArray??new JArray();
            var custom=new JArray();foreach(var row in raw["CustomAchievements"] as JArray??new JArray()){var item=new JObject();foreach(var field in new[]{"Id","Unlocked","UnlockTimeUtc","ProgressNum"})if(row[field]!=null)item[field]=row[field].DeepClone();custom.Add(item);}state["CustomAchievements"]=custom;
            var manual=raw["ManualLink"] as JObject;if(manual!=null){var item=new JObject();foreach(var field in new[]{"UnlockTimes","UnlockStates"})if(manual[field]!=null)item[field]=manual[field].DeepClone();state["ManualLink"]=item;}
            var overrides=new JObject();foreach(var property in (raw["AchievementOverrides"] as JObject??new JObject()).Properties()){var item=new JObject();foreach(var field in new[]{"UnlockTimeUtc","ClearUnlockTime"})if(property.Value[field]!=null)item[field]=property.Value[field].DeepClone();if(item.Count>0)overrides[property.Name]=item;}state["AchievementOverrides"]=overrides;
            return state;
        }
        SortedDictionary<string,string> CaptureAchievementCustomization(Game game)
        {
            var store=EngineCustomData;var result=new SortedDictionary<string,string>(StringComparer.Ordinal);if(store==null)return result;
            var raw=PluginActivityStore.Call(store,"LoadOrDefault",game.Id);if(raw==null)return result;
            var temp=Path.Combine(Root,"scratch","achievement-"+Guid.NewGuid().ToString("N")+".pa.zip");Directory.CreateDirectory(Path.GetDirectoryName(temp));
            try
            {
                if(Convert.ToBoolean(PluginActivityStore.Call(store,"HasPortableData",game.Id)))
                {
                    PluginActivityStore.Call(store,"ExportPortablePackage",game.Id,temp);
                    using(var stream=File.OpenRead(temp))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))foreach(var entry in zip.Entries)
                    {
                        if(entry.Length==0&&entry.FullName.EndsWith("/",StringComparison.Ordinal))continue;
                        using(var input=entry.Open())
                        {
                            if(entry.FullName=="custom-data.pa")
                            {
                                if(entry.Length>16*1024*1024)throw new IOException("Achievement customization manifest is too large.");JObject manifest;using(var reader=new StreamReader(input))manifest=JsonSafety.Object(reader.ReadToEnd());manifest["PlayniteGameId"]=Guid.Empty.ToString();manifest.Remove("GameKeys");
                                using(var bytes=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest.ToString(Formatting.None))))result.Add(entry.FullName,blobs.Put(bytes).ToString());
                            }
                            else result.Add(entry.FullName,blobs.Put(input).ToString());
                        }
                    }
                }
                var personal=PersonalAchievementState(raw);
                var hasPersonal=((JArray)personal["GoalAchievementApiNames"]).Count>0||((JArray)personal["CustomAchievements"]).Count>0||personal["ManualLink"]!=null||((JObject)personal["AchievementOverrides"]).Count>0;
                if(hasPersonal)using(var bytes=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(personal.ToString(Formatting.None))))result.Add("link-personal-state.json",blobs.Put(bytes).ToString());
                SaveFiles.Validate(result);return result;
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        void ApplyAchievementCustomization(Game game,SortedDictionary<string,string> data)
        {
            SaveFiles.Validate(data);blobs.Ensure(data);var store=EngineCustomData;if(store==null)throw new IOException("Built-in achievement data store is not ready.");
            var previous=PluginActivityStore.Call(store,"LoadOrDefault",game.Id);var previousCopy=JObject.FromObject(previous).ToObject(previous.GetType());
            var temp=Path.Combine(Root,"scratch","achievement-apply-"+Guid.NewGuid().ToString("N")+".pa.zip");Directory.CreateDirectory(Path.GetDirectoryName(temp));
            try
            {
                if(data.ContainsKey("custom-data.pa"))
                {
                    using(var stream=File.Create(temp))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))foreach(var pair in data.Where(p=>p.Key!="link-personal-state.json"))using(var output=zip.CreateEntry(pair.Key).Open())blobs.CopyVerified(pair.Value,output);
                    PluginActivityStore.Call(store,"ImportReplacePortable",game.Id,temp);
                }
                var current=PluginActivityStore.Call(store,"LoadOrDefault",game.Id);var raw=JObject.FromObject(current);
                if(!data.ContainsKey("custom-data.pa")){var reset=JObject.FromObject(Activator.CreateInstance(current.GetType()));foreach(var field in new[]{"ExcludedFromRefreshes","ExcludedFromSummaries","RuleClaims","UserPinnedFields","UnlockSounds","NotificationBehaviorOverride","CaptureOverride"})if(raw[field]!=null)reset[field]=raw[field].DeepClone();raw=reset;}
                var personal=data.ContainsKey("link-personal-state.json")?JsonSafety.Object(System.Text.Encoding.UTF8.GetString(blobs.ReadSmall(data["link-personal-state.json"]))):new JObject();
                if(personal.Properties().Any(p=>!new[]{"GoalAchievementApiNames","CustomAchievements","ManualLink","AchievementOverrides"}.Contains(p.Name)))throw new IOException("Unexpected personal achievement fields.");
                raw["GoalAchievementApiNames"]=personal["GoalAchievementApiNames"] as JArray??new JArray();
                foreach(var item in raw["CustomAchievements"] as JArray??new JArray())
                {
                    var matching=(personal["CustomAchievements"] as JArray??new JArray()).SingleOrDefault(r=>(string)r["Id"]==(string)item["Id"]);if(matching==null)continue;foreach(var field in new[]{"Unlocked","UnlockTimeUtc","ProgressNum"})if(matching[field]!=null)item[field]=matching[field].DeepClone();
                }
                var manual=raw["ManualLink"] as JObject;var supplied=personal["ManualLink"] as JObject;if(manual!=null&&supplied!=null)foreach(var field in new[]{"UnlockStates","UnlockTimes"})if(supplied[field]!=null)manual[field]=supplied[field].DeepClone();
                foreach(var property in (personal["AchievementOverrides"] as JObject??new JObject()).Properties()){var map=raw["AchievementOverrides"] as JObject;if(map==null){map=new JObject();raw["AchievementOverrides"]=map;}var item=map[property.Name] as JObject;if(item==null){item=new JObject();map[property.Name]=item;}foreach(var field in new[]{"UnlockTimeUtc","ClearUnlockTime"})if(property.Value[field]!=null)item[field]=property.Value[field].DeepClone();}
                raw["PlayniteGameId"]=game.Id.ToString();PluginActivityStore.Call(store,"Save",game.Id,raw.ToObject(current.GetType()));
            }
            catch{PluginActivityStore.Call(store,"Save",game.Id,previousCopy);throw;}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        async Task CaptureAchievementCustomizations()
        {
            capturedAchievementCustomizations.Clear();if(!onboarding.SyncAchievements||EngineCustomData==null)return;
            foreach(var pair in repository.Snapshot().Mappings)
            {
                CheckTransfer();var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(pair.Key)));if(game==null||UI(()=>GameBusy(game.Id)))continue;
                try
                {
                    var data=UI(()=>CaptureAchievementCustomization(game));capturedAchievementCustomizations[pair.Value.Key]=data;var checkpoint=AchievementCustomCheckpoint(pair.Value.Key);var hash=Revision.ContentHashOf(data);
                    if(checkpoint.Revision!=null&&checkpoint.Content!=hash){var revision=new Revision{Version=2,Game=pair.Value.Key,Kind="save",Device=repository.Snapshot().Device,Parents=new List<string>{checkpoint.Revision},Data=data};AchievementCustomVault.Add(revision);checkpoint.Revision=revision.Hash();checkpoint.Content=hash;AchievementCustomVault.Save();}
                }
                catch(Exception ex){achievementSyncPending=true;achievementStatus="Achievement customization capture is pending; originals are preserved.";logger.Warn(ex,"Achievement customization capture deferred.");}
                await Task.Delay(1);
            }
        }
        async Task SyncAchievementCustomizations(string metadataCache,bool interactive)
        {
            if(!onboarding.SyncAchievements||EngineCustomData==null)return;
            AchievementCustomVault.Import(Path.Combine(metadataCache,"achievement-customizations","objects"));
            foreach(var pair in repository.Snapshot().Mappings)
            {
                CheckTransfer();SortedDictionary<string,string> local;if(!capturedAchievementCustomizations.TryGetValue(pair.Value.Key,out local))continue;var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(pair.Key)));if(game==null||UI(()=>GameBusy(game.Id)))continue;
                var checkpoint=AchievementCustomCheckpoint(pair.Value.Key);if(local.Count==0&&checkpoint.Revision==null&&AchievementCustomVault.Heads(pair.Value.Key,"save").Count==0)continue;if(UI(()=>Revision.ContentHashOf(CaptureAchievementCustomization(game)))!=Revision.ContentHashOf(local)){achievementSyncPending=true;continue;}var result=AchievementCustomVault.Reconcile(pair.Value.Key,"save",repository.Snapshot().Device,local,checkpoint,conflict=>interactive?UI(()=>ChooseAchievementCustomization(game,conflict)):"cancel",data=>{blobs.Ensure(data);UI(()=>ApplyAchievementCustomization(game,data));});
                if(result.Cancelled){achievementSyncPending=true;achievementStatus="Achievement customization versions need review. Both versions are preserved.";}await Task.Delay(1);
            }
            var incoming=Path.Combine(metadataCache,"achievement-customizations","objects");var revisions=AchievementCustomVault.Read(AchievementCustomVault.Objects).Where(r=>!File.Exists(Path.Combine(incoming,r.Key+".json"))).ToDictionary(r=>r.Key,r=>r.Value);var blobNames=revisions.Values.SelectMany(r=>r.Data.Values).Distinct().Select(r=>BlobReference.Parse(r).Hash+".blob");
            new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,()=>closing||TransferStopped).PushFiles(blobs.DirectoryPath,blobNames);
            new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/achievement-customizations/objects",cancelled:()=>closing||TransferStopped).PushFiles(AchievementCustomVault.Objects,revisions.Keys.Select(r=>r+".json"));
        }
        string ChooseAchievementCustomization(Game game,Conflict conflict)
        {
            string choice="cancel";var window=Window("Achievement customization conflict",720,540);var panel=Page(window,"Choose achievement customizations",game.Name);Label(panel,"Both devices changed achievement notes, goals, manual states or presentation. Both versions remain in history. This choice does not replace game saves.");
            Button(panel,"Keep this device's customizations",()=>{choice="local";window.Close();});foreach(var head in conflict.Heads){var revision=head;Button(panel,"Use device "+revision.Device.Substring(0,8)+" · "+revision.CreatedUtc,()=>{choice=revision.Hash();window.Close();});}Button(Footer(window),"Decide later",()=>window.Close());window.ShowDialog();return choice;
        }
    }
}
