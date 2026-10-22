using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using PlaytimeRclone;

class CrashRecoveryTests
{
    static int Main(string[] args)
    {
        try
        {
            var root=args[0];Directory.CreateDirectory(root);
            if(args.Length>1&&args[1]=="recover"){BlobStore.RecoverRestore(Path.Combine(root,"saves"));return 0;}
            if(args.Length>1&&args[1]=="child")
            {
                var blobs=new BlobStore(Path.Combine(root,"blobs"));var saves=Path.Combine(root,"saves");var source=Path.Combine(root,"source");
                Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"game.sav"),"new save bytes");
                typeof(BlobStore).GetField("RestoreBoundary",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,new Action<string>(point=>{
                    if(point==args[2]){File.WriteAllText(Path.Combine(root,"ready"),point);Thread.Sleep(Timeout.Infinite);}
                }));
                blobs.Restore(saves,blobs.Capture(source),Revision.ContentHashOf(blobs.Capture(saves)));return 2;
            }
            int passed=0;
            foreach(var original in new[]{true,false})foreach(var point in new[]{"staged","journal","backup","installed","committed"})
            {
                var fixture=Path.Combine(root,(original?"existing-":"absent-")+point);Directory.CreateDirectory(fixture);
                var saves=Path.Combine(fixture,"saves");
                if(original){Directory.CreateDirectory(saves);File.WriteAllText(Path.Combine(saves,"game.sav"),"original save bytes");}
                using(var child=Run(fixture,"child "+point))
                {
                    var until=DateTime.UtcNow.AddSeconds(25);
                    while(!File.Exists(Path.Combine(fixture,"ready"))&&!child.HasExited&&DateTime.UtcNow<until)Thread.Sleep(20);
                    if(!File.Exists(Path.Combine(fixture,"ready"))){if(!child.HasExited)child.Kill();throw new Exception("Fault boundary not reached: "+point);}
                    child.Kill();child.WaitForExit();
                }
                for(int restart=0;restart<2;restart++)using(var recovery=Run(fixture,"recover")){if(!recovery.WaitForExit(25000)){recovery.Kill();throw new Exception("Recovery stalled");}if(recovery.ExitCode!=0)throw new Exception("Recovery failed");}
                var installed=point=="installed"||point=="committed";
                if(installed||original)
                {
                    var expected=installed?"new save bytes":"original save bytes";
                    if(File.ReadAllText(Path.Combine(saves,"game.sav"))!=expected)throw new Exception("Incorrect recovered bytes: "+point);
                }
                else if(Directory.Exists(saves))throw new Exception("Absent save root was unexpectedly installed");
                if(original&&installed){var backup=Directory.GetDirectories(fixture,"saves.pl-backup-*");if(backup.Length!=1||File.ReadAllText(Path.Combine(backup[0],"game.sav"))!="original save bytes")throw new Exception("Original backup lost");}
                if(File.Exists(saves+".pl-restore"))throw new Exception("Recovery not completed");
                Console.WriteLine("PASS abrupt kill + two restarts: "+(original?"existing":"absent")+" root / "+point);passed++;
            }
            var invalid=Path.Combine(root,"invalid");Directory.CreateDirectory(invalid);var target=Path.Combine(invalid,"saves");
            File.WriteAllText(target+".pl-restore","../../elsewhere|1");
            bool rejected=false;try{BlobStore.RecoverRestore(target);}catch(IOException){rejected=true;}
            if(!rejected||!File.Exists(target+".pl-restore"))throw new Exception("Malformed journal accepted");
            Console.WriteLine("PASS malformed journal fails closed and is preserved");passed++;
            Console.WriteLine(passed+" passed, 0 failed");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static Process Run(string root,string mode)
    {return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"\""+root+"\" "+mode){UseShellExecute=false,CreateNoWindow=true});}
}
