using DisplayMagicianService;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Set content root to the exe directory so appsettings.json is found
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<SessionMonitor>();

if (WindowsServiceHelpers.IsWindowsService())
{
    // Use our custom lifetime that handles session lock/unlock events
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "DisplayMagicianService";
    });

    // Replace the default WindowsServiceLifetime with our session-aware version
    builder.Services.AddSingleton<IHostLifetime, SessionAwareServiceLifetime>();
}

builder.Services.AddHostedService<TelegramBotWorker>();

var host = builder.Build();
host.Run();
