using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using PlaytimeRclone;

internal static class LiveSetup
{
    public static void Main(string[] args)
    {
        var dataRoot=args[0];var seedRoot=args[1];var exe=args[2];var config=args[3];var remote=args[4];Directory.CreateDirectory(dataRoot);Directory.CreateDirectory(seedRoot);
        var gameId=Guid.NewGuid().ToString();var source="manual:cloud-demo-"+Guid.NewGuid().ToString("N").Substring(0,8);var key=Identity.Hash(source);var peer=Guid.NewGuid().ToString("N");string me;
        using(var repository=new Repository(dataRoot)){me=repository.Snapshot().Device;repository.Enroll(gameId,source,100,100);}
        var work=Path.Combine(dataRoot,"CloudDemo");var saves=Path.Combine(work,"DemoSaves");Directory.CreateDirectory(saves);File.WriteAllText(Path.Combine(saves,"progress.dat"),"baseline");
        var blobs=new BlobStore(Path.Combine(dataRoot,"blobs"));var vault=new Vault(dataRoot);var a=new Revision{Version=2,Kind="save",Game=key,Device=me,Data=blobs.Capture(saves)};vault.Add(a);
        var preferences=new SortedDictionary<string,string>(StringComparer.Ordinal){{"Favorite","False"},{"Hidden","False"},{"Notes",""}};var pa=new Revision{Kind="preferences",Game=key,Device=me,Data=preferences};vault.Add(pa);
        vault.State.Links[key]=new SaveLink{Folder=saves,Save=new Checkpoint{Revision=a.Hash(),Content=a.ContentHash()},Preferences=new Checkpoint{Revision=pa.Hash(),Content=pa.ContentHash()}};vault.Pin(remote);
        var seedBlobs=new BlobStore(Path.Combine(seedRoot,"blobs"));seedBlobs.Capture(saves);var remoteSaves=Path.Combine(seedRoot,"peer-saves");Directory.CreateDirectory(remoteSaves);File.WriteAllText(Path.Combine(remoteSaves,"progress.dat"),"cloud-before-launch");
        var seedVault=new Vault(Path.Combine(seedRoot,"vault"));seedVault.Add(a);seedVault.Add(pa);seedVault.Add(new Revision{Version=2,Kind="save",Game=key,Device=peer,Data=seedBlobs.Capture(remoteSaves),Parents=new List<string>{a.Hash()}});
        var remotePreferences=new SortedDictionary<string,string>(preferences,StringComparer.Ordinal);remotePreferences["Favorite"]="True";remotePreferences["Notes"]="From peer using real Google Drive";
        seedVault.Add(new Revision{Kind="preferences",Game=key,Device=peer,Data=remotePreferences,Parents=new List<string>{pa.Hash()}});
        var devices=new DeviceDirectory(seedRoot);devices.PublishProfile(peer,new DevicePreferences{Name="Cloud test peer (simulated)"},new List<DeviceGame>{new DeviceGame{Key=key,Name="Playnite Link Cloud Demo",SavesEnabled=true}});
        devices.SetSettings(peer,me,new DevicePreferences{Name=Environment.MachineName,IntervalSeconds=600});
        File.WriteAllText(Path.Combine(dataRoot,"rclone-settings.json"),JsonConvert.SerializeObject(new SyncConfig{RcloneExe=exe,ConfigFile=config,RemoteFolder=remote},Formatting.Indented));
        File.WriteAllText(Path.Combine(dataRoot,"live-probe-ticket.json"),JsonConvert.SerializeObject(new{GameId=gameId,Key=key,Peer=peer,Me=me,Work=work,Remote=remote,Exe=exe,Config=config,SeedRoot=seedRoot},Formatting.Indented));
        Console.WriteLine("Live Playnite fixture prepared; uploading only synthetic saves.");
        new RcloneTransport(exe,config,remote,"playnite-link-v1/blobs","*.blob",1800).Push(seedBlobs.DirectoryPath);
        new RcloneTransport(exe,config,remote,"playnite-link-v1/revisions/objects",limitSeconds:600).Push(seedVault.Objects);
        new RcloneTransport(exe,config,remote,"playnite-link-v1/devices/objects",limitSeconds:600).Push(devices.Objects);
        File.WriteAllText(Path.Combine(dataRoot,"live-probe-seed-ready"),"ready");Console.WriteLine("Real Google Drive seed uploaded.");
    }
}
