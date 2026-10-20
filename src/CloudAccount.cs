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
    }
    public static class CloudAccount
    {
        public static JObject Parse(string json)
        {
            if(String.IsNullOrWhiteSpace(json))return new JObject { {"State",""} };
            var result=JObject.Parse(json);
            if(result["State"]!=null&&result["State"].Type!=JTokenType.String)throw new InvalidDataException("Unexpected Rclone configuration state.");
            return result;
        }
        public static void Configure(string exe,string config,string remote,string provider,Func<CloudQuestion,string> ask,Action<string,Action> progress,Dictionary<string,string> initial=null)
        {
            ConfigureProtocol(args=>Run(exe,args),config,remote,provider,ask,progress,initial);
        }
        public static void ConfigureProtocol(Func<List<string>,string> run,string config,string remote,string provider,Func<CloudQuestion,string> ask,Action<string,Action> progress,Dictionary<string,string> initial=null)
        {
            var args=new List<string>{"config","create",remote,provider,"config_is_local","true","--config",config,"--non-interactive"};
            if(initial!=null)foreach(var pair in initial){if(pair.Key!="client_id"&&pair.Key!="client_secret")throw new ArgumentException("Unsupported provider credential field.");args.Add(pair.Key);args.Add(pair.Value);}
            for(int step=0;step<30;step++)
            {
                string response=null;progress("Connecting your cloud account in the browser…",()=>response=run(args));
                var state=Parse(response);var continuation=(string)state["State"];
                if(String.IsNullOrEmpty(continuation))return;
                var option=state["Option"] as JObject;if(option==null)throw new IOException("Rclone returned a continuation without a question.");
                var examples=option["Examples"] as JArray;
                var help=((string)state["Error"]??"")+"\n"+((string)option["Help"]??"");
                if(examples!=null)help+="\n"+String.Join("\n",examples.Select(e=>(string)e["Value"]+" — "+(string)e["Help"]));
                var question=new CloudQuestion{Name=(string)option["Name"],Help=help,Default=(string)option["Default"]??"",Secret=(bool?)option["IsPassword"]??false,Required=(bool?)option["Required"]??false};
                var answer=ask(question);if(answer==null)throw new OperationCanceledException("Cloud connection cancelled. Existing account settings were kept.");
                if(question.Required&&String.IsNullOrWhiteSpace(answer))throw new ArgumentException("This provider requires a value for "+question.Name+".");
                args=new List<string>{"config","update",remote,"config_is_local","true","--config",config,"--non-interactive","--continue","--state",continuation,"--result",answer};
            }
            throw new IOException("Too many cloud configuration steps. Configure this provider using rclone config.");
        }
        private static string Run(string exe,List<string> args)
        {
            var info=new ProcessStartInfo(exe,String.Join(" ",args.Select(RcloneTransport.Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(info))
            {
                var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(240000)){process.Kill();process.WaitForExit();throw new IOException("Cloud authorization timed out. Retry or configure with rclone config.");}
                System.Threading.Tasks.Task.WaitAll(output,errors);
                if(process.ExitCode!=0)throw new IOException("Cloud configuration failed (Rclone exit "+process.ExitCode+"). Provider output is not logged because it may contain credentials.");
                return output.Result;
            }
        }
    }
}
