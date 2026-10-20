using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace PlaytimeRclone
{
    public sealed class CloudQuestion
    {
        public string Name,Help,Default;
        public bool Secret,Required;
        public bool Boolean;
        public List<CloudChoice> Choices=new List<CloudChoice>();
    }
    public sealed class CloudChoice {public string Value {get;set;} public string Label {get;set;} public override string ToString(){return Label;}}
    public static class CloudAccount
    {
        public static JObject Parse(string json)
        {
            if(String.IsNullOrWhiteSpace(json))return new JObject { {"State",""} };
            var result=JObject.Parse(json);
            if(result["State"]!=null&&result["State"].Type!=JTokenType.String)throw new InvalidDataException("Unexpected Rclone configuration state.");
            return result;
        }
        public static void Configure(string exe,string config,string remote,string provider,Func<CloudQuestion,string> ask,Action<string,Action> progress,Dictionary<string,string> initial=null,Func<bool> cancelled=null)
        {
            ConfigureProtocol(args=>Run(exe,args,cancelled),config,remote,provider,ask,progress,initial,cancelled);
        }
        public static void ConfigureProtocol(Func<List<string>,string> run,string config,string remote,string provider,Func<CloudQuestion,string> ask,Action<string,Action> progress,Dictionary<string,string> initial=null,Func<bool> cancelled=null)
        {
            var args=new List<string>{"config","create",remote,provider,"config_is_local","true","--config",config,"--non-interactive"};
            if(initial!=null)foreach(var pair in initial){if(pair.Key!="client_id"&&pair.Key!="client_secret")throw new ArgumentException("Unsupported provider credential field.");args.Add(pair.Key);args.Add(pair.Value);}
            for(int step=0;step<30;step++)
            {
                if(cancelled!=null&&cancelled())throw new OperationCanceledException("Connection cancelled. Your previous connection is unchanged.");
                string response=null;progress("Connecting your cloud account in the browser…",()=>response=run(args));
                var state=Parse(response);var continuation=(string)state["State"];
                if(String.IsNullOrEmpty(continuation))return;
                var option=state["Option"] as JObject;if(option==null)throw new IOException("Rclone returned a continuation without a question.");
                var examples=option["Examples"] as JArray;
                var help=((string)state["Error"]??"")+"\n"+((string)option["Help"]??"");
                
                var question=new CloudQuestion{Name=(string)option["Name"],Help=help,Default=(string)option["Default"]??"",Secret=(bool?)option["IsPassword"]??false,Required=(bool?)option["Required"]??false};
                question.Boolean=String.Equals((string)option["Type"],"bool",StringComparison.OrdinalIgnoreCase);
                if(examples!=null)foreach(var example in examples){var value=(string)example["Value"];if(value!=null)question.Choices.Add(new CloudChoice{Value=value,Label=(string)example["Help"]??value});}
                if(question.Choices.Count>0&&question.Choices.All(c=>c.Value.Equals("true",StringComparison.OrdinalIgnoreCase)||c.Value.Equals("false",StringComparison.OrdinalIgnoreCase)))question.Boolean=true;
                // This entry point connects personal Drive. Shared Drives remain available
                // through an existing configured Rclone connection in Advanced.
                var answer=provider=="drive"&&question.Name=="config_change_team_drive"?"false":ask(question);if(answer==null)throw new OperationCanceledException("Cloud connection cancelled. Existing account settings were kept.");
                if(question.Required&&String.IsNullOrWhiteSpace(answer))throw new ArgumentException("This provider requires a value for "+question.Name+".");
                args=new List<string>{"config","update",remote,"config_is_local","true","--config",config,"--non-interactive","--continue","--state",continuation,"--result",answer};
            }
            throw new IOException("Too many cloud configuration steps. Configure this provider using rclone config.");
        }
        private static string Run(string exe,List<string> args,Func<bool> cancelled)
        {
            var info=new ProcessStartInfo(exe,String.Join(" ",args.Select(RcloneTransport.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(info))
            {
                var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
                var watch=Stopwatch.StartNew();
                while(!process.WaitForExit(250))if((cancelled!=null&&cancelled())||watch.Elapsed.TotalSeconds>240){process.Kill();process.WaitForExit();if(cancelled!=null&&cancelled())throw new OperationCanceledException("Connection cancelled. Your previous connection is unchanged.");throw new IOException("Cloud authorization timed out. Retry connecting.");}
                System.Threading.Tasks.Task.WaitAll(output,errors);
                if(process.ExitCode!=0)throw new IOException("Cloud configuration failed (Rclone exit "+process.ExitCode+"). Provider output is not logged because it may contain credentials.");
                return output.Result;
            }
        }
    }
}
