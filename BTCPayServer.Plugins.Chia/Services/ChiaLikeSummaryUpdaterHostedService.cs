using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chia.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Chia.Services;

public class ChiaLikeSummaryUpdaterHostedService(
    ChiaRpcProvider chiaRpcProvider,
    ChiaPluginConfiguration chiaPluginConfiguration,
    Logs logs)
    : IHostedService
{
    private CancellationTokenSource? _cts;


    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var chiaPluginConfigurationItem in chiaPluginConfiguration.ChiaConfigurationItems)
            _ = StartLoop(_cts.Token, chiaPluginConfigurationItem.Key);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task StartLoop(CancellationToken cancellation, PaymentMethodId pmi)
    {
        logs.PayServer.LogInformation($"Starting listening Chia-like daemons ({pmi})");
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await chiaRpcProvider.UpdateSummary(pmi);
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                {
                    logs.PayServer.LogError(ex, $"Unhandled exception in Summary updater ({pmi})");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
    }
}