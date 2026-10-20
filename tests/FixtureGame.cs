using System;
using System.IO;
using System.Threading;
internal static class FixtureGame
{
    public static void Main()
    {
        var root=Environment.CurrentDirectory;var saves=Path.Combine(root,"DemoSaves");Directory.CreateDirectory(saves);
        var path=Path.Combine(saves,"progress.dat");var value=File.Exists(path)?File.ReadAllText(path):"missing";
        File.AppendAllText(Path.Combine(root,"demo-launches.txt"),value+Environment.NewLine);
        Thread.Sleep(6500);
        File.WriteAllText(path,value=="cloud-before-launch"?"session-1":"session-2");
    }
}
