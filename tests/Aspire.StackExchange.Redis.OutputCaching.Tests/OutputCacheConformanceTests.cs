// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.StackExchange.Redis.Tests;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.StackExchange.Redis.OutputCaching.Tests;

public class OutputCacheConformanceTests : ConformanceTests
{
    protected override void RegisterComponent(HostApplicationBuilder builder, Action<StackExchangeRedisSettings>? configure = null, string? key = null)
    {
        if (key is null)
        {
            builder.AddRedisOutputCache(configureSettings: configure);
        }
        else
        {
            builder.AddKeyedRedisOutputCache(key, configure);
        }
    }

    [ConditionalFact]
    public async Task WorksWithOpenTelemetryTracing()
    {
        SkipIfCanNotConnectToServer();

        var builder = CreateHostBuilder();

        builder.AddRedisOutputCache("redis");

        var tcs = new TaskCompletionSource();
        var exportedActivities = new List<Activity>();
        builder.Services.AddOpenTelemetry().WithTracing(builder =>
        {
            builder.AddInMemoryExporter(exportedActivities);
            builder.AddProcessor(new NotificationProcessor(tcs));
        });

        // set the FlushInterval to to zero so the Activity gets created immediately
        builder.Services.Configure<StackExchangeRedisInstrumentationOptions>(options => options.FlushInterval = TimeSpan.Zero);

        using var host = builder.Build();
        // We start the host to make it build TracerProvider.
        // If we don't, nothing gets reported!
        host.Start();

        var cacheStore = host.Services.GetRequiredService<IOutputCacheStore>();
        await cacheStore.GetAsync("myFakeKey", CancellationToken.None);

        // wait for the Activity to be processed
        await tcs.Task;

        Assert.Single(exportedActivities);

        var activity = exportedActivities[0];
        Assert.Equal("GET", activity.OperationName);
        Assert.Contains(activity.Tags, kvp => kvp.Key == "db.system" && kvp.Value == "redis");
    }

    /// <summary>
    /// An OpenTelemetry processor that can notify callers when it has processed an Activity.
    /// </summary>
    private sealed class NotificationProcessor(TaskCompletionSource taskSource) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            taskSource.SetResult();
        }
    }
}
