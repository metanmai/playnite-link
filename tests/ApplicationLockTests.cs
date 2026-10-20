using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PlaytimeRclone;

internal static class ApplicationLockTests
{
    static int passed,failed;
    static void True(bool value){if(!value)throw new Exception("Assertion failed");}
    static void Test(string title,Action action){try{action();passed++;Console.WriteLine("PASS "+title);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+title+": "+ex);}}
    static void Pump(int milliseconds=300){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=(_,e)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    [STAThread]static void Main(string[] args)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var now=DateTimeOffset.UtcNow;var security=new LocalSecurity(args[0],()=>now);security.Configure("isolated-test-passcode",1);security.Lock();
        Test("interface uses active Playnite theme resources",()=>{
            var keys=new[]{"TextBrush","TextBrushDarker","PopupBackgroundBrush","ButtonBackgroundBrush","PopupBorderBrush","HighlightGlyphBrush"};
            var names=new[]{"Ink","Muted","Canvas","Surface","Line","Accent"};
            for(int i=0;i<keys.Length;i++){var brush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(i==0?240:i==1?190:20+i),(byte)(i<2?190:40),(byte)(i<2?190:60)));app.Resources[keys[i]]=brush;}
            for(int i=0;i<keys.Length;i++){var property=typeof(PlaytimePlugin).GetProperty(names[i],System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);True(Object.ReferenceEquals(property.GetValue(null,null),app.Resources[keys[i]]));}
        });
        Test("fullscreen palette falls back to fullscreen theme resources instead of white",()=>{
            foreach(var key in new[]{"PopupBackgroundBrush","ButtonBackgroundBrush","PopupBorderBrush","HighlightGlyphBrush","TextBrushDarker"})app.Resources.Remove(key);
            var dark=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.MidnightBlue);app.Resources["ControlBackgroundDarkBrush"]=dark;
            var surface=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.DarkSlateBlue);app.Resources["ControlBackgroundBrush"]=surface;
            foreach(var pair in new[]{new object[]{"Canvas",dark},new object[]{"Surface",surface}}){var property=typeof(PlaytimePlugin).GetProperty((string)pair[0],System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static);True(Object.ReferenceEquals(property.GetValue(null,null),pair[1]));}
        });
        var library=new Window{Title="Synthetic library",Width=400,Height=300,Opacity=0.8};library.Show();
        var settings=new Window{Title="Synthetic settings",Width=300,Height=250,IsEnabled=false};settings.Show();int quit=0;
        PasswordBox input=null;
        using(var controller=new ApplicationLock(app,()=>!security.IsUnlocked,()=>{input=new PasswordBox();return new Window{Title="Synthetic unlock gate",Width=400,Height=300,Content=input};},()=>quit++))
        {
            Test("startup covers library and existing settings before unlocking",()=>{True(library.Opacity==0&&!library.IsEnabled);True(settings.Opacity==0&&!settings.IsEnabled);True(controller.Gate.IsVisible);});
            Test("gate still receives password text through the WPF input pipeline",()=>{controller.Gate.Activate();input.Focus();Pump(50);var composition=new TextComposition(InputManager.Current,input,"x");TextCompositionManager.StartComposition(composition);True(input.Password=="x");input.Clear();});
            Test("closing lock screen requests exit and never reveals library",()=>{controller.Gate.Close();True(quit==1);True(controller.Gate.IsVisible&&library.Opacity==0);});
            Test("wrong passcode keeps all app surfaces blocked",()=>{True(!security.Verify("wrong"));controller.Enforce();True(library.Opacity==0&&!library.IsEnabled);});
            Window extra=null,owner=null;
            Test("new plugin windows are covered while locked",()=>{extra=new Window{Title="Late add-on dialog",Width=200,Height=200};extra.Show();Pump();True(extra.Opacity==0&&!extra.IsEnabled);});
            Test("a theme opacity animation cannot reveal a covered window",()=>{extra.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromSeconds(5)){RepeatBehavior=RepeatBehavior.Forever});controller.Enforce();Pump(100);True(extra.Opacity==0&&!DependencyPropertyHelper.GetValueSource(extra,UIElement.OpacityProperty).IsAnimated);});
            Test("a startup dialog re-enabling its owner is remembered behind the lock",()=>{owner=new Window{Title="Startup dialog owner",Width=200,Height=200,IsEnabled=false};owner.Show();Pump();owner.IsEnabled=true;owner.Opacity=0.65;Pump();True(!owner.IsEnabled&&owner.Opacity==0);});
            Test("verified passcode restores window states exactly",()=>{True(security.Verify("isolated-test-passcode"));controller.Enforce();True(controller.Gate==null);True(library.IsEnabled&&library.Opacity==0.8);True(!settings.IsEnabled&&settings.Opacity==1);True(extra.IsEnabled&&extra.Opacity==1);True(owner.IsEnabled&&owner.Opacity==0.65);});
            Test("expiry automatically relocks without a menu action",()=>{now=now.AddMinutes(2);Pump();True(controller.Gate!=null&&library.Opacity==0&&!library.IsEnabled);});
            Test("passcode unlocks again after expiry",()=>{True(security.Verify("isolated-test-passcode"));controller.Enforce();True(library.IsEnabled&&controller.Gate==null);});
            Test("manual lock immediately covers the app again",()=>{security.Lock();controller.Enforce();True(controller.Gate!=null&&library.Opacity==0);});
        }
        Test("disposal restores surfaces and removes the gate",()=>True(library.IsEnabled&&library.Opacity==0.8));
        Test("unlock-screen construction failure hides app and requests shutdown",()=>{
            var requested=false;using(var broken=new ApplicationLock(app,()=>true,()=>{throw new Exception("fixture render failure");},()=>requested=true))
                True(requested&&library.Opacity==0&&!library.IsEnabled);
        });
        foreach(Window window in new System.Collections.Generic.List<Window>(System.Linq.Enumerable.Cast<Window>(app.Windows)))window.Close();
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");Environment.ExitCode=failed==0?0:1;app.Shutdown();
    }
}
