using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private Window walkthroughWindow;
        private bool walkthroughWorking;
        private void ShowWalkthrough(){ShowWalkthrough(true);}
        private void ShowWalkthrough(bool modal)
        {
            if(!EnsureAccess())return;
            if(walkthroughWindow!=null){walkthroughWindow.Activate();return;}
            if(syncing!=0)StopTransfer();
            var window=Window("Set up Playnite Link",760,700);walkthroughWindow=window;
            var refresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
            Action render=null;TextBlock progress=null;Button run=null,stop=null,finish=null,back=null,next=null,connectionSelect=null,connectionAdvanced=null,connectionReset=null,connectionTest=null,connectionCancel=null;
            render=()=>{
                progress=null;run=null;stop=null;finish=null;back=null;next=null;connectionSelect=null;connectionAdvanced=null;connectionReset=null;connectionTest=null;connectionCancel=null;
                var step=onboarding.SetupStep;
                var names=new[]{"Welcome","Storage","Games and saves","Your choices","Ready"};
                var panel=Page(window,names[step],"Step "+(step+1)+" of 5");
                Label(panel,"Your setup choices are saved. Close this window to continue later.",13,true);
                if(step==0)
                {
                    var intro=Card(panel,"Your progress, in your own cloud","");
                    Label(intro,"Use the same cloud account and folder on every device.");
                    Label(intro,"Sync starts after setup.",14,true);
                }
                else if(step==1)
                {
                    var storage=Card(panel,String.IsNullOrWhiteSpace(config.RemoteFolder)?"Connect your storage":"Storage selected",String.IsNullOrWhiteSpace(config.RemoteFolder)?"Sign in to your provider or reuse a connection already on this device.":config.RemoteFolder);
                    var connect=Button(storage,"Choose storage or sign in",()=>{});connectionSelect=connect;
                    connect.Click+=async(_,e)=>{connect.IsEnabled=false;walkthroughWorking=true;try{await ConnectStorageAsync();}catch(OperationCanceledException){ReportStatus("Connection cancelled. Your previous connection was kept.",SyncDisplayState.Paused,"Connection cancelled");}catch(Exception ex){ReportStatus(ex.Message,SyncDisplayState.Failed,"Could not connect");}finally{walkthroughWorking=false;if(walkthroughWindow==window)render();}};
                    connectionAdvanced=Button(storage,"Advanced: existing Rclone connection",()=>{Configure();render();});
                    progress=SyncStatusView(storage);
                    var connectionActions=Actions(storage);
                    connectionTest=Button(connectionActions,"Test connection",()=>{});var testFeedback=progress;var testButton=connectionTest;
                    connectionTest.Click+=(_,e)=>TestConnection(testFeedback,testButton);
                    connectionReset=Button(connectionActions,"Reset connection",()=>{ResetConnection();render();});
                    Button(storage,"Switch vault / start fresh",()=>{SwitchVault();render();});
                    connectionCancel=Button(connectionActions,"Cancel connection",()=>{connectionCancelled=true;status="Cancelling connection… Your previous connection is unchanged.";});
                    Label(storage,"Google Drive requires a Desktop OAuth client JSON.",14,true);
                    
                }
                else if(step==2)
                {
                    var games=Card(panel,"Choose which games to sync","Choose a save folder to include saves.");
                    var automatic=Toggle(games,"Automatically include library games",onboarding.AutomaticGames);
                    automatic.Click+=(_,e)=>{onboarding.AutomaticGames=automatic.IsChecked==true;SaveOnboarding();if(onboarding.AutomaticGames)EnsureAutomaticLinks();render();};
                    if(onboarding.AutomaticGames)EnsureAutomaticLinks();
                    Label(games,onboarding.AutomaticGames?"Store games included automatically. Existing links are kept if disabled.":"Link individual games below.",13,true);
                    var mappings=repository.Snapshot().Mappings;
                    var saves=vault.State.Links.Count(p=>!String.IsNullOrWhiteSpace(p.Value.Folder));
                    Label(games,mappings.Count+" "+(mappings.Count==1?"game":"games")+" selected for playtime"+" · "+saves+" save folders selected");
                    var search=EntryField(games,"Find a game","");
                    var available=PlayniteApi.Database.Games.OrderBy(g=>g.Name).ToArray();
                    var list=new ListBox{ItemsSource=available,DisplayMemberPath="Name",Height=125,Margin=new Thickness(0,0,0,12)};StyleInput(list);games.Children.Add(list);
                    search.TextChanged+=(_,e)=>list.ItemsSource=available.Where(g=>(g.Name??"").IndexOf(search.Text,StringComparison.CurrentCultureIgnoreCase)>=0).ToArray();
                    var selection=Label(games,"Select a game to link it or choose its save folder.",13,true);
                    Action selected=()=>{var game=list.SelectedItem as Playnite.SDK.Models.Game;if(game==null)return;Mapping map;
                        if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map)){selection.Text=game.Name+" · not linked";return;}
                        SaveLink link;selection.Text=game.Name+" · linked · "+(vault.State.Links.TryGetValue(map.Key,out link)&&!String.IsNullOrWhiteSpace(link.Folder)?link.Folder:"No save folder selected");};
                    list.SelectionChanged+=(_,e)=>selected();
                    var gameActions=Actions(games);
                    Button(gameActions,"Link selected game",()=>{var game=list.SelectedItem as Playnite.SDK.Models.Game;if(game==null){selection.Text="Select a game first.";return;}if(!repository.Snapshot().Mappings.ContainsKey(game.Id.ToString()))Enroll(game);selected();});
                    Button(gameActions,"Choose save folder",()=>{
                        var game=list.SelectedItem as Playnite.SDK.Models.Game;if(game==null){selection.Text="Select a game first.";return;}
                        if(!repository.Snapshot().Mappings.ContainsKey(game.Id.ToString())){Enroll(game);if(!repository.Snapshot().Mappings.ContainsKey(game.Id.ToString()))return;}
                        var folder=PlayniteApi.Dialogs.SelectFolder();if(String.IsNullOrWhiteSpace(folder))return;
                        SetSaveFolder(game,folder);selected();
                    });
                    
                    Label(games,"Use one save-sync tool per game. Avoid managing the same folder with Steam Cloud, Save Manager or Syncthing.",13,true);
                }
                else if(step==3)
                {
                    var options=Card(panel,"Optional features","");
                    if(setup!=null){var addons=Toggle(options,"Share extension and theme installations",setup.Options.Enabled);addons.Click+=(_,e)=>{if(!UpdateSetupOptions(()=>setup.Options.Enabled=addons.IsChecked==true))addons.IsChecked=setup.Options.Enabled;};}
                    Label(options,"Settings and active theme stay local.",13,true);
                    var online=Toggle(options,"Update my online HowLongToBeat progress",onboarding.OnlineHltb);
                    online.Click+=(_,e)=>{onboarding.OnlineHltb=online.IsChecked==true;SaveOnboarding();};
                    Label(options,"Requires HowLongToBeat sign-in.",13,true);
                    Button(options,security.Enabled?"Review app passcode":"Set an optional app passcode",()=>{var protection=Window("Playnite Link — passcode",760,700);BuildProtection(protection);protection.ShowDialog();render();});
                }
                else
                {
                    var summary=Card(panel,"Ready for your first sync",config.RemoteFolder);
                    var linked=repository.Snapshot().Mappings.Count;Label(summary,linked+" "+(linked==1?"game":"games")+" selected for playtime"+" · "+vault.State.Links.Count(p=>!String.IsNullOrWhiteSpace(p.Value.Folder))+" save folders");
                    Label(summary,"Playtime: "+(devicePreferences.SyncPlaytime?"on":"off")+" · Save sync: "+(devicePreferences.SyncSaves?"on":"off"));
                    Label(summary,"Add-on sharing: "+(setup!=null&&setup.Options.Enabled?"on":"off")+" · Online HLTB: "+(onboarding.OnlineHltb?"on":"off")+" · Passcode: "+(security.Enabled?"on":"off"));
                    progress=SyncStatusView(summary);
                    var actions=Actions(summary);
                    run=Button(actions,IsSyncPaused?"Resume sync":"Start first sync",()=>{});StyleAction(run,true);
                    run.Click+=async(_,e)=>{
                        if(walkthroughWorking||syncing!=0)return;
                        walkthroughWorking=true;
                        try{onboarding.SetupComplete=true;SaveOnboarding();await SyncPersonalAsync(true,null);}
                        catch(OperationCanceledException){ReportStatus("Sync paused. Your local changes are safe. Choose Resume sync to retry.",SyncDisplayState.Paused,"Paused");}
                        catch(Exception ex){ReportStatus(ex.Message+" You can retry here; your setup is saved.",ex is CloudTransferException?SyncDisplayState.Waiting:SyncDisplayState.Failed,ex is CloudTransferException?"Waiting for cloud":"Could not sync");logger.Error(ex,"First sync can be retried.");}
                        finally{walkthroughWorking=false;if(walkthroughWindow==window)render();}
                    };
                    stop=Button(actions,"Stop transfer",StopTransfer);
                    Label(summary,"Stopping also pauses automatic sync.",13,true);
                }
                var footer=Actions(Footer(window));
                back=Button(footer,"Back",()=>{onboarding.SetupStep--;SaveOnboarding();render();});back.IsEnabled=step>0&&syncing==0;
                if(step<4)
                {
                    next=Button(footer,step==0?"Get started":"Continue",()=>{
                        if(step==1&&String.IsNullOrWhiteSpace(config.RemoteFolder)){status="Choose storage before continuing.";render();return;}
                        onboarding.SetupStep++;SaveOnboarding();render();
                    });StyleAction(next,true);next.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;
                }
                else
                {
                    finish=Button(footer,"Finish setup",()=>{onboarding.SetupComplete=true;SaveOnboarding();window.Close();if(!IsSyncPaused)QueueSync();});
                    StyleAction(finish,true);finish.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;
                }
            };
            refresh.Tick+=(_,e)=>{
                if(progress!=null)progress.Text=status;
                if(connectionSelect!=null)connectionSelect.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;
                if(connectionAdvanced!=null)connectionAdvanced.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;
                if(connectionTest!=null)connectionTest.IsEnabled=!walkthroughWorking&&!connectionTesting;
                if(connectionReset!=null)connectionReset.IsEnabled=!walkthroughWorking&&!connectionTesting;
                if(connectionCancel!=null)connectionCancel.IsEnabled=walkthroughWorking&&!connectionCancelled;
                if(back!=null)back.IsEnabled=onboarding.SetupStep>0&&syncing==0&&!walkthroughWorking&&!connectionTesting;
                if(next!=null)next.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;
                if(run!=null){run.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;run.Content=IsSyncPaused?"Resume sync":"Sync now";}
                if(stop!=null)stop.IsEnabled=syncing!=0&&!transferCancelled;
                if(finish!=null)finish.IsEnabled=syncing==0&&!walkthroughWorking&&!connectionTesting;
            };
            window.Closed+=(_,e)=>{connectionCancelled=true;refresh.Stop();walkthroughWindow=null;};
            render();refresh.Start();if(modal)window.ShowDialog();else window.Show();
        }
    }
}
