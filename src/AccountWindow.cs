using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private Window accountWindow;
        private string accountSection;
        private readonly Dictionary<string,object> accountPages=new Dictionary<string,object>();
        private TextBlock overviewStatus,overviewHeading;
        private Button overviewStop;
        private Button overviewSync;
        private void PersonalAccount(){if(!onboarding.SetupComplete)ShowWalkthrough();else OpenAccountPage("Overview");}
        private void ManageDevices(){OpenAccountPage("Devices");}
        private void ManageSecurity(){if(EnsureAccess())OpenAccountPage("Protection");}
        private void OpenAccountPage(string section)
        {
            if(accountWindow!=null){ShowAccountPage(section);accountWindow.Activate();return;}
            var window=Window("Playnite Link",860,780);accountWindow=window;
            window.KeyDown+=(_,e)=>{if(!e.Handled&&e.Key==System.Windows.Input.Key.Escape){e.Handled=true;window.Close();}};
            var updates=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
            updates.Tick+=(_,e)=>{
                if(overviewStatus!=null)overviewStatus.Text=status;
                if(overviewHeading!=null)overviewHeading.Text=syncing!=0?"Syncing in the background":status.IndexOf("throttl",StringComparison.OrdinalIgnoreCase)>=0?"Waiting for your cloud provider":String.IsNullOrWhiteSpace(config.RemoteFolder)?"Connect your cloud":"Storage configured";
                if(overviewSync!=null){overviewSync.IsEnabled=syncing==0;overviewSync.Content=IsSyncPaused?"Resume sync":"Sync now";}
                if(overviewStop!=null)overviewStop.IsEnabled=syncing!=0;
                if(setupStatusText!=null)setupStatusText.Text=setupStatus;
                if(activityStatusText!=null)activityStatusText.Text=activityStatus;
            };
            window.Closed+=(_,e)=>{updates.Stop();accountPages.Clear();accountWindow=null;accountSection=null;overviewStatus=null;overviewHeading=null;overviewSync=null;overviewStop=null;};
            ShowAccountPage(section);updates.Start();window.ShowDialog();
        }
        private void ShowAccountPage(string section)
        {
            object page;
            if(accountPages.TryGetValue(section,out page)){accountWindow.Content=page;accountSection=section;return;}
            if(section=="Overview")BuildOverview(accountWindow);
            else if(section=="Devices")BuildDevices(accountWindow);
            else if(section=="Protection")BuildProtection(accountWindow);
            else BuildSetup(accountWindow);
            accountPages[section]=accountWindow.Content;accountSection=section;
        }
        private void RefreshOverviewPage()
        {
            accountPages.Remove("Overview");overviewStatus=null;overviewHeading=null;overviewSync=null;overviewStop=null;
            if(accountWindow!=null&&accountSection=="Overview")ShowAccountPage("Overview");
        }
        private void RefreshDevicesPage()
        {
            accountPages.Remove("Devices");
            if(accountWindow!=null&&accountSection=="Devices")ShowAccountPage("Devices");
        }
        private async void RefreshDevices()
        {
            if(refreshingDevices)return;
            refreshingDevices=true;deviceRefreshStatus=syncing!=0?"Waiting for the current sync before refreshing devices…":"Checking your cloud for device updates…";RefreshDevicesPage();
            try
            {
                await RefreshDeviceDirectoryAsync();
                deviceRefreshStatus="Devices refreshed at "+DateTime.Now.ToString("T")+". "+deviceViewCache.Length+" devices received.";
            }
            catch(OperationCanceledException){deviceRefreshStatus="Device refresh stopped. The last received list is still shown.";}
            catch(Exception ex){deviceRefreshStatus="Could not refresh devices: "+ex.Message;logger.Error(ex,"Device list refresh failed.");}
            finally{refreshingDevices=false;RefreshDevicesPage();}
        }
        private System.Threading.Tasks.Task RefreshDeviceDirectoryAsync()
        {
            if(vaultSwitchPending)throw new InvalidOperationException("Restart Playnite to finish switching vaults before refreshing devices.");
            var generation=System.Threading.Volatile.Read(ref transferGeneration);
            return System.Threading.Tasks.Task.Run(async()=>{
                await syncGate.WaitAsync();
                try
                {
                    if(closing||generation!=System.Threading.Volatile.Read(ref transferGeneration))throw new OperationCanceledException();
                    System.Threading.Interlocked.Exchange(ref syncing,1);
                    var captured=config;
                    if(String.IsNullOrWhiteSpace(captured.RemoteFolder))throw new InvalidOperationException("Connect cloud storage from Overview first.");
                    CheckActiveGameVault(captured);
                    var cache=System.IO.Path.Combine(Root,"cloud-cache","metadata","devices","objects");System.IO.Directory.CreateDirectory(cache);
                    new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/devices/objects",cancelled:()=>closing||vaultSwitchPending||generation!=System.Threading.Volatile.Read(ref transferGeneration)).Pull(cache);
                    if(closing||generation!=System.Threading.Volatile.Read(ref transferGeneration))throw new OperationCanceledException();
                    devices.Import(cache);deviceViewCache=devices.Views().ToArray();
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref syncing,0);syncGate.Release();
                    if(System.Threading.Interlocked.Exchange(ref queuedSync,0)!=0&&!closing&&!IsSyncPaused&&!transferCancelled)QueuePersonalSync();
                }
            });
        }
    }
}
