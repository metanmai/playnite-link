using System;using System.IO;using System.Linq;using Newtonsoft.Json;
namespace PlaytimeRclone
{
 public sealed class CloudTransferException:IOException {public CloudTransferException(string message):base(message){}}
 public sealed class MetadataTransferProgress {public int Cached,Downloaded;public double ElapsedSeconds;}
 public sealed class TransferWatchdog
 {
  readonly double started,firstGrace,idleLimit,maximum;double lastProgress;int previous;
  public TransferWatchdog(double now,int initial,double firstGrace=45,double idleLimit=30,double maximum=900){started=lastProgress=now;previous=initial;this.firstGrace=firstGrace;this.idleLimit=idleLimit;this.maximum=maximum;}
  public string Observe(double now,int completed){if(completed>previous){lastProgress=now;previous=completed;}if(now-started>=maximum)return "This background history download reached its work limit. Downloaded records are cached; the next retry resumes them.";if(now-lastProgress>=(lastProgress==started?firstGrace:idleLimit))return "The cloud stopped delivering history records. Downloaded records are cached; retry will resume them.";return null;}
 }
 public static class MetadataCache
 {
  public static readonly string[] Areas={"devices","ledger","activity","revisions"};
  public static int Count(string root){return Areas.Sum(a=>{var p=Path.Combine(root,a,"objects");return Directory.Exists(p)?Directory.GetFiles(p,"*.json").Length:0;});}
  static void ValidateObject(string file,string area)
  {
var length=new FileInfo(file).Length;if(length>(area=="devices"?16L*1024*1024:area=="revisions"?64L*1024*1024:16384))throw new InvalidDataException("Oversized cached record.");var raw=File.ReadAllText(file);string hash;var options=new JsonSerializerSettings{MissingMemberHandling=MissingMemberHandling.Error};
     if(area=="devices"){var value=JsonSafety.Deserialize<DeviceEvent>(raw,options);if(value==null)throw new InvalidDataException();value.Validate();hash=value.Hash();}
     else if(area=="revisions"){var value=JsonSafety.Deserialize<Revision>(raw,options);if(value==null)throw new InvalidDataException();value.Validate();hash=value.Hash();}
     else{var value=JsonSafety.Deserialize<Entry>(raw,options);if(value==null)throw new InvalidDataException();value.Validate();hash=value.Hash();}
     if(Path.GetFileNameWithoutExtension(file)!=hash)throw new InvalidDataException("Cached hash mismatch.");

  }
  public static int Seed(string source,string destination)
  {
   int copied=0;foreach(var area in Areas){var from=Path.Combine(source,area,"objects");if(!Directory.Exists(from))continue;var to=Path.Combine(destination,area,"objects");Directory.CreateDirectory(to);foreach(var file in Directory.GetFiles(from,"*.json")){var target=Path.Combine(to,Path.GetFileName(file));if(File.Exists(target))continue;string raw;try{ValidateObject(file,area);raw=File.ReadAllText(file);}catch(Exception ex){if(ex is IOException||ex is JsonException||ex is ArgumentException||ex is FormatException)continue;throw;}Atomic.Write(target,raw);copied++;}}return copied;
  }
  public static int Validate(string root)
  {
   int repaired=0;foreach(var area in Areas){var directory=Path.Combine(root,area,"objects");Directory.CreateDirectory(directory);foreach(var file in Directory.GetFiles(directory,"*.json")){
    try{ValidateObject(file,area);    }catch(Exception ex){if(!(ex is JsonException)&&!(ex is InvalidDataException)&&!(ex is ArgumentException)&&!(ex is FormatException)&&!(ex is OverflowException))throw;var quarantine=Path.Combine(root,"invalid-cache");Directory.CreateDirectory(quarantine);File.Move(file,Path.Combine(quarantine,Guid.NewGuid().ToString("N")+".json"));repaired++;}
   }}return repaired;
  }
 }
}
