using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Agent.Telephone.Abstractions.Configs;

namespace Agent.Telephone.Management
{
    internal class LoggerManager
    {
        public static IHostBuilder RegisterServices(IHostBuilder builder, TelephoneConfig config)
        {
            return builder.ConfigureLogging((context, loggerBuilder) =>
            {
                LogSetting logSetting = config.LogSetting;
                LoggingLevelSwitch levelSwitch = new LoggingLevelSwitch();
                levelSwitch.MinimumLevel = ConvertLogLevel(logSetting.LogLevel);

                LoggerConfiguration loggerConfig = new LoggerConfiguration()
                    .MinimumLevel.ControlledBy(levelSwitch)
                    .WriteTo.Async(a => a.File
                    (
                        path: logSetting.LogFilePath,
                        outputTemplate: logSetting.OutputTemplate,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: logSetting.RetainedFileCountLimit
                    ))
                    .WriteTo.Async(a => a.Console(
                        outputTemplate: logSetting.OutputTemplate,
                        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code,
                        applyThemeToRedirectedOutput: true
                    ));
                Log.Logger = loggerConfig.CreateLogger();

                loggerBuilder.ClearProviders();
                loggerBuilder.AddSerilog(Log.Logger, dispose: true);
            });
            
        }

        private static LogEventLevel ConvertLogLevel(string logLevel)
        {

            return logLevel.ToUpper() switch
            {
                "VERB" => LogEventLevel.Verbose,
                "DEBUG" => LogEventLevel.Debug,
                "INFO" => LogEventLevel.Information,
                "WARN" => LogEventLevel.Warning,
                "ERROR" => LogEventLevel.Error,
                "FATAL" => LogEventLevel.Fatal,
                _ => LogEventLevel.Information,
            };
        }
    }
}
