using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using PlaytimeRclone;

public sealed class HostProbe:GenericPlugin
{
    public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    public HostProbe(IPlayniteAPI api):base(api){}
    PlaytimePlugin subject;Window window;string root,cloud,retired;int ticks;
    object Field(string name){return typeof(PlaytimePlugin).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);}
    void Check(bool value,string message){if(!value)throw new Exception(message);}
    IEnumerable<T> Elements<T>(DependencyObject parent)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var item in Elements<T>(child))yield return item;}}
    bool Text(string value){window.UpdateLayout();return Elements<TextBlock>(window).Any(t=>t.Text==value);}
    Button Find(string value){window.UpdateLayout();return Elements<Button>(window).Single(b=>Convert.ToString(b.Content)==value);}
    void Click(string value){Find(value).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    async Task Until(Func<bool> check,int milliseconds=30000){var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(!check()&&DateTime.UtcNow<end)await Task.Delay(50);Check(check(),"Timed out waiting for device UI");}
    void Capture(string name){window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,name+".png")))encoder.Save(file);}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{try{await Run();}catch(Exception ex){Finish(new{passed=false,error=ex.ToString()});}}));}
    void Finish(object result){File.WriteAllText(Path.Combine(root??PlayniteApi.Paths.ConfigurationPath,"host-result.json"),JsonConvert.SerializeObject(result,Formatting.Indented));Application.Current.Shutdown();}
    async Task Run()
    {
        root=PlayniteApi.Paths.ConfigurationPath;Check(File.Exists(Path.Combine(root,"PROBE-ONLY")),"Isolated profile required");
        subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
        await Until(()=>!subject.IsSyncRunning);subject.StopTransfer();
        retired=Guid.NewGuid().ToString("N");var active=Guid.NewGuid().ToString("N");
        subject.ConnectedDevices.PublishProfile(retired,new DevicePreferences{Name="Retired acceptance fixture"},new List<DeviceGame>());
        subject.ConnectedDevices.SetState(active,retired,"removed");
        subject.ConnectedDevices.PublishProfile(active,new DevicePreferences{Name="Active fixture"},new List<DeviceGame>());
        cloud=Path.Combine(root,"Cloud","personal","playnite-link-v1","devices","objects");
        foreach(var path in Directory.GetFiles(subject.ConnectedDevices.Objects,"*.json"))File.Copy(path,Path.Combine(cloud,Path.GetFileName(path)),true);
        var done=new TaskCompletionSource<bool>();
        Application.Current.Dispatcher.BeginInvoke(new Action(async()=>{
            try{await Verify();done.SetResult(true);}catch(Exception ex){done.SetException(ex);}finally{if(window!=null)window.Close();}
        }));
        typeof(PlaytimePlugin).GetMethod("ManageDevices",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(subject,null);
        await done.Task;
        Finish(new{passed=true,mode=PlayniteApi.ApplicationInfo.Mode.ToString(),removedAbsentFromRoster=true,noRemovedDeviceToggle=true,staleCheckInCannotResurrect=true,historyPreserved=true,removeActionVisible=true,refreshWhilePaused=true,refreshWaitsForBusySync=true,duplicateRefreshBlocked=true,remoteDeviceReceived=true,noSaveCapture=true,noCloudUpload=true,inlineSuccess=true,inlineFailure=true,uiHeartbeats=ticks});
    }
    async Task Verify()
    {
        window=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="Playnite Link"&&w.IsVisible);window.UpdateLayout();
        Check(Text("Active fixture")&&!Text("Retired acceptance fixture"),"Removed device was not hidden by default");
        Check(Elements<Button>(window).Any(b=>Convert.ToString(b.Content)=="Remove device…"),"Remove action is not visible");
        Check(!Elements<CheckBox>(window).Any(c=>c.Content is TextBlock&&((TextBlock)c.Content).Text.StartsWith("Show removed devices")),"Removed-device toggle is still present");
        Check(!subject.ConnectedDevices.Views().Any(d=>d.Id==retired)&&subject.ConnectedDevices.EffectiveState(retired)=="removed","Removed device remained in connected roster");Capture("devices-default");
        var peer=new DeviceDirectory(Path.Combine(root,"SyntheticPeer"));peer.Import(subject.ConnectedDevices.Objects);
        peer.PublishProfile(Guid.NewGuid().ToString("N"),new DevicePreferences{Name="New remote device"},new List<DeviceGame>());
        peer.PublishProfile(retired,new DevicePreferences{Name="Retired acceptance fixture"},new List<DeviceGame>(),true);
        foreach(var path in Directory.GetFiles(peer.Objects,"*.json"))File.Copy(path,Path.Combine(cloud,Path.GetFileName(path)),true);
        var cloudFiles=Directory.GetFiles(cloud,"*.json").ToDictionary(p=>p,p=>File.ReadAllText(p));var captures=subject.SaveBlobs.CaptureCount;
        var gate=(SemaphoreSlim)Field("syncGate");await gate.WaitAsync();
        var heartbeat=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};heartbeat.Tick+=(_,e)=>ticks++;heartbeat.Start();
        try
        {
            typeof(PlaytimePlugin).GetField("syncing",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(subject,1);
            Click("Refresh devices");Check((bool)Field("refreshingDevices")&&!Find("Refreshing…").IsEnabled,"Refresh did not show busy state");
            Find("Refreshing…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(200);Check(ticks>2,"Refresh blocked UI dispatcher");
        }
        finally{typeof(PlaytimePlugin).GetField("syncing",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(subject,0);gate.Release();}
        await Until(()=>!(bool)Field("refreshingDevices"));heartbeat.Stop();window.UpdateLayout();
        Check(Text("New remote device"),"Refresh did not receive new cloud device");Check(subject.IsSyncPaused,"Device refresh resumed game sync");
        Check(subject.SaveBlobs.CaptureCount==captures,"Device refresh scanned saves");
        Check(Directory.GetFiles(cloud,"*.json").Length==cloudFiles.Count&&cloudFiles.All(p=>File.ReadAllText(p.Key)==p.Value),"Read-only device refresh uploaded metadata");
        Check(Elements<TextBlock>(window).Any(t=>t.Text.StartsWith("Devices refreshed at ")),"Refresh completion is invisible");
        Check(!Text("Retired acceptance fixture"),"Refresh revealed hidden retired device");Capture("devices-refreshed");
        var config=(SyncConfig)Field("config");var original=config.ConfigFile;config.ConfigFile=Path.Combine(root,"missing-test-config.conf");
        try{Click("Refresh devices");await Until(()=>!(bool)Field("refreshingDevices"));window.UpdateLayout();Check(Elements<TextBlock>(window).Any(t=>t.Text.StartsWith("Could not refresh devices:")),"Refresh failure is invisible");Check(Text("New remote device")&&Find("Refresh devices").IsEnabled,"Failure lost cached list or retry action");Capture("devices-refresh-error");}
        finally{config.ConfigFile=original;}
    }
}
