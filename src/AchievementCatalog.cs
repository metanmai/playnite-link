using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlaytimeRclone
{
    public sealed class AchievementCatalog
    {
        public int Version=1;
        public string Game,Scope,CreatedUtc;
        public JObject Data;
        public string Canonical(){return JsonConvert.SerializeObject(this,Formatting.None);}
        public string Hash(){return Identity.Hash(Canonical());}
        public static readonly string[] Fields={"ApiName","DisplayName","Description","Points","ScaledPoints","CategoryType","Category","TrophyType","IsCapstone","GlobalPercentUnlocked","Rarity","Hidden","IsCustom","UnlockedIconPath","LockedIconPath"};
        public IEnumerable<string> IconReferences(){return ((JArray)Data["Achievements"]).OfType<JObject>().SelectMany(r=>r.Properties()).Where(p=>p.Name.EndsWith("IconPath",StringComparison.Ordinal)&&p.Value.Type==JTokenType.String).Select(p=>(string)p.Value).Where(s=>s.StartsWith("blob:",StringComparison.Ordinal)).Distinct();}
        public void Validate()
        {
            DateTimeOffset date;
            if(Version!=1||!Regex.IsMatch(Game??"","^[a-f0-9]{64}$")||!Regex.IsMatch(Scope??"","^[a-f0-9]{64}$")||!DateTimeOffset.TryParse(CreatedUtc,out date)||Data==null)throw new InvalidDataException("Invalid achievement catalog.");
            if(Data.Properties().Any(p=>!new[]{"ProviderKey","ProviderPlatformKey","AppId","Achievements"}.Contains(p.Name)))throw new InvalidDataException("Unexpected achievement catalog metadata.");
            foreach(var key in new[]{"ProviderKey","ProviderPlatformKey"})if(Data[key]!=null&&Data[key].Type!=JTokenType.Null&&!Regex.IsMatch((string)Data[key]??"","^[A-Za-z0-9_. -]{1,80}$"))throw new InvalidDataException("Invalid achievement provider.");
            if(Data["AppId"]==null||Data["AppId"].Type!=JTokenType.Integer||(long)Data["AppId"]<0||(long)Data["AppId"]>Int32.MaxValue)throw new InvalidDataException("Invalid achievement provider game ID.");
            var rows=Data["Achievements"] as JArray;if(rows==null||rows.Count>50000)throw new InvalidDataException("Invalid achievement catalog rows.");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var value in rows)
            {
                var row=value as JObject;if(row==null||row.Properties().Any(p=>!Fields.Contains(p.Name)))throw new InvalidDataException("Unexpected achievement catalog field.");
                var id=(string)row["ApiName"];if(String.IsNullOrWhiteSpace(id)||id.Length>512||id.Any(c=>c<32)||!ids.Add(id))throw new InvalidDataException("Ambiguous achievement catalog identity.");
                foreach(var property in row.Properties())
                {
                    if(property.Value.Type==JTokenType.Array||property.Value.Type==JTokenType.Object||property.Value.ToString().Length>8000)throw new InvalidDataException("Invalid achievement catalog value.");
                    if(property.Value.Type!=JTokenType.Null)
                    {
                        if(new[]{"Hidden","IsCustom","IsCapstone"}.Contains(property.Name)&&property.Value.Type!=JTokenType.Boolean)throw new InvalidDataException("Invalid achievement flag.");
                        if(new[]{"Points","ScaledPoints"}.Contains(property.Name)&&(property.Value.Type!=JTokenType.Integer||(long)property.Value<0||(long)property.Value>Int32.MaxValue))throw new InvalidDataException("Invalid achievement points.");
                        if(property.Name=="GlobalPercentUnlocked"){if(property.Value.Type!=JTokenType.Integer&&property.Value.Type!=JTokenType.Float)throw new InvalidDataException("Invalid achievement rarity.");var percent=(double)property.Value;if(Double.IsNaN(percent)||Double.IsInfinity(percent)||percent<0||percent>100)throw new InvalidDataException("Invalid achievement rarity.");}
                    }
                    if(property.Name.EndsWith("IconPath",StringComparison.Ordinal)&&property.Value.Type!=JTokenType.Null){Uri uri;var path=(string)property.Value;if(path!=null&&path.StartsWith("blob:",StringComparison.Ordinal)){var reference=BlobReference.Parse(path);if(reference.Length>8*1024*1024)throw new InvalidDataException("Achievement icon is too large.");continue;}if(!String.IsNullOrEmpty(path)&&(!Uri.TryCreate(path,UriKind.Absolute,out uri)||uri.Scheme!="https"||!String.IsNullOrEmpty(uri.UserInfo)))throw new InvalidDataException("Only public HTTPS or verified blob achievement icons may sync.");}
                }
            }
        }
        public static AchievementCatalog Read(string path)
        {
            if(new FileInfo(path).Length>16*1024*1024)throw new InvalidDataException("Oversized achievement catalog.");var result=JsonSafety.Deserialize<AchievementCatalog>(File.ReadAllText(path),new JsonSerializerSettings{MissingMemberHandling=MissingMemberHandling.Error});if(result==null)throw new InvalidDataException("Empty achievement catalog.");result.Validate();if(result.Hash()!=Path.GetFileNameWithoutExtension(path))throw new InvalidDataException("Achievement catalog hash mismatch.");return result;
        }
    }
    public sealed class AchievementCatalogStore
    {
        public readonly string Objects;
        public AchievementCatalogStore(string root){Objects=Path.Combine(root,"achievement-catalogs");Directory.CreateDirectory(Objects);}
        public List<AchievementCatalog> ReadAll(){return Directory.GetFiles(Objects,"*.json").Select(AchievementCatalog.Read).ToList();}
        public void Import(string directory){if(!Directory.Exists(directory))return;var batch=Directory.GetFiles(directory,"*.json").Select(AchievementCatalog.Read).ToArray();foreach(var value in batch)Put(value);}
        void Put(AchievementCatalog value){value.Validate();var path=Path.Combine(Objects,value.Hash()+".json");if(!File.Exists(path))Atomic.Write(path,value.Canonical());}
        public void Observe(AchievementCatalog value){value.Validate();if(ReadAll().Any(r=>r.Game==value.Game&&r.Scope==value.Scope&&JToken.DeepEquals(r.Data,value.Data)))return;Put(value);}
        public AchievementCatalog Single(string game){var sets=ReadAll().Where(r=>r.Game==game).GroupBy(r=>r.Scope).ToArray();return sets.Length==1?sets[0].OrderByDescending(r=>DateTimeOffset.Parse(r.CreatedUtc)).ThenBy(r=>r.Hash(),StringComparer.Ordinal).First():null;}
    }
}
