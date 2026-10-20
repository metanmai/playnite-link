using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlaytimeRclone;

internal static class SecurityTests
{
    static string root;static int passed,failed;
    static string Dir(){var path=Path.Combine(root,Guid.NewGuid().ToString("N").Substring(0,10));Directory.CreateDirectory(path);return path;}
    static void True(bool result){if(!result)throw new Exception("Assertion failed");}
    static void Throws(Action action){try{action();}catch{return;}throw new Exception("Expected rejection");}
    static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex);}}
    [STAThread]static void Main(string[] args)
    {
        root=args[0];Directory.CreateDirectory(root);
        Test("local protection defaults off without inventing a user passcode",()=>{var security=new LocalSecurity(Dir());True(!security.Enabled&&security.IsUnlocked);});
        Test("passcode requires minimum length",()=>{Throws(()=>LocalSecurity.ValidatePasscode("123"));Throws(()=>LocalSecurity.ValidatePasscode("      "));});
        Test("passcode verifier persists under Windows DPAPI without plaintext",()=>{var path=Dir();var security=new LocalSecurity(path);security.Configure("fixture-passcode-123",5);var raw=File.ReadAllText(Path.Combine(path,"local-security.dpapi"));True(!raw.Contains("fixture-passcode-123"));True(!raw.Contains("Hash"));var restarted=new LocalSecurity(path);True(restarted.Enabled&&!restarted.IsUnlocked);True(!restarted.Verify("wrong"));True(restarted.Verify("fixture-passcode-123"));});
        Test("lock now timeout and restart all require authentication",()=>{var now=DateTimeOffset.UtcNow;var path=Dir();var security=new LocalSecurity(path,()=>now);security.Configure("fixture-passcode-123",1);True(security.IsUnlocked);now=now.AddMinutes(2);True(!security.IsUnlocked);True(security.Verify("fixture-passcode-123"));security.Lock();True(!security.IsUnlocked);True(!new LocalSecurity(path,()=>now).IsUnlocked);});
        Test("failed-attempt lockout persists and expires without sleeping",()=>{var now=DateTimeOffset.UtcNow;var path=Dir();var security=new LocalSecurity(path,()=>now);security.Configure("fixture-passcode-123",1);security.Lock();for(int i=0;i<5;i++)True(!security.Verify("wrong"));Throws(()=>security.Verify("fixture-passcode-123"));var restarted=new LocalSecurity(path,()=>now);Throws(()=>restarted.Verify("fixture-passcode-123"));now=now.AddSeconds(31);True(restarted.Verify("fixture-passcode-123"));});
        Test("locked controls cannot replace or remove protection",()=>{var security=new LocalSecurity(Dir());security.Configure("fixture-passcode-123",1);security.Lock();Throws(()=>security.Configure("replacement-passcode",1));Throws(()=>security.Disable());});
        Test("a session lock during passcode verification cannot be undone by its late result",()=>{
            var security=new LocalSecurity(Dir());security.Configure("fixture-passcode-123",1);security.Lock();
            var attempt=Task.Run(()=>{try{return security.Verify("fixture-passcode-123");}catch(OperationCanceledException){return false;}});
            while(!attempt.IsCompleted){security.Lock();Thread.Sleep(10);}True(!attempt.Result&&!security.IsUnlocked);
        });
        Test("damaged protected settings do not silently reset security",()=>{var path=Dir();File.WriteAllText(Path.Combine(path,"local-security.dpapi"),"damaged");Throws(()=>new LocalSecurity(path));});
        Test("unlocked user can remove local protection",()=>{var security=new LocalSecurity(Dir());security.Configure("fixture-passcode-123",1);security.Disable();True(!security.Enabled);});
        Console.WriteLine("RESULT "+passed+" passed, "+failed+" failed");Environment.ExitCode=failed==0?0:1;
    }
}
