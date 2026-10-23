using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private bool refreshingDevices;
        private string deviceRefreshStatus;
        private List<DeviceGame> DeviceGames()
        {
            var games=new List<DeviceGame>();
            foreach(var pair in repository.Snapshot().Mappings)
            {
                var game=PlayniteApi.Database.Games.Get(new Guid(pair.Key));if(game==null)continue;SaveLink link;vault.State.Links.TryGetValue(pair.Value.Key,out link);
                games.Add(new DeviceGame{Key=pair.Value.Key,Name=GameLabel(pair.Value.Key,game.Name),SaveFolder=devicePreferences.SharePaths&&link!=null?link.Folder:null,
                    SavesEnabled=devicePreferences.SyncSaves&&link!=null&&link.HasSaves});
            }
            return games.OrderBy(g=>g.Key,StringComparer.Ordinal).ToList();
        }
        private void BuildDevices(Window window)
        {
            var me=repository.Snapshot().Device;
            if(syncing==0){if(!devices.Heads(me,"profile").Any()&&devices.EffectiveState(me)!="removed")devices.PublishProfile(me,devicePreferences,DeviceGames(),true);deviceViewCache=devices.Views().ToArray();}
            var panel=Page(window,"Your devices","","Devices");
            var toolbar=Actions(panel);var refresh=Button(toolbar,refreshingDevices?"Refreshing…":"Refresh devices",()=>RefreshDevices());refresh.IsEnabled=!refreshingDevices;
            if(!String.IsNullOrWhiteSpace(deviceRefreshStatus))Label(panel,deviceRefreshStatus,13,true);
            Label(panel,syncing!=0?"Sync is running. This is the last received device list.":"Last seen shows the last check-in, not live online status.",13,true);
            if(devices.EffectiveState(me)=="removed")
            {
                var disconnected=Card(panel,"This device is disconnected","This device was removed from synchronization. Connect it again only if you want to use it.");
                Button(disconnected,"Connect this device again",()=>ApplyDeviceChange(()=>devices.SetState(me,me,"active")));
            }
            foreach(var item in deviceViewCache.Where(d=>d.State!="removed").OrderByDescending(d=>d.Id==me).ThenBy(d=>d.Name))
            {
                var device=item;var card=Card(panel,device.Name+(device.Id==me?"  ·  This device":""));
                DateTimeOffset last;var seen=DateTimeOffset.TryParse(device.LastSeen,out last)?last.ToLocalTime().ToString("g"):"Not checked in yet";
                Label(card,(device.State=="active"?"Active":device.State=="paused"?"Paused":"Removed")+"  ·  Last seen "+seen,13,true);
                if(device.SettingsConflict)Label(card,"Settings need review: two devices changed them at the same time. Save settings to resolve the versions.",14);
                var enabled=new List<string>();if(device.Settings.SyncSaves)enabled.Add("Saves");if(device.Settings.SyncPlaytime)enabled.Add("Playtime");if(device.Settings.SyncPreferences)enabled.Add("Favorites & notes");
                Label(card,enabled.Count==0?"Game data sync is off":String.Join("  ·  ",enabled),14);
                Label(card,device.Games.Count+" linked games  ·  "+(device.Settings.AutoSync?"Idle checks from "+device.Settings.IntervalSeconds+" seconds, with backoff":"Manual sync")+"  ·  "+(device.Settings.BandwidthKiB==0?"No bandwidth limit":device.Settings.BandwidthKiB+" KiB/s"),13,true);
                var actions=Actions(card);Button(actions,device.SettingsConflict?"Review settings":"Settings",()=>{EditDevice(device);});
                Button(actions,"Linked games",()=>ShowDeviceGames(device));
                if(device.State=="active")Button(actions,"Pause sync",()=>{ChangeDeviceState(device,"paused");});
                else Button(actions,"Resume sync",()=>{ChangeDeviceState(device,"active");});
                var advanced=new StackPanel();Label(advanced,"Device ID: "+device.Id,12,true);
                if(device.State!="removed")Button(actions,"Remove device…",()=> {
                    if(PlayniteApi.Dialogs.ShowMessage("Remove "+device.Name+" from your connected devices?\n\nYour synced saves and playtime will stay in the vault. The device will disappear from this list and stop synchronization when it next checks in. A removal record prevents stale check-ins from adding it again. This does not erase copies on that device or revoke its cloud credentials.","Remove connected device",MessageBoxButton.YesNo)==MessageBoxResult.Yes)
                    {ChangeDeviceState(device,"removed");}
                });
                Details(card,"Device details",advanced);
            }
            if(!deviceViewCache.Any(d=>d.State!="removed"))Card(panel,"No connected devices","Connect cloud storage from Overview, then refresh this list.");
            Label(panel,"To revoke cloud access, remove the device’s authorization with your storage provider.",13,true);
        }
        private void ChangeDeviceState(DeviceView device,string state)
        {
            ApplyDeviceChange(()=>devices.SetState(repository.Snapshot().Device,device.Id,state));
        }
        private async void ApplyDeviceChange(Action action)
        {
            transferCancelled=true;
            try
            {
                await Task.Run(async()=>{await syncGate.WaitAsync();try{action();deviceViewCache=devices.Views().ToArray();}finally{syncGate.Release();}});
                RefreshDevicesPage();RequestSync(true);
            }
            catch(Exception ex){UI(()=>NotifyError(ex));}
        }
        private static CheckBox Toggle(Panel panel,string label,bool value)
        {var checkbox=new CheckBox{Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,Foreground=Ink},IsChecked=value,Foreground=Ink,Margin=new Thickness(0,8,0,8),VerticalContentAlignment=VerticalAlignment.Center};panel.Children.Add(checkbox);return checkbox;}
        private static TextBox EntryField(Panel panel,string label,string value)
        {Label(panel,label,13,true);var box=new TextBox{Text=value,Padding=new Thickness(10),Margin=new Thickness(0,0,0,16),Background=Canvas,Foreground=Ink,CaretBrush=Accent,BorderBrush=Line,BorderThickness=new Thickness(1)};panel.Children.Add(box);return box;}
        private void EditDevice(DeviceView device)
        {
            var window=Window("Device configuration — "+device.Name,740,780);var panel=Page(window,"Device settings",device.Name+" · changes apply at its next check-in.");
            var identity=Card(panel,"Device name");var name=EntryField(identity,"Display name",device.Settings.Name);
            var content=Card(panel,"What to sync");
            var auto=Toggle(content,"Sync automatically in the background",device.Settings.AutoSync);
            var saves=Toggle(content,"Game saves",device.Settings.SyncSaves);
            var playtime=Toggle(content,"Playtime",device.Settings.SyncPlaytime);
            var preferences=Toggle(content,"Favorites, hidden games and notes",device.Settings.SyncPreferences);
            var advanced=new StackPanel{Margin=new Thickness(0,14,0,0)};
            var interval=EntryField(advanced,"Minimum idle check interval in seconds (30–86400)",device.Settings.IntervalSeconds.ToString());
            var bandwidth=EntryField(advanced,"Bandwidth limit in KiB/s (0 = unlimited)",device.Settings.BandwidthKiB.ToString());
            var paths=Toggle(advanced,"Share save-folder paths with my other devices",device.Settings.SharePaths);
            paths.IsEnabled=device.Id==repository.Snapshot().Device;
            if(!paths.IsEnabled)Label(advanced,"Enable path sharing on that device itself.",13,true);
            Label(advanced,"Paths stay private unless this device opts in. Store credentials and save paths cannot be changed remotely.",13,true);Details(panel,"Advanced settings",advanced);
            var footer=Actions(Footer(window));var save=Button(footer,"Save changes",()=> {
                int seconds,rate;if(!Int32.TryParse(interval.Text,out seconds)||!Int32.TryParse(bandwidth.Text,out rate))throw new ArgumentException("Use whole numbers for interval and bandwidth.");
                var next=new DevicePreferences{Name=name.Text.Trim(),IntervalSeconds=seconds,BandwidthKiB=rate,AutoSync=auto.IsChecked==true,SyncSaves=saves.IsChecked==true,SyncPlaytime=playtime.IsChecked==true,SyncPreferences=preferences.IsChecked==true,SharePaths=paths.IsChecked==true};next.Validate();
                window.Close();ApplyDeviceChange(()=>devices.SetSettings(repository.Snapshot().Device,device.Id,next));
            });
            StyleAction(save,true);Button(footer,"Cancel",()=>window.Close());window.ShowDialog();
        }
        private void ShowDeviceGames(DeviceView device)
        {
            var window=Window("Per-game configuration — "+device.Name,760,620);var panel=Page(window,"Linked games",device.Name);
            foreach(var game in device.Games)
            {
                var card=Card(panel,game.Name,game.SavesEnabled?"Save sync enabled":"Save sync not configured");Label(card,"Save folder: "+(game.SaveFolder??"Private to this device"),13,true);
                var details=new StackPanel();Label(details,"Shared game key: "+game.Key,12,true);Details(card,"Matching details",details);
            }
            if(device.Games.Count==0)Text(panel,"No linked games have been published by this device yet.");
            Button(panel,"Close",()=>window.Close());window.ShowDialog();
        }
        private async void RestoreHistoryAsync(string key,Revision revision)
        {
            try{await RestoreHistoryCoreAsync(key,revision);RequestSync(true);}
            catch(Exception ex){UI(()=>NotifyError(ex));}
        }
        private Task RestoreHistoryCoreAsync(string key,Revision revision)
        {
            if(MigrationBlocksSync)throw new IOException("Finish migration and restart Playnite before restoring with Link.");
            var game=UI(()=>{
                var mapping=repository.Snapshot().Mappings.Single(p=>p.Value.Key==key);
                var id=new Guid(mapping.Key);
                if(GameBusy(id))throw new InvalidOperationException("Stop the game before restoring save history.");
                return id;
            });
            var generation=Volatile.Read(ref transferGeneration);
            return Task.Run(async()=> {
                    await syncGate.WaitAsync();
                    if(generation!=Volatile.Read(ref transferGeneration)||closing){syncGate.Release();throw new OperationCanceledException();}
                    Volatile.Write(ref activeTransferGeneration,generation);transferCancelled=false;Interlocked.Exchange(ref syncing,1);
                    try
                    {
                        if(UI(()=>GameBusy(game)))throw new InvalidOperationException("Stop the game before restoring save history.");
                        var link=Link(key);var plan=Plan(PlayniteApi.Database.Games.Get(game),key);var local=plan.Capture();var data=blobs.ImportLegacy(revision.Data);var projected=plan.Filter(data);
                        LastBackup=plan.Restore(data,Revision.ContentHashOf(local),()=>UI(()=>{
                            if(GameBusy(game))throw new IOException("The game is starting or running; history restore deferred.");
                            restoringSaves.Add(game);
                        }));
                        var next=new Revision{Version=data.Keys.Any(k=>k.StartsWith(SavePlan.Mounts,StringComparison.Ordinal))?3:2,Game=key,Kind="save",Device=repository.Snapshot().Device,Data=data,Parents=vault.Heads(key,"save").Select(h=>h.Hash()).OrderBy(h=>h,StringComparer.Ordinal).ToList()};
                        vault.Add(next);link.Save.Revision=next.Hash();link.Save.Content=Revision.ContentHashOf(projected);vault.Save();
                    }finally{UI(()=>restoringSaves.Remove(game));Interlocked.Exchange(ref syncing,0);syncGate.Release();}
                });
        }
    }
}

