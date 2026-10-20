using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlaytimeRclone;

public sealed class HostProbe:GenericPlugin
{
    public override Guid Id{get{return new Guid("130f2083-f8e6-46bd-a9a9-56ff84451f95");}}
    public HostProbe(IPlayniteAPI api):base(api){}
    T Field<T>(PlaytimePlugin subject,string name){return (T)typeof(PlaytimePlugin).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(subject);}
    string Root{get{return PlayniteApi.Paths.ConfigurationPath;}}
    void Assert(bool value,string error){if(!value)throw new Exception(error);}
    public override void OnApplicationStarted(OnApplicationStartedEventArgs args){Run();}
    async Task EnterPasscode(ApplicationLock controller,string value,bool shouldUnlock)
    {
        var gate=controller.Gate;gate.UpdateLayout();
        var password=Find<System.Windows.Controls.PasswordBox>(gate).Single();password.Password=value;
        Find<System.Windows.Controls.Button>(gate).Single(b=>Convert.ToString(b.Content)=="Unlock Playnite").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        for(int i=0;i<200;i++){await Task.Delay(50);if(shouldUnlock?controller.Gate==null:Find<System.Windows.Controls.TextBlock>(gate).Any(t=>t.Text=="Incorrect passcode. Try again."))return;}
        throw new Exception("Passcode button did not complete");
    }
    System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject parent)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T)yield return (T)child;foreach(var item in Find<T>(child))yield return item;}}
    async void Run()
    {
        try
        {
            Assert(File.Exists(Path.Combine(Root,"PROBE-ONLY")),"Refused outside isolated profile");
            var subject=(PlaytimePlugin)PlayniteApi.Addons.Plugins.Single(p=>p.Id==new Guid("8ca18482-3ebe-4bf7-b229-a259b2094767"));
            var security=Field<LocalSecurity>(subject,"security");var controller=Field<ApplicationLock>(subject,"applicationLock");
            await Task.Delay(1500);Assert(subject.IsApplicationLocked,"Startup did not lock");
            var library=Application.Current.MainWindow;Assert(library!=controller.Gate&&library.Opacity==0&&!library.IsEnabled,"Library was exposed");
            var gate=controller.Gate;gate.UpdateLayout();var bitmap=new RenderTargetBitmap((int)gate.ActualWidth,(int)gate.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(gate);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(Root,"app-lock.png")))encoder.Save(file);
            var game=new Game{Id=Guid.NewGuid(),Name="Unlinked app-lock fixture",IsInstalled=true,GameActions=new ObservableCollection<GameAction>{new GameAction{Type=GameActionType.File,Path=Path.Combine(Root,"FixtureGame.exe"),WorkingDir=Root,IsPlayAction=true,TrackingMode=TrackingMode.OriginalProcess}}};PlayniteApi.Database.Games.Add(game);
            PlayniteApi.StartGame(game.Id);await Task.Delay(600);Assert(!File.Exists(Path.Combine(Root,"demo-launches.txt")),"Unlinked game launched while locked");
            var pluginDialog=PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions());pluginDialog.Title="Synthetic add-on settings";pluginDialog.Width=400;pluginDialog.Height=300;pluginDialog.Content=new System.Windows.Controls.TextBlock{Text="Private synthetic settings"};pluginDialog.Show();await Task.Delay(400);
            Assert(pluginDialog.Opacity==0&&!pluginDialog.IsEnabled,"Add-on settings were accessible");
            await EnterPasscode(controller,"wrong",false);Assert(subject.IsApplicationLocked&&library.Opacity==0,"Wrong attempt exposed app");
            await EnterPasscode(controller,"isolated-test-passcode",true);
            Assert(!subject.IsApplicationLocked&&library.IsEnabled&&library.Opacity>0&&controller.Gate==null,"App did not unlock");pluginDialog.Close();
            PlayniteApi.StartGame(game.Id);
            for(int i=0;i<50&&!File.Exists(Path.Combine(Root,"demo-launches.txt"));i++)await Task.Delay(100);
            Assert(File.Exists(Path.Combine(Root,"demo-launches.txt")),"Unlocked game could not launch");
            await Task.Delay(8500);
            subject.GetMainMenuItems(new GetMainMenuItemsArgs()).Single(m=>m.Description=="Lock Playnite now").Action(null);await Task.Delay(300);
            Assert(subject.IsApplicationLocked&&library.Opacity==0&&!library.IsEnabled,"Manual app lock failed");
            await EnterPasscode(controller,"isolated-test-passcode",true);
            File.WriteAllText(Path.Combine(Root,"host-result.json"),JsonConvert.SerializeObject(new{passed=true,mode=PlayniteApi.ApplicationInfo.Mode.ToString(),startupLibraryHidden=true,existingAndNewWindowsBlocked=true,unlinkedGameBlocked=true,wrongPasscodeRejected=true,correctPasscodeRestoredApp=true,unlockedGameLaunched=true,manualRelockVerified=true,fixturePasscodeOnly=true,actualPasscodeButtonsExercised=true},Formatting.Indented));
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(Root,"host-result.json"),JsonConvert.SerializeObject(new{passed=false,error=ex.ToString()},Formatting.Indented));}
    }
}
