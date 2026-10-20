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
    if(previous==null||String.IsNullOrWhiteSpace(previous.RemoteFolder))previous=File.Exists(PreviousConnectionPath)?JsonConvert.DeserializeObject<SyncConfig>(File.ReadAllText(PreviousConnectionPath)):null;
    if(previous!=null&&!String.IsNullOrWhiteSpace(previous.RemoteFolder))expected=ConnectionProbe.Test(previous).Identity;
    if(String.IsNullOrWhiteSpace(expected))throw new InvalidOperationException("The previous vault cannot be identified safely. Reconnect the original configured remote first, then test its connection. Your history and current connection were kept.");
   }
   if(!String.IsNullOrWhiteSpace(vault.State.Remote)&&(String.IsNullOrWhiteSpace(target.Identity)||expected!=target.Identity))
    throw new InvalidOperationException("This connection points to a different vault. Select the original Google account and vault folder. Reusing an OAuth JSON identifies the app, not the Google account. Your history and current connection were kept.");
   return new ConnectionVerification{Identity=target.Identity,Previous=expected};
  }
  private void CommitConnection(SyncConfig next,ConnectionVerification proof)
  {
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
   status="Testing the connection and verifying your vault…";
   var proof=await Task.Run(()=>VerifyConnection(next));
   if(connectionCancelled)throw new OperationCanceledException("Connection cancelled. Your previous connection was kept.");
   if(syncing!=0)throw new InvalidOperationException("A sync started while testing. Stop it, then retry. Your signed-in connection is saved locally.");
   CommitConnection(next,proof);status="Connected. Your cloud vault is ready. Continue setup when you are ready.";
  }
  private async void TestConnection(TextBlock feedback,Button button)
  {
   if(connectionTesting)return;
   if(String.IsNullOrWhiteSpace(config.RemoteFolder)){feedback.Text=status="Choose a storage connection first.";return;}
   var selected=config;connectionTesting=true;button.IsEnabled=false;feedback.Text=status="Testing access to your cloud folder… No saves will be uploaded.";
   try
   {
    var result=await Task.Run(()=>ConnectionProbe.Test(selected));
    if(config!=selected){feedback.Text=status="The connection changed during the test. Test the current selection again.";return;}
    if(!String.IsNullOrWhiteSpace(result.Identity))
    {if(!syncGate.Wait(0))throw new InvalidOperationException("A sync is running. Test again when it finishes.");try{vault.PinVerified(selected.RemoteFolder,result.Identity,String.IsNullOrWhiteSpace(vault.State.ConnectionIdentity)?result.Identity:vault.State.ConnectionIdentity);}finally{syncGate.Release();}}
    feedback.Text=status=result.Message;
   }
   catch(Exception ex){feedback.Text=status="Connection test failed: "+ex.Message;}
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
