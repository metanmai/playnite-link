using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
namespace PlaytimeRclone
{
 public sealed class VaultProfile {public string Id,Directory,Remote;public override string ToString(){return (Id=="legacy"?"Original vault":Id.Substring(0,8))+" · "+(String.IsNullOrWhiteSpace(Remote)?"Not connected":Remote);}}
 public static class VaultProfiles
 {
  static string Pointer(string root){return Path.Combine(root,"active-vault-profile.json");}
  public static string Resolve(string root)
  {
   if(!File.Exists(Pointer(root)))return root;
   var id=JsonSafety.Deserialize<string>(File.ReadAllText(Pointer(root)));
   if(id=="legacy")return root;
   Guid parsed;if(!Guid.TryParseExact(id,"N",out parsed))throw new InvalidDataException("Invalid vault profile selection. Existing history was kept.");
   var path=Path.Combine(root,"vault-profiles",id);
   if(!File.Exists(Path.Combine(path,"state.json"))||!File.Exists(Path.Combine(path,"profile-ready.json")))throw new InvalidDataException("Selected vault profile is incomplete. Existing history was kept.");
   if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked vault profile directories are not supported.");
   return path;
  }
  public static VaultProfile[] List(string root)
  {
   var paths=new[]{root}.Concat(Directory.Exists(Path.Combine(root,"vault-profiles"))?Directory.GetDirectories(Path.Combine(root,"vault-profiles")):new string[0]);
   return paths.Where(p=>File.Exists(Path.Combine(p,"state.json"))&&(p==root||File.Exists(Path.Combine(p,"profile-ready.json")))).Select(p=>{var cfg=Path.Combine(p,"rclone-settings.json");var c=File.Exists(cfg)?JsonSafety.Deserialize<SyncConfig>(File.ReadAllText(cfg)):null;return new VaultProfile{Id=p==root?"legacy":Path.GetFileName(p),Directory=p,Remote=c==null?"":c.RemoteFolder};}).ToArray();
  }
  public static VaultProfile Prepare(string root,SyncConfig previous)
  {
   var id=Guid.NewGuid().ToString("N");var path=Path.Combine(root,"vault-profiles",id);Directory.CreateDirectory(path);
   using(var repository=new Repository(path)){}
   new Vault(path);
   Atomic.Write(Path.Combine(path,"rclone-settings.json"),JsonConvert.SerializeObject(new SyncConfig{RcloneExe=previous.RcloneExe,ConfigFile=previous.ConfigFile,RemoteFolder=""}));
   Atomic.Write(Path.Combine(path,"onboarding.json"),JsonConvert.SerializeObject(new OnboardingOptions{SetupStep=1,SyncPaused=true,AutomaticGames=false}));
   Atomic.Write(Path.Combine(path,"profile-ready.json"),JsonConvert.SerializeObject(DateTimeOffset.UtcNow));
   return new VaultProfile{Id=id,Directory=path,Remote=""};
  }
  public static void Activate(string root,VaultProfile profile)
  {
   var expected=profile.Id=="legacy"?root:Path.Combine(root,"vault-profiles",profile.Id);
   if(!String.Equals(Path.GetFullPath(expected),Path.GetFullPath(profile.Directory),StringComparison.OrdinalIgnoreCase)||!List(root).Any(p=>p.Id==profile.Id))throw new InvalidDataException("Vault profile is not available.");
   Atomic.Write(Pointer(root),JsonConvert.SerializeObject(profile.Id));
  }
 }
}
