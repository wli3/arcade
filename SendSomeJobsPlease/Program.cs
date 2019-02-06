using Microsoft.DotNet.Helix.Client;
using System;

namespace SendSomeJobsPlease
{
    class Program
    {
        static void Main(string[] args)
        {
            HelixApi api = (HelixApi)ApiFactory.GetAuthenticated("");

            api.BaseUri = new Uri("https://helix.dot.net");

            var job = api.Job.Define()
              .WithSource("pr/test/pip-test")
              .WithType("test/pip")
              .WithBuild("77777.123")
              .WithTargetQueue("RedHat.6.Amd64")
                .DefineWorkItem("Hello World")
                .WithCommand("pip --version")
                .WithEmptyPayload()
                .AttachToJob()
              .SendAsync();

            Console.WriteLine($"Job '{job.GetAwaiter().GetResult().CorrelationId}' created.");

            Console.ReadKey();
        }
    }
}
