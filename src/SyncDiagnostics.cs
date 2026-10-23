using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private async void ExportSyncDiagnostics()
        {
            try
            {
                var script=Path.Combine(Path.GetDirectoryName(typeof(PlaytimePlugin).Assembly.Location),"Collect-LinkDiagnostics.ps1");
                if(!File.Exists(script))throw new IOException("Diagnostic collector is missing. Reinstall the complete Link package.");
                var args="-NoProfile -ExecutionPolicy Bypass -File "+RcloneTransport.Quote(script)+" -ProfilePath "+RcloneTransport.Quote(PlayniteApi.Paths.ConfigurationPath);
                await Task.Run(()=>{using(var process=Process.Start(new ProcessStartInfo("powershell.exe",args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true})){var error=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(60000)){process.Kill();throw new IOException("Diagnostic collection timed out.");}if(process.ExitCode!=0)throw new IOException("Diagnostic collection failed. Check the active profile location.");}});
                PlayniteApi.Dialogs.ShowMessage("A privacy-filtered PlayniteLink-diagnostics ZIP has been saved in Downloads. It contains session and sync summaries, with no credentials or save files.","Playnite Link");
            }
            catch(Exception ex){NotifyError(ex);}
        }
    }
}
