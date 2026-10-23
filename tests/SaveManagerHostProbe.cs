using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlaytimeRclone;
using SaveManager.Services;

public sealed class HostProbe:GenericPlugin
{
    public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    public HostProbe(IPlayniteAPI api):base(api){}
    PlaytimePlugin subject;string root;Window window;int checks;
    object Field(string name){return typeof(PlaytimePlugin).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);}
    object Call(string name,params object[] args){try{return typeof(PlaytimePlugin).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(subject,args);}catch(TargetInvocationException ex){throw ex.InnerException;}}
    static T Event<T>(Game game)where T:new(){var value=new T();typeof(T).GetField("<Game>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,game);return value;}
    void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    IEnumerable<T> Elements<T>(DependencyObject parent)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var nested in Elements<T>(child))yield return nested;}}
    void Capture(string name){window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,name+".png")))encoder.Save(file);}
    async Task Until(Func<bool> test){var until=DateTime.UtcNow.AddSeconds(60);while(!test()&&DateTime.UtcNow<until)await Task.Delay(50);Check(test(),"Host check timed out");}
    async Task UntilIdle(){var deadline=DateTime.UtcNow.AddSeconds(60);var quiet=DateTime.UtcNow;while(DateTime.UtcNow<deadline){if(subject.IsSyncRunning)quiet=DateTime.UtcNow;else if(DateTime.UtcNow-quiet>TimeSpan.FromSeconds(2)){checks++;return;}await Task.Delay(100);}throw new Exception("Sync did not settle before UI verification");}
    async Task Open(string method,object[] args,Action inspect)
    {
        var existing=new HashSet<Window>(Application.Current.Windows.OfType<Window>());
        var task=Task.Run(async()=>{await Task.Delay(method=="ShowBuiltInAchievementOverview"?5000:150);await Application.Current.Dispatcher.InvokeAsync(new Action(()=>{
            try{window=Application.Current.Windows.OfType<Window>().Last(w=>w.IsVisible&&!existing.Contains(w));window.UpdateLayout();inspect();}
            finally{if(window!=null)window.Close();}
        }));});
        Call(method,args);await task;
    }
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{try{await Run();}catch(Exception ex){Finish(new{passed=false,error=ex.ToString(),checks});}}));}
    void Finish(object result){File.WriteAllText(Path.Combine(root??PlayniteApi.Paths.ConfigurationPath,"host-result.json"),JsonConvert.SerializeObject(result,Formatting.Indented));Application.Current.Shutdown();}
    async Task Run()
    {
        root=PlayniteApi.Paths.ConfigurationPath;Check(File.Exists(Path.Combine(root,"PROBE-ONLY")),"Isolated profile required");
        subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));await UntilIdle();subject.StopTransfer();
        if(Directory.Exists(Path.Combine(Path.GetDirectoryName(subject.GetType().Assembly.Location),"AchievementEngine")))
        {
            Check(Field("achievementEngine")!=null,"Built-in achievement engine failed: "+Field("achievementEngineError"));
            Check(!PlayniteApi.Addons.Plugins.Any(p=>p.GetType().FullName=="PlayniteAchievements.PlayniteAchievementsPlugin"),"Achievement component registered as a separate add-on");
            Check(subject.GetSidebarItems().Any(i=>i.Title=="Link achievements"&&i.Opened!=null),"Full achievement sidebar is missing");
            await Open("ShowBuiltInAchievementOverview",new object[0],()=>{Check(window.Content is System.Windows.FrameworkElement,"Full achievement overview did not load");Capture("built-in-achievements");});
            await Open("ShowAchievementSettings",new object[0],()=>{Check(Elements<Button>(window).Any(b=>Convert.ToString(b.Content)=="Cancel"),"Built-in provider/settings window did not load");Capture("built-in-achievement-settings");});
            var hub=subject.GetSidebarItems().Single(i=>i.Title=="Playnite Link");Check(hub.Type==SiderbarItemType.View&&hub.Opened!=null,"Main Link sidebar is not an in-app view");
            window=new Window{Title="Link sidebar preview",Width=1000,Height=760,Content=hub.Opened(),WindowStartupLocation=WindowStartupLocation.CenterScreen};window.Show();await Task.Delay(700);
            Check(Elements<TextBlock>(window).Any(t=>t.Text=="Playnite Link"),"Sidebar dashboard title is missing");Check(Elements<Button>(window).Any(b=>Convert.ToString(b.Content)=="Connected devices")&&Elements<Button>(window).Any(b=>Convert.ToString(b.Content)=="Achievements"),"Sidebar dashboard navigation is incomplete");
            var prior=Field("status");typeof(PlaytimePlugin).GetField("status",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(subject,"Live sidebar status fixture");await Task.Delay(700);Check(Elements<TextBlock>(window).Any(t=>t.Text=="Live sidebar status fixture"),"Sidebar status does not refresh");Capture("link-sidebar");typeof(PlaytimePlugin).GetField("status",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(subject,prior);window.Close();
        }
        var repo=(Repository)Field("repository");var gameId=new Guid("f3d3b83a-fc3f-40ec-87c5-24094bdd81d9");
        var game=new Game{Id=gameId,Name="Cross-device save fixture",InstallDirectory=root,IsInstalled=true,Playtime=100};PlayniteApi.Database.Games.Add(game);var key=repo.Snapshot().Mappings[gameId.ToString()].Key;
        if(Field("achievementEngine")!=null)
        {
            await subject.SyncPersonalAsync(true,null);var engine=Field("achievementEngine");var custom=PluginActivityStore.Member(engine,"GameCustomDataStore");var current=PluginActivityStore.Call(custom,"LoadOrDefault",game.Id);var authored=JObject.FromObject(current);var customId=Guid.NewGuid().ToString("N");
            authored["CustomAchievements"]=new JArray(new JObject{{"Id",customId},{"DisplayName","Portable authored achievement"},{"Description","Custom description"},{"Unlocked",true},{"UnlockTimeUtc","2026-10-23T01:00:00Z"},{"ProgressNum",3},{"ProgressDenom",10},{"Points",25},{"Category","Story"}});
            authored["AchievementNotes"]=new JObject{{"TEST_UNLOCK","A note from the PC"}};PluginActivityStore.Call(custom,"Save",game.Id,authored.ToObject(current.GetType()));
            var package=(SortedDictionary<string,string>)Call("CaptureAchievementCustomization",game);Check(package.ContainsKey("custom-data.pa")&&package.ContainsKey("link-personal-state.json"),"Authored achievement package/personal state missing");
            var other=new Game{Id=Guid.NewGuid(),Name="Receiving device fixture"};PlayniteApi.Database.Games.Add(other);Call("ApplyAchievementCustomization",other,package);var received=JObject.FromObject(PluginActivityStore.Call(custom,"LoadOrDefault",other.Id));var authoredRows=(JArray)received["CustomAchievements"];
            Check(authoredRows.Count==1&&(string)authoredRows[0]["DisplayName"]=="Portable authored achievement","Custom achievement catalog did not transfer");Check((bool)authoredRows[0]["Unlocked"]&&(int)authoredRows[0]["ProgressNum"]==3,"Custom unlock/progress did not transfer");Check((string)authoredRows[0]["Category"]=="Story"&&(int)authoredRows[0]["Points"]==25,"Custom category/points did not transfer");
            var again=(SortedDictionary<string,string>)Call("CaptureAchievementCustomization",game);Check(Revision.ContentHashOf(package)==Revision.ContentHashOf(again),"Repeated customization export is not content-stable");
            authoredRows[0]["Unlocked"]=false;authoredRows[0]["UnlockTimeUtc"]=null;PluginActivityStore.Call(custom,"Save",other.Id,received.ToObject(current.GetType()));var relocked=(SortedDictionary<string,string>)Call("CaptureAchievementCustomization",other);Call("ApplyAchievementCustomization",game,relocked);var afterReset=JObject.FromObject(PluginActivityStore.Call(custom,"LoadOrDefault",game.Id));Check(!(bool)afterReset["CustomAchievements"][0]["Unlocked"],"Explicit manual relock was lost across devices");
            Call("ApplyAchievementCustomization",game,new SortedDictionary<string,string>());var cleared=JObject.FromObject(PluginActivityStore.Call(custom,"LoadOrDefault",game.Id));Check((cleared["CustomAchievements"] as JArray??new JArray()).Count==0,"Removing authored achievements did not transfer");
            var assembly=engine.GetType().Assembly;var dataType=assembly.GetType("PlayniteAchievements.Models.Achievements.GameAchievementData",true);var rowType=assembly.GetType("PlayniteAchievements.Models.Achievements.AchievementDetail",true);var sourceData=Activator.CreateInstance(dataType);dataType.GetProperty("ProviderKey").SetValue(sourceData,"Steam",null);dataType.GetProperty("AppId").SetValue(sourceData,1234,null);
            var row=Activator.CreateInstance(rowType);rowType.GetProperty("ApiName").SetValue(row,"PORTABLE_PROVIDER_UNLOCK",null);rowType.GetProperty("DisplayName").SetValue(row,"Provider achievement",null);rowType.GetProperty("Description").SetValue(row,"Full provider metadata",null);rowType.GetProperty("Points").SetValue(row,25,null);rowType.GetProperty("GlobalPercentUnlocked").SetValue(row,17.5,null);rowType.GetProperty("Unlocked").SetValue(row,true,null);rowType.GetProperty("UnlockTimeUtc").SetValue(row,new DateTime(2026,10,23,1,0,0,DateTimeKind.Utc),null);((System.Collections.IList)dataType.GetProperty("Achievements").GetValue(sourceData,null)).Add(row);
            var cache=PluginActivityStore.Member(engine,"CacheManager");var saved=PluginActivityStore.Call(cache,"SaveGameData",game.Id.ToString(),sourceData);Check(Convert.ToBoolean(PluginActivityStore.Member(saved,"Success")),"Provider fixture cache did not save");var nativeStore=new AchievementStore(Path.Combine(root,"provider-catalog-fixture"));var catalogKey=Identity.Hash("provider-catalog-fixture");var adapter=new AchievementAdapter(engine,true);adapter.Capture(nativeStore,game,catalogKey,repo.Snapshot().Device);
            var fresh=new Game{Id=Guid.NewGuid(),Name="Fresh receiving device fixture"};PlayniteApi.Database.Games.Add(fresh);Check(PluginActivityStore.Call(cache,"LoadGameData",fresh.Id.ToString())==null,"Fresh receiver already had provider data");adapter.Project(nativeStore,fresh,catalogKey);var freshData=PluginActivityStore.Call(cache,"LoadGameData",fresh.Id.ToString());Check(freshData!=null,"Fresh receiver did not get the achievement catalog");var freshRow=((System.Collections.IEnumerable)PluginActivityStore.Member(freshData,"Achievements")).Cast<object>().Single();Check(Convert.ToBoolean(PluginActivityStore.Member(freshRow,"Unlocked")),"Fresh receiver did not get the provider unlock");Check(Convert.ToInt32(PluginActivityStore.Member(freshRow,"Points"))==25&&Convert.ToDouble(PluginActivityStore.Member(freshRow,"GlobalPercentUnlocked"))==17.5,"Fresh receiver lost score/rarity metadata");
            var icon=Path.Combine(root,"provider-icon.png");File.WriteAllBytes(icon,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1cAAAAASUVORK5CYII="));
            var newer=Activator.CreateInstance(rowType);rowType.GetProperty("ApiName").SetValue(newer,"NEW_PROVIDER_ROW",null);rowType.GetProperty("DisplayName").SetValue(newer,"New catalog entry",null);rowType.GetProperty("UnlockedIconPath").SetValue(newer,icon,null);((System.Collections.IList)dataType.GetProperty("Achievements").GetValue(sourceData,null)).Add(newer);PluginActivityStore.Call(cache,"SaveGameData",game.Id.ToString(),sourceData);adapter.Capture(nativeStore,game,catalogKey,repo.Snapshot().Device);adapter.Project(nativeStore,fresh,catalogKey);
            var expanded=((System.Collections.IEnumerable)PluginActivityStore.Member(PluginActivityStore.Call(cache,"LoadGameData",fresh.Id.ToString()),"Achievements")).Cast<object>().ToArray();Check(expanded.Length==2,"Existing receiving cache did not get a new catalog row");var expandedIcon=Convert.ToString(PluginActivityStore.Member(expanded.Single(r=>Convert.ToString(PluginActivityStore.Member(r,"ApiName"))=="NEW_PROVIDER_ROW"),"UnlockedIconPath"));Check(File.Exists(expandedIcon)&&SaveMigrationFiles.Hash(expandedIcon)==SaveMigrationFiles.Hash(icon),"Verified provider icon did not transfer");File.WriteAllText(expandedIcon,"interrupted icon");var repair=new Game{Id=Guid.NewGuid(),Name="Icon recovery fixture"};PlayniteApi.Database.Games.Add(repair);adapter.Project(nativeStore,repair,catalogKey);Check(SaveMigrationFiles.Hash(expandedIcon)==SaveMigrationFiles.Hash(icon),"Damaged existing provider icon was trusted");
        }
        var first=Path.Combine(root,"saves");var second=Path.Combine(root,"settings");Directory.CreateDirectory(first);Directory.CreateDirectory(second);File.WriteAllText(Path.Combine(first,"progress.sav"),"first checkpoint");File.WriteAllText(Path.Combine(first,"config.ini"),"local graphics");File.WriteAllText(Path.Combine(second,"profile.dat"),"profile");
        var link=new SaveLink{Sources=new List<SaveSource>{new SaveSource{Id="main",Path=first},new SaveSource{Id="source2",Path=Path.Combine(second,"profile.dat"),IsDirectory=false}},RestoreExclusions=new List<string>{Path.Combine(first,"config.ini")},CheckBeforeLaunch=true};subject.PersonalVault.State.Links[key]=link;subject.PersonalVault.Save();
        var operation=(Task)Call("SaveOperation",game,new Action(()=>Call("CreateBackup",game,key,"Before boss",false)));await operation;
        var backupService=(BackupService)typeof(PlaytimePlugin).GetProperty("Backups",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject,null);Check(backupService.GetBackups(game.Id).Count==1,"Core did not create ZIP backup");
        var backup=backupService.GetBackups(game.Id).Single();File.WriteAllText(Path.Combine(first,"progress.sav"),"later progress");File.WriteAllText(Path.Combine(first,"config.ini"),"new local graphics");
        await (Task)Call("SaveOperation",game,new Action(()=>Call("RestoreArchive",game,key,backupService.GetFullBackupPath(backup.BackupFilePath))));
        Check(File.ReadAllText(Path.Combine(first,"progress.sav"))=="first checkpoint","ZIP restore lost checkpoint");Check(File.ReadAllText(Path.Combine(first,"config.ini"))=="new local graphics","Restore exclusion overwritten");Check(backupService.GetBackups(game.Id).Count==2,"Pre-restore backup missing");
        var peer=Guid.NewGuid().ToString("N");subject.ConnectedDevices.PublishProfile(peer,new DevicePreferences{Name="Handheld fixture"},new List<DeviceGame>{new DeviceGame{Key=key,Name=game.Name,SavesEnabled=true}});
        var entry=new Entry{Kind="session",Id=Guid.NewGuid().ToString("N"),Game=key,Device=peer,Seconds=300,StartedUtc=DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"),EndedUtc=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")};repo.Merge(new[]{entry});
        await subject.SyncPersonalAsync(true,null);Check(subject.PersonalVault.Heads(key,"save").Single().Version==3,"Multiple sources did not publish v3 manifest");
        var catalog=(BackupCatalog)typeof(PlaytimePlugin).GetProperty("CloudBackups",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject,null);Check(catalog.Heads(key).Count==2,"Named backups were not catalogued");
        var cloudBackups=Path.Combine(root,"Cloud","personal","playnite-link-v1","backups","objects");Check(Directory.GetFiles(cloudBackups,"*.json").Length>=2,"Backup metadata was not uploaded");
        await (Task)Call("SaveOperation",game,new Action(()=>Call("VerifyGameCloud",game,key)));Check(true,"Cloud verification finished");
        foreach(var record in catalog.Heads(key))
        {
            var temporary=Path.Combine(root,"inspect-cloud.zip");using(var output=File.Create(temporary))subject.SaveBlobs.CopyVerified(record.Archive,output);
            using(var input=File.OpenRead(temporary))using(var zip=new System.IO.Compression.ZipArchive(input,System.IO.Compression.ZipArchiveMode.Read))using(var reader=new StreamReader(zip.GetEntry("__save_paths__.json").Open()))Check(!reader.ReadToEnd().Contains(root.Replace("\\","\\\\")),"Cloud backup leaks an absolute source path");
        }
        var receipts=new GameSyncReceipts(Path.Combine(Path.GetDirectoryName(backupService.BackupsPath),"..","sync-receipts"));
        // Use the active vault's exact receipt store through the plugin property.
        receipts=(GameSyncReceipts)typeof(PlaytimePlugin).GetProperty("SyncReceipts",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject,null);
        Check(receipts.Latest(repo.Snapshot().Device,key)!=null,"Full sync did not record a receipt");
        var managerOptions=(SaveManagerOptions)typeof(PlaytimePlugin).GetProperty("ManagerOptions",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject,null);
        Check(subject.GetSidebarItems().Any(i=>i.Title=="Playnite Link"),"Main Link sidebar access missing");Check(subject.GetSidebarItems().Any(i=>i.Title=="Playnite Link saves"),"Save sidebar access missing");managerOptions.ShowSidebar=false;Check(subject.GetSidebarItems().Any(i=>i.Title=="Playnite Link")&&!subject.GetSidebarItems().Any(i=>i.Title=="Playnite Link saves"),"Save sidebar setting incorrectly hides the main hub");managerOptions.ShowSidebar=true;
        Check(subject.GetGameMenuItems(new GetGameMenuItemsArgs{Games=new List<Game>{game}}).Any(i=>i.Description=="Quick backup"),"Quick backup menu missing");Check(subject.GetGameMenuItems(new GetGameMenuItemsArgs{Games=new List<Game>{game,game}}).Any(i=>i.Description=="Quick backup selected games"),"Batch backup menu missing");
        PlayniteApi.Notifications.Remove("PlayniteLink.Reinstalled."+game.Id);var notifications=PlayniteApi.Notifications.Count;subject.OnGameUninstalled(Event<OnGameUninstalledEventArgs>(game));subject.OnGameInstalled(Event<OnGameInstalledEventArgs>(game));Check(PlayniteApi.Notifications.Count==notifications+1,"Reinstall recovery prompt absent");PlayniteApi.Notifications.Remove("PlayniteLink.Reinstalled."+game.Id);link.RestoreOnInstall=false;subject.OnGameInstalled(Event<OnGameInstalledEventArgs>(game));Check(PlayniteApi.Notifications.Count==notifications,"Disabled reinstall recovery still notified");link.RestoreOnInstall=true;
        await Open("ShowSaveManager",new object[]{game,key},()=>{Check(Elements<Button>(window).Any(b=>Convert.ToString(b.Content)=="Create backup…"),"Backup UI missing");Check(Elements<Button>(window).Any(b=>Convert.ToString(b.Content)=="Import ZIP…"),"Import UI missing");Capture("save-manager");});
        await Open("ShowSavePaths",new object[]{game,key},()=>{Check(Elements<TextBlock>(window).Any(c=>c.Text=="Check and sync cloud saves before launching"),"Preflight option missing");Check(Elements<TextBlock>(window).Any(t=>t.Text=="Keep local files when restoring"),"Exclusions UI missing");Capture("save-paths");});
        await Open("ShowGameSyncHistory",new object[]{game},()=>{Check(Elements<TextBlock>(window).Any(t=>t.Text.Contains("Handheld fixture")),"Named device absent");Check(Elements<TextBlock>(window).Any(t=>t.Text.Contains("No confirmed sync information")),"Unconfirmed device was not identified");Check(Elements<TextBlock>(window).Any(t=>t.Text.Contains("5.0 min")&&t.Text.Contains("Handheld fixture")),"Dated device session absent");Capture("sessions-and-sync");});
        await Open("ShowBackupSettings",new object[0],()=>{Check(Elements<TextBlock>(window).Any(t=>t.Text=="Global backup root (blank = plugin data)"),"Global root setting missing");Check(Elements<TextBlock>(window).Any(t=>t.Text=="Show the saves button in Playnite's sidebar"),"Global sidebar setting missing");});
        // Migrate an upstream-format configuration and its real core-created ZIP.
        var legacy=Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,"e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b");Directory.CreateDirectory(legacy);var config=backupService.GetGameConfig(game.Id);var token=JObject.Parse(JsonConvert.SerializeObject(config));var configId=config.ConfigId.ToString();
        var legacyZip=Path.Combine(legacy,"old-backup.zip");File.Copy(backupService.GetFullBackupPath(backup.BackupFilePath),legacyZip);
        using(var file=new FileStream(legacyZip,FileMode.Open,FileAccess.ReadWrite))using(var zip=new System.IO.Compression.ZipArchive(file,System.IO.Compression.ZipArchiveMode.Update)){var info=zip.GetEntry("backup_info.json");JObject meta;using(var reader=new StreamReader(info.Open()))meta=JObject.Parse(reader.ReadToEnd());meta["Description"]="Migrated checkpoint";info.Delete();using(var writer=new StreamWriter(zip.CreateEntry("backup_info.json").Open()))writer.Write(meta.ToString());}
        var records=new JArray(new JObject{{"BackupFilePath","old-backup.zip"},{"Description","Migrated checkpoint"}});var data=new JObject{{"Version",2},{"GameConfigs",new JObject{{configId,token}}},{"GameBackups",new JObject{{configId,records}}}};File.WriteAllText(Path.Combine(legacy,"config.json"),data.ToString());
        var incoming=(SaveLink)Call("ParseSaveConfiguration",token.ToString());await (Task)Call("SaveOperation",game,new Action(()=>Call("MigrateLegacyGame",configId,token,records,game,key,incoming)));
        Call("VerifyLegacyMigration",data);Check(backupService.GetBackups(game.Id).Count==3,"Legacy backup was not migrated");
        var before=backupService.GetBackups(game.Id).Count;await (Task)Call("SaveOperation",game,new Action(()=>Call("MigrateLegacyGame",configId,token,records,game,key,incoming)));Check(backupService.GetBackups(game.Id).Count==before,"Migration replay duplicated backups");
        await (Task)Call("SaveOperation",game,new Action(()=>Call("ImportSaveBackup",game,key,legacyZip)));Check(backupService.GetBackups(game.Id).Count==before,"Repeated ZIP import duplicated a backup");
        var moved=Path.Combine(root,"CustomBackups");await (Task)Call("SaveOperation",game,new Action(()=>Call("RelocateBackupDirectory",config.ConfigId,moved)));subject.PersonalVault.State.Links[key].BackupRoot=moved;subject.PersonalVault.Save();Check(backupService.GetBackups(game.Id).All(b=>File.Exists(backupService.GetFullBackupPath(b.BackupFilePath))),"Relocated backup paths invalid");Check(Directory.GetFiles(Path.Combine(moved,config.ConfigId.ToString()),"*.zip").Length==3,"Custom backup root lost files");
        var unlinked=Guid.NewGuid();backupService.GetOrCreateGameConfig(unlinked,"Unlinked backup fixture");backupService.ImportBackup(unlinked,"Unlinked backup fixture",backupService.GetFullBackupPath(backup.BackupFilePath));
        Check(!PlayniteApi.Addons.Plugins.Any(p=>p.GetType().FullName=="HowLongToBeat.HowLongToBeat"||p.Id==new Guid("afbb1a0d-04a1-4d0c-9afa-c6e42ca855b4")),"Independent-session fixture unexpectedly has optional add-ons");
        subject.OnGameStarted(Event<OnGameStartedEventArgs>(game));
        ((HashSet<string>)Field("live")).Clear();
        var stopped=Event<OnGameStoppedEventArgs>(game);typeof(OnGameStoppedEventArgs).GetField("<ElapsedSeconds>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(stopped,5UL);
        game.Playtime+=5;PlayniteApi.Database.Games.Update(game);subject.OnGameStopped(stopped);
        Check(!repo.Snapshot().Active.ContainsKey(game.Id.ToString()),"Durable session was lost when volatile tracking was absent");
        var recorded=repo.Snapshot().Entries.Last(e=>e.Kind=="session"&&e.Game==key);Check(recorded.Seconds==5,"Native session duration changed");
        using(var held=new FileStream(Path.Combine(first,"progress.sav"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            await subject.SyncPersonalAsync(true,null);
            Check(game.Playtime==new Ledger(repo.Snapshot().Entries).Total(key),"Locked save blocked native playtime projection");
            var remote=Path.Combine(root,"Cloud","personal","playnite-link-v1","ledger","objects");
            Check(Directory.GetFiles(remote,"*.json").Any(path=>File.ReadAllText(path).Contains(recorded.Id)),"Locked save blocked session upload");
        }
        await UntilIdle();
        var achievements=new AchievementStore(Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,"8ca18482-3ebe-4bf7-b229-a259b2094767"));
        var achievement=new AchievementRecord{Game=key,Scope=Identity.Hash("host-platform"),Id="TEST_UNLOCK",Device=repo.Snapshot().Device,Name="Cross-device achievement",Description="Fixture",Unlocked=true,UnlockedUtc="2026-10-23T01:00:00Z"};achievements.Observe(achievement);
        await subject.SyncPersonalAsync(true,null);Check(File.Exists(Path.Combine(root,"Cloud","personal","playnite-link-v1","achievements","objects",achievement.Hash()+".json")),"Achievement did not upload with game sync");
        await Open("ShowAchievements",new object[]{game},()=>{Check(Elements<TextBlock>(window).Any(t=>t.Text.Contains(" / ")&&t.Text.Contains("unlocked")),"Native achievement summary missing");Check(Elements<ListBox>(window).Any(l=>l.Items.Cast<object>().Any(i=>i.ToString().Contains("Cross-device achievement"))),"Remote achievement absent without add-ons");Capture("achievements");});
        await Open("ShowSaveBackups",new object[0],()=>{Check(!Elements<TextBlock>(window).Any(t=>t.Text.Contains("migration")||t.Text.Contains("retire")),"Migration workflow is still exposed");});
        var bundle=Path.Combine(root,"all-backups.zip");Call("ExportSaveBundle",bundle);Check(File.Exists(bundle),"Global export missing");
        using(var file=File.OpenRead(bundle))using(var zip=new System.IO.Compression.ZipArchive(file,System.IO.Compression.ZipArchiveMode.Read)){var manifest=SaveMigrationFiles.BundleManifest(zip);Check(((JArray)manifest["Games"]).Count==2,"Global export omitted an unlinked game's backups");Check(((JArray)manifest["Games"][0]["Backups"]).Count==backupService.GetBackups(game.Id).Count,"Global export lost backups");}
        Call("ExportSaveBundle",bundle);Check(File.Exists(bundle+".bak"),"Export overwrite did not preserve the prior bundle");
        await Open("ShowBundleImport",new object[]{bundle},()=>{Check(Elements<Button>(window).Count(b=>Convert.ToString(b.Content)=="Review and import…")==2,"Bundle import omitted a configuration");Check(Elements<TextBlock>(window).Any(t=>t.Text=="Unlinked backup fixture"),"Unlinked backup import unavailable");});
        File.WriteAllText(Path.Combine(first,"config.ini"),"force restore fixture");await (Task)Call("SaveOperation",game,new Action(()=>Call("RestoreArchiveIncludingExcluded",game,key,backupService.GetFullBackupPath(backup.BackupFilePath))));Check(File.ReadAllText(Path.Combine(first,"config.ini"))=="local graphics","Restore including exclusions did not restore settings");Check(subject.PersonalVault.State.Links[key].RestoreExclusions.Count==1,"One-time force restore changed exclusion preferences");Check(subject.GetGameMenuItems(new GetGameMenuItemsArgs{Games=new List<Game>{game}}).Any(i=>i.MenuSection=="Playnite Link|Restore backup"),"Recent backup shortcuts missing");
        var head=catalog.Heads(key).First(r=>!r.Deleted);var downloadGame=new Game{Id=Guid.NewGuid(),Name="Download metadata fixture"};await (Task)Call("SaveOperation",game,new Action(()=>Call("DownloadCloudBackup",downloadGame,key,head)));var downloaded=backupService.GetBackups(downloadGame.Id).Single();Check(downloaded.Description==head.Note&&downloaded.IsAutoBackup==head.Automatic&&downloaded.CreatedAt.ToUniversalTime()==DateTimeOffset.Parse(head.CreatedUtc).UtcDateTime,"Downloaded metadata changed");
        Call("ExportSaveBundle",bundle);var liveBefore=File.ReadAllText(Path.Combine(first,"progress.sav"));Call("ResetLocalSaveManagerCore",bundle);Check(File.ReadAllText(Path.Combine(first,"progress.sav"))==liveBefore,"Reset changed live game saves");Check(!subject.PersonalVault.State.Links[key].HasSaves,"Reset retained live save paths");Check(backupService.GetAllGameConfigs().Count==0,"Reset retained native backup configuration");Check(subject.IsSyncPaused,"Reset did not pause cloud synchronization");
        Finish(new{passed=true,checks,backupCore="SaveManager.Core.dll",multiSourceRestore=true,exclusions=true,namedDeviceSessions=true,receiptAfterSync=true,migrationReplay=true,globalBundle=true});
    }
}
