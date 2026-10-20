using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using PlaytimeRclone;

public sealed class AddonPeerProbe:GenericPlugin
{
    public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    public AddonPeerProbe(IPlayniteAPI api):base(api){}
    PlaytimePlugin subject;JObject scenario;DispatcherTimer timer,confirm;int phase;string root;
    SetupProfile Setup{get{return (SetupProfile)typeof(PlaytimePlugin).GetField("setup",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);}}
    static System.Collections.Generic.IEnumerable<T> Controls<T>(DependencyObject parent)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var match in Controls<T>(child))yield return match;}}
    void Assert(bool value,string message){if(!value)throw new Exception(message);}
    void Capture(Window window,string name)
    {window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,name+".png")))encoder.Save(file);}
    void Finish(bool passed,string error=null)
    {if(timer!=null)timer.Stop();if(confirm!=null)confirm.Stop();File.WriteAllText(Path.Combine(root,"addon-result.json"),JsonConvert.SerializeObject(new{passed,error,peer=(string)scenario["Peer"],mode=PlayniteApi.ApplicationInfo.Mode.ToString(),restartVerified=File.Exists(Path.Combine(root,"restart-expected")),activeTheme=(string)JObject.Parse(File.ReadAllText(Path.Combine(root,"config.json")))["Theme"],settingsNotSynchronized=true,packages=Setup.State.Local.Count},Formatting.Indented));Application.Current.Dispatcher.BeginInvoke(new Action(()=>Application.Current.Shutdown()));}
    public override async void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        root=PlayniteApi.Paths.ConfigurationPath;
        try{
            Assert(File.Exists(Path.Combine(root,"PROBE-ONLY")),"Not an isolated profile");scenario=JObject.Parse(File.ReadAllText(Path.Combine(root,"addon-scenario.json")));
            subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
            if(File.Exists(Path.Combine(root,"restart-expected")))
            {
                await subject.SyncPersonalAsync(false,null);
                foreach(var id in (JArray)scenario["ExpectedPlugins"])Assert(PlayniteApi.Addons.Plugins.Any(p=>p.Id==new Guid((string)id)),"An installed extension was not loaded after restart: "+id);
                Assert(Directory.Exists(Path.Combine(root,"Themes","Desktop","FixtureTheme")),"Theme was not installed");
                Assert((bool)JObject.Parse(File.ReadAllText(Path.Combine(root,"config.json")))["StartInFullscreen"]==(bool)scenario["FullscreenPreference"],"Local launch preference changed");
                Assert(File.ReadAllText(Path.Combine(root,"local-settings-sentinel.txt"))=="keep-local-"+(string)scenario["Peer"],"Local settings changed");
                Capture(Application.Current.MainWindow,"addon-after-restart");Finish(true);return;
            }
            await Task.Delay(1500);await subject.SyncPersonalAsync(false,null);
            timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(300)};timer.Tick+=(_,e)=>Tick();timer.Start();
            subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Add-ons").Action(null);
        }catch(Exception ex){Finish(false,ex.ToString());}
    }
    void Tick()
    {
        try{
            var window=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Playnite Link"&&w.IsVisible);if(window==null)return;window.UpdateLayout();
            if(subject.IsSyncRunning)return;
            if(phase==0){
                var toggle=Controls<CheckBox>(window).Single(c=>c.Content is TextBlock&&((TextBlock)c.Content).Text=="Share extension and theme installations");
                Assert(!Setup.Options.Enabled,"Fresh device did not default to opt-out");Assert(Setup.History.Read(Setup.History.Objects).Count==0,"Opted-out device published packages");toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));window.UpdateLayout();phase=1;
                var check=Controls<Button>(window).Single(b=>Convert.ToString(b.Content)=="Check for changes");check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(!check.IsEnabled&&Convert.ToString(check.Content)=="Checking…","Manual check did not give immediate busy feedback");
                Assert(Controls<TextBlock>(window).Any(t=>t.Text.Contains("Checking installed")||t.Text.Contains("Waiting for the current")),"Manual check progress was not visible");return;
            }
            var status=(string)typeof(PlaytimePlugin).GetField("setupStatus",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);Assert(!status.Contains("waiting:"),status);
            if(PlayniteApi.ApplicationInfo.Mode.ToString()=="Fullscreen"){window.Width=800;window.Height=640;window.UpdateLayout();var applyButton=Controls<Button>(window).Single(b=>Convert.ToString(b.Content)=="Apply selected changes and restart");var point=applyButton.TransformToAncestor(window).Transform(new Point(0,0));Assert(point.Y+applyButton.ActualHeight<=window.ActualHeight,"Apply button fell outside handheld viewport");}
            Capture(window,"addon-review");
            if((string)scenario["Action"]=="Publish")
            {Assert(Setup.State.Local.Count>=2,"Installed packages were not captured");Assert(Setup.Changes().Count==0,"Unexpected publisher conflicts");window.Close();Finish(true);return;}
            Assert(Setup.Changes().Count>=2,"New device did not discover both extension and theme");
            File.WriteAllText(Path.Combine(root,"restart-expected"),"ready");phase=2;timer.Stop();
            confirm=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};confirm.Tick+=(_,e)=>{
                foreach(var dialog in Application.Current.Windows.OfType<Window>().Where(w=>w.Title=="Apply add-ons"&&w.IsVisible).ToArray())
                {dialog.UpdateLayout();var yes=Controls<Button>(dialog).FirstOrDefault(b=>Convert.ToString(b.Content).Replace("_","")=="Yes");if(yes!=null){confirm.Stop();yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return;}}
            };confirm.Start();
            var apply=Controls<Button>(window).Single(b=>Convert.ToString(b.Content)=="Apply selected changes and restart");apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }catch(Exception ex){Finish(false,ex.ToString());}
    }
}
