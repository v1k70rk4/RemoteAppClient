using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteAgent.Updater;

RemoteAgent.Globalization.RuntimeLanguage.ApplyFromSharedSettings();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "RemoteAgent.Updater");
builder.Logging.AddEventLog(o => o.SourceName = "RemoteAgent.Updater");
builder.Services.AddHostedService<SupervisorWorker>();

var host = builder.Build();
host.Run();
