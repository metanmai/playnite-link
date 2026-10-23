using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private volatile bool preparingExistingBackups;
        private async void AdoptExistingSaveBackups()
        {
            if(!File.Exists(Path.Combine(LegacySaveData,"config.json")))return;
            preparingExistingBackups=true;
            try
            {
                var data=LegacyConfiguration();var all=PlayniteApi.Database.Games.ToArray();
                foreach(var property in LegacyConfigs(data).ToArray())
                {
                    var token=(JObject)property.Value;
                    var ids=(token["GameIds"] as JArray??new JArray()).Select(t=>(string)t).ToList();if(token["GameId"]!=null)ids.Add((string)token["GameId"]);
                    var matches=all.Where(g=>ids.Contains(g.Id.ToString(),StringComparer.OrdinalIgnoreCase)).ToArray();
                    if(matches.Length!=1)throw new IOException("An existing backup configuration has no unique local game identity. Its original data is preserved.");
                    var game=matches[0];if(GameBusy(game.Id))continue;
                    var local=game.Id.ToString();Mapping map;
                    if(!repository.Snapshot().Mappings.TryGetValue(local,out map))
                    {
                        if(game.PluginId==Guid.Empty&&all.Count(g=>String.Equals(g.Name,game.Name,StringComparison.OrdinalIgnoreCase))!=1)throw new IOException("An existing manual game name is ambiguous. Original backups are preserved.");
                        repository.Enroll(local,game.PluginId==Guid.Empty?"manual:"+game.Name.Trim().ToLowerInvariant():Source(game),game.Playtime,game.Playtime,game.PlayCount);
                        map=repository.Snapshot().Mappings[local];
                    }
                    var incoming=ParseSaveConfiguration(token.ToString());
                    var settings=Path.Combine(LegacySaveData,"settings.json");if(File.Exists(settings))SaveManagerOptions.ApplyLegacy(incoming,JsonSafety.Object(File.ReadAllText(settings)),property.Name);
                    SaveLink existing;if(vault.State.Links.TryGetValue(map.Key,out existing)&&existing.HasSaves)incoming=JsonSafety.Deserialize<SaveLink>(JsonConvert.SerializeObject(existing));
                    ValidatePlan(game,incoming);
                    var records=(data["GameBackups"]==null?null:data["GameBackups"][property.Name]) as JArray??new JArray();
                    var key=map.Key;await SaveOperation(game,()=>MigrateLegacyGame(property.Name,token,records,game,key,incoming));
                    // Missing historical files remain recorded, never reconstructed.
                    var path=Path.Combine(MigrationDirectory,property.Name+".json");var receipt=JsonSafety.Deserialize<SaveMigrationReceipt>(File.ReadAllText(path));receipt.UnavailableAcknowledged=true;Atomic.Write(path,JsonConvert.SerializeObject(receipt));
                }
                VerifyLegacyMigration(data);
                if(PlayniteApi.Addons.Plugins.Any(p=>p.Id==SaveManagerId)&&!File.Exists(Path.Combine(MigrationDirectory,"retire-request.json")))
                {ScheduleSaveManagerRetirement();status="Existing backups are ready. Restart Playnite to finish replacing the loaded Save Manager extension.";}
            }
            catch(Exception ex){logger.Error(ex,"Background backup compatibility needs attention; original data was preserved.");status="Existing backups need attention: "+ex.Message;}
            finally{preparingExistingBackups=false;if(!MigrationBlocksSync&&!closing)QueueSync();}
        }
    }
}
