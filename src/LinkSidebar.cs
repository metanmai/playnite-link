using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        UserControl CreateLinkSidebarView()
        {
            if(!EnsureAccess())return new UserControl();
            var body=new StackPanel{Margin=new Thickness(24),MaxWidth=1100,HorizontalAlignment=HorizontalAlignment.Stretch};
            Label(body,"Playnite Link",28).FontWeight=FontWeights.SemiBold;
            Label(body,"Your saves, sessions, achievements and connected devices",14,true);
            var summary=Card(body,"Cloud sync");var state=Label(summary,status,14,true);var actions=Actions(summary);
            var sync=Button(actions,IsSyncPaused?"Resume sync":"Sync now",()=>RequestSync(true));
            var pause=Button(actions,"Pause sync",StopTransfer);
            Button(actions,"Account & connection settings",PersonalAccount);
            var games=Card(body,"Your library");
            Button(games,"Games, saves & session history",ManageGames);
            Button(games,"Achievements",ShowBuiltInAchievementOverview);
            Button(games,"Achievement providers & settings",ShowAchievementSettings);
            Button(games,"Save backups",ShowSaveBackups);
            var devicesCard=Card(body,"Devices & settings");
            Button(devicesCard,"Connected devices",ManageDevices);
            Button(devicesCard,"Protection",ManageSecurity);
            Button(devicesCard,"Add-ons",ManageSetup);
            Button(devicesCard,"Export sync diagnostics",ExportSyncDiagnostics);
            var view=new UserControl{Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},Background=Canvas,Foreground=Ink};
            var updates=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
            updates.Tick+=(_,e)=>{state.Text=status;sync.IsEnabled=!IsSyncRunning;sync.Content=IsSyncPaused?"Resume sync":"Sync now";pause.IsEnabled=IsSyncRunning||!IsSyncPaused;};
            view.Loaded+=(_,e)=>updates.Start();view.Unloaded+=(_,e)=>updates.Stop();return view;
        }
    }
}
