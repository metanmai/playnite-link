using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.Generic;
using Playnite.SDK;using Playnite.SDK.Events;using Playnite.SDK.Models;using Playnite.SDK.Plugins;using PlaytimeRclone;using Newtonsoft.Json;
public sealed class HostProbe:GenericPlugin
{
 public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
 public HostProbe(IPlayniteAPI api):base(api){}
 string Root{get{return PlayniteApi.Paths.ConfigurationPath;}}
 string phase="start";PlaytimePlugin subject;int ticks;DispatcherTimer heartbeat;
 IEnumerable<T> Elements<T>(DependencyObject p)where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++){var c=VisualTreeHelper.GetChild(p,i);if(c is T)yield return (T)c;foreach(var e in Elements<T>(c))yield return e;}}
 Window Wizard(){return Application.Current.Windows.OfType<Window>().SingleOrDefault(w=>w.IsVisible&&w.Title=="Set up Playnite Link");}
 Button Find(Window w,string name){w.UpdateLayout();return Elements<Button>(w).Single(b=>Convert.ToString(b.Content)==name);}
 void Click(Window w,string name){var b=Find(w,name);if(!b.IsEnabled)throw new Exception("Disabled: "+name);b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
 void Capture(string name,Window w){w.UpdateLayout();var b=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(w);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(b));using(var f=File.Create(Path.Combine(Root,name+".png")))e.Save(f);}
 void Check(bool value,string message){if(!value)throw new Exception(message);}
 async Task Until(Func<bool> predicate,int ms=10000){var watch=System.Diagnostics.Stopwatch.StartNew();while(!predicate()){if(watch.ElapsedMilliseconds>ms)throw new Exception("Timed out");await Task.Delay(30);}}
 public override void OnApplicationStarted(OnApplicationStartedEventArgs args){PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(Run));}
 async void Run(){try{
  Check(File.Exists(Path.Combine(Root,"PROBE-ONLY")),"Refuse production profile");
  subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
  var data=Path.Combine(PlayniteApi.Paths.ExtensionsDataPath,"8ca18482-3ebe-4bf7-b229-a259b2094767");
  var gameId=new Guid("f3d3b83a-fc3f-40ec-87c5-24094bdd81d9");if(PlayniteApi.Database.Games.Get(gameId)==null)PlayniteApi.Database.Games.Add(new Game{Id=gameId,Name="Onboarding test adventure",Playtime=100});
  heartbeat=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};heartbeat.Tick+=(_,e)=>ticks++;heartbeat.Start();
  await Until(()=>Wizard()!=null);await Until(()=>File.ReadAllText(Path.Combine(Root,"playnite.log")).Contains("Application "+PlayniteApi.ApplicationInfo.ApplicationVersion+" started"));var w=Wizard();Capture("walkthrough-welcome",w);
  Check(!subject.IsSyncRunning,"Sync ran before setup");Click(w,"Get started");Capture("walkthrough-storage",w);
  Check(Find(w,"Test connection")!=null&&Find(w,"Reset connection")!=null,"Connection actions missing");
  phase="test connection";Click(w,"Test connection");await Until(()=>Elements<TextBlock>(w).Any(t=>t.Text.Contains("Connection works"))&&Find(w,"Test connection").IsEnabled);
  await Until(()=>Find(w,"Reset connection").IsEnabled);phase="reset connection";var originalState=File.ReadAllText(Path.Combine(data,"state.json"));
  var resetWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};resetWatcher.Tick+=(_,e)=>{var d=Application.Current.Windows.OfType<Window>().FirstOrDefault(x=>x.Title=="Reset connection"&&x.IsVisible);if(d==null)return;resetWatcher.Stop();Check(d.Owner==w,"Reset dialog owner is not walkthrough");Click(d,"Reset connection");};resetWatcher.Start();
  Click(w,"Reset connection");Check(File.ReadAllText(Path.Combine(data,"state.json"))==originalState,"Reset changed session history");Check(JsonConvert.DeserializeObject<SyncConfig>(File.ReadAllText(Path.Combine(data,"rclone-settings.json"))).RemoteFolder=="","Reset did not clear selection");
  var cfg=Path.Combine(data,"personal-rclone-fixture.conf");File.WriteAllText(cfg,"[second]\ntype = alias\nremote = "+Path.Combine(Root,"Cloud")+"\ntoken = synthetic-test-only\n");
  var selectWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};selectWatcher.Tick+=(_,e)=>{var d=Application.Current.Windows.OfType<Window>().FirstOrDefault(x=>x.Title=="Connect your storage"&&x.IsVisible);if(d==null)return;var list=Elements<ComboBox>(d).Single();if(list.Items.Count<2)return;selectWatcher.Stop();list.SelectedIndex=list.Items.Count-1;Check(System.ComponentModel.TypeDescriptor.GetProperties(list.SelectedItem)["Label"]!=null,"Saved sign-in label cannot be bound by WPF");Click(d,"Use this connection");};selectWatcher.Start();
  phase="reconnect saved sign-in";Click(w,"Choose storage or sign in");await Until(()=>Elements<TextBlock>(w).Any(t=>t.Text.StartsWith("Connected."))&&Find(w,"Continue").IsEnabled);
  var reconnected=JsonConvert.DeserializeObject<SyncConfig>(File.ReadAllText(Path.Combine(data,"rclone-settings.json")));Check(reconnected.RemoteFolder=="second:personal"&&reconnected.ConfigFile==cfg,"Saved sign-in did not reconnect to the original folder");
  Check(!File.Exists(Path.Combine(Root,"transfer-started.txt")),"Testing connection uploaded files");
  phase="choice buttons";var promptWatcher=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};promptWatcher.Tick+=(_,e)=>{var d=Application.Current.Windows.OfType<Window>().FirstOrDefault(x=>x.Title=="Finish connecting storage"&&x.IsVisible);if(d==null)return;promptWatcher.Stop();Check(d.Owner==w,"Cloud prompt hidden behind wrong owner");Check(!Elements<TextBox>(d).Any(),"Boolean prompt used text input");Capture("cloud-choice-buttons",d);Click(d,"No");};promptWatcher.Start();
  var choice=subject.GetType().GetMethod("AskCloudQuestion",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(subject,new object[]{new CloudQuestion{Boolean=true,Help="Use the selected account?",Name="synthetic-choice"}});Check((string)choice=="false","No button did not send false");
  phase="resume walkthrough";
  // Closing setup retains the current step and never begins network work.
  Click(w,"Continue later");Check(Wizard()==null,"Continue later failed");
  var saved=JsonConvert.DeserializeObject<OnboardingOptions>(File.ReadAllText(Path.Combine(data,"onboarding.json")));Check(saved.SetupStep==1&&!saved.SetupComplete,"Wizard progress not saved");
  PlayniteApi.MainView.UIDispatcher.BeginInvoke(new Action(()=>subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Setup walkthrough").Action(null)));
  await Until(()=>Wizard()!=null);w=Wizard();Check(Elements<TextBlock>(w).Any(t=>t.Text=="Storage"),"Wizard did not resume at storage");
  Click(w,"Continue");Capture("walkthrough-games",w);Click(w,"Continue");Capture("walkthrough-options",w);
  Check(!Elements<CheckBox>(w).Single(c=>c.Content is TextBlock&&((TextBlock)c.Content).Text=="Update my online HowLongToBeat progress").IsChecked.Value,"HLTB uploads opted in silently");
  Click(w,"Continue");Capture("walkthrough-review",w);
  phase="first sync";Click(w,subject.IsSyncPaused?"Resume sync":"Start first sync");await Until(()=>File.Exists(Path.Combine(Root,"transfer-started.txt")));
  var queued=subject.SyncPersonalAsync(false,null);var cancelWatch=System.Diagnostics.Stopwatch.StartNew();Click(w,"Stop transfer");
  try{await queued;}catch(OperationCanceledException){}
  await Until(()=>!subject.IsSyncRunning,3000);cancelWatch.Stop();await Task.Delay(350);
  Check(subject.IsSyncPaused,"Stop did not pause automatic sync");Check(Find(w,"Resume sync").IsEnabled,"Resume unavailable");
  var options=JsonConvert.DeserializeObject<OnboardingOptions>(File.ReadAllText(Path.Combine(data,"onboarding.json")));Check(options.SyncPaused,"Pause was not persisted");
  var stamp=File.GetLastWriteTimeUtc(Path.Combine(Root,"transfer-started.txt"));await Task.Delay(1500);await subject.SyncPersonalAsync(false,null);Check(File.GetLastWriteTimeUtc(Path.Combine(Root,"transfer-started.txt"))==stamp&&!subject.IsSyncRunning,"Cancelled sync restarted automatically");
  Capture("walkthrough-paused",w);
  // Swap the deterministic stalled fixture for the installed Rclone in this isolated lab.
  File.Copy(File.ReadAllText(Path.Combine(Root,"real-rclone-path.txt")).Trim(),Path.Combine(Root,"fixture-rclone.exe"),true);
  phase="resume sync";Click(w,"Resume sync");await Until(()=>!subject.IsSyncRunning&&Find(w,"Finish setup").IsEnabled,15000);Check(!subject.IsSyncPaused,"Resume left sync paused");
  Check(Elements<TextBlock>(w).Any(t=>t.Text.Contains("Personal vault synced")),"Resume did not complete");
  await subject.SyncPersonalAsync(true,null);await subject.SyncPersonalAsync(true,null);
  var state=JsonConvert.DeserializeObject<State>(File.ReadAllText(Path.Combine(data,"state.json")));Check(state.Entries.Count(e=>e.Kind=="session")==0,"Retry invented sessions");
  await Until(()=>Find(w,"Finish setup").IsEnabled,3000);Capture("walkthrough-complete",w);Click(w,"Finish setup");Check(Wizard()==null,"Finish did not close walkthrough");
  var done=JsonConvert.DeserializeObject<OnboardingOptions>(File.ReadAllText(Path.Combine(data,"onboarding.json")));Check(done.SetupComplete&&!done.SyncPaused,"Completion not saved");
  Check(ticks>30,"UI did not stay responsive");heartbeat.Stop();
  File.WriteAllText(Path.Combine(Root,"host-result.json"),JsonConvert.SerializeObject(new{passed=true,mode=PlayniteApi.ApplicationInfo.Mode.ToString(),wizardResume=true,startupCompletesWithWizardOpen=true,connectionTestNoUploads=true,resetPreservedHistory=true,savedSignInReconnected=true,booleanPromptButtons=true,promptOwnerCorrect=true,optionalUploadsOff=true,queuedSyncCancelled=true,noAutomaticRestart=true,pausePersisted=true,retrySucceeded=true,repeatedSyncNoDuplicateSessions=true,finishSaved=true,cancelMilliseconds=cancelWatch.ElapsedMilliseconds,uiHeartbeats=ticks},Formatting.Indented));
 }catch(Exception ex){File.WriteAllText(Path.Combine(Root,"host-result.json"),JsonConvert.SerializeObject(new{passed=false,phase,error=ex.ToString()},Formatting.Indented));}}
}
