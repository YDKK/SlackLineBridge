using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SlackLineBridge
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            bool useSentry = false;
            string sentryDsn = "";
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory());
                    config.AddJsonFile("config.json", false, true);
                    config.AddJsonFile("appsettings.AWS.json", true, true);
                    var buildConfig = config.Build();
                    useSentry = buildConfig.GetValue<bool>("Sentry:UseSentry");
                    if (useSentry)
                    {
                        sentryDsn = buildConfig.GetValue<string>("Sentry:Dsn");
                    }
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    if (useSentry)
                    {
                        webBuilder.UseSentry(o =>
                        {
                            o.Dsn = sentryDsn;
                            o.TracesSampleRate = 0;
                            o.SendDefaultPii = true;
                        });
                    }
                    webBuilder.UseStartup<Startup>();
                });
        }
    }
}
