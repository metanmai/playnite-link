using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
namespace PlaytimeRclone
{
 public sealed class ConnectionResult {public string Identity,Message;public bool FolderExists;}
 public static class ConnectionProbe
 {
  const string MarkerPrefix="marker:";
  public const string LegacyConfirmation="This profile predates vault identity checks. Check the configured account and vault folder. Confirm only if this is the original vault for this profile.";
  static string Run(SyncConfig config,List<string> args,Func<bool> cancelled=null,bool allowMissing=false)
  {
   CheckCancelled(cancelled);
   if(!String.IsNullOrEmpty(config.ConfigFile)){args.Add("--config");args.Add(config.ConfigFile);}
   args.AddRange(new[]{"--retries","1","--low-level-retries","1","--contimeout","5s","--timeout","10s","--log-level","ERROR"});
   using(var process=Process.Start(new ProcessStartInfo(config.RcloneExe,String.Join(" ",args.Select(RcloneTransport.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
   {
    var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
    var watch=Stopwatch.StartNew();
    while(!process.WaitForExit(100))
     if((cancelled!=null&&cancelled())||watch.ElapsedMilliseconds>=15000)
     {
      try{process.Kill();}catch(InvalidOperationException){}
      process.WaitForExit();
      CheckCancelled(cancelled);
      throw new CloudTransferException("Vault verification timed out. Local changes remain queued.");
     }
    System.Threading.Tasks.Task.WaitAll(output,errors);
    CheckCancelled(cancelled);
    if(allowMissing&&(process.ExitCode==3||process.ExitCode==4))return null;
    if(process.ExitCode!=0)throw new CloudTransferException("Could not verify the cloud vault. Check the account sign-in and folder, then retry. Local changes remain queued.");
    return output.Result;
   }
  }
  static void CheckCancelled(Func<bool> cancelled)
  {if(cancelled!=null&&cancelled())throw new OperationCanceledException();}

  public static ConnectionResult Test(SyncConfig config,Func<bool> cancelled=null,bool allowMissingParent=false)
  {
   config.Validate();var remote=config.RemoteFolder.TrimEnd('/');var colon=remote.IndexOf(':');var relative=remote.Substring(colon+1);var slash=relative.LastIndexOf('/');
   var name=relative.Substring(slash+1);var parent=remote.Substring(0,colon+1)+(slash<0?"":relative.Substring(0,slash));
   if(name.Length==0)
   {
    var root=Run(config,new List<string>{"lsjson",remote,"--dirs-only","--max-depth","1"},cancelled,allowMissingParent);
    return new ConnectionResult{FolderExists=root!=null,Message=root==null?"The cloud root will be created.":"Connection works. Cloud root is readable."};
   }
   var folders=Run(config,new List<string>{"lsjson",parent,"--dirs-only","--max-depth","1"},cancelled,allowMissingParent);
   return ParseFolders(folders==null?new JArray():JsonSafety.Array(folders),name);
  }
  // Check the actual destination before touching its data. A directory ID
  // (including an older pin) takes precedence. Backends that do not supply
  // one, including virtual directories and remote roots, use an immutable
  // random marker. Never create a marker when checking an existing pin.
  public static string VerifyIdentity(SyncConfig config,string expected,Func<bool> cancelled=null)
  {
   config.Validate();
   var remote=config.RemoteFolder.TrimEnd('/');
   var marker=remote+(remote.EndsWith(":",StringComparison.Ordinal)?"":"/")+".playnite-link-v1-vault-id";
   var pinned=!String.IsNullOrWhiteSpace(expected);
   if(pinned&&expected.StartsWith(MarkerPrefix,StringComparison.Ordinal))
   {
    var found=ReadMarker(config,marker,cancelled);
    if(found!=expected)throw WrongVault();
    return found;
   }
   var result=Test(config,cancelled,!pinned);
   if(pinned)
   {
    if(result.Identity!=expected)throw WrongVault();
    return result.Identity;
   }
   if(result.Identity!=null)return result.Identity;
   if(!result.FolderExists)
   {
    Run(config,new List<string>{"mkdir",remote},cancelled);
    result=Test(config,cancelled,true);
    if(result.Identity!=null)return result.Identity;
   }
   var identity=ReadMarker(config,marker,cancelled);
   if(identity!=null)return identity;
   var random=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(random);
   var token=BitConverter.ToString(random).Replace("-","").ToLowerInvariant();
   var temporary=Path.GetTempFileName();
   try
   {
    File.WriteAllText(temporary,token);
    try{Run(config,new List<string>{"copyto",temporary,marker,"--checksum","--immutable"},cancelled);}
    catch(CloudTransferException)
    {
     // A peer may have installed the marker first. Adopt only the value
     // read back from storage, never assume the upload was committed.
     identity=ReadMarker(config,marker,cancelled);
     if(identity==null)throw;
     return identity;
    }
    identity=ReadMarker(config,marker,cancelled);
    if(identity==null)throw new IOException("Vault identity could not be read back. Local changes remain queued.");
    return identity;
   }
   finally{File.Delete(temporary);}
  }
  static string ReadMarker(SyncConfig config,string marker,Func<bool> cancelled)
  {
   var value=Run(config,new List<string>{"cat",marker,"--head","65"},cancelled,true);
   if(value==null)return null;
   if(!Regex.IsMatch(value,@"\A[a-f0-9]{64}\z"))throw new InvalidDataException("The selected vault has an invalid identity marker. Local changes remain queued.");
   return MarkerPrefix+Identity.Hash("rclone-vault-marker:"+value);
  }
  static Exception WrongVault()
  {return new InvalidOperationException("This is a different cloud vault. Reconnect the original account and folder. Local changes remain queued.");}

  public static void VerifyVault(Vault vault,SyncConfig config,Func<bool> cancelled=null,bool confirmLegacy=false)
  {
   var remote=(config.RemoteFolder??"").TrimEnd('/');
   var expected=vault.State.ConnectionIdentity;
   if(vault.State.Remote.Length>0)
   {
    if(vault.State.Remote!=remote)throw WrongVault();
    if(String.IsNullOrWhiteSpace(expected)&&!confirmLegacy)
     throw new InvalidOperationException("Verify the original vault from Overview → Test connection before syncing this older profile. Local changes remain queued.");
   }
   var identity=VerifyIdentity(config,expected,cancelled);
   CheckCancelled(cancelled);
   if(vault.State.Remote!=remote||expected!=identity)vault.PinVerified(remote,identity,String.IsNullOrWhiteSpace(expected)?identity:expected);
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
