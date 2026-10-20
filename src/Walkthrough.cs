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
        private void ShowWalkthrough()
        {
            if(!EnsureAccess())return;
            if(walkthroughWindow!=null){walkthroughWindow.Activate();return;}
            if(syncing!=0)StopTransfer();
            var window=Window("Set up Playnite Link",760,700);walkthroughWindow=window;
            var refresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
            Action render=null;TextBlock progress=null;Button run=null,stop=null,finish=null,back=null,next=null;
            render=()=>{
                progress=null;run=null;stop=null;finish=null;back=null;next=null;
                var step=onboarding.SetupStep;
                var names=new[]{"Welcome","Storage","Games and saves","Your choices","Ready"};
                var panel=Page(window,names[step],"Step "+(step+1)+" of 5 · Set up once on each device. Your choices are saved as you go.");
                if(step==0)
                {
                    var intro=Card(panel,"Your progress, in your own cloud","Connect storage, choose games and save folders, then review your first sync.");
                    Label(intro,"Use the same storage account and vault folder on every device. No Playnite Link account is required.");
                    Label(intro,"You can close setup and continue later. Nothing syncs automatically until you finish.",14,true);
                }
                else if(step==1)
                {
                    var storage=Card(panel,String.IsNullOrWhiteSpace(config.RemoteFolder)?"Connect your storage":"Storage selected",String.IsNullOrWhiteSpace(config.RemoteFolder)?"Sign in to your provider or reuse a connection already on this device.":config.RemoteFolder);
                    var connect=Button(storage,"Choose storage or sign in",()=>{});
                    connect.Click+=async(_,e)=>{connect.IsEnabled=false;walkthroughWorking=true;try{await ConnectStorageAsync();}catch(Exception ex){status=ex.Message;}finally{walkthroughWorking=false;if(walkthroughWindow==window)render();}};
                    Button(storage,"Advanced: existing Rclone connection",()=>{Configure();render();});
                    Label(storage,"Google Drive currently needs your own Desktop OAuth client JSON. Dropbox, OneDrive and existing authorized Rclone remotes are also supported.",14,true);
                    if(!String.IsNullOrWhiteSpace(status)&&String.IsNullOrWhiteSpace(config.RemoteFolder))Label(storage,status,13,true);
                }
                else if(step==2)
                {
                    var games=Card(panel,"Choose what follows you","Playtime and save folders are separate: linking a game does not guess where it stores its saves.");
                    var automatic=Toggle(games,"Automatically link library games",onboarding.AutomaticGames);
                    automatic.Click+=(_,e)=>{onboarding.AutomaticGames=automatic.IsChecked==true;SaveOnboarding();if(onboarding.AutomaticGames)EnsureAutomaticLinks();render();};
                    if(onboarding.AutomaticGames)EnsureAutomaticLinks();
                    var mappings=repository.Snapshot().Mappings;
                    var saves=vault.State.Links.Count(p=>!String.IsNullOrWhiteSpace(p.Value.Folder));
                    Label(games,mappings.Count+" linked "+(mappings.Count==1?"game":"games")+" · "+saves+" save folders selected");
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
                    Label(games,"Choose each game's dedicated save folder on this device. Games without a folder still sync playtime. Manually added games need the same shared link name on both devices.",14,true);
                    Label(games,"Use one save-sync tool per game. Avoid managing the same folder with Steam Cloud, Save Manager or Syncthing.",13,true);
                }
                else if(step==3)
                {
                    var options=Card(panel,"Optional features","These choices apply to this device. You can change them later.");
                    if(setup!=null){var addons=Toggle(options,"Share extension and theme installations",setup.Options.Enabled);addons.Click+=(_,e)=>{if(!UpdateSetupOptions(()=>setup.Options.Enabled=addons.IsChecked==true))addons.IsChecked=setup.Options.Enabled;};}
                    Label(options,"Sharing installations does not switch your active theme or copy settings and sign-ins. Incoming installation changes still need review.",13,true);
                    var online=Toggle(options,"Update my online HowLongToBeat progress",onboarding.OnlineHltb);
                    online.Click+=(_,e)=>{onboarding.OnlineHltb=online.IsChecked==true;SaveOnboarding();};
                    Label(options,"Requires HowLongToBeat's local sign-in and an existing mapped profile entry. GameActivity history is detected automatically when installed.",13,true);
                    Button(options,security.Enabled?"Review app passcode":"Set an optional app passcode",()=>{var protection=Window("Playnite Link — passcode",760,700);BuildProtection(protection);protection.ShowDialog();render();});
                }
                else
                {
                    var summary=Card(panel,"Ready for your first sync",config.RemoteFolder);
                    var linked=repository.Snapshot().Mappings.Count;Label(summary,linked+" linked "+(linked==1?"game":"games")+" · "+vault.State.Links.Count(p=>!String.IsNullOrWhiteSpace(p.Value.Folder))+" save folders");
                    Label(summary,"Playtime: "+(devicePreferences.SyncPlaytime?"on":"off")+" · Save sync: "+(devicePreferences.SyncSaves?"on":"off"));
                    Label(summary,"Add-on sharing: "+(setup!=null&&setup.Options.Enabled?"on":"off")+" · Online HLTB: "+(onboarding.OnlineHltb?"on":"off")+" · Passcode: "+(security.Enabled?"on":"off"));
                    progress=Label(summary,status);
                    var actions=Actions(summary);
                    run=Button(actions,IsSyncPaused?"Resume sync":"Start first sync",()=>{});StyleAction(run,true);
                    run.Click+=async(_,e)=>{
                        if(walkthroughWorking||syncing!=0)return;
                        walkthroughWorking=true;
                        try{onboarding.SetupComplete=true;SaveOnboarding();await SyncPersonalAsync(true,null);}
                        catch(OperationCanceledException){status="Sync paused. Your local changes are safe. Choose Resume sync to retry.";}
                        catch(Exception ex){status="Sync could not finish: "+ex.Message+" You can retry here; your setup is saved.";logger.Error(ex,"First sync can be retried.");}
                        finally{walkthroughWorking=false;if(walkthroughWindow==window)render();}
                    };
                    stop=Button(actions,"Stop transfer",StopTransfer);
                    Label(summary,"Stopping pauses automatic sync too. Resume retries the saved changes safely; it does not create another copy of a recorded session.",13,true);
                }
                var footer=Actions(Footer(window));
                back=Button(footer,"Back",()=>{onboarding.SetupStep--;SaveOnboarding();render();});back.IsEnabled=step>0&&syncing==0;
                if(step<4)
                {
                    next=Button(footer,step==0?"Get started":"Continue",()=>{
                        if(step==1&&String.IsNullOrWhiteSpace(config.RemoteFolder)){status="Choose storage before continuing.";render();return;}
                        onboarding.SetupStep++;SaveOnboarding();render();
                    });StyleAction(next,true);next.IsEnabled=syncing==0&&!walkthroughWorking;
                }
                else
                {
                    finish=Button(footer,"Finish setup",()=>{onboarding.SetupComplete=true;SaveOnboarding();window.Close();if(!IsSyncPaused)QueueSync();});
                    StyleAction(finish,true);finish.IsEnabled=syncing==0&&!walkthroughWorking;
                }
                Button(footer,"Continue later",()=>window.Close());
            };
            refresh.Tick+=(_,e)=>{
                if(progress!=null)progress.Text=status;
                if(back!=null)back.IsEnabled=onboarding.SetupStep>0&&syncing==0&&!walkthroughWorking;
                if(next!=null)next.IsEnabled=syncing==0&&!walkthroughWorking;
                if(run!=null){run.IsEnabled=syncing==0&&!walkthroughWorking;run.Content=IsSyncPaused?"Resume sync":"Sync now";}
                if(stop!=null)stop.IsEnabled=syncing!=0&&!transferCancelled;
                if(finish!=null)finish.IsEnabled=syncing==0&&!walkthroughWorking;
            };
            window.Closed+=(_,e)=>{refresh.Stop();walkthroughWindow=null;};
            render();refresh.Start();window.ShowDialog();
        }
    }
}
