using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json;

namespace PlaytimeRclone
{
    public sealed class GameSyncReceipt
    {
        public int Version=1;
        public long Sequence;
        public string Device,Game,Revision,Content,Sessions,State,CheckedUtc;
        public string Canonical(){return JsonConvert.SerializeObject(this,Formatting.None);}
        public string Hash(){return Identity.Hash(Canonical());}
        public void Validate()
        {
            Guid id;DateTimeOffset date;
            if(Version!=1||Sequence<1||!Guid.TryParseExact(Device,"N",out id)||!Regex.IsMatch(Game??"","^[a-f0-9]{64}$")||!DateTimeOffset.TryParse(CheckedUtc,out date)||
                !Regex.IsMatch(Sessions??"","^[a-f0-9]{64}$")||!(State=="confirmed"||State=="playing"||State=="pending"||State=="disabled"))throw new InvalidDataException("Invalid game sync receipt.");
            foreach(var hash in new[]{Revision,Content})if(hash!=null&&!Regex.IsMatch(hash,"^[a-f0-9]{64}$"))throw new InvalidDataException("Invalid receipt revision/hash.");
            if(State=="confirmed"&&(Revision==null||Content==null))throw new InvalidDataException("A confirmed receipt needs a save revision and content hash.");
        }
        public static string SessionDigest(IEnumerable<Entry> entries,string game)
        {return Identity.Hash(String.Join("\n",entries.Where(e=>e.Game==game&&e.Kind!="baseline").Select(e=>e.Hash()).OrderBy(h=>h,StringComparer.Ordinal)));}
        public static bool NeedsReport(bool configured,GameSyncReceipt previous)
        {return configured||(previous!=null&&previous.State!="disabled");}
    }
    public sealed class GameSyncReceipts
    {
        readonly string root;
        public GameSyncReceipts(string root){this.root=root;Directory.CreateDirectory(root);}
        public string DirectoryPath{get{return root;}}
        public List<GameSyncReceipt> Read()
        {
            var result=new List<GameSyncReceipt>();
            foreach(var file in Directory.GetFiles(root,"*.json"))
            {if(new FileInfo(file).Length>16384)throw new InvalidDataException("Oversized sync receipt.");var r=JsonSafety.Deserialize<GameSyncReceipt>(File.ReadAllText(file),new JsonSerializerSettings{MissingMemberHandling=MissingMemberHandling.Error});if(r==null)throw new InvalidDataException("Null sync receipt.");r.Validate();if(Path.GetFileNameWithoutExtension(file)!=r.Hash())throw new InvalidDataException("Sync receipt hash mismatch.");result.Add(r);}
            return result;
        }
        public void Import(string directory)
        {foreach(var receipt in new GameSyncReceipts(directory).Read()){var target=Path.Combine(root,receipt.Hash()+".json");if(!File.Exists(target))Atomic.Write(target,receipt.Canonical());}}
        public void ImportRecords(IEnumerable<GameSyncReceipt> records)
        {
            var verified=records.ToList();foreach(var record in verified)record.Validate();
            foreach(var record in verified){var target=Path.Combine(root,record.Hash()+".json");if(!File.Exists(target))Atomic.Write(target,record.Canonical());}
        }
        public void Publish(GameSyncReceipt value)
        {
            value.Sequence=checked(Read().Where(r=>r.Device==value.Device&&r.Game==value.Game).Select(r=>r.Sequence).DefaultIfEmpty(0).Max()+1);
            value.Validate();Atomic.Write(Path.Combine(root,value.Hash()+".json"),value.Canonical());
        }
        public GameSyncReceipt Latest(string device,string game)
        {var list=Read().Where(r=>r.Device==device&&r.Game==game).ToList();if(list.Count==0)return null;var newest=list.Where(r=>r.Sequence==list.Max(x=>x.Sequence)).ToList();return newest.Count==1?newest[0]:null;}
        public static string Describe(GameSyncReceipt receipt,string revision,string content,string sessions,DateTimeOffset now,int staleSeconds)
        {
            if(receipt==null)return "No confirmed sync information";
            if(receipt.State=="playing")return "Playing at last check-in; saves may be changing";
            if(receipt.State=="disabled")return "Save sync disabled or not configured";
            if(receipt.State!="confirmed")return "Changes pending / review needed";
            if(receipt.Revision!=revision||receipt.Content!=content||receipt.Sessions!=sessions)return "Behind the latest known saves or sessions";
            var checkedAt=DateTimeOffset.Parse(receipt.CheckedUtc);
            if(checkedAt>now.AddMinutes(5)||now-checkedAt>TimeSpan.FromSeconds(Math.Max(120,staleSeconds)))return "Matched at last sync; current state unknown";
            return "Up to date at last confirmed sync";
        }
    }
    public sealed partial class PlaytimePlugin
    {
        GameSyncReceipts SyncReceipts{get{return new GameSyncReceipts(Path.Combine(Root,"sync-receipts"));}}
        private void PublishGameSyncReceipts(IEnumerable<Entry> sentSessions)
        {
            var state=repository.Snapshot();var confirmed=SyncReceipts;
            var receipts=new GameSyncReceipts(Path.Combine(Root,"sync-receipts-outbox"));receipts.Import(confirmed.DirectoryPath);
            var previous=confirmed.Read();var pending=new List<GameSyncReceipt>();
            foreach(var pair in state.Mappings)
            {
                var game=UI(()=>PlayniteApi.Database.Games.Get(new Guid(pair.Key)));if(game==null)continue;
                var link=Link(pair.Value.Key);var heads=vault.Heads(pair.Value.Key,"save");
                var last=previous.Where(r=>r.Device==state.Device&&r.Game==pair.Value.Key).OrderByDescending(r=>r.Sequence).FirstOrDefault();
                if(!GameSyncReceipt.NeedsReport(link.HasSaves,last))continue;
                var receipt=new GameSyncReceipt{Device=state.Device,Game=pair.Value.Key,CheckedUtc=DateTimeOffset.UtcNow.ToString("O"),Sessions=GameSyncReceipt.SessionDigest(state.Entries,pair.Value.Key),State="pending",Revision=link.Save.Revision,Content=link.Save.Content};
                if(UI(()=>GameBusy(game.Id))||state.Active.ContainsKey(pair.Key))receipt.State="playing";
                else if(!link.HasSaves||!devicePreferences.SyncSaves)receipt.State="disabled";
                else if(!unavailableSaveGames.Contains(pair.Value.Key)&&devicePreferences.SyncPlaytime&&GameSyncReceipt.SessionDigest(sentSessions,pair.Value.Key)==receipt.Sessions&&heads.Count==1&&heads[0].Hash()==link.Save.Revision&&Revision.ContentHashOf(Plan(game,pair.Value.Key).Capture())==link.Save.Content){receipt.State="confirmed";receipt.Content=heads[0].ContentHash();}
                receipts.Publish(receipt);pending.Add(receipt);
            }
            var transport=new RcloneTransport(config.RcloneExe,config.ConfigFile,config.RemoteFolder,"playnite-link-v1/receipts/objects",cancelled:()=>closing||TransferStopped);
            transport.PushFiles(receipts.DirectoryPath,pending.Select(r=>r.Hash()+".json"));
            confirmed.ImportRecords(pending); // Do not confirm abandoned or unuploaded outbox history.
        }
        private void ShowGameSyncHistory(Playnite.SDK.Models.Game game)
        {
            var window=Window("Sessions & cloud status — "+game.Name,900,760);Mapping map;
            if(!repository.Snapshot().Mappings.TryGetValue(game.Id.ToString(),out map))return;
            StackPanel content=null;bool refreshing=false;string feedback="";
            Action render=()=>{
                content=Page(window,"Sessions & cloud status",game.Name);
                var summary=Card(content,"Cloud status");SyncStatusView(summary);
                Label(summary,"Each device reports the revision it successfully synced. An offline device may have newer unsent progress. Refresh checks this vault; it cannot contact an offline PC.",13,true);
                if(!String.IsNullOrWhiteSpace(feedback))Label(summary,feedback);
                var refresh=Button(summary,refreshing?"Checking…":"Check cloud now",()=>{});refresh.IsEnabled=!refreshing;
                refresh.Click+=async(_,e)=>{
                    if(refreshing)return;refreshing=true;refresh.IsEnabled=false;refresh.Content="Checking…";
                    try{await SyncPersonalAsync(true,null);feedback="Cloud check finished at "+DateTime.Now.ToString("T");}
                    catch(Exception ex){feedback="Cloud check failed: "+ex.Message;}
                    finally{refreshing=false;if(window.IsVisible)RenderHistoryWindow(window,game,map.Key,feedback);}
                };
                RenderDeviceSessions(content,game,map.Key);
            };
            render();Button(Footer(window),"Close",()=>window.Close());window.ShowDialog();
        }
        private void RenderHistoryWindow(Window window,Playnite.SDK.Models.Game game,string key,string feedback)
        {
            // Re-open the same page contents after a completed check; no stale captured controls.
            var panel=Page(window,"Sessions & cloud status",game.Name);var card=Card(panel,"Cloud check result",feedback);SyncStatusView(card);
            var check=Button(card,"Check cloud now",()=>{});
            check.Click+=async(_,e)=>{check.IsEnabled=false;try{await SyncPersonalAsync(true,null);RenderHistoryWindow(window,game,key,"Cloud check finished at "+DateTime.Now.ToString("T"));}catch(Exception ex){RenderHistoryWindow(window,game,key,"Cloud check failed: "+ex.Message);}};
            RenderDeviceSessions(panel,game,key);Button(Footer(window),"Close",()=>window.Close());
        }
        private void RenderDeviceSessions(Panel panel,Playnite.SDK.Models.Game game,string key)
        {
            if(syncing!=0){Label(panel,"Sync is running. Check again when it finishes.");return;}
            if(!syncGate.Wait(0)){Label(panel,"Sync is preparing. Check again when it finishes.");return;}
            try
            {
                var state=repository.Snapshot();var heads=vault.Heads(key,"save");var head=heads.Count==1?heads[0]:null;
                var known=devices.Views().ToDictionary(d=>d.Id);var receipts=SyncReceipts;var sessions=GameSyncReceipt.SessionDigest(state.Entries,key);
                var ids=known.Values.Where(d=>d.Games.Any(g=>g.Key==key)).Select(d=>d.Id).Concat(new[]{state.Device}).Distinct().ToArray();
                var allMatched=head!=null&&!IsSyncPaused&&!game.IsRunning&&!game.IsLaunching&&!state.Active.ContainsKey(game.Id.ToString())&&ids.All(id=>{DeviceView device;known.TryGetValue(id,out device);return (device==null||(device.State=="active"&&device.Settings.SyncSaves&&!device.SettingsConflict))&&GameSyncReceipts.Describe(receipts.Latest(id,key),head.Hash(),head.ContentHash(),sessions,DateTimeOffset.UtcNow,(device==null?600:device.Settings.IntervalSeconds)*2).StartsWith("Up to date",StringComparison.Ordinal);});
                Label(panel,heads.Count>1?"Save conflict needs review.":allMatched?"All reporting devices matched at their last confirmed sync.":"Some devices are behind or have no recent confirmation. Check their rows below.",15);
                var deviceCard=Card(panel,"Devices",heads.Count>1?"Save conflict: choose a version before continuing.":"Latest known save revision: "+(head==null?"None":head.Hash().Substring(0,12)));
                foreach(var id in ids)
                {
                    DeviceView device;known.TryGetValue(id,out device);var receipt=receipts.Latest(id,key);
                    var name=id==state.Device?devicePreferences.Name+" (this device)":device==null?"Device "+id.Substring(0,8):device.Name;
                    var description=heads.Count>1?"Conflicting cloud revisions":GameSyncReceipts.Describe(receipt,head==null?null:head.Hash(),head==null?null:head.ContentHash(),sessions,DateTimeOffset.UtcNow,(device==null?600:device.Settings.IntervalSeconds)*2);
                    if(id==state.Device&&(game.IsRunning||game.IsLaunching||state.Active.ContainsKey(game.Id.ToString())))description="Playing / unfinished session — saves pending";
                    else if(id==state.Device&&IsSyncPaused)description="Sync paused — changes may be pending";
                    else if(device!=null&&(device.State!="active"||!device.Settings.SyncSaves||device.SettingsConflict))description="Device sync unavailable / paused / disabled";
                    Label(deviceCard,name+" · "+description);
                    Label(deviceCard,"Last confirmed check: "+(receipt==null?"Never":DateTimeOffset.Parse(receipt.CheckedUtc).ToLocalTime().ToString("g"))+" · revision "+(receipt==null||receipt.Revision==null?"None":receipt.Revision.Substring(0,12)),12,true);
                }
                var ledger=new Ledger(state.Entries);var history=Card(panel,"Sessions",ledger.SessionCount(key)+" plays · "+(ledger.Total(key)/3600.0).ToString("0.0")+" hours");
                var rows=new List<string>();
                foreach(var entry in state.Entries.Where(e=>e.Game==key&&e.Kind!="baseline").OrderByDescending(e=>DateTimeOffset.Parse(e.StartedUtc)))
                {
                    DeviceView device;known.TryGetValue(entry.Device,out device);
                    var name=entry.Kind=="history"?"Imported earlier history":entry.Device==state.Device?devicePreferences.Name:device==null?"Former / unknown device "+entry.Device.Substring(0,8):device.Name;
                    rows.Add(DateTimeOffset.Parse(entry.StartedUtc).ToLocalTime().ToString("g")+" → "+DateTimeOffset.Parse(entry.EndedUtc).ToLocalTime().ToString("g")+" · "+(entry.Seconds/60.0).ToString("0.0")+" min · "+name);
                }
                var list=new ListBox{ItemsSource=rows,MinHeight=100,MaxHeight=300};StyleInput(list);history.Children.Add(list);
                foreach(var active in state.Active.Where(p=>p.Key==game.Id.ToString()))Label(history,"In progress / interrupted on this device since "+DateTimeOffset.Parse(active.Value.StartedUtc).ToLocalTime().ToString("g"));
                if(!state.Entries.Any(e=>e.Game==key&&e.Kind!="baseline"))Label(history,"No dated sessions recorded yet. Older total playtime has not been invented as dated sessions.");
            }
            finally{syncGate.Release();}
        }
    }
}
