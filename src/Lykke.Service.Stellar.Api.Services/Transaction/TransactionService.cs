﻿using System;
using System.Threading.Tasks;
using Common;
using Common.Log;
using JetBrains.Annotations;
using Lykke.Common.Chaos;
using Lykke.Common.Log;
using Lykke.Service.Stellar.Api.Core;
using Lykke.Service.Stellar.Api.Core.Domain;
using Lykke.Service.Stellar.Api.Core.Domain.Balance;
using Lykke.Service.Stellar.Api.Core.Domain.Observation;
using Lykke.Service.Stellar.Api.Core.Domain.Transaction;
using Lykke.Service.Stellar.Api.Core.Exceptions;
using Lykke.Service.Stellar.Api.Core.Services;
using Newtonsoft.Json;
using stellar_dotnet_sdk;
using stellar_dotnet_sdk.requests;
using stellar_dotnet_sdk.xdr;
using Operation = stellar_dotnet_sdk.Operation;
using TimeBounds = stellar_dotnet_sdk.xdr.TimeBounds;

namespace Lykke.Service.Stellar.Api.Services.Transaction
{
    public class TransactionService : ITransactionService
    {
        private string _lastJobError;

        private readonly IBalanceService _balanceService;
        private readonly IHorizonService _horizonService;
        private readonly IObservationRepository<BroadcastObservation> _observationRepository;
        private readonly IWalletBalanceRepository _balanceRepository;
        private readonly ITxBroadcastRepository _broadcastRepository;
        private readonly ITxBuildRepository _buildRepository;
        private readonly TimeSpan _transactionExpirationTime;
        private readonly ILog _log;
        private readonly IBlockchainAssetsService _blockchainAssetsService;
        private readonly IChaosKitty _chaos;

        [UsedImplicitly]
        public TransactionService(IBalanceService balanceService,
                                  IHorizonService horizonService,
                                  IObservationRepository<BroadcastObservation> observationRepository,
                                  IWalletBalanceRepository balanceRepository,
                                  ITxBroadcastRepository broadcastRepository,
                                  ITxBuildRepository buildRepository,
                                  TimeSpan transactionExpirationTime,
                                  ILogFactory logFactory,
                                  IBlockchainAssetsService blockchainAssetsService,
                                  IChaosKitty chaosKitty)
        {
            _balanceService = balanceService;
            _horizonService = horizonService;
            _observationRepository = observationRepository;
            _balanceRepository = balanceRepository;
            _broadcastRepository = broadcastRepository;
            _buildRepository = buildRepository;
            _transactionExpirationTime = transactionExpirationTime;
            _log = logFactory.CreateLog(this);
            _blockchainAssetsService = blockchainAssetsService;
            _chaos = chaosKitty;
        }

        public bool CheckSignature(string xdrBase64)
        {
            bool isSignOk = true;

            try
            {
                var xdr = Convert.FromBase64String(xdrBase64);
                var reader = new XdrDataInputStream(xdr);
                var txEnvelope = TransactionEnvelope.Decode(reader);
            }
            catch (Exception e)
            {
                isSignOk = false;
            }

            return isSignOk;
        }

        public async Task<TxBroadcast> GetTxBroadcastAsync(Guid operationId)
        {
            return await _broadcastRepository.GetAsync(operationId);
        }

        public async Task BroadcastTxAsync(Guid operationId, string xdrBase64)
        {
            long amount = 0;
            var xdr = Convert.FromBase64String(xdrBase64);
            var reader = new XdrDataInputStream(xdr);
            var txEnvelope = TransactionEnvelope.Decode(reader);

            if (!await ProcessDwToHwTransaction(operationId, txEnvelope.V1.Tx))
            {
                var operation = _horizonService.GetFirstOperationFromTxEnvelope(txEnvelope);
                var operationType = operation.Discriminant.InnerValue;

                // ReSharper disable once SwitchStatementMissingSomeCases
                switch (operationType)
                {
                    case OperationType.OperationTypeEnum.CREATE_ACCOUNT:
                        {
                            amount = operation.CreateAccountOp.StartingBalance.InnerValue;
                            break;
                        }
                    case OperationType.OperationTypeEnum.PAYMENT:
                        {
                            amount = operation.PaymentOp.Amount.InnerValue;
                            break;
                        }
                    case OperationType.OperationTypeEnum.ACCOUNT_MERGE:
                        {
                            // amount not yet known
                            break;
                        }
                    default:
                        throw new BusinessException($"Unsupported operation type. type={operationType}");
                }

                string hash;

                try
                {
                    hash = await _horizonService.SubmitTransactionAsync(xdrBase64);
                }
                catch (BadRequestHorizonApiException ex)
                {
                    _log.Error(ex, message: "Broadcasting has failed!", context: new { OperationId = operationId });
                    throw new BusinessException($"Broadcasting transaction failed. operationId={operationId}, message={GetErrorMessage(ex)}", ex, GetErrorCode(ex).ToString());
                }

                _chaos.Meow(nameof(BroadcastTxAsync));

                var observation = new BroadcastObservation
                {
                    OperationId = operationId
                };

                await _observationRepository.InsertOrReplaceAsync(observation);

                _chaos.Meow(nameof(BroadcastTxAsync));

                await _broadcastRepository.InsertOrReplaceAsync(new TxBroadcast
                {
                    OperationId = operationId,
                    State = TxBroadcastState.InProgress,
                    Amount = amount,
                    Hash = hash,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        private async Task<bool> ProcessDwToHwTransaction(Guid operationId, stellar_dotnet_sdk.xdr.Transaction tx)
        {
            var fromKeyPair = KeyPair.FromPublicKey(tx.SourceAccount.Ed25519.InnerValue);
            if (!_balanceService.IsDepositBaseAddress(fromKeyPair.Address) || tx.Operations.Length != 1 ||
                tx.Operations[0].Body.PaymentOp == null || string.IsNullOrWhiteSpace(tx.Memo.Text)) return false;

            var toKeyPair = KeyPair.FromPublicKey(tx.Operations[0].Body.PaymentOp.Destination.Ed25519.InnerValue);
            if (!_balanceService.IsDepositBaseAddress(toKeyPair.Address)) return false;

            var fromAddress = $"{fromKeyPair.Address}{Constants.PublicAddressExtension.Separator}{tx.Memo.Text}";
            var amount = tx.Operations[0].Body.PaymentOp.Amount.InnerValue;

            // Use our guid-ed OperationId as transaction hash, as it uniquely identifies the transaction,
            // just without dashes to look more hash-y.
            var hash = operationId.ToString("N");

            // While we have only single action within DW->HW transaction,
            // we can use any value to identify action within transaction.
            // Use hashed operation ID to add more diversity.
            var opId = operationId.ToString("N").CalculateHash64();

            var ledger = await _horizonService.GetLatestLedger();
            var updateLedger = (ledger.Sequence * 10) + 1;
            var broadcast = new TxBroadcast
            {
                OperationId = operationId,
                Amount = amount,
                Fee = 0,
                Hash = hash,
                // ReSharper disable once ArrangeRedundantParentheses
                Ledger = updateLedger,
                CreatedAt = DateTime.UtcNow
            };

            var assetId = _blockchainAssetsService.GetNativeAsset().Id;
            var balance = await _balanceRepository.GetAsync(assetId, fromAddress);

            if (balance.Balance < amount)
            {
                broadcast.State = TxBroadcastState.Failed;
                broadcast.Error = "Not enough balance!";
                broadcast.ErrorCode = TxExecutionError.NotEnoughBalance;
            }
            else
            {
                await _balanceRepository.RecordOperationAsync(assetId, fromAddress, updateLedger, opId, hash, (-1) * amount);
                await _balanceRepository.RefreshBalance(assetId, fromAddress);
                broadcast.State = TxBroadcastState.Completed;
            }

            _chaos.Meow(nameof(ProcessDwToHwTransaction));

            // update state
            await _broadcastRepository.InsertOrReplaceAsync(broadcast);
            return true;
        }

        private static string GetErrorMessage(Exception ex)
        {
            var errorMessage = ex.Message;
            // ReSharper disable once InvertIf
            if (ex is BadRequestHorizonApiException badRequest)
            {
                //TODO:
                var resultCodes = JsonConvert.SerializeObject(badRequest.ErrorCodes);
                errorMessage += $"{badRequest.Message}. ResultCodes={resultCodes}";
            }

            return errorMessage;
        }

        private static TxExecutionError GetErrorCode(Exception ex)
        {
            //TODO:
            var bre = ex as BadRequestHorizonApiException;
            var resultCodes = bre?.ErrorCodes;
            var transactionDetail = bre?.Message;

            if (transactionDetail != null
                && resultCodes != null
                && resultCodes.Length > 0
                && (resultCodes[0].Equals(StellarSdkConstants.OperationUnderfunded)
                    || resultCodes[0].Equals(StellarSdkConstants.OperationLowReserve)))
            {
                return TxExecutionError.NotEnoughBalance;
            }

            if (transactionDetail == "tx_too_late" ||
                transactionDetail == "tx_bad_seq")
            {
                return TxExecutionError.BuildingShouldBeRepeated;
            }

            return TxExecutionError.Unknown;
        }

        public async Task DeleteTxBroadcastAsync(Guid operationId)
        {
            var deleteObservationTask = _observationRepository.DeleteIfExistAsync(operationId.ToString());
            var deleteBroadcastTask = _broadcastRepository.DeleteAsync(operationId);
            await Task.WhenAll(deleteObservationTask, deleteBroadcastTask);
        }

        public async Task<Fees> GetFeesAsync()
        {
            var latest = await _horizonService.GetLatestLedger();
            var fees = new Fees
            {
                BaseFee = latest.BaseFee,
                BaseReserve = Convert.ToDecimal(latest.BaseReserve)
            };
            return fees;
        }

        public async Task<TxBuild> GetTxBuildAsync(Guid operationId)
        {
            return await _buildRepository.GetAsync(operationId);
        }

        public async Task<string> BuildTransactionAsync(Guid operationId, AddressBalance from, string toAddress, string memoText, long amount)
        {
            var fromKeyPair = KeyPair.FromAccountId(from.Address);
            var fromAccount = new Account(fromKeyPair, from.Sequence);

            var toKeyPair = KeyPair.FromAccountId(toAddress);

            var transferableBalance = from.Balance - from.MinBalance;

            Operation operation;
            if (await _horizonService.AccountExists(toAddress))
            {
                if (amount <= transferableBalance)
                {
                    var asset = new AssetTypeNative();
                    operation = new PaymentOperation.Builder(toKeyPair, asset, Operation.FromXdrAmount(amount))
                                                    .SetSourceAccount(fromKeyPair)
                                                    .Build();
                }
                else if (!_balanceService.IsDepositBaseAddress(from.Address))
                {
                    operation = new AccountMergeOperation.Builder(toKeyPair)
                                                         .SetSourceAccount(fromKeyPair)
                                                         .Build();
                }
                else
                {
                    throw new BusinessException($"It isn't allowed to merge the entire balance from the deposit base into another account! Transfer less funds. transferable={transferableBalance}");
                }
            }
            else
            {
                if (amount <= transferableBalance)
                {
                    operation = new CreateAccountOperation.Builder(toKeyPair, Operation.FromXdrAmount(amount))
                                                      .SetSourceAccount(fromKeyPair)
                                                      .Build();
                }
                else
                {
                    throw new BusinessException($"It isn't possible to merge the entire balance into an unused account! Use a destination in existance. transferable={transferableBalance}");
                }
            }

            var builder = new TransactionBuilder(fromAccount)
                                         .AddOperation(operation);
            if (!string.IsNullOrWhiteSpace(memoText))
            {
                var memo = new MemoText(memoText);
                builder = builder.AddMemo(memo);
            }

            var tx = builder.Build();

            var xdr = tx.ToUnsignedEnvelopeXdr(TransactionBase.TransactionXdrVersion.V1);
            var expirationDate = (DateTime.UtcNow + _transactionExpirationTime);
            var maxUnixTimeDouble = expirationDate.ToUnixTime() / 1000;//ms to seconds
            var maxTimeUnix = (ulong)maxUnixTimeDouble;
            xdr.V1.Tx.TimeBounds = new TimeBounds()
            {
                MaxTime = new TimePoint(new Uint64(maxTimeUnix)),
                MinTime = new TimePoint(new Uint64(0)),
            };

            var writer = new XdrDataOutputStream();
            stellar_dotnet_sdk.xdr.TransactionEnvelope.Encode(writer, xdr);
            var xdrBase64 = Convert.ToBase64String(writer.ToArray());

            var build = new TxBuild
            {
                OperationId = operationId,
                XdrBase64 = xdrBase64
            };
            await _buildRepository.AddAsync(build);

            return xdrBase64;
        }

        public string GetLastJobError()
        {
            return _lastJobError;
        }

        public async Task<int> UpdateBroadcastsInProgress(int batchSize)
        {
            var count = 0;

            try
            {
                string continuationToken = null;
                do
                {
                    var observations = await _observationRepository.GetAllAsync(batchSize, continuationToken);
                    foreach (var item in observations.Items)
                    {
                        await ProcessBroadcastInProgress(item.OperationId);
                        count++;
                    }
                    continuationToken = observations.ContinuationToken;
                } while (continuationToken != null);

                _lastJobError = null;
            }
            catch (Exception ex)
            {
                _lastJobError = $"Error in job {nameof(TransactionService)}.{nameof(UpdateBroadcastsInProgress)}: {ex.Message}";
                throw new JobExecutionException("Failed to execute broadcast in progress updates", ex, count);
            }

            return count;
        }

        private async Task ProcessBroadcastInProgress(Guid operationId)
        {
            TxBroadcast broadcast = null;
            try
            {
                broadcast = await _broadcastRepository.GetAsync(operationId);
                if (broadcast == null)
                {
                    await _observationRepository.DeleteIfExistAsync(operationId.ToString());
                    throw new BusinessException($"Broadcast for observed operation not found. operationId={operationId}");
                }

                var tx = await _horizonService.GetTransactionDetails(broadcast.Hash);
                if (tx == null)
                {
                    // transaction still in progress
                    return;
                }
                if (!broadcast.Hash.Equals(tx.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BusinessException($"Transaction hash mismatch. actual={tx.Hash}, expected={broadcast.Hash}");
                }

                var operation = _horizonService.GetFirstOperationFromTxEnvelopeXdr(tx.EnvelopeXdr);
                var operationType = operation.Discriminant.InnerValue;

                // ReSharper disable once SwitchStatementMissingSomeCases
                switch (operationType)
                {
                    case OperationType.OperationTypeEnum.CREATE_ACCOUNT:
                        {
                            broadcast.Amount = operation.CreateAccountOp.StartingBalance.InnerValue;
                            break;
                        }
                    case OperationType.OperationTypeEnum.PAYMENT:
                        {
                            broadcast.Amount = operation.PaymentOp.Amount.InnerValue;
                            break;
                        }
                    case OperationType.OperationTypeEnum.ACCOUNT_MERGE:
                        {
                            broadcast.Amount = _horizonService.GetAccountMergeAmount(tx.ResultXdr, 0);
                            break;
                        }
                    default:
                        throw new BusinessException($"Unsupported operation type. type={operationType}");
                }

                DateTime.TryParse(tx.CreatedAt, out var createdAt);
                broadcast.State = TxBroadcastState.Completed;
                broadcast.Fee = tx.FeeCharged;
                broadcast.CreatedAt = createdAt;
                broadcast.Ledger = tx.Ledger * 10;

                await _broadcastRepository.MergeAsync(broadcast);
                await _observationRepository.DeleteIfExistAsync(operationId.ToString());
            }
            catch (Exception ex)
            {
                if (broadcast != null)
                {
                    broadcast.State = TxBroadcastState.Failed;
                    broadcast.Error = ex.Message;
                    broadcast.ErrorCode = TxExecutionError.Unknown;

                    await _broadcastRepository.MergeAsync(broadcast);
                    await _observationRepository.DeleteIfExistAsync(operationId.ToString());
                }

                throw new BusinessException($"Failed to process in progress broadcast. operationId={operationId}", ex);
            }
        }
    }
}
