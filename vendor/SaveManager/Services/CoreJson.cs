using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SaveManager.Services
{
    // Playnite supplies Json.NET. Bound input locally without changing the host's
    // global serializer settings or replacing its dependency assembly.
    public static class CoreJson
    {
        public const int MaximumDepth=64;
        public static string Serialize(object value,bool indented){return JsonConvert.SerializeObject(value,indented?Formatting.Indented:Formatting.None);}
        public static T Deserialize<T>(string json,JsonSerializerSettings settings=null)
        {
            settings=settings??new JsonSerializerSettings();
            settings.MaxDepth=MaximumDepth;
            settings.TypeNameHandling=TypeNameHandling.None;
            return JsonConvert.DeserializeObject<T>(json,settings);
        }
        public static JObject Object(string json){return Parse<JObject>(json);}
        public static JArray Array(string json){return Parse<JArray>(json);}
        private static T Parse<T>(string json)where T:JToken
        {
            using(var text=new StringReader(json))
            using(var reader=new JsonTextReader(text){MaxDepth=MaximumDepth})
            {
                var result=JToken.Load(reader) as T;
                if(result==null)throw new JsonSerializationException("Unexpected JSON root.");
                if(reader.Read())throw new JsonSerializationException("Additional JSON content.");
                return result;
            }
        }
    }
}

