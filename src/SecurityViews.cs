using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PlaytimeRclone
{
    public sealed partial class PlaytimePlugin
    {
        private LocalSecurity security;
        private void WindowsSessionChanged(object sender,SessionSwitchEventArgs args)
        {if(args.Reason==SessionSwitchReason.SessionLock||args.Reason==SessionSwitchReason.SessionLogoff)LockApplication();}
        private Button SecurityButton(Panel panel,string label,Action action)
        {
            var button=new Button{Content=label,Padding=new Thickness(14,9,14,9),Margin=new Thickness(0,7,0,7),HorizontalAlignment=HorizontalAlignment.Left};
            StyleAction(button);button.Click+=(_,e)=>{try{action();}catch(Exception ex){PlayniteApi.Dialogs.ShowErrorMessage(ex.Message,"Local protection");}};panel.Children.Add(button);return button;
        }
        private bool EnsureAccess()
        {
            if(!IsApplicationLocked)return true;
            if(applicationLock!=null){applicationLock.Enforce();if(applicationLock.Gate!=null)applicationLock.Gate.Activate();}return false;
        }
        private void BuildProtection(Window window)
        {
            if(!EnsureAccess())return;
            var panel=Page(window,"Lock Playnite","Require a passcode to access Playnite.","Protection");
            var summary=Card(panel,security.Enabled?"App lock is on":"App lock is off","Locks the library, settings and game launching. Background sync continues.");
            var form=Card(panel,security.Enabled?"Change your passcode":"Set a passcode","Use 6–128 characters. A longer passphrase is easier to remember.");
            Label(form,"New passcode",13,true);var passcode=new PasswordBox{Padding=new Thickness(10),Background=Canvas,Foreground=Ink,BorderBrush=Line,Margin=new Thickness(0,0,0,16)};form.Children.Add(passcode);
            Label(form,"Confirm passcode",13,true);var repeat=new PasswordBox{Padding=new Thickness(10),Background=Canvas,Foreground=Ink,BorderBrush=Line,Margin=new Thickness(0,0,0,12)};form.Children.Add(repeat);
            var options=new StackPanel{Margin=new Thickness(0,12,0,0)};var minutes=EntryField(options,"Lock Playnite after this many minutes since unlock (1–60)",security.UnlockMinutes.ToString());Label(options,"Playnite also locks on startup, Windows session lock and manual locking. Running games continue; relocking does not stop the game.",13,true);Details(form,"Auto-lock settings",options);
            var feedback=Label(form,"",13);bool busy=false;
            var footer=Actions(Footer(window));var save=SecurityButton(footer,security.Enabled?"Update app lock":"Enable Playnite lock",async()=> {
                if(busy)return;int duration;if(!Int32.TryParse(minutes.Text,out duration)){feedback.Text="Enter whole minutes.";return;}
                var value=passcode.Password;if(value!=repeat.Password){feedback.Text="Passcodes do not match.";return;}
                try{LocalSecurity.ValidatePasscode(value);}catch(Exception ex){feedback.Text=ex.Message;return;}
                busy=true;passcode.Clear();repeat.Clear();feedback.Text="Saving protected settings…";
                try{await Task.Run(()=>security.Configure(value,duration));feedback.Text="Passcode updated.";}catch(Exception ex){feedback.Text=ex.Message;}finally{value=null;busy=false;}
            });StyleAction(save,true);
            if(security.Enabled)SecurityButton(summary,"Lock Playnite now",()=>{window.Close();LockApplication();});
            var details=new StackPanel();Label(details,"This hides and disables Playnite's windows and blocks game launches while this extension is running. It does not encrypt files or prevent bypass by disabling the extension, using safe mode, or accessing files as the Windows account owner.",13,true);
            if(security.Enabled)SecurityButton(details,"Remove passcode protection…",()=> {
                if(PlayniteApi.Dialogs.ShowMessage("Remove the Playnite app lock on this device? Cloud credentials and data are unaffected.","App protection",MessageBoxButton.YesNo)==MessageBoxResult.Yes){security.Disable();accountPages.Remove("Protection");ShowAccountPage("Protection");}
            });
            Details(panel,"What this protects",details);
            SecurityButton(footer,"Close",()=>{if(!busy)window.Close();});

        }
    }
}
