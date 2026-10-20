using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace PlaytimeRclone
{
    public sealed class CloudSettings : ISettings
    {
        private readonly Func<SyncConfig> get;
        private readonly Action<SyncConfig> save;
        private readonly Action manageGames,syncNow;
        private SyncConfig draft;
        private TextBox exe, remote, config;
        public CloudSettings(Func<SyncConfig> get, Action<SyncConfig> save,Action manageGames=null,Action syncNow=null) { this.get=get;this.save=save;this.manageGames=manageGames;this.syncNow=syncNow; BeginEdit(); }
        public void BeginEdit()
        {
            var value=get(); draft=new SyncConfig { RcloneExe=value.RcloneExe,RemoteFolder=value.RemoteFolder,ConfigFile=value.ConfigFile };
            if(exe!=null) { exe.Text=draft.RcloneExe;remote.Text=draft.RemoteFolder;config.Text=draft.ConfigFile; }
        }
        public void CancelEdit() { BeginEdit(); }
        private SyncConfig Read() { return exe==null ? draft : new SyncConfig { RcloneExe=exe.Text.Trim(),RemoteFolder=remote.Text.Trim(),ConfigFile=config.Text.Trim() }; }
        public void EndEdit() { save(Read()); }
        public bool VerifySettings(out List<string> errors)
        {
            errors=new List<string>();try { var value=Read();if(!String.IsNullOrWhiteSpace(value.RemoteFolder))value.Validate(); }catch(Exception ex){errors.Add(ex.Message);}return errors.Count==0;
        }
        private static TextBox Field(Panel panel,string title,string value)
        {
            panel.Children.Add(new TextBlock { Text=title,Margin=new Thickness(0,12,0,5) });
            var box=new TextBox { Text=value,Padding=new Thickness(7),MinWidth=320 };PlaytimePlugin.StyleInput(box);panel.Children.Add(box);return box;
        }
        public UserControl View()
        {
            var panel=new StackPanel { Margin=new Thickness(14) };
            panel.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
            panel.Children.Add(new TextBlock { Text="Your personal cloud vault",FontSize=20,TextWrapping=TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text="Your linked games sync automatically.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,5) });
            var actions=new WrapPanel();
            if(manageGames!=null){var button=new Button{Content="Manage games",Margin=new Thickness(0,8,12,8),Padding=new Thickness(12,6,12,6)};button.Click+=(_,e)=>manageGames();actions.Children.Add(button);}
            if(syncNow!=null){var button=new Button{Content="Sync now",Margin=new Thickness(0,8,0,8),Padding=new Thickness(12,6,12,6)};button.Click+=(_,e)=>syncNow();actions.Children.Add(button);}
            panel.Children.Add(actions);
            exe=Field(panel,"Rclone executable",draft.RcloneExe);
            remote=Field(panel,"Cloud remote and parent folder (cloud:Playnite; blank pauses cloud sync)",draft.RemoteFolder);
            config=Field(panel,"Rclone config file (optional; blank uses the Rclone default)",draft.ConfigFile);
            panel.Children.Add(new TextBlock { Text="Use main menu → Playnite Link → Personal cloud account for browser OAuth. Each user owns a separate remote folder. Saves, playtime and portable preferences use immutable history under playnite-link-v1. To encrypt content, select an Rclone crypt remote. Do not let two tools manage the same live save folder.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,5) });
            return new UserControl { Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto } };
        }
    }
}

