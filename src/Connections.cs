using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
namespace PlaytimeRclone
{
 public sealed partial class PlaytimePlugin
 {
  private volatile bool connectionCancelled;
  private bool connectionTesting;
  private sealed class ConnectionVerification {public string Identity,Previous;}
  private string SelectedVaultFolder()
  {
   var remote=!String.IsNullOrWhiteSpace(config.RemoteFolder)?config.RemoteFolder:vault.State.Remote;
   return String.IsNullOrWhiteSpace(remote)?"PlayniteLink":remote.Substring(remote.IndexOf(':')+1);
  }
  private string PreviousConnectionPath {get{return Path.Combine(Root,"previous-connection.json");}}
  private bool ConnectionChanged(SyncConfig next){return config==null||next.RemoteFolder!=config.RemoteFolder||next.ConfigFile!=config.ConfigFile||next.RcloneExe!=config.RcloneExe;}
  private ConnectionVerification VerifyConnection(SyncConfig next)
  {
   var target=ConnectionProbe.Test(next);var expected=vault.State.ConnectionIdentity;
   if(String.IsNullOrWhiteSpace(expected)&&!String.IsNullOrWhiteSpace(vault.State.Remote))
   {
    var previous=config;
    if(previous==null||String.IsNullOrWhiteSpace(previous.RemoteFolder))previous=File.Exists(PreviousConnectionPath)?JsonSafety.Deserialize<SyncConfig>(File.ReadAllText(PreviousConnectionPath)):null;
    if(previous!=null&&!String.IsNullOrWhiteSpace(previous.RemoteFolder))expected=ConnectionProbe.Test(previous).Identity;
    if(String.IsNullOrWhiteSpace(expected))throw new InvalidOperationException("The previous vault cannot be identified safely. Reconnect the original configured remote first, then test its connection. Your history and current connection were kept.");
   }
   if(!String.IsNullOrWhiteSpace(vault.State.Remote)&&(String.IsNullOrWhiteSpace(target.Identity)||expected!=target.Identity))
    throw new InvalidOperationException("This connection points to a different vault. Select the original Google account and vault folder. Reusing an OAuth JSON identifies the app, not the Google account. Use Switch vault / start fresh to intentionally change vaults. Your history and current connection were kept.");
   return new ConnectionVerification{Identity=target.Identity,Previous=expected};
  }
  private void CommitConnection(SyncConfig next,ConnectionVerification proof)
  {
   if(vaultSwitchPending)throw new InvalidOperationException("Restart Playnite to finish switching vaults before changing storage.");
   if(syncing!=0)throw new InvalidOperationException("A sync is running. Stop it before changing the connection; your existing selection is unchanged.");
   if(!String.IsNullOrWhiteSpace(next.RemoteFolder))next.Validate();
   var oldRemote=vault.State.Remote;var oldIdentity=vault.State.ConnectionIdentity;
   try
   {
    if(!String.IsNullOrWhiteSpace(next.RemoteFolder)){if(proof!=null)vault.PinVerified(next.RemoteFolder,proof.Identity,proof.Previous);else vault.Pin(next.RemoteFolder);}
    Atomic.Write(Path.Combine(Root,"rclone-settings.json"),JsonConvert.SerializeObject(next,Formatting.Indented));
   }
   catch{vault.State.Remote=oldRemote;vault.State.ConnectionIdentity=oldIdentity;vault.Save();throw;}
   config=next;RefreshOverviewPage();if(onboarding!=null&&onboarding.SetupComplete)QueueSync();
  }
  private async Task SaveConnectionAsync(SyncConfig next)
  {
   if(!EnsureAccess())throw new OperationCanceledException();
   if(syncing!=0)throw new InvalidOperationException("Stop the current transfer before changing storage.");
   ReportStatus("Testing the connection and verifying your vault…",SyncDisplayState.Working,"Testing connection");
   var proof=await Task.Run(()=>VerifyConnection(next));
   if(connectionCancelled)throw new OperationCanceledException("Connection cancelled. Your previous connection was kept.");
   if(syncing!=0)throw new InvalidOperationException("A sync started while testing. Stop it, then retry. Your signed-in connection is saved locally.");
   CommitConnection(next,proof);ReportStatus("Connected. Your cloud vault is ready. Continue setup when you are ready.",SyncDisplayState.Completed,"Connected");
  }
  private async void TestConnection(TextBlock feedback,Button button)
  {
   if(connectionTesting)return;
   if(String.IsNullOrWhiteSpace(config.RemoteFolder)){feedback.Text=ReportStatus("Choose a storage connection first.",SyncDisplayState.Ready,"Connect storage");return;}
   var selected=config;connectionTesting=true;button.IsEnabled=false;feedback.Text=ReportStatus("Testing access to your cloud folder… No saves will be uploaded.",SyncDisplayState.Working,"Testing connection");
   try
   {
    var result=await Task.Run(()=>ConnectionProbe.Test(selected));
    if(config!=selected){feedback.Text=ReportStatus("The connection changed during the test. Test the current selection again.",SyncDisplayState.Review,"Test current connection");return;}
    if(!String.IsNullOrWhiteSpace(result.Identity))
    {if(!syncGate.Wait(0))throw new InvalidOperationException("A sync is running. Test again when it finishes.");try{vault.PinVerified(selected.RemoteFolder,result.Identity,String.IsNullOrWhiteSpace(vault.State.ConnectionIdentity)?result.Identity:vault.State.ConnectionIdentity);}finally{syncGate.Release();}}
    feedback.Text=ReportStatus(result.Message,SyncDisplayState.Completed,"Connection verified");
   }
   catch(Exception ex){feedback.Text=ReportStatus("Connection test failed: "+ex.Message,SyncDisplayState.Failed,"Connection test failed");}
   finally{connectionTesting=false;button.IsEnabled=true;}
  }
  private void ResetConnection()
  {
   if(syncing!=0){StopTransfer();status="Stopping the current transfer. Reset connection again once it stops.";return;}
   var owner=walkthroughWindow??accountWindow??PlayniteApi.Dialogs.GetCurrentAppWindow();
   var dialog=Window("Reset connection",620,390);dialog.Owner=owner;
   var panel=Page(dialog,"Reset this device's connection?","This clears the selected connection. Local history, cloud saves and saved provider sign-ins are kept. You can reconnect to the same vault without losing progress.");
   var confirmed=false;var actions=Actions(Footer(dialog));StyleAction(Button(actions,"Reset connection",()=>{confirmed=true;dialog.Close();}),true);Button(actions,"Keep connection",()=>dialog.Close());dialog.ShowDialog();if(!confirmed)return;
   if(!String.IsNullOrWhiteSpace(config.RemoteFolder))Atomic.Write(PreviousConnectionPath,JsonConvert.SerializeObject(config,Formatting.Indented));
   CommitConnection(new SyncConfig{RcloneExe=config.RcloneExe,ConfigFile="",RemoteFolder=""},null);
   onboarding.SetupComplete=false;onboarding.SetupStep=1;onboarding.SyncPaused=true;SaveOnboarding();
   status="Connection reset on this device. Choose a saved sign-in or connect an account. Your history and cloud saves are unchanged.";
  }
  private bool vaultSwitchPending;
  private void SwitchVault()
  {
   if(vaultSwitchPending){RestartForVault();return;}
   if(!EnsureAccess())return;
   if(syncing!=0||connectionTesting||walkthroughWorking){PlayniteApi.Dialogs.ShowMessage("Stop the current sync or connection attempt before switching vaults.","Playnite Link");return;}
   if(PlayniteApi.Database.Games.Any(g=>g.IsRunning||g.IsLaunching)||repository.Snapshot().Active.Count>0){PlayniteApi.Dialogs.ShowMessage("Stop running games and recover unfinished sessions before switching vaults.","Playnite Link");return;}
   var dialog=Window("Switch vault / start fresh",700,570);
   var panel=Page(dialog,"Switch vault / start fresh","Keep each vault's history separate on this device.");
   Label(panel,"Start fresh opens storage setup with no linked games, save folders or vault lock. You can reuse the same OAuth JSON and choose the Google account and vault you want.");
   Label(panel,"Your existing local sync history is retained as a separate profile. Cloud files, game saves, Playnite's library, installed add-ons and app passcode are kept. Nothing is uploaded or deleted by this action. Playnite must restart before connecting.",14,true);
   VaultProfile selected=null;
   StyleAction(Button(panel,"Start fresh vault setup",()=>{selected=VaultProfiles.Prepare(BaseRoot,config);dialog.Close();}),true);
   var profiles=VaultProfiles.List(BaseRoot).Where(p=>!String.Equals(Path.GetFullPath(p.Directory),Path.GetFullPath(Root),StringComparison.OrdinalIgnoreCase)).ToArray();
   if(profiles.Length>0){Label(panel,"Or return to a saved local vault profile:");var list=new ComboBox{ItemsSource=profiles,SelectedIndex=0,MinWidth=350};StyleInput(list);panel.Children.Add(list);Button(panel,"Use selected vault profile",()=>{selected=list.SelectedItem as VaultProfile;if(selected!=null)dialog.Close();});}
   Button(panel,"Cancel",()=>dialog.Close());dialog.ShowDialog();if(selected==null)return;
   if(!syncGate.Wait(0))throw new InvalidOperationException("A sync started. Stop it before switching vaults.");
   try{if(PlayniteApi.Database.Games.Any(g=>g.IsRunning||g.IsLaunching)||repository.Snapshot().Active.Count>0)throw new InvalidOperationException("Stop running games before switching vaults.");VaultProfiles.Activate(BaseRoot,selected);vaultSwitchPending=true;StopTransfer();}
   finally{syncGate.Release();}
   ReportStatus("Vault selection saved. Restart Playnite to continue setup. Your previous history is retained.",SyncDisplayState.Paused,"Restart Playnite");
   var restart=Window("Vault ready",620,330);var content=Page(restart,"Restart to switch vaults","Your selection is saved. The current vault stays paused until Playnite restarts.");
   Button(content,"Restart Playnite",()=>RestartForVault());Button(content,"Restart later",()=>restart.Close());restart.ShowDialog();
  }
  private void RestartForVault()
  {
   var script=Path.Combine(BaseRoot,"restart-for-vault.ps1");
   Atomic.Write(script,@"param([int]$ParentId,[string]$Executable,[string]$Profile,[switch]$Master)
$ErrorActionPreference='Stop'
trap {$_ | Out-String | Set-Content (Join-Path $PSScriptRoot 'restart-for-vault-error.txt');exit 1}
$parent=Get-Process -Id $ParentId -ErrorAction SilentlyContinue
if($parent){if($parent.Path -ne $Executable){throw 'Unexpected application'};if(!$parent.WaitForExit(120000)){throw 'Playnite has not exited'}}
$argsList=@('--nolibupdate','--hidesplashscreen','--userdatadir',('""'+$Profile+'""'))
if($Master){$argsList+='--masterinstance'}
Start-Process -FilePath $Executable -ArgumentList $argsList -WindowStyle Hidden
");
   var process=System.Diagnostics.Process.GetCurrentProcess();var arguments="-NoProfile -ExecutionPolicy Bypass -File "+RcloneTransport.Quote(script)+" -ParentId "+process.Id+" -Executable "+RcloneTransport.Quote(process.MainModule.FileName)+" -Profile "+RcloneTransport.Quote(PlayniteApi.Paths.ConfigurationPath);
   if(Environment.GetCommandLineArgs().Contains("--masterinstance"))arguments+=" -Master";
   System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",arguments){UseShellExecute=false,CreateNoWindow=true});Application.Current.Shutdown();
  }
  private string AskCloudQuestion(CloudQuestion question)
  {
   if(connectionCancelled)return null;
   var window=Window("Finish connecting storage",650,490);var panel=Page(window,"Finish connecting storage",question.Help.Trim());string answer=null;
   if(question.Boolean)
   {
    var actions=Actions(panel);StyleAction(Button(actions,"Yes",()=>{answer="true";window.Close();}),true);Button(actions,"No",()=>{answer="false";window.Close();});
   }
   else if(!question.Secret&&question.Choices.Count>0)
   {
    var list=new ListBox{ItemsSource=question.Choices,DisplayMemberPath="Label",Height=170};StyleInput(list);panel.Children.Add(list);
    list.SelectedItem=question.Choices.FirstOrDefault(c=>c.Value==question.Default)??question.Choices[0];
    StyleAction(Button(panel,"Continue",()=>{var selected=list.SelectedItem as CloudChoice;if(selected!=null){answer=selected.Value;window.Close();}}),true);
   }
   else if(question.Secret)
   {
    var password=new PasswordBox{Padding=new Thickness(10)};StyleInput(password);panel.Children.Add(password);
    StyleAction(Button(panel,"Continue",()=>{answer=password.Password;password.Clear();window.Close();}),true);
   }
   else
   {
    var value=EntryField(panel,"Value",question.Default);StyleAction(Button(panel,"Continue",()=>{if(question.Required&&String.IsNullOrWhiteSpace(value.Text))return;answer=value.Text;window.Close();}),true);
   }
   Button(Footer(window),"Cancel connection",()=>window.Close());window.ShowDialog();return answer;
  }
 }
}
