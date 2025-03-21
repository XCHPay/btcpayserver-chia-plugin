using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chia.Configuration;
using BTCPayServer.Plugins.Chia.Services.Payments;
using BTCPayServer.Services.Invoices;
using chia.dotnet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBXplorer;

namespace BTCPayServer.Plugins.Chia.Services;

public class ChiaListener(
    InvoiceRepository invoiceRepository,
    ISettingsRepository settingsRepository,
    EventAggregator eventAggregator,
    ChiaRpcProvider chiaRpcProvider,
    ChiaPluginConfiguration chiaPluginConfiguration,
    ILogger<ChiaListener> logger,
    PaymentMethodHandlerDictionary handlers,
    PaymentService paymentService) : IHostedService
{
    public static readonly List<InvoiceStatus> StatusToTrack =
    [
        InvoiceStatus.New,
        InvoiceStatus.Processing
    ];

    private readonly CompositeDisposable _leases = new();
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (chiaPluginConfiguration.ChiaConfigurationItems.Count == 0) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = LoopIndex(chiaPluginConfiguration.ChiaConfigurationItems.Single().Value, _cts.Token);
        return Task.CompletedTask;
    }


    public Task StopAsync(CancellationToken cancellationToken)
    {
        _leases.Dispose();
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task LoopIndex(ChiaPluginConfigurationItem configurationItem, CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
            try
            {
                var listenerState = await LoadTrackingState(configurationItem);
                var pmi = configurationItem.GetPaymentMethodId();

                var fullNodeClient = chiaRpcProvider.GetFullNodeRpcClient(pmi);
                if (listenerState == null)
                {
                    logger.LogInformation("No tracking state found, new blockchain");

                    var blockchainState = await fullNodeClient.GetBlockchainState(cancellationToken: stoppingToken);

                    listenerState = new ChiaListenerState { LastBlockHeight = blockchainState.Peak!.Height };
                    await SetTrackingState(configurationItem, listenerState);
                }
                else
                {
                    var blockchainState = await fullNodeClient.GetBlockchainState(cancellationToken: stoppingToken);
                    var latestBlockNumber = blockchainState.Peak!.Height;

                    logger.LogInformation(
                        "Tracking state, current={CurrentBlockNumber}, latest={LatestBlockNumber}",
                        listenerState.LastBlockHeight, latestBlockNumber);
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    if ((await invoiceRepository.GetMonitoredInvoices(pmi, true, cancellationToken: stoppingToken))
                        .Any(i => StatusToTrack.Any(s => s == i.Status)) ==
                        false)
                    {
                        var blockchainState = await fullNodeClient.GetBlockchainState(cancellationToken: stoppingToken);
                        var lastBlockNumber = blockchainState.Peak!.Height;
                        if (lastBlockNumber > listenerState.LastBlockHeight)
                        {
                            logger.LogInformation("No open invoices, skipping from {BlockNumber} to {NewBlockNumber}",
                                listenerState.LastBlockHeight, lastBlockNumber);
                            listenerState.LastBlockHeight = lastBlockNumber;
                        }

                        Thread.Sleep(30_000);
                    }
                    else
                    {
                        try
                        {
                            var block =
                                await fullNodeClient.GetBlockRecordByHeight(listenerState.LastBlockHeight + 1,
                                    stoppingToken);

                            await OnNewBlockToIndex(pmi, block);
                            logger.LogInformation("New block indexed {BlockNumber}", block.Height);

                            listenerState.LastBlockHeight = block.Height;
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.Contains("Record not found"))
                            {
                                logger.LogInformation("Block not present on node yet {BlockNumber}",
                                    listenerState.LastBlockHeight + 1);
                                Thread.Sleep(5_000);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                    
                    await SetTrackingState(configurationItem, listenerState);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "An error occurred while indexing");
                Thread.Sleep(10_000);
            }
    }

    private async Task SetTrackingState(ChiaPluginConfigurationItem config, ChiaListenerState trackingState)
    {
        await settingsRepository.UpdateSetting(trackingState, ChiaRpcProvider.ListenerStateSettingKey(config));
    }

    private async Task<ChiaListenerState?> LoadTrackingState(ChiaPluginConfigurationItem config)
    {
        return await settingsRepository.GetSettingAsync<ChiaListenerState>(
            ChiaRpcProvider.ListenerStateSettingKey(config));
    }

    private Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation(
            $"Invoice {invoice.Id} received payment {payment.Value} {payment.Currency} {payment.Id}");

        eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return Task.CompletedTask;
    }

    private ChiaPluginConfigurationItem GetConfig(PaymentMethodId pmi)
    {
        return chiaPluginConfiguration.ChiaConfigurationItems[pmi];
    }

    private async Task OnNewBlockToIndex(PaymentMethodId pmi, BlockRecord block)
    {
        await UpdateAnyPendingChiaLikePayment(pmi, block);
    }

    private async Task UpdateAnyPendingChiaLikePayment(PaymentMethodId pmi, BlockRecord block)
    {
        var invoices = (await invoiceRepository.GetMonitoredInvoices(pmi, true))
            .Where(i => StatusToTrack.Contains(i.Status))
            .Where(i => i.GetPaymentPrompt(pmi)?.Activated is true)
            .ToArray();

        if (invoices.Length == 0)
            return;

        await UpdatePaymentStates(pmi, invoices, block);
    }
    
    private async Task UpdatePaymentStates(PaymentMethodId pmi, InvoiceEntity[] invoices, BlockRecord block)
    {
        if (invoices.Length == 0) return;

        var handler = (ChiaLikePaymentMethodHandler)handlers[pmi];

        var expandedInvoices = invoices.Select(entity => (Invoice: entity,
                Prompt: entity.GetPaymentPrompt(pmi),
                PaymentMethodDetails: handler.ParsePaymentPromptDetails(entity.GetPaymentPrompt(pmi)!.Details)))
            .Select(tuple => (
                tuple.Invoice,
                tuple.PaymentMethodDetails,
                tuple.Prompt
            )).ToArray();

        var invoicesPerAddress = expandedInvoices.Where(i => i.Prompt is { Destination: not null })
            .ToDictionary(i => i.Prompt!.Destination.ToLowerInvariant(), i => i);

        var fullNodeClient = chiaRpcProvider.GetFullNodeRpcClient(pmi);

        var (additions, removals) =
            await fullNodeClient.GetAdditionsAndRemovals(block.HeaderHash);
        
        var matches = additions
            .Where(addition => removals.All(removal => removal.Coin.Name != addition.Coin.Name))
            .Where(coinRecord => invoicesPerAddress.ContainsKey(ChiaAddressHelper
                .PuzzleHashToAddress(coinRecord.Coin.PuzzleHash)
                .ToLowerInvariant()));
        
        foreach (var coinRecord in matches)
        {
            var parentCoin = removals.First(removal =>
                removal.Coin.Name.ToLowerInvariant() ==
                coinRecord.Coin.ParentCoinInfo.Replace("0x", "").ToLowerInvariant());
            var (invoice, _, _) =
                invoicesPerAddress
                    [ChiaAddressHelper.PuzzleHashToAddress(coinRecord.Coin.PuzzleHash).ToLowerInvariant()];
            await HandlePaymentData(pmi, ChiaAddressHelper.PuzzleHashToAddress(parentCoin.Coin.PuzzleHash),
                ChiaAddressHelper.PuzzleHashToAddress(coinRecord.Coin.PuzzleHash),
                coinRecord.Coin.Amount,
                coinRecord.Coin.Name, 0, block.Height,
                invoice);
        }

        var updatedPaymentEntities =
            new BlockingCollection<(PaymentEntity Payment, InvoiceEntity invoice)>();
        foreach (var invoice in invoices)
        foreach (var payment in GetPendingChiaLikePayments(invoice, pmi))
        {
            var paymentData = handler.ParsePaymentDetails(payment.Details);
            paymentData.ConfirmationCount = block.Height - paymentData.BlockHeight;

            payment.Status = paymentData.PaymentConfirmed(invoice.SpeedPolicy)
                ? PaymentStatus.Settled
                : PaymentStatus.Processing;
            payment.SetDetails(handler, paymentData);

            updatedPaymentEntities.Add((payment, invoice));
        }

        await paymentService.UpdatePayments(updatedPaymentEntities.Select(tuple => tuple.Payment).ToList());
        foreach (var valueTuples in updatedPaymentEntities.GroupBy(entity => entity.invoice))
            if (valueTuples.Any())
                eventAggregator.Publish(new InvoiceNeedUpdateEvent(valueTuples.Key.Id));
    }
    
    
    private async Task HandlePaymentData(
        PaymentMethodId pmi,
        string from,
        string to,
        BigInteger totalAmount,
        string txId, uint confirmations, uint blockHeight, InvoiceEntity invoice)
    {
        var divisor = BigInteger.Pow(10, GetConfig(pmi).Divisibility);
        var quotient = totalAmount / divisor;
        var remainder = totalAmount % divisor;
        var fractionalPart = (decimal)remainder / (decimal)divisor;
        var totalAmountDecimal = (decimal)quotient + fractionalPart;

        var config = GetConfig(pmi);
        var handler = (ChiaLikePaymentMethodHandler)handlers[pmi];
        ChiaLikePaymentData details = new()
        {
            To = to,
            From = from,
            TransactionId = txId,
            ConfirmationCount = confirmations,
            BlockHeight = blockHeight,
        };

        var paymentData = new PaymentData
        {
            Status = details.PaymentConfirmed(invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing,
            Amount = totalAmountDecimal,
            Created = DateTimeOffset.UtcNow,
            Id = txId,
            Currency = config.Currency,
            InvoiceDataId = invoice.Id
        }.Set(invoice, handler, details);

        var payment = await paymentService.AddPayment(paymentData, [txId]);
        if (payment != null)
            await ReceivedPayment(invoice, payment);
    }
    
    private static IEnumerable<PaymentEntity> GetPendingChiaLikePayments(InvoiceEntity invoice, PaymentMethodId pmi)
    {
        return invoice.GetPayments(false)
            .Where(p => p.PaymentMethodId == pmi)
            .Where(p => p.Status == PaymentStatus.Processing);
    }
}