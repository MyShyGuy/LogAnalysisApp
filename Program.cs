using System.Reflection;
using Krones.Lms.LogAnalysisApp;
using Krones.Lms.LogAnalysisApp.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

builder.Services.AddWindowsService(options => options.ServiceName = "Krones.Lms.LogAnalysisApp");
builder.Services.Configure<LogAnalysisOptions>(builder.Configuration.GetSection(LogAnalysisOptions.SectionName));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
