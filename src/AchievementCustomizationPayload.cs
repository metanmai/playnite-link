using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PlaytimeRclone
{
    public static class AchievementCustomizationPayload
    {
        public const string Manifest="custom-data.pa";
        public const string PersonalState="link-personal-state.json";
        // Match the portable importer's image/package ceilings and Link's
        // existing manifest cap. Limit personal state independently.
        public const int ManifestBytes=16*1024*1024;
        public const int PersonalStateBytes=16*1024*1024;
        public const int ImageBytes=64*1024*1024;
        public const long TotalBytes=256L*1024*1024;
        public const int EntryLimit=4096;

        public static void Validate(SortedDictionary<string,string> data)
        {
            if(data.Count>EntryLimit)throw new InvalidDataException("Too many achievement customization entries.");
            long total=0;
            foreach(var pair in data)
            {
                var limit=pair.Key==Manifest?ManifestBytes:PersonalStateBytes;
                if(pair.Key!=Manifest&&pair.Key!=PersonalState)
                {
                    if(!data.ContainsKey(Manifest)||!pair.Key.StartsWith("images/",StringComparison.Ordinal)||pair.Key.IndexOf('/',7)>=0)
                        throw new InvalidDataException("Unexpected achievement customization entry.");
                    limit=ImageBytes;
                }
                var reference=BlobReference.Parse(pair.Value);
                if(reference.Length>limit)throw new InvalidDataException("Achievement customization entry exceeds its byte budget.");
                total+=reference.Length;
                if(total>TotalBytes)throw new InvalidDataException("Achievement customizations exceed the combined byte budget.");
            }
            SaveFiles.Validate(data);
        }

        public static JObject Prepare(SortedDictionary<string,string> data,BlobStore blobs,Action<BlobReference,Stream> fetch,string package)
        {
            // Reject all declared sizes before the first download.
            Validate(data);
            string personalReference,manifestReference;
            var personal=new JObject();
            if(data.TryGetValue(PersonalState,out personalReference))
            {
                using(var bytes=ReadJsonBlob(personalReference,blobs,fetch))personal=Parse(bytes);
                if(personal.Properties().Any(p=>!new[]{"GoalAchievementApiNames","CustomAchievements","ManualLink","AchievementOverrides"}.Contains(p.Name)))
                    throw new InvalidDataException("Unexpected personal achievement fields.");
            }
            if(data.TryGetValue(Manifest,out manifestReference))
            {
                using(var manifest=ReadJsonBlob(manifestReference,blobs,fetch))
                {
                    // Parse the exact verified bytes the vendor will receive.
                    // In particular, bound the depth of recursive KindStyles.
                    Parse(manifest);manifest.Position=0;
                    using(var stream=new FileStream(package,FileMode.CreateNew,FileAccess.Write))
                    using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                    {
                        using(var output=zip.CreateEntry(Manifest).Open())manifest.CopyTo(output);
                        foreach(var pair in data.Where(p=>p.Key!=Manifest&&p.Key!=PersonalState))
                        {
                            blobs.FetchVerified(BlobReference.Parse(pair.Value),fetch);
                            using(var output=zip.CreateEntry(pair.Key).Open())blobs.CopyVerified(pair.Value,output);
                        }
                    }
                }
            }
            return personal;
        }

        private static MemoryStream ReadJsonBlob(string value,BlobStore blobs,Action<BlobReference,Stream> fetch)
        {
            var reference=BlobReference.Parse(value);blobs.FetchVerified(reference,fetch);
            var buffer=new MemoryStream((int)reference.Length);
            try{blobs.CopyVerified(value,buffer);buffer.Position=0;return buffer;}
            catch{buffer.Dispose();throw;}
        }

        private static JObject Parse(Stream buffer)
        {using(var reader=new StreamReader(buffer,new UTF8Encoding(false),true,1024,true))return JsonSafety.Object(reader);}
    }
}
