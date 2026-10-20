using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Playnite.SDK.Models;

namespace PlaytimeRclone
{
    public sealed class OnboardingOptions {public bool AutomaticGames=true;public bool OnlineHltb;public bool SetupComplete;public int SetupStep;public bool SyncPaused;}
    public sealed partial class PlaytimePlugin
    {
        private OnboardingOptions onboarding;
        private void InitializeOnboarding(bool fresh)
        {
            var path=Path.Combine(Root,"onboarding.json");
            onboarding=File.Exists(path)?JsonConvert.DeserializeObject<OnboardingOptions>(File.ReadAllText(path)):new OnboardingOptions{AutomaticGames=fresh};
            if(onboarding==null)throw new InvalidDataException("Invalid onboarding preferences.");
            if(onboarding.SetupStep<0||onboarding.SetupStep>4)onboarding.SetupStep=0;
            // Older connected profiles keep their setup. Fresh installs always get the walkthrough.
            var connection=Path.Combine(Root,"rclone-settings.json");
            if(File.Exists(connection)&&(!File.Exists(path)||Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path))["SetupComplete"]==null))
            {var previous=JsonConvert.DeserializeObject<SyncConfig>(File.ReadAllText(connection));onboarding.SetupComplete=previous!=null&&!String.IsNullOrWhiteSpace(previous.RemoteFolder);}

        }
        private void SaveOnboarding(){Atomic.Write(Path.Combine(Root,"onboarding.json"),JsonConvert.SerializeObject(onboarding));}
        private void EnsureAutomaticLinks()
        {
            if(onboarding==null||!onboarding.AutomaticGames||String.IsNullOrWhiteSpace(config.RemoteFolder))return;
            var mappings=repository.Snapshot().Mappings;
            var candidates=PlayniteApi.Database.Games.Where(g=>!g.IsRunning&&!g.IsLaunching&&g.PluginId!=Guid.Empty&&!String.IsNullOrWhiteSpace(g.GameId))
                .GroupBy(g=>Source(g)).Where(g=>g.Count()==1).Select(g=>g.First()).ToArray();
            var requests=new List<Enrollment>();
            foreach(var game in candidates)
            {
                var source=Source(game);
                if(mappings.ContainsKey(game.Id.ToString())||mappings.Values.Any(m=>m.Source==source))continue;
                requests.Add(new Enrollment{Local=game.Id.ToString(),Source=source,Original=game.Playtime,Baseline=game.Playtime,Count=game.PlayCount});
                // Save folders remain device-local; a missing location must never be
                // interpreted as an empty save to restore or publish.
            }
            repository.EnrollBatch(requests);
            if(requests.Count>0)RefreshOverviewPage();
        }
        private async void ConnectStorage(){try{await ConnectStorageAsync();}catch(Exception ex){NotifyError(ex);}}
        private sealed class StorageChoice {public string Remote {get;set;} public string ConfigFile {get;set;} public string Label {get;set;}}
        private async Task ConnectStorageAsync()
        {
            connectionCancelled=false;
            if(!EnsureAccess())return;
            var exe=config.RcloneExe;
            if(String.IsNullOrWhiteSpace(exe)||!File.Exists(exe))
            {
                status="Preparing verified cloud connection tools in the background…";
                try{exe=await Task.Run(()=>RcloneRuntime.Ensure(Root));config.RcloneExe=exe;}
                catch(Exception ex){NotifyError(ex);return;}
            }
            var window=Window("Connect your storage",680,560);var panel=Page(window,"Connect your storage","Select your own cloud connection. Games from supported library providers are linked automatically; GameActivity and HowLongToBeat are detected automatically.");
            var feedback=Label(panel,"Finding available connections…");
            var choices=new ComboBox{MinWidth=320,Margin=new Thickness(0,12,0,12)};StyleInput(choices);panel.Children.Add(choices);
            StorageChoice selected=null;bool signIn=false;
            choices.DisplayMemberPath="Label";
            var connect=Button(panel,"Use this connection",()=>{selected=choices.SelectedItem as StorageChoice;if(selected!=null)window.Close();});connect.IsEnabled=false;
            Button(panel,"Sign in to a new cloud account",()=>{signIn=true;window.Close();});
            Label(panel,"Manual games need a shared link name. Save folders are selected locally when no reliable location is known. Extension/theme sharing remains a separate opt-in.",13,true);
            var load=Task.Run(()=>{
                var files=new List<string>{config.ConfigFile??""};
                files.AddRange(Directory.GetFiles(Root,"personal-rclone-*.conf").Where(file=>System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file),"(?m)^token\\s*=")));
                var found=new List<StorageChoice>();
                foreach(var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var args="listremotes"+(String.IsNullOrWhiteSpace(file)?"":" --config "+RcloneTransport.Quote(file));
                    using(var process=Process.Start(new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
                    {
                        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                        if(!process.WaitForExit(10000)){process.Kill();throw new IOException("Storage discovery timed out. You can retry or sign in again.");}
                        Task.WaitAll(output,error);if(process.ExitCode!=0)continue;
                        foreach(var name in output.Result.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Where(n=>System.Text.RegularExpressions.Regex.IsMatch(n,"^[A-Za-z0-9][A-Za-z0-9_-]*:$")))
                            found.Add(new StorageChoice{Remote=name,ConfigFile=file,Label=name.TrimEnd(':')+(file==config.ConfigFile?" · current sign-in":" · saved sign-in "+File.GetLastWriteTime(file).ToString("t"))});
                    }
                }
                return found.ToArray();
            });
            window.Loaded+=async(_,e)=>{try{var names=await load;foreach(var name in names)choices.Items.Add(name);if(names.Length>0){choices.SelectedIndex=0;connect.IsEnabled=true;}feedback.Text=names.Length==0?"No configured connection found. Sign in below.":"Choose a connection. Your existing vault is discovered in its PlayniteLink folder.";}catch(Exception ex){feedback.Text=ex.Message;}};
            window.ShowDialog();
            if(signIn)await ConnectProviderAsync();
            else if(selected!=null)await SaveConnectionAsync(new SyncConfig{RcloneExe=exe,ConfigFile=selected.ConfigFile,RemoteFolder=selected.Remote+SelectedVaultFolder()});
        }
    }
}
