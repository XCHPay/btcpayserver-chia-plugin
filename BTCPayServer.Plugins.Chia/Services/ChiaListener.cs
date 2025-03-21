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
using NBitcoin.JsonConverters;
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
        var timeOfLastBlock = DateTimeOffset.UtcNow;
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
                        var newBlockHeight = listenerState.LastBlockHeight + 1;
                        try
                        {
                            var block = await fullNodeClient.GetBlockRecordByHeight(newBlockHeight, stoppingToken);

                            await OnNewBlockToIndex(pmi, block);
                            logger.LogInformation("New block indexed {BlockNumber}", block.Height);

                            listenerState.LastBlockHeight = block.Height;
                            timeOfLastBlock = DateTimeOffset.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.Contains("Record not found"))
                            {
                                if (DateTimeOffset.UtcNow - timeOfLastBlock > TimeSpan.FromSeconds(120))
                                {
                                    logger.LogWarning("No new block for 120 seconds.");
                                }
                                Thread.Sleep(5_000);
                            }
                            else if (ex.Message.Contains("No additions found"))
                            {
                                logger.LogWarning("No additions found in transaction block {BlockHeight}",
                                    newBlockHeight);
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
        if (invoices.Length == 0)
            return;

        var handler = (ChiaLikePaymentMethodHandler)handlers[pmi];

        if (block.IsTransactionBlock)
        {
            var expandedInvoices = invoices.Select(entity => (Invoice: entity,
                    Prompt: entity.GetPaymentPrompt(pmi),
                    PaymentMethodDetails: handler.ParsePaymentPromptDetails(entity.GetPaymentPrompt(pmi)!.Details)))
                .Select(tuple => (
                    tuple.Invoice,
                    tuple.PaymentMethodDetails,
                    tuple.Prompt
                )).ToArray();

            logger.LogInformation("Found {InvoiceCount} invoices for processing", expandedInvoices.Length);

            var invoicesPerPuzzleHash = expandedInvoices.Where(i => i.Prompt is { Destination: not null })
                .ToDictionary(i => ChiaAddressHelper
                    .AddressToPuzzleHash(i.Prompt!.Destination).ToLowerInvariant(), i => i);

            var fullNodeClient = chiaRpcProvider.GetFullNodeRpcClient(pmi);

            var (additions, removals) = await fullNodeClient.GetAdditionsAndRemovals(block.HeaderHash);
            logger.LogInformation("Retrieved {AdditionCount} additions and {RemovalCount} removals", additions.Count(),
                removals.Count());

            if (!additions.Any())
            {
                throw new Exception($"No additions found in transaction block {block.Height}");
            }
            
            var matches = additions
                .Where(addition => removals.All(removal => removal.Coin.Name != addition.Coin.Name))
                .Where(coinRecord => invoicesPerPuzzleHash.ContainsKey(coinRecord.Coin.PuzzleHash.Replace("0x", "")));

            logger.LogInformation("Found {MatchCount} matching coin records", matches.Count());

            foreach (var coinRecord in matches)
            {
                var parentCoin = removals.FirstOrDefault(removal =>
                    removal.Coin.Name.ToLowerInvariant() ==
                    coinRecord.Coin.ParentCoinInfo.Replace("0x", ""));

                if (parentCoin == null)
                {
                    logger.LogWarning("Parent coin not found for transaction {TransactionId}", coinRecord.Coin.Name);
                    continue;
                }

                var (invoice, _, _) = invoicesPerPuzzleHash[coinRecord.Coin.PuzzleHash.Replace("0x", "")];

                await HandlePaymentData(pmi,
                    ChiaAddressHelper.PuzzleHashToAddress(parentCoin.Coin.PuzzleHash),
                    ChiaAddressHelper.PuzzleHashToAddress(coinRecord.Coin.PuzzleHash),
                    coinRecord.Coin.Amount,
                    coinRecord.Coin.Name,
                    0, block.Height,
                    invoice);
            }
        }

        var updatedPaymentEntities = new BlockingCollection<(PaymentEntity Payment, InvoiceEntity invoice)>();
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

        if (updatedPaymentEntities.Any())
            logger.LogInformation("Updating confirmations for {PaymentCount} payments", updatedPaymentEntities.Count);
        
        await paymentService.UpdatePayments(updatedPaymentEntities.Select(tuple => tuple.Payment).ToList());

        foreach (var valueTuples in updatedPaymentEntities.GroupBy(entity => entity.invoice))
        {
            if (valueTuples.Any())
            {
                logger.LogInformation("Publishing InvoiceNeedUpdateEvent for invoice {InvoiceId}", valueTuples.Key.Id);
                eventAggregator.Publish(new InvoiceNeedUpdateEvent(valueTuples.Key.Id));
            }
        }
    }

    private async Task HandlePaymentData(
        PaymentMethodId pmi,
        string from,
        string to,
        BigInteger totalAmount,
        string txId, uint confirmations, uint blockHeight, InvoiceEntity invoice)
    {
        logger.LogInformation("Handling payment data for Invoice: {InvoiceId}, TxId: {TransactionId}, Amount: {Amount}",
            invoice.Id, txId, totalAmount);

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
        {
            logger.LogInformation("Payment {PaymentId} added successfully for Invoice {InvoiceId}", payment.Id,
                invoice.Id);
            await ReceivedPayment(invoice, payment);
        }
        else
        {
            logger.LogWarning("Failed to add payment {TransactionId} for Invoice {InvoiceId}", txId, invoice.Id);
        }
    }


    private static IEnumerable<PaymentEntity> GetPendingChiaLikePayments(InvoiceEntity invoice, PaymentMethodId pmi)
    {
        return invoice.GetPayments(false)
            .Where(p => p.PaymentMethodId == pmi)
            .Where(p => p.Status == PaymentStatus.Processing);
    }
}