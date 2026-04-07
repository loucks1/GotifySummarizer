namespace GotifySummarizer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);


            if (builder.Environment.IsDevelopment())
            {
                builder.Configuration.AddUserSecrets<Program>();
            }

            // Environment variables (Docker / production) take highest priority
            builder.Configuration.AddEnvironmentVariables();

            builder.Services.Configure<GotifyOptions>(builder.Configuration.GetSection("Gotify"));

            builder.Services.AddHostedService<Worker>();

            var host = builder.Build();
            host.Run();
        }
    }
}