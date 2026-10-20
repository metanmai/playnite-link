using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class SyncConfig
    {
        public string RcloneExe = "";
        public string ConfigFile = "";
        public string RemoteFolder = "";
        public void Validate()
        {
            if (!Path.IsPathRooted(RcloneExe) || !File.Exists(RcloneExe) || !RcloneExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose the absolute path to rclone.exe.");
            if (ConfigFile.Length > 0 && (!Path.IsPathRooted(ConfigFile) || !File.Exists(ConfigFile)))
                throw new ArgumentException("Rclone config file must exist, or leave blank for the default config.");
            if (!Regex.IsMatch(RemoteFolder ?? "", "^[A-Za-z0-9][A-Za-z0-9_-]*:[^:]*$") ||
                RemoteFolder.Any(c => c < 32 || c == '"' || c == '\\') ||
                RemoteFolder.Split('/').Any(x => x == ".." || x == ".") || RemoteFolder.Contains("//"))
                throw new ArgumentException("Use a configured Rclone remote such as cloud:Playnite. No local path or command options.");
        }
    }
    public interface ITransport { void Pull(string destination); void Push(string source); }
    public sealed class RcloneTransport : ITransport
    {
        public static Action<string> FailureDiagnostic;
        private readonly string exe, config, root,pattern;
        private readonly int limitSeconds,bandwidthKiB;
        private readonly Func<bool> cancelled;
        public RcloneTransport(string exe, string config, string root, string area = "playnite-link-v1/ledger/objects",string pattern="*.json",int limitSeconds=105,int bandwidthKiB=0,Func<bool> cancelled=null)
        { this.exe = exe; this.config = config; this.root = root.TrimEnd('/', '\\') + "/" + area;this.pattern=pattern;this.limitSeconds=limitSeconds;this.bandwidthKiB=bandwidthKiB;this.cancelled=cancelled; }
        // Windows argv quoting; never a shell, and no arbitrary extra flags.
        public static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        private void Copy(string source, string destination, bool upload,string fileList=null,string[] metadata=null)
        {
            if(cancelled!=null&&cancelled())throw new OperationCanceledException("Cloud transfer cancelled; local changes remain queued.");
            var args = new List<string> { "copy", source, destination, "--immutable",
                "--retries", "3", "--low-level-retries", "4", "--retries-sleep", "10s", "--tpslimit", "4", "--tpslimit-burst", "4", "--contimeout", "10s", "--timeout", "60s", "--stats", "0", "--log-level", "ERROR" };
            if(pattern=="*.blob"){args.Add("--ignore-existing");}else args.Add("--checksum");
            if(fileList!=null){args.Add("--files-from");args.Add(fileList);args.Add("--no-traverse");}
            else if(metadata!=null){foreach(var area in metadata){args.Add("--include");args.Add("/"+area+"/objects/*.json");}args.Add("--max-depth");args.Add("3");}
            else {args.Add("--include");args.Add("/"+pattern);args.Add("--max-depth");args.Add("1");}
            if(bandwidthKiB>0){args.Add("--bwlimit");args.Add(bandwidthKiB.ToString(System.Globalization.CultureInfo.InvariantCulture)+"k");}
            if (config.Length > 0) { args.Add("--config"); args.Add(config); }
            // Directory absence is an empty ledger on first use, not an arbitrary ignored failure.
            if (!upload) args.Add("--error-on-no-transfer");
            var info = new ProcessStartInfo(exe, String.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(info))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                var watch=Stopwatch.StartNew();
                while(!process.WaitForExit(250))
                    if((cancelled!=null&&cancelled())||watch.Elapsed.TotalSeconds>limitSeconds)
                    {process.Kill();process.WaitForExit();if(cancelled!=null&&cancelled())throw new OperationCanceledException("Cloud transfer cancelled; local changes remain queued.");throw new IOException("Cloud transfer timed out. Local changes remain queued.");}
                System.Threading.Tasks.Task.WaitAll(stdout, stderr);
                if(cancelled!=null&&cancelled())throw new OperationCanceledException("Cloud transfer cancelled; local changes remain queued.");
                // rclone exit 3 = directory missing, 9 = no transfer. Other errors always fail closed.
                if (process.ExitCode != 0 && !( !upload && (process.ExitCode == 3 || process.ExitCode == 9)))
                {
                    if(FailureDiagnostic!=null)FailureDiagnostic(stderr.Result);
                    if(stderr.Result.IndexOf("quota",StringComparison.OrdinalIgnoreCase)>=0||stderr.Result.IndexOf("rate limit",StringComparison.OrdinalIgnoreCase)>=0)
                        throw new IOException("The cloud provider is throttling this Rclone connection. Local changes are queued; retry after the provider's quota window resets.");
                    throw new IOException("Rclone failed (exit " + process.ExitCode + "). Check remote access/configuration. Local sessions remain queued.");
                }
            }
        }
        public void Pull(string destination) { Copy(root, destination, false); }
        public void PullMetadata(string destination)
        {Copy(root,destination,false,null,new[]{"devices","ledger","activity","revisions"});}
        public void Push(string source) { Copy(source, root, true); }
        public void PullFiles(string destination,IEnumerable<string> names)
        {
            var list=names.Distinct().ToList();if(list.Count==0)return;
            if(list.Any(n=>!Regex.IsMatch(n,"^[a-f0-9]{64}\\.blob$")))throw new InvalidDataException("Unsafe blob transfer path.");
            Directory.CreateDirectory(destination);var path=Path.Combine(destination,Guid.NewGuid().ToString("N")+".files.tmp");
            try{File.WriteAllLines(path,list,new UTF8Encoding(false));Copy(root,destination,false,path);}finally{if(File.Exists(path))File.Delete(path);}
        }
        public void PushFiles(string source,IEnumerable<string> names)
        {
            var list=names.Distinct().ToList();if(list.Count==0)return;
            if(list.Any(n=>!Regex.IsMatch(n,"^[a-f0-9]{64}\\.(blob|json)$")))throw new InvalidDataException("Unsafe immutable upload path.");
            var path=Path.Combine(source,Guid.NewGuid().ToString("N")+".files.tmp");
            try{File.WriteAllLines(path,list,new UTF8Encoding(false));Copy(source,root,true,path);}finally{if(File.Exists(path))File.Delete(path);}
        }
    }
    public sealed class SyncEngine
    {
        public static List<Entry> ReadObjects(string directory)
        {
            var records = new List<Entry>();
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Oversized cloud record.");
                var raw = File.ReadAllText(path);
                var entry = JsonConvert.DeserializeObject<Entry>(raw, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
                if (entry == null) throw new InvalidDataException("Null cloud record.");
                entry.Validate();
                if (Path.GetFileNameWithoutExtension(path) != entry.Hash()) throw new InvalidDataException("Cloud record hash mismatch. Sync stopped.");
                records.Add(entry);
            }
            return new Ledger(records).Entries;
        }
        public void Run(Repository repository, ITransport transport, string scratch, string cache = null,bool activity=false,bool pull=true)
        {
            var run = Path.Combine(scratch, Guid.NewGuid().ToString("N").Substring(0, 12));
            var incoming = cache ?? Path.Combine(run, "i"); var outgoing = Path.Combine(run, "o");
            Directory.CreateDirectory(incoming); Directory.CreateDirectory(outgoing);
            try
            {
                if(pull)transport.Pull(incoming);
                repository.Merge(ReadObjects(incoming));
                foreach (var entry in repository.Snapshot().Entries)
                    if(activity?(entry.Kind=="history"||entry.Count.HasValue):(entry.Kind!="history"&&!entry.Count.HasValue))
                    if(!File.Exists(Path.Combine(incoming,entry.Hash()+".json")))
                        File.WriteAllText(Path.Combine(outgoing, entry.Hash() + ".json"), entry.Canonical(), new UTF8Encoding(false));
                if(Directory.GetFiles(outgoing,"*.json").Length>0)transport.Push(outgoing);
            }
            finally { Directory.Delete(run, true); }
        }
    }
}


