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
            CheckTransfer();
            var id=game.Id;
            var version=CustomDataVersion(id);
            var store=achievementCustomDataStore;var result=new SortedDictionary<string,string>(StringComparer.Ordinal);if(store==null)return result;
            // LoadOrDefault returns a detached clone from the engine's locked store.
            var raw=PluginActivityStore.Call(store,"LoadOrDefault",id);if(raw==null)return result;
            var temp=Path.Combine(Root,"scratch","achievement-"+Guid.NewGuid().ToString("N")+".pa.zip");Directory.CreateDirectory(Path.GetDirectoryName(temp));
            try
            {
                if(Convert.ToBoolean(PluginActivityStore.Call(store,"HasPortableData",id)))
                {
                    // The standalone add-on may resolve game metadata through Playnite's UI API.
                    UI(()=>PluginActivityStore.Call(store,"ExportPortablePackage",id,temp));
                    CheckTransfer();
                    using(var stream=File.OpenRead(temp))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))foreach(var entry in zip.Entries)
                    {
                        CheckTransfer();
                        if(entry.Length==0&&entry.FullName.EndsWith("/",StringComparison.Ordinal))continue;
                        using(var input=entry.Open())
                        {
                            if(entry.FullName=="custom-data.pa")
                            {
                                if(entry.Length>AchievementCustomizationPayload.ManifestBytes)throw new IOException("Achievement customization manifest is too large.");JObject manifest;using(var reader=new StreamReader(input))manifest=JsonSafety.Object(reader.ReadToEnd());manifest["PlayniteGameId"]=Guid.Empty.ToString();manifest.Remove("GameKeys");
                                using(var bytes=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest.ToString(Formatting.None))))result.Add(entry.FullName,blobs.Put(bytes).ToString());
                            }
                            else result.Add(entry.FullName,blobs.Put(input).ToString());
                        }
                    }
                }
                var personal=PersonalAchievementState(raw);
                var hasPersonal=((JArray)personal["GoalAchievementApiNames"]).Count>0||((JArray)personal["CustomAchievements"]).Count>0||personal["ManualLink"]!=null||((JObject)personal["AchievementOverrides"]).Count>0;
                if(hasPersonal)using(var bytes=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(personal.ToString(Formatting.None))))result.Add("link-personal-state.json",blobs.Put(bytes).ToString());
                if(version!=CustomDataVersion(id))throw new IOException("Achievement customizations changed during export; retrying preserves the edit.");
                AchievementCustomizationPayload.Validate(result);return result;
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        void ApplyAchievementCustomization(Game game,bool hasManifest,string package,JObject personal)
        {
            var store=achievementCustomDataStore;if(store==null)throw new IOException("The Playnite Achievements customization interface is unavailable.");
            var previous=PluginActivityStore.Call(store,"LoadOrDefault",game.Id);var previousCopy=JObject.FromObject(previous).ToObject(previous.GetType());
            try
            {
                if(hasManifest)PluginActivityStore.Call(store,"ImportReplacePortable",game.Id,package);
                var current=PluginActivityStore.Call(store,"LoadOrDefault",game.Id);var raw=JObject.FromObject(current);
                if(!hasManifest){var reset=JObject.FromObject(Activator.CreateInstance(current.GetType()));foreach(var field in new[]{"ExcludedFromRefreshes","ExcludedFromSummaries","RuleClaims","UserPinnedFields","UnlockSounds","NotificationBehaviorOverride","CaptureOverride"})if(raw[field]!=null)reset[field]=raw[field].DeepClone();raw=reset;}
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
        }
        async Task CaptureAchievementCustomizations(KeyValuePair<string,Mapping>[] mappings)
        {
            capturedAchievementCustomizations.Clear();if(!onboarding.SyncAchievements||achievementCustomDataStore==null)return;
            foreach(var pair in mappings)
            {
                CheckTransfer();var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(pair.Key)));if(game==null||UI(()=>GameBusy(game.Id)))continue;
                try
                {
                    var data=CaptureAchievementCustomization(game);capturedAchievementCustomizations[pair.Value.Key]=data;var checkpoint=AchievementCustomCheckpoint(pair.Value.Key);var hash=Revision.ContentHashOf(data);
                    if(checkpoint.Revision!=null&&checkpoint.Content!=hash){var revision=new Revision{Version=2,Game=pair.Value.Key,Kind="save",Device=repository.Snapshot().Device,Parents=new List<string>{checkpoint.Revision},Data=data};AchievementCustomVault.Add(revision);checkpoint.Revision=revision.Hash();checkpoint.Content=hash;AchievementCustomVault.Save();}
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){achievementSyncPending=true;achievementStatus="Achievement customization capture is pending; originals are preserved.";logger.Warn(ex,"Achievement customization capture deferred.");}
                await Task.Delay(1);
            }
        }
        async Task SyncAchievementCustomizations(string metadataCache,bool interactive,KeyValuePair<string,Mapping>[] mappings,ISet<string> games)
        {
            if(!onboarding.SyncAchievements||achievementCustomDataStore==null)return;
            var transport=new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/blobs","*.blob",86400,devicePreferences.BandwidthKiB,()=>closing||TransferStopped);
            AchievementCustomVault.Import(Path.Combine(metadataCache,"achievement-customizations","objects"));
            foreach(var pair in mappings)
            {
                CheckTransfer();SortedDictionary<string,string> local;if(!capturedAchievementCustomizations.TryGetValue(pair.Value.Key,out local))continue;var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(pair.Key)));if(game==null||UI(()=>GameBusy(game.Id)))continue;
                var checkpoint=AchievementCustomCheckpoint(pair.Value.Key);if(local.Count==0&&checkpoint.Revision==null&&AchievementCustomVault.Heads(pair.Value.Key,"save").Count==0)continue;var version=CustomDataVersion(game.Id);if(Revision.ContentHashOf(CaptureAchievementCustomization(game))!=Revision.ContentHashOf(local)){achievementSyncPending=true;continue;}var result=AchievementCustomVault.Reconcile(pair.Value.Key,"save",repository.Snapshot().Device,local,checkpoint,conflict=>interactive?UI(()=>ChooseAchievementCustomization(game,conflict)):"cancel",data=>{
                    var temp=Path.Combine(Root,"scratch","achievement-apply-"+Guid.NewGuid().ToString("N")+".pa.zip");Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    try
                    {
                        var personal=AchievementCustomizationPayload.Prepare(data,blobs,transport.FetchBoundedBlob,temp);
                        CheckTransfer();
                        UI(()=>{
                            CheckTransfer();
                            if(GameBusy(game.Id)||version!=CustomDataVersion(game.Id))
                                throw new IOException("Achievement customizations changed while preparing the replacement; retrying preserves the edit.");
                            ApplyAchievementCustomization(game,data.ContainsKey(AchievementCustomizationPayload.Manifest),temp,personal);
                        });
                    }
                    finally{if(File.Exists(temp))File.Delete(temp);}
                });
                if(result.Cancelled){achievementSyncPending=true;achievementStatus="Achievement customization versions need review. Both versions are preserved.";}await Task.Delay(1);
            }
            var incoming=Path.Combine(metadataCache,"achievement-customizations","objects");var revisions=AchievementCustomVault.Read(AchievementCustomVault.Objects).Where(r=>(games==null||games.Contains(r.Value.Game))&&!File.Exists(Path.Combine(incoming,r.Key+".json"))).ToDictionary(r=>r.Key,r=>r.Value);var blobNames=revisions.Values.SelectMany(r=>r.Data.Values).Distinct().Select(r=>BlobReference.Parse(r).Hash+".blob");
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
