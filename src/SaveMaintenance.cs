using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlaytimeRclone
{
    public static class VaultMaintenance
    {
        public static string ProtocolArea(string remote)
        {
            if(String.IsNullOrWhiteSpace(remote)||remote.IndexOf(':')<1||remote.StartsWith("-",StringComparison.Ordinal)||remote.Any(c=>c<32)||remote.Contains("\""))throw new ArgumentException("Choose a configured cloud vault.");
            return remote.TrimEnd('/','\\')+"/playnite-link-v1";
        }
        public static void Purge(SyncConfig config)
        {
            config.Validate();var target=ProtocolArea(config.RemoteFolder);
            var args="purge "+RcloneTransport.Quote(target)+" --retries 1 --low-level-retries 1"+(String.IsNullOrWhiteSpace(config.ConfigFile)?"":" --config "+RcloneTransport.Quote(config.ConfigFile));
            using(var process=Process.Start(new ProcessStartInfo(config.RcloneExe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}))
            {
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(300000)){process.Kill();process.WaitForExit();throw new IOException("Vault erasure timed out. Keep devices paused and review the vault before retrying.");}
                Task.WaitAll(output,error);if(process.ExitCode!=0)throw new IOException("Cloud vault erasure did not finish (Rclone exit "+process.ExitCode+"). Local data remains intact; keep devices paused.");
            }
        }
    }
    public sealed partial class PlaytimePlugin
    {
        private void ResetLocalSaveManager()
        {
            if(syncing!=0||repository.Snapshot().Active.Count>0)throw new IOException("Stop games and wait for sync first.");
            if(PlayniteApi.Dialogs.ShowMessage("Reset local ZIP backups and save-path configurations? Live game saves and playtime are kept. A verified recovery bundle is created first. Cloud history is retained and sync is paused.","Reset save manager",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            var bundle=Path.Combine(Root,"save-manager-reset-"+Guid.NewGuid().ToString("N")+".zip");Network("Preserving a recovery bundle…",()=>ExportSaveBundle(bundle));
            ResetLocalSaveManagerCore(bundle);
            PlayniteApi.Dialogs.ShowMessage("Save manager reset. Recovery bundle: "+bundle+"\nSync is paused. Reconnecting to the same vault can show its shared history again.","Playnite Link");
        }
        private void ResetLocalSaveManagerCore(string recoveryBundle)
        {
            if(!File.Exists(recoveryBundle))throw new IOException("Create the recovery bundle before resetting local backups.");
            var archived=new System.Collections.Generic.Dictionary<string,string>(StringComparer.Ordinal);
            using(var file=File.OpenRead(recoveryBundle))using(var zip=new System.IO.Compression.ZipArchive(file,System.IO.Compression.ZipArchiveMode.Read))
            {
                var manifest=SaveMigrationFiles.BundleManifest(zip);if((int?)manifest["Version"]!=1)throw new IOException("Unsupported recovery bundle.");
                foreach(var game in manifest["Games"])foreach(var backup in game["Backups"])
                {var entries=zip.Entries.Where(e=>e.FullName==(string)backup["Path"]).ToList();if(entries.Count!=1)throw new IOException("Incomplete recovery bundle.");using(var input=entries[0].Open())using(var hash=System.Security.Cryptography.SHA256.Create()){var actual=BitConverter.ToString(hash.ComputeHash(input)).Replace("-","").ToLowerInvariant();if(actual!=(string)backup["Sha256"])throw new IOException("Recovery bundle checksum mismatch.");archived[Path.GetFileNameWithoutExtension(entries[0].FullName)]=actual;}}
            }
            if(!syncGate.Wait(0))throw new IOException("A sync started. Retry reset after it finishes; the recovery bundle was kept.");
            try
            {
                if(repository.Snapshot().Active.Count>0)throw new IOException("Stop games before resetting save management.");
                foreach(var config in Backups.GetAllGameConfigs())foreach(var backup in Backups.GetBackupsByConfigId(config.ConfigId))
                {string hash;if(!archived.TryGetValue(backup.Id,out hash)||hash!=SaveMigrationFiles.Hash(Backups.GetFullBackupPath(backup.BackupFilePath)))throw new IOException("Backups changed after export. Create a new recovery bundle before resetting.");}
                StopTransfer();
                foreach(var config in Backups.GetAllGameConfigs().ToList())Backups.DeleteGameConfig(config.ConfigId);
                foreach(var link in vault.State.Links.Values){link.Folder=null;if(link.Sources!=null)link.Sources.Clear();if(link.RestoreExclusions!=null)link.RestoreExclusions.Clear();link.Save=new Checkpoint();}vault.Save();
                backupService=null;
            }
            finally{syncGate.Release();}
        }
        private void EraseCloudVault()
        {
            if(syncing!=0||repository.Snapshot().Active.Count>0)throw new IOException("Stop games and wait for sync first.");
            if(String.IsNullOrWhiteSpace(config.RemoteFolder))throw new IOException("Connect the intended vault first.");
            var target=VaultMaintenance.ProtocolArea(config.RemoteFolder);
            if(PlayniteApi.Dialogs.ShowMessage("Permanently erase ALL shared saves, backup archives, sessions, preferences, devices and add-on history under:\n"+target+"\n\nPause every other device first. Local saves and local history are kept, but un-downloaded cloud history may be lost. Other clients can upload data again. Export needed backups before proceeding.","Erase cloud vault",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            if(Ask("Type the exact vault destination to confirm permanent erasure:\n"+target,"")!=target)return;
            if(!syncGate.Wait(0))throw new IOException("A sync started. Try again when it finishes.");
            try
            {
                StopTransfer();Network("Verifying and erasing the selected vault…",()=>{CheckActiveGameVault(config);VaultMaintenance.Purge(config);});
                PlayniteApi.Dialogs.ShowMessage("Cloud vault erased. Sync stays paused. Local history can be uploaded again if you resume; use Switch vault / start fresh for a new empty profile.","Playnite Link");
            }
            finally
            {
                // Immutable-cache assumptions no longer apply after explicit erasure,
                // even when the provider reported a partial failure.
                try
                {
                    var owned=Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                    foreach(var path in new[]{Path.Combine(Root,"cloud-cache","metadata"),Path.Combine(Root,"setup","incoming")})if(Directory.Exists(path))
                    {var source=Path.GetFullPath(path);var destination=source+".before-erase-"+Guid.NewGuid().ToString("N");if(!source.StartsWith(owned,StringComparison.OrdinalIgnoreCase)||!destination.StartsWith(owned,StringComparison.OrdinalIgnoreCase))throw new IOException("Cache archive path escaped this profile.");SaveFiles.CheckAncestors(source);SaveFiles.CheckAncestors(destination);Directory.Move(source,destination);}
                }
                finally{syncGate.Release();}
            }
        }
    }
}
