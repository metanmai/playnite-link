using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace PlaytimeRclone
{
 public sealed class ConnectionResult {public string Identity,Message;public bool FolderExists;}
 public static class ConnectionProbe
 {
  static string Run(SyncConfig config,List<string> args)
  {
   if(!String.IsNullOrEmpty(config.ConfigFile)){args.Add("--config");args.Add(config.ConfigFile);}
   args.AddRange(new[]{"--retries","1","--low-level-retries","1","--contimeout","5s","--timeout","10s","--log-level","ERROR"});
   using(var process=Process.Start(new ProcessStartInfo(config.RcloneExe,String.Join(" ",args.Select(RcloneTransport.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
   {
    var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
    if(!process.WaitForExit(15000)){process.Kill();process.WaitForExit();throw new IOException("Connection test timed out. Your previous connection is unchanged. Try again.");}
    System.Threading.Tasks.Task.WaitAll(output,errors);
    if(process.ExitCode!=0)throw new IOException("Could not access the selected cloud folder. Check the account sign-in, folder and provider availability, then retry. Your previous connection is unchanged.");
    return output.Result;
   }
  }
  public static ConnectionResult Test(SyncConfig config)
  {
   config.Validate();var remote=config.RemoteFolder.TrimEnd('/');var colon=remote.IndexOf(':');var relative=remote.Substring(colon+1);var slash=relative.LastIndexOf('/');
   var name=relative.Substring(slash+1);var parent=remote.Substring(0,colon+1)+(slash<0?"":relative.Substring(0,slash));
   if(name.Length==0){Run(config,new List<string>{"lsjson",remote,"--dirs-only","--max-depth","1"});return new ConnectionResult{FolderExists=true,Message="Connection works. Cloud root is readable."};}
   var folders=JArray.Parse(Run(config,new List<string>{"lsjson",parent,"--dirs-only","--max-depth","1"}));
   return ParseFolders(folders,name);
  }
  public static ConnectionResult ParseFolders(JArray folders,string name)
  {
   var matches=folders.Where(f=>(string)f["Name"]==name&&(bool?)f["IsDir"]==true).ToArray();
   if(matches.Length>1)throw new IOException("More than one cloud folder has this name. Choose an unambiguous vault folder before connecting.");
   if(matches.Length==0)return new ConnectionResult{FolderExists=false,Message="Connection works. The vault folder will be created on first sync. No files were uploaded."};
   var id=(string)matches[0]["ID"];
   return new ConnectionResult{FolderExists=true,Identity=String.IsNullOrWhiteSpace(id)?null:Identity.Hash("rclone-directory:"+id),Message="Connection works. Your vault folder is accessible. No files were uploaded."};
  }
 }
}
