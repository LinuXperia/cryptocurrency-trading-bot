﻿using System.Collections.Generic;
using System.Threading.Tasks;
using mleader.tradingbot.Data;
using Microsoft.Extensions.Logging;

namespace mleader.tradingbot.Engine
{
    public interface ITradingEngine
    {
        ExchangeApiConfig ApiConfig { get; set; }
        ITradingStrategy TradingStrategy { get; }

        string ReserveCurrency { get; set; }
        Dictionary<string, decimal> MinimumCurrencyOrderAmount { get; set; }

        List<ITradeHistory> LatestPublicSaleHistory { get; set; }
        List<ITradeHistory> LatestPublicPurchaseHistory { get; set; }
        List<IOrder> LatestAccountSaleHistory { get; set; }
        List<IOrder> LatestAccountPurchaseHistory { get; set; }


        Task<AccountBalance> GetAccountBalanceAsync();
        Task<List<IOrder>> GetOpenOrdersAsync();

        Task StartAsync();
        Task StopAsync();

        /// <summary>
        /// Load data required for strategy calculations from exchange Apis
        /// </summary>
        /// <returns></returns>
        Task<bool> MarkeDecisionsAsync();

        /// <summary>
        /// [SellingPriceInPrinciple] = [ProposedSellingPrice] * (1+ [TradingFeeInPercentage] + [AverageTradingChangeRatio] * ([IsPublicUp]? 1: -1)) + [TradingFeeInAmount]
        /// </summary>
        /// <returns></returns>
        Task<decimal> GetSellingPriceInPrincipleAsync();

        /// <summary>
        /// [PurchasePriceInPrinciple] = [ProposedPurchasePrice] * (1 - [TradingFeeInPercentage] + [AverageTradingChangeRatio] * ([IsPublicUp] ? 1: -1)) + [TradingFeeInAmount]
        /// </summary>
        /// <returns></returns>
        Task<decimal> GetPurchasePriceInPrincipleAsync();
    }
}