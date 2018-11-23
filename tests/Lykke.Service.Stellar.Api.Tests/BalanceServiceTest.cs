﻿using Castle.Components.DictionaryAdapter;
using Common.Log;
using Lykke.Common.Log;
using Lykke.Service.Stellar.Api.Core;
using Lykke.Service.Stellar.Api.Core.Domain;
using Lykke.Service.Stellar.Api.Core.Domain.Balance;
using Lykke.Service.Stellar.Api.Core.Domain.Observation;
using Lykke.Service.Stellar.Api.Core.Services;
using Lykke.Service.Stellar.Api.Services.Balance;
using Moq;
using StellarSdk.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Lykke.Service.Stellar.Api.Tests
{
    public class BalanceServiceTest
    {
        [Fact]
        public async Task BalanceService_UpdateWallets_SkipErrorMemo()
        {
            // Arrange

            Mock<IHorizonService> horizonService = new Mock<IHorizonService>();
            Mock<IKeyValueStoreRepository> keyValueStoreRepository = new Mock<IKeyValueStoreRepository>();
            Mock < IObservationRepository < BalanceObservation >> observationRepository = 
                new Mock<IObservationRepository<BalanceObservation>>();
            Mock<IWalletBalanceRepository> walletBalanceRepository = new Mock<IWalletBalanceRepository>();
            string depositBaseAddress = "CX...";
            string memo = "http://stellar-win.me/";
            string[] explorerUrlFormats = new []{""};
            Mock<ILog> l1 = new Mock<ILog>();
            Mock<ILogFactory> log = new Mock<ILogFactory>();
            log.Setup(x => x.CreateLog(It.IsAny<object>())).Returns(l1.Object);
            var transactionDetails = new TransactionDetails()
            {
                Memo = memo,
                CreatedAt = DateTime.UtcNow,
            };

            horizonService.Setup(x => x.GetMemo(It.IsAny<TransactionDetails>())).Returns(memo);
            walletBalanceRepository
                .Setup(x => x.RecordOperationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
                .Verifiable();
            walletBalanceRepository
                .Setup(x => x.RefreshBalance(It.IsAny<IEnumerable<(string, string)>>()))
                .Verifiable();

            horizonService.Setup(x => x.GetTransactions(depositBaseAddress, 
                StellarSdkConstants.OrderAsc, 
                null, 
                It.IsAny<int>()))
                .ReturnsAsync(new EditableList<TransactionDetails>()
                {
                    new TransactionDetails()
                    {
                        Hash = "hash",
                        Memo = memo,
                        CreatedAt = DateTime.UtcNow,
                        EnvelopeXdr = "AAAAAAdlB/ts6RCzHAoU/FjtFBGyu66ibVPVoQuJh9CPgAueAAABk" +
                                      "AClxo0AAAABAAAAAAAAAAEAAAAcc3RlbGwwNV81Yjk5MmUwZDAzOW" +
                                      "I5NS4wMjQzNwAAAAQAAAABAAAAAIJL979+ksErfRfiXWlzB+rQZdH" +
                                      "2j4pZdur2OwL02RfxAAAAAQAAAAAr5Jq3XoimudpnjzcQbriV22rX" +
                                      "httVbubwIx31oPeU1AAAAAFGWU9VAAAAAHC5SBVcvtgAfFcLHrI8q" +
                                      "ouvob0F0uxHhlP9otowVtJvAAAAAAAIi4AAAAABAAAAAIJL979+ks" +
                                      "ErfRfiXWlzB+rQZdH2j4pZdur2OwL02RfxAAAAAQAAAABHY0WsIeN" +
                                      "z8/GVG6ienwp48nk2H0ec8UagsCoFsnd+0wAAAAFGWU9VAAAAAHC5" +
                                      "SBVcvtgAfFcLHrI8qouvob0F0uxHhlP9otowVtJvAAAAAAAAnEAAA" +
                                      "AABAAAAAIJL979+ksErfRfiXWlzB+rQZdH2j4pZdur2OwL02RfxAA" +
                                      "AAAQAAAAAW1fr/5UFFVCSXQSIaRg+Bhgg6pYuTcsIiB0a+PA7cNAA" +
                                      "AAAFGWU9VAAAAAHC5SBVcvtgAfFcLHrI8qouvob0F0uxHhlP9otow" +
                                      "VtJvAAAAAAAB1MAAAAABAAAAAIJL979+ksErfRfiXWlzB+rQZdH2j" +
                                      "4pZdur2OwL02RfxAAAAAQAAAAAW1fr/5UFFVCSXQSIaRg+Bhgg6pYu" +
                                      "TcsIiB0a+PA7cNAAAAAFGWU9VAAAAAHC5SBVcvtgAfFcLHrI8qouvo" +
                                      "b0F0uxHhlP9otowVtJvAAAAAAABOIAAAAAAAAAAAvTZF/EAAABAX/d" +
                                      "mfgcuMq0sTlNhvq4RIFtSNRe+RdNsmantkMWKqcfWjTWNO8YSW26ct" +
                                      "Kens8g9EIiD0RJZUr8oGBAoILCrBY+AC54AAABAEoGfpIrRlugsk0" +
                                      "F5Br3Q7tInzScxEDgoYOKIKoBy3f3nWemHz6puW48rjPlFMs+ovx7X" +
                                      "w" +
                                      "hZnOS27iloMkVzeDA==" 
                    }
                });

            BalanceService balanceService = new BalanceService(horizonService.Object,
                keyValueStoreRepository.Object,
                observationRepository.Object,
                walletBalanceRepository.Object,
                depositBaseAddress,
                explorerUrlFormats,
                log.Object);

            // Act

            await balanceService.UpdateWalletBalances();

            // Assert

            walletBalanceRepository.Verify(
                x => x.RecordOperationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<long>()),
                Times.Never
            );

            walletBalanceRepository.Verify(
                x => x.RefreshBalance(It.Is<IEnumerable<(string, string)>>(v => v.Count() == 0)),
                Times.Exactly(1)
            );
        }

        [Fact]
        public async Task BalanceService_UpdateWallets_ProcessDepositMemo()
        {
            // Arrange

            Mock<IHorizonService> horizonService = new Mock<IHorizonService>();
            Mock<IKeyValueStoreRepository> keyValueStoreRepository = new Mock<IKeyValueStoreRepository>();
            Mock<IObservationRepository<BalanceObservation>> observationRepository =
                new Mock<IObservationRepository<BalanceObservation>>();
            Mock<IWalletBalanceRepository> walletBalanceRepository = new Mock<IWalletBalanceRepository>();
            string depositBaseAddress = "CX...";
            string memo = "r6mzsfwnbkgwtc8cktx4i5nw8e";
            string[] explorerUrlFormats = new[] { "" };
            Mock<ILog> l1 = new Mock<ILog>();
            Mock<ILogFactory> log = new Mock<ILogFactory>();
            log.Setup(x => x.CreateLog(It.IsAny<object>())).Returns(l1.Object);
            var transactionDetails = new TransactionDetails()
            {
                Memo = memo,
                CreatedAt = DateTime.UtcNow,
            };

            horizonService.Setup(x => x.GetMemo(It.IsAny<TransactionDetails>())).Returns(memo);
            walletBalanceRepository.Setup(x => x.RecordOperationAsync(It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<long>())).Verifiable();
            walletBalanceRepository.Setup(x => x.RefreshBalance(It.IsAny<(string, string)[]>()))
                .Verifiable();

            horizonService.Setup(x => x.GetTransactions(depositBaseAddress,
                StellarSdkConstants.OrderAsc,
                null,
                It.IsAny<int>()))
                .ReturnsAsync(new EditableList<TransactionDetails>()
                {
                    new TransactionDetails()
                    {
                        Hash = "hash",
                        Memo = memo,
                        CreatedAt = DateTime.UtcNow,
                        EnvelopeXdr = "AAAAAAdlB/ts6RCzHAoU/FjtFBGyu66ibVPVoQuJh9CPgAueAAABk" +
                                      "AClxo0AAAABAAAAAAAAAAEAAAAcc3RlbGwwNV81Yjk5MmUwZDAzOW" +
                                      "I5NS4wMjQzNwAAAAQAAAABAAAAAIJL979+ksErfRfiXWlzB+rQZdH" +
                                      "2j4pZdur2OwL02RfxAAAAAQAAAAAr5Jq3XoimudpnjzcQbriV22rX" +
                                      "httVbubwIx31oPeU1AAAAAFGWU9VAAAAAHC5SBVcvtgAfFcLHrI8q" +
                                      "ouvob0F0uxHhlP9otowVtJvAAAAAAAIi4AAAAABAAAAAIJL979+ks" +
                                      "ErfRfiXWlzB+rQZdH2j4pZdur2OwL02RfxAAAAAQAAAABHY0WsIeN" +
                                      "z8/GVG6ienwp48nk2H0ec8UagsCoFsnd+0wAAAAFGWU9VAAAAAHC5" +
                                      "SBVcvtgAfFcLHrI8qouvob0F0uxHhlP9otowVtJvAAAAAAAAnEAAA" +
                                      "AABAAAAAIJL979+ksErfRfiXWlzB+rQZdH2j4pZdur2OwL02RfxAA" +
                                      "AAAQAAAAAW1fr/5UFFVCSXQSIaRg+Bhgg6pYuTcsIiB0a+PA7cNAA" +
                                      "AAAFGWU9VAAAAAHC5SBVcvtgAfFcLHrI8qouvob0F0uxHhlP9otow" +
                                      "VtJvAAAAAAAB1MAAAAABAAAAAIJL979+ksErfRfiXWlzB+rQZdH2j" +
                                      "4pZdur2OwL02RfxAAAAAQAAAAAW1fr/5UFFVCSXQSIaRg+Bhgg6pYu" +
                                      "TcsIiB0a+PA7cNAAAAAFGWU9VAAAAAHC5SBVcvtgAfFcLHrI8qouvo" +
                                      "b0F0uxHhlP9otowVtJvAAAAAAABOIAAAAAAAAAAAvTZF/EAAABAX/d" +
                                      "mfgcuMq0sTlNhvq4RIFtSNRe+RdNsmantkMWKqcfWjTWNO8YSW26ct" +
                                      "Kens8g9EIiD0RJZUr8oGBAoILCrBY+AC54AAABAEoGfpIrRlugsk0" +
                                      "F5Br3Q7tInzScxEDgoYOKIKoBy3f3nWemHz6puW48rjPlFMs+ovx7X" +
                                      "w" +
                                      "hZnOS27iloMkVzeDA=="
                    }
                });

            observationRepository.Setup(x => x.GetAsync(It.IsAny<string>())).ReturnsAsync(new BalanceObservation()
            {
                Address = depositBaseAddress
            });

            BalanceService balanceService = new BalanceService(horizonService.Object,
                keyValueStoreRepository.Object,
                observationRepository.Object,
                walletBalanceRepository.Object,
                depositBaseAddress,
                explorerUrlFormats,
                log.Object);

            // Act

            await balanceService.UpdateWalletBalances();

            // Assert

            walletBalanceRepository.Verify(x => x.RecordOperationAsync(It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<long>()), Times.AtLeastOnce);
            
            walletBalanceRepository.Verify(
                x => x.RefreshBalance(It.Is<IEnumerable<(string, string)>>(v => v.Count() > 0)),
                Times.Exactly(1)
            );
        }
    }
}
