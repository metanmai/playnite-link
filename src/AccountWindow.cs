using System;
using System.Collections.Generic;
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
        private void PersonalAccount(){OpenAccountPage("Overview");}
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
                if(overviewSync!=null)overviewSync.IsEnabled=syncing==0;
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
            try{await SyncPersonalAsync(true,null);RefreshDevicesPage();}
            catch(Exception ex){NotifyError(ex);}
        }
    }
}
