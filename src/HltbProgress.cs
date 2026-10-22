using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public interface IHltbProgress
    {
        Task<object> Read();
        ulong Seconds(object edit);
        Task<bool> Write(object edit,ulong seconds);
    }
    public sealed class HltbProgressReceipt
    {
        public ulong Desired;
        public DateTime Verified;
    }
    public sealed class HltbProgress
    {
        readonly string path;
        readonly Dictionary<string,HltbProgressReceipt> receipts;
        public HltbProgress(string root)
        {
            path=Path.Combine(root,"hltb-progress.json");
            receipts=File.Exists(path)?JsonSafety.Deserialize<Dictionary<string,HltbProgressReceipt>>(File.ReadAllText(path)):new Dictionary<string,HltbProgressReceipt>();
            if(receipts==null)throw new InvalidDataException("Invalid HLTB upload receipts.");
        }
        // Absolute progress is idempotent after a timeout/crash. Always read the
        // existing submission first and preserve any greater online progress.
        // Periodic verification also repairs a simultaneous stale-device write.
        public async Task<bool> Update(string identity,ulong desired,IHltbProgress api,DateTime now)
        {
            HltbProgressReceipt receipt;
            if(receipts.TryGetValue(identity,out receipt)&&receipt.Desired==desired&&now>=receipt.Verified&&now-receipt.Verified<TimeSpan.FromMinutes(10))return false;
            var edit=await api.Read();if(edit==null)throw new IOException("HLTB could not load the existing profile entry. Progress remains queued.");
            var current=api.Seconds(edit);
            if(current<desired)
            {
                if(!await api.Write(edit,desired))throw new IOException("HLTB did not confirm the progress update. It will be retried.");
                var verified=await api.Read();
                if(verified==null||api.Seconds(verified)<desired)throw new IOException("HLTB progress could not be verified. It will be checked again.");
            }
            receipts[identity]=new HltbProgressReceipt{Desired=desired,Verified=now};
            Atomic.Write(path,JsonConvert.SerializeObject(receipts));return current<desired;
        }
    }
}
