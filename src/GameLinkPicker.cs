using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Models;
using Newtonsoft.Json;
namespace PlaytimeRclone
{
 public sealed class CloudGameChoice
 {
  public string Key {get;set;}public string Name {get;set;}public string Devices {get;set;}
  public string Label {get{return String.IsNullOrWhiteSpace(Devices)?Name:Name+" · "+Devices;}}
  public static CloudGameChoice[] FromDevices(IEnumerable<DeviceView> devices)
  {return devices.SelectMany(d=>d.Games.Select(g=>new{Device=d.Name,Game=g})).GroupBy(p=>p.Game.Key).Select(g=>new CloudGameChoice{Key=g.Key,Name=g.First().Game.Name,Devices=String.Join(", ",g.Select(p=>p.Device).Distinct().OrderBy(n=>n))}).OrderBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();}
 }
 public sealed partial class PlaytimePlugin
 {
  private Dictionary<string,string> gameLabels;
  private Dictionary<string,string> GameLabels{get{return gameLabels??(gameLabels=File.Exists(Path.Combine(Root,"game-labels.json"))?JsonSafety.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(Root,"game-labels.json"))):new Dictionary<string,string>());}}
  private string GameLabel(string key,string fallback){string value;return GameLabels.TryGetValue(key,out value)?value:fallback;}
  private void CheckActiveGameVault(SyncConfig captured)
  {
   captured.Validate();var proof=ConnectionProbe.Test(captured);
   if(!String.IsNullOrWhiteSpace(vault.State.Remote)&&vault.State.Remote.TrimEnd('/')!=captured.RemoteFolder.TrimEnd('/'))throw new InvalidOperationException("Use Switch vault / start fresh before selecting another vault.");
   var expected=vault.State.ConnectionIdentity;
   if(!String.IsNullOrWhiteSpace(expected)||!String.IsNullOrWhiteSpace(proof.Identity))vault.PinVerified(captured.RemoteFolder,proof.Identity,String.IsNullOrWhiteSpace(expected)?proof.Identity:expected);
   else vault.Pin(captured.RemoteFolder);
  }
  private async Task<CloudGameChoice[]> DiscoverCloudGames(Func<bool> cancelled)
  {
   if(vaultSwitchPending)throw new InvalidOperationException("Restart Playnite to finish switching vaults.");
   if(String.IsNullOrWhiteSpace(config.RemoteFolder))throw new InvalidOperationException("Connect your storage before adding games to the cloud.");
   if(!await syncGate.WaitAsync(0))throw new InvalidOperationException("A sync is running. Wait for it to finish, then refresh.");
   Interlocked.Exchange(ref syncing,1);
   try{return await Task.Run(()=>{CheckTransferForDiscovery();var captured=config;CheckActiveGameVault(captured);var cache=Path.Combine(Root,"cloud-game-discovery");Directory.CreateDirectory(cache);ReportStatus("Reading games linked in this vault.",SyncDisplayState.Working,"Checking cloud games");new RcloneTransport(captured.RcloneExe,captured.ConfigFile,captured.RemoteFolder,"playnite-link-v1/devices/objects",limitSeconds:20,cancelled:()=>closing||cancelled()).Pull(cache);devices.Import(cache);var discovered=new DeviceDirectory(Path.Combine(cache,"verified-cloud"));discovered.Import(cache);deviceViewCache=devices.Views().ToArray();ReportStatus("Cloud game list checked.",SyncDisplayState.Completed,"Cloud games ready");return CloudGameChoice.FromDevices(discovered.Views());});}
   finally{Interlocked.Exchange(ref syncing,0);syncGate.Release();}
  }
  private void CheckTransferForDiscovery(){if(closing)throw new OperationCanceledException();}
  private void Enroll(Game game)
  {
   if(vaultSwitchPending)throw new InvalidOperationException("Restart Playnite to finish switching vaults.");
   if(game.IsRunning||game.IsLaunching)throw new InvalidOperationException("Stop this game before configuring sync.");
   if(repository.Snapshot().Mappings.ContainsKey(game.Id.ToString())){ReportStatus(game.Name+" is already linked. Its existing cloud identity is unchanged.",SyncDisplayState.Ready,"Already linked");return;}
   var window=Window("Link game to cloud",760,780);var panel=Page(window,"Link this game",game.Name);
   var feedback=Label(panel,"Checking your cloud vault before you choose a game…");
   
   var list=new ComboBox{MinWidth=350,DisplayMemberPath="Label",Margin=new Thickness(0,6,0,12)};StyleInput(list);panel.Children.Add(list);
   var link=Button(panel,"Link game",()=>{});link.IsEnabled=false;
   var historyNote=Label(panel,"Uses existing cloud progress.",13,true);
   var newForm=new StackPanel();panel.Children.Add(newForm);historyNote.Visibility=Visibility.Collapsed;
   var name=EntryField(newForm,"Game name",game.Name??"");name.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,"CloudGameName");
   var add=link;
   var refresh=Button(panel,"Check cloud games again",()=>{});
   var savePanel=Card(panel,"Include save files","Optional");
   var saveFolder=new TextBox{IsReadOnly=true,Text="",Margin=new Thickness(0,4,0,4)};saveFolder.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,"LinkSaveFolder");StyleInput(saveFolder);savePanel.Children.Add(saveFolder);
   var saveActions=Actions(savePanel);var chooseSave=Button(saveActions,"Choose save folder…",()=>{var folder=PlayniteApi.Dialogs.SelectFolder();if(!String.IsNullOrWhiteSpace(folder))saveFolder.Text=SaveFiles.Root(folder);});
   var clearSave=Button(saveActions,"Skip save files",()=>saveFolder.Text="");
   Label(savePanel,"Choose only this game’s saves. Don’t use another sync tool for the same folder.",12,true);
   var busy=SyncStatusView(panel);bool closed=false;window.Closed+=(_,e)=>closed=true;var footer=Footer(window);panel.Children.Remove(link);footer.Children.Add(link);StyleAction(link,true);Button(footer,"Cancel",()=>window.Close());

   bool checkedCloud=false;CloudGameChoice[] choices=new CloudGameChoice[0];
   Action update=()=>{var selected=list.SelectedItem as CloudGameChoice;if(selected!=null&&selected.Key==null)selected=null;newForm.Visibility=selected==null?Visibility.Visible:Visibility.Collapsed;historyNote.Visibility=selected==null?Visibility.Collapsed:Visibility.Visible;name.IsEnabled=checkedCloud&&selected==null;link.IsEnabled=checkedCloud&&(selected!=null||!String.IsNullOrWhiteSpace(name.Text));};
   list.SelectionChanged+=(_,e)=>update();name.TextChanged+=(_,e)=>update();
   Func<Task> load=async()=>{checkedCloud=false;refresh.IsEnabled=false;link.IsEnabled=false;add.IsEnabled=false;try{choices=await DiscoverCloudGames(()=>closed);var used=new HashSet<string>(repository.Snapshot().Mappings.Values.Select(m=>m.Key));list.ItemsSource=new[]{new CloudGameChoice{Name="Add a new game to cloud"}}.Concat(choices.Where(c=>!used.Contains(c.Key))).ToArray();list.SelectedIndex=0;string provider=null;try{provider=Identity.Hash(Source(game));}catch(InvalidOperationException){}var exact=choices.FirstOrDefault(c=>c.Key==provider&&!used.Contains(c.Key));if(exact!=null)list.SelectedItem=exact;checkedCloud=true;feedback.Text=choices.Length==0?"No cloud games yet.":"";update();}catch(Exception ex){ReportStatus("Could not check cloud games: "+ex.Message,SyncDisplayState.Failed,"Couldn't check cloud games");feedback.Text="Couldn't check the cloud. Adding and linking are disabled until a check succeeds. "+ex.Message;}finally{refresh.IsEnabled=true;}};
   refresh.Click+=async(_,e)=>await load();window.Loaded+=async(_,e)=>await load();
   string pendingKey=null;
   Func<string,string,Task> commit=async(key,label)=>{
    if(!await syncGate.WaitAsync(0)){feedback.Text="A sync is running. Wait for it to finish, then retry.";return;}
    Interlocked.Exchange(ref syncing,1);feedback.Text="Publishing this game…";link.IsEnabled=false;add.IsEnabled=false;refresh.IsEnabled=false;list.IsEnabled=false;name.IsEnabled=false;chooseSave.IsEnabled=false;clearSave.IsEnabled=false;
    try{
     if(game.IsRunning||game.IsLaunching)throw new InvalidOperationException("Stop the game before linking.");
     if(String.IsNullOrWhiteSpace(label)||label.Length>1000)throw new InvalidOperationException("Use a game name between 1 and 1000 characters.");
     if(pendingKey==null){if(key!=null)repository.EnrollShared(game.Id.ToString(),key,game.Playtime);else{string source;try{source=Source(game);}catch(InvalidOperationException){source="manual:"+Guid.NewGuid().ToString("N");}repository.Enroll(game.Id.ToString(),source,game.Playtime,game.Playtime,game.PlayCount);}pendingKey=repository.Snapshot().Mappings[game.Id.ToString()].Key;GameLabels[pendingKey]=label;Atomic.Write(Path.Combine(Root,"game-labels.json"),JsonConvert.SerializeObject(GameLabels));}
     if(!String.IsNullOrWhiteSpace(saveFolder.Text))SetSaveFolder(game,saveFolder.Text,true);
     var entries=repository.SnapshotGame(game.Id.ToString()).Entries;var me=repository.Snapshot().Device;
     ReportStatus("",SyncDisplayState.Working,"Publishing game");
     await Task.Run(()=>{
      CheckActiveGameVault(config);var outgoing=Path.Combine(Root,"game-publish",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outgoing);
      try{foreach(var activity in new[]{false,true}){var selected=entries.Where(e=>activity?(e.Kind=="history"||e.Count.HasValue):(e.Kind!="history"&&!e.Count.HasValue)).ToArray();foreach(var entry in selected)File.WriteAllText(Path.Combine(outgoing,entry.Hash()+".json"),entry.Canonical());new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,activity?"playnite-link-v1/activity/objects":"playnite-link-v1/ledger/objects",limitSeconds:30,cancelled:()=>closing||closed).PushFiles(outgoing,selected.Select(e=>e.Hash()+".json"));}
       devices.PublishProfile(me,devicePreferences,UI(()=>DeviceGames()));var heads=devices.Heads(me,"profile");var graph=devices.Read(devices.Objects);var needed=new HashSet<string>();Action<string> include=null;include=hash=>{if(File.Exists(Path.Combine(Root,"cloud-game-discovery",hash+".json"))||!needed.Add(hash))return;DeviceEvent item;if(!graph.TryGetValue(hash,out item))throw new InvalidDataException("Cloud game metadata has an incomplete history. Sync and retry.");foreach(var parent in item.Parents)include(parent);};foreach(var head in heads)include(head.Hash());var headHashes=new HashSet<string>(heads.Select(h=>h.Hash()));var profiles=new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/devices/objects",limitSeconds:30,cancelled:()=>closing||closed);profiles.PushFiles(devices.Objects,needed.Where(h=>!headHashes.Contains(h)).Select(h=>h+".json"));profiles.PushFiles(devices.Objects,heads.Select(h=>h.Hash()+".json"));deviceViewCache=devices.Views().ToArray();
      }finally{Directory.Delete(outgoing,true);}
     });
     ReportStatus(label+" is available in this vault. Other devices can select it by name.",SyncDisplayState.Completed,"Game linked");window.Close();
    }catch(Exception ex){logger.Error(ex,"Cloud game publication deferred");ReportStatus("Publishing could not finish: "+ex.Message,SyncDisplayState.Failed,"Game queued locally");feedback.Text="Your local link is saved. Retry publishing the same game; another identity will not be created. "+ex.Message;newForm.Visibility=Visibility.Visible;add.Content="Retry publishing";add.Visibility=Visibility.Visible;add.IsEnabled=true;}
    finally{Interlocked.Exchange(ref syncing,0);syncGate.Release();}
   };
   link.Click+=async(_,e)=>{if(pendingKey!=null){await commit(pendingKey,GameLabel(pendingKey,name.Text));return;}if(!checkedCloud)return;var choice=list.SelectedItem as CloudGameChoice;if(choice!=null&&choice.Key!=null){await commit(choice.Key,choice.Name);return;}var label=name.Text.Trim();if(choices.Any(c=>String.Equals(c.Name,label,StringComparison.OrdinalIgnoreCase))){feedback.Text="This game is already in your cloud. Select it above to keep its progress.";return;}await commit(null,label);};
   window.ShowDialog();
   if(repository.Snapshot().Mappings.ContainsKey(game.Id.ToString()))QueueSync();
  }
 }
}
