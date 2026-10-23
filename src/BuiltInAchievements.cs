using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using Expression=System.Linq.Expressions.Expression;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        GenericPlugin achievementEngine;
        string achievementEngineError;
        Timer achievementChangeTimer;
        readonly List<Tuple<object,EventInfo,Delegate>> achievementSubscriptions=new List<Tuple<object,EventInfo,Delegate>>();
        void AchievementDataChanged()
        {
            if(closing||IsSyncPaused)return;
            lock(achievementSubscriptions)
            {
                if(achievementChangeTimer==null)achievementChangeTimer=new Timer(_=>{if(!closing&&!IsSyncPaused)QueueSync();},null,1500,Timeout.Infinite);
                else achievementChangeTimer.Change(1500,Timeout.Infinite);
            }
        }
        void WatchAchievementChanges(object target,string eventName)
        {
            if(target==null)return;var info=target.GetType().GetEvent(eventName);if(info==null)return;
            var parameters=info.EventHandlerType.GetMethod("Invoke").GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
            var handler=Expression.Lambda(info.EventHandlerType,Expression.Call(Expression.Constant(this),GetType().GetMethod("AchievementDataChanged",BindingFlags.Instance|BindingFlags.NonPublic)),parameters).Compile();
            info.AddEventHandler(target,handler);achievementSubscriptions.Add(Tuple.Create(target,info,handler));
        }
        void StopAchievementChanges()
        {
            lock(achievementSubscriptions){if(achievementChangeTimer!=null)achievementChangeTimer.Dispose();foreach(var item in achievementSubscriptions)item.Item2.RemoveEventHandler(item.Item1,item.Item3);achievementSubscriptions.Clear();}
        }
        sealed class AchievementApiProxy:RealProxy
        {
            readonly object target;readonly Action settings;readonly Guid id;
            public AchievementApiProxy(Type type,object target,Action settings,Guid id):base(type){this.target=target;this.settings=settings;this.id=id;}
            public override IMessage Invoke(IMessage message)
            {
                var call=(IMethodCallMessage)message;
                try
                {
                    if(call.MethodName=="OpenPluginSettings"&&call.Args.Length==1&&Object.Equals(call.Args[0],id)){settings();return new ReturnMessage(null,null,0,call.LogicalCallContext,call);}
                    var method=(MethodInfo)call.MethodBase;var result=method.Invoke(target,call.Args);
                    if(call.MethodName=="get_MainView")result=new AchievementApiProxy(method.ReturnType,result,settings,id).GetTransparentProxy();
                    return new ReturnMessage(result,call.Args,call.Args.Length,call.LogicalCallContext,call);
                }
                catch(Exception ex){return new ReturnMessage(ex.InnerException??ex,call);}
            }
        }
        void InitializeAchievementEngine()
        {
            var directory=Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location),"AchievementEngine");
            var module=Path.Combine(directory,"PlayniteAchievements.dll");if(!File.Exists(module))return;
            try
            {
                var localization=Path.Combine(directory,"Localization","en_US.xaml");
                if(File.Exists(localization))Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri(localization,UriKind.Absolute)});
                var language=PlayniteApi.ApplicationSettings.Language;
                if(!String.IsNullOrWhiteSpace(language)&&System.Text.RegularExpressions.Regex.IsMatch(language,"^[a-z]{2}_[A-Z]{2}$")){var translated=Path.Combine(directory,"Localization",language+".xaml");if(language!="en_US"&&File.Exists(translated))Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri(translated,UriKind.Absolute)});}
                var extensions=Path.Combine(PlayniteApi.Paths.ConfigurationPath,"Extensions");var own=Path.GetFullPath(Path.GetDirectoryName(GetType().Assembly.Location));
                foreach(var folder in Directory.GetDirectories(extensions))
                {
                    if(String.Equals(Path.GetFullPath(folder),own,StringComparison.OrdinalIgnoreCase))continue;
                    if(!File.Exists(Path.Combine(folder,"PlayniteAchievements.dll"))||!File.Exists(Path.Combine(folder,"extension.yaml")))continue;
                    var versionText=System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(folder,"extension.yaml")),"(?m)^Version:\\s*([0-9.]+)").Groups[1].Value;Version installed;if(!Version.TryParse(versionText,out installed)||installed>new Version(4,1,1))throw new IOException("A newer or unsupported separate achievement installation must be reviewed before Link can take over. Its data is preserved.");
                    SaveFiles.CheckAncestors(folder);var request=Path.Combine(BaseRoot,"achievement-engine-recovery","retire-"+Identity.Hash(folder)+".json");var destination=Path.Combine(BaseRoot,"achievement-engine-recovery","packages",Guid.NewGuid().ToString("N"));
                    var files=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Select(file=>new{Name=file.Substring(folder.Length+1),Hash=SaveMigrationFiles.Hash(file)}).ToArray();
                    Atomic.Write(request,Newtonsoft.Json.JsonConvert.SerializeObject(new{Process=Process.GetCurrentProcess().Id,Source=folder,Extensions=extensions,Destination=destination,Files=files}));
                    var helper=Path.Combine(own,"RetireAchievementAddon.ps1");if(!File.Exists(helper))throw new IOException("The achievement integration helper is missing.");
                    Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "+RcloneTransport.Quote(helper)+" -Request "+RcloneTransport.Quote(request)){UseShellExecute=false,CreateNoWindow=true});
                    if(setup!=null){setup.Options.LocalOnly.Add(Identity.Hash("Extensions:"+Path.GetFileName(folder)));setup.SaveOptions();}
                    achievementEngineError="Existing achievement data is preserved. Restart Playnite to finish the built-in integration.";return;
                }
                if(PlayniteApi.Addons.Plugins.Any(p=>p.GetType().FullName=="PlayniteAchievements.PlayniteAchievementsPlugin"))throw new IOException("A separate Playnite Achievements instance is already loaded. Its data was preserved; only one engine may run.");
                AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{
                    var name=new AssemblyName(e.Name).Name;if(name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return null;
                    var dependency=Path.Combine(directory,name+".dll");return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
                };
                var assembly=Assembly.LoadFrom(module);var manifestType=assembly.GetType("PlayniteAchievements.Common.PluginManifest");
                if(manifestType!=null){var versionField=manifestType.GetField("CachedVersion",BindingFlags.NonPublic|BindingFlags.Static);if(versionField!=null)versionField.SetValue(null,new Lazy<string>(()=>"4.1.1"));}
                var type=assembly.GetType("PlayniteAchievements.PlayniteAchievementsPlugin",true);
                var engineApi=(IPlayniteAPI)new AchievementApiProxy(typeof(IPlayniteAPI),PlayniteApi,()=>Guard(ShowAchievementSettings),new Guid("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b")).GetTransparentProxy();
                achievementEngine=(GenericPlugin)Activator.CreateInstance(type,engineApi);
                WatchAchievementChanges(PluginActivityStore.Member(achievementEngine,"CacheManager"),"GameCacheUpdated");
                WatchAchievementChanges(PluginActivityStore.Member(achievementEngine,"GameCustomDataStore"),"CustomDataChanged");
            }
            catch(Exception ex){achievementEngineError=ex.GetBaseException().Message;logger.Error(ex,"Built-in achievement engine could not initialize; save sync is preserved.");}
        }
        void AchievementEvent(Action<GenericPlugin> action)
        {if(achievementEngine==null)return;try{action(achievementEngine);}catch(Exception ex){achievementEngineError=ex.GetBaseException().Message;logger.Error(ex,"Built-in achievement engine event failed.");}}
        IEnumerable<SidebarItem> AchievementSidebarItems()
        {if(achievementEngine==null)return new SidebarItem[0];return achievementEngine.GetSidebarItems().Select(item=>{item.Title="Link achievements";var opened=item.Opened;item.Opened=()=>EnsureAccess()?opened():null;return item;}).ToArray();}
        IEnumerable<GameMenuItem> BuiltInAchievementGameMenus(GetGameMenuItemsArgs args)
        {if(achievementEngine==null)return new GameMenuItem[0];return achievementEngine.GetGameMenuItems(args).Select(item=>{var section=item.MenuSection??"";var split=section.IndexOf('|');item.MenuSection="Playnite Link|Achievements"+(split<0?"":section.Substring(split));var action=item.Action;if(action!=null)item.Action=eventArgs=>Guard(()=>action(eventArgs));return item;}).ToArray();}
        IEnumerable<MainMenuItem> BuiltInAchievementMainMenus(GetMainMenuItemsArgs args)
        {if(achievementEngine==null)return new MainMenuItem[0];return achievementEngine.GetMainMenuItems(args).Select(item=>{var section=item.MenuSection??"";var split=section.IndexOf('|');item.MenuSection="@Playnite Link|Achievements"+(split<0?"":section.Substring(split));var action=item.Action;if(action!=null)item.Action=eventArgs=>Guard(()=>action(eventArgs));return item;}).ToArray();}
        void ShowGameAchievements(Playnite.SDK.Models.Game game)
        {if(achievementEngine==null){ShowAchievements(game);return;}PluginActivityStore.Call(achievementEngine,"OpenViewAchievementsWindow",game.Id,null);}
        void ShowAchievementSettings()
        {
            if(achievementEngine==null)throw new IOException("The built-in achievement engine is unavailable. "+achievementEngineError);
            var settings=achievementEngine.GetSettings(false);settings.BeginEdit();
            var window=Window("Playnite Link achievement settings",1100,800);var dock=new DockPanel();var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);
            bool saved=false;Button(footer,"Save",()=>{List<string> errors;if(!settings.VerifySettings(out errors))throw new IOException(String.Join("\n",errors));settings.EndEdit();saved=true;window.Close();});Button(footer,"Cancel",()=>window.Close());
            var view=achievementEngine.GetSettingsView(false);view.DataContext=settings;dock.Children.Add(view);window.Content=dock;window.Closed+=(_,e)=>{if(!saved)settings.CancelEdit();};window.ShowDialog();
        }
        void ShowBuiltInAchievementOverview()
        {
            if(achievementEngine==null)throw new IOException("Built-in achievement engine is unavailable. "+achievementEngineError);
            var sidebar=achievementEngine.GetSidebarItems().First();var view=sidebar.Opened();
            var window=Window("Playnite Link achievements",1200,860);window.Content=view;window.ShowDialog();
        }
        public override void OnGameSelected(OnGameSelectedEventArgs args){AchievementEvent(engine=>engine.OnGameSelected(args));}
        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args){AchievementEvent(engine=>engine.OnLibraryUpdated(args));}
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args){AchievementEvent(engine=>engine.OnControllerButtonStateChanged(args));}
        public override IEnumerable<TopPanelItem> GetTopPanelItems(){return achievementEngine==null?new TopPanelItem[0]:achievementEngine.GetTopPanelItems();}
        public override Control GetGameViewControl(GetGameViewControlArgs args){return achievementEngine==null?null:achievementEngine.GetGameViewControl(args);}
    }
}
