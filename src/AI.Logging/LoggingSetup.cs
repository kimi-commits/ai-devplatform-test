using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AI.Logging;

/// <summary>
/// 全部 Agent 記錄:開始、結束、Tool 呼叫、耗時、Token、Cost(規格書 v1 第 14 節)。
/// </summary>
public static class LoggingSetup
{
    public static IServiceCollection AddPlatformLogging(this IServiceCollection services, string logFilePath)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        services.AddLogging(builder => builder.AddSerilog(dispose: true));
        return services;
    }
}
