using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Chia.Configuration;
using BTCPayServer.Plugins.Chia.Controllers.ViewModels;
using BTCPayServer.Plugins.Chia.Services;
using BTCPayServer.Plugins.Chia.Services.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nethermind.Crypto;

namespace BTCPayServer.Plugins.Chia.Controllers;

[Route("stores/{storeId}/chia")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIChiaLikeStoreController(
    StoreRepository storeRepository,
    ChiaRpcProvider chiaRpcProvider,
    PaymentMethodHandlerDictionary handlers,
    InvoiceRepository invoiceRepository,
    DisplayFormatter displayFormatter,
    ChiaPluginConfiguration pluginConfiguration) : Controller
{
    private StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet]
    public IActionResult GetStoreChiaLikePaymentMethods()
    {
        var vm = GetVM(StoreData);

        return View(vm);
    }

    [NonAction]
    public ViewChiaStoreOptionsViewModel GetVM(StoreData storeData)
    {
        var excludeFilters = storeData.GetStoreBlob().GetExcludedPaymentMethods();

        var vm = new ViewChiaStoreOptionsViewModel();
        foreach (var item in pluginConfiguration.ChiaConfigurationItems.Values)
        {
            var pmi = item.GetPaymentMethodId();
            var matchedPaymentMethod = storeData.GetPaymentMethodConfig<ChiaPaymentMethodConfig>(pmi, handlers);
            vm.Items.Add(new ViewChiaStoreOptionItemViewModel
            {
                PaymentMethodId = pmi,
                DisplayName = item.DisplayName,
                Enabled = matchedPaymentMethod != null && !excludeFilters.Match(pmi),
                MasterPublicKey = matchedPaymentMethod == null ? "" : matchedPaymentMethod.MasterPublicKey
            });
        }

        return vm;
    }


    [HttpGet("{paymentMethodId}")]
    public async Task<IActionResult> GetStoreChiaLikePaymentMethod(PaymentMethodId paymentMethodId)
    {
        if (pluginConfiguration.ChiaConfigurationItems.ContainsKey(paymentMethodId) == false)
            return NotFound();
        
        var excludeFilters = StoreData.GetStoreBlob().GetExcludedPaymentMethods();
        var matchedPaymentMethodConfig =  StoreData.GetPaymentMethodConfig<ChiaPaymentMethodConfig>(paymentMethodId, handlers);

        if (matchedPaymentMethodConfig == null)
            return View(new EditChiaPaymentMethodViewModel
            {
                Enabled = false
            });

        var balances =
            await chiaRpcProvider.GetBalances(paymentMethodId, [.. matchedPaymentMethodConfig.Addresses]);
        var reservedAddresses =
            await ChiaPaymentMethodConfig.GetReservedAddresses(paymentMethodId, invoiceRepository);

        return View(new EditChiaPaymentMethodViewModel
        {
            Enabled = !excludeFilters.Match(paymentMethodId),
            MasterPublicKey = matchedPaymentMethodConfig.MasterPublicKey,
            Addresses = matchedPaymentMethodConfig.Addresses.Select(s =>
                new EditChiaPaymentMethodViewModel.EditChiaPaymentMethodAddressViewModel
                {
                    Available = reservedAddresses.Contains(s) == false,
                    Balance = balances.Single(x => x.Item1 == s).Item2 == null
                        ? "N/A"
                        : displayFormatter.Currency(balances.Single(x => x.Item1 == s).Item2!.Value, "XCH"),
                    Value = s,
                }).ToArray()
        });
    }

    [HttpPost("{paymentMethodId}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> GetStoreChiaLikePaymentMethod(EditChiaPaymentMethodViewModel viewModel,
        PaymentMethodId paymentMethodId)
    {
        if (pluginConfiguration.ChiaConfigurationItems.ContainsKey(paymentMethodId) == false)
            return NotFound();

        var store = StoreData;
        var blob = StoreData.GetStoreBlob();
        var currentPaymentMethodConfig = StoreData.GetPaymentMethodConfig<ChiaPaymentMethodConfig>(paymentMethodId, handlers);
        currentPaymentMethodConfig ??= new ChiaPaymentMethodConfig();
        
        if (!string.IsNullOrEmpty(viewModel.MasterPublicKey) && viewModel.MasterPublicKey != currentPaymentMethodConfig.MasterPublicKey)
        {
            if (!ChiaKeyHelper.IsValidChiaKey(viewModel.MasterPublicKey))
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "Invalid master public key",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction("GetStoreChiaLikePaymentMethod", new { storeId = store.Id, paymentMethodId = paymentMethodId });
            }
            
            // if the master public key changed -> derive new addresses
            var addresses = new List<string>();
            for (uint i = 0; i < 100; i++)
            {
                addresses.Add(ChiaAddressHelper.DeriveAddress(viewModel.MasterPublicKey, i));
            }

            currentPaymentMethodConfig.MasterPublicKey = viewModel.MasterPublicKey;
            currentPaymentMethodConfig.Addresses = addresses.ToArray();
            currentPaymentMethodConfig.DerivationIndex = 100;

            if(addresses.Any() == false)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "No addresses were added. Please make sure the master public key is valid.",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });

                return RedirectToAction("GetStoreChiaLikePaymentMethod", new { storeId = store.Id, paymentMethodId = paymentMethodId });
            }

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"{addresses.Count} addresses were derived from your public key for {paymentMethodId}",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        else if (viewModel.Enabled == blob.IsExcluded(paymentMethodId))
        {
            blob.SetExcluded(paymentMethodId, !viewModel.Enabled);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"{paymentMethodId} is now {(viewModel.Enabled ? "enabled" : "disabled")}",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }

        StoreData.SetPaymentMethodConfig(handlers[paymentMethodId], currentPaymentMethodConfig);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);


        return RedirectToAction("GetStoreChiaLikePaymentMethod", new { storeId = store.Id, paymentMethodId });
    }
}