﻿using Avalonia;
using Avalonia.ReactiveUI;
using System;
using System.Diagnostics;
using System.Text;
using LauncherBackend.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;
using SharedBase.Utilities;
using ThriveLauncher.Utilities;
using ThriveLauncher.ViewModels;
using LogLevel = NLog.LogLevel;

namespace ThriveLauncher
{
    class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // We build services before starting avalonia so that we can use launcher backend services before we decide
            // if we want to fire up ourGUI
            var services = BuildLauncherServices();

            Trace.Listeners.Clear();
            Trace.Listeners.Add(services.GetRequiredService<AvaloniaLogger>());

            var programLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(Program));

            programLogger.LogInformation("Thrive Launcher version {Version} starting",
                services.GetRequiredService<VersionUtilities>().LauncherVersion);

            // TODO: detect transparent mode

            programLogger.LogInformation("Launcher starting GUI");

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

                programLogger.LogInformation("Launcher process exiting normally");
            }
            catch (Exception)
            {
            }

            /*finally */
            {
                // Need to dispose the services to get last logs to save

                var loggerProvider = services.GetRequiredService<ILoggerProvider>();

                var nLogLoggerProvider = (NLogLoggerProvider)loggerProvider;
                nLogLoggerProvider.LogFactory.Shutdown();

                loggerProvider.Dispose();
                nLogLoggerProvider.LogFactory.Dispose();

                services.Dispose();
                LogManager.Shutdown();
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        // Can't be made private without breaking the designer
        // ReSharper disable once MemberCanBePrivate.Global
        public static AppBuilder BuildAvaloniaApp()
            => BuildAvaloniaAppWithServices(BuildLauncherServices(false));

        private static AppBuilder BuildAvaloniaAppWithServices(IServiceProvider serviceProvider)
            => AppBuilder.Configure(() => new App(serviceProvider))
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();

        private static ServiceProvider BuildLauncherServices(bool fileLogging = true)
        {
            var services = new ServiceCollection()
                .AddThriveLauncher()
                .AddSingleton<VersionUtilities>()
                .AddScoped<MainWindowViewModel>()
                .AddSingleton<ViewLocator>()
                .AddLogging(config =>
                {
                    // TODO: log to file
                    config.ClearProviders();
                    config.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                    config.AddNLog(GetNLogConfiguration(fileLogging));
                })
                .AddScoped<AvaloniaLogger>()
                .BuildServiceProvider();

            return services;
        }

        private static LoggingConfiguration GetNLogConfiguration(bool fileLogging = true)
        {
            var configuration = new LoggingConfiguration();

            configuration.AddRule(LogLevel.Info, LogLevel.Fatal, new ConsoleTarget());

            if (Debugger.IsAttached)
                configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, new DebuggerTarget());

            if (fileLogging)
            {
                var fileTarget = new FileTarget
                {
                    // TODO: detect the launcher folder we should put the logs folder in
                    FileName = "${basedir}/logs/thrive-launcher-log.txt",
                    ArchiveAboveSize = GlobalConstants.MEBIBYTE * 2,
                    ArchiveEvery = FileArchivePeriod.Month,
                    ArchiveFileName = "${basedir}/logs/thrive-launcher-log.{#}.txt",
                    ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                    ArchiveDateFormat = "yyyy-MM-dd",
                    MaxArchiveFiles = 4,
                    Encoding = Encoding.UTF8,
                    KeepFileOpen = true,
                    ConcurrentWrites = true,

                    // TODO: should we use default instead?
                    LineEnding = LineEndingMode.LF,
                };

                configuration.AddRule(LogLevel.Debug, LogLevel.Fatal, fileTarget);
            }

            configuration.LogFactory.AutoShutdown = true;

            return configuration;
        }
    }
}
