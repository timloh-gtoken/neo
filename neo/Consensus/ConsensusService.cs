﻿using Akka.Actor;
using Akka.Configuration;
using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Actors;
using Neo.Ledger;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.Wallets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Neo.Consensus
{
    public sealed class ConsensusService : UntypedActor
    {
        public class Start { }
        public class SetViewNumber { public byte ViewNumber; }
        internal class Timer { public uint Height; public byte ViewNumber; }

        private const byte ContextSerializationPrefix = 0xf4;

        private readonly IConsensusContext context;
        private readonly IActorRef localNode;
        private readonly IActorRef taskManager;
        private readonly Store store;
        private ICancelable timer_token;
        private DateTime block_received_time;
        private bool started = false;
        private readonly Wallet wallet;
        /// <summary>
        /// This will be cleared every block (so it will not grow out of control, but is used to prevent repeatedly
        /// responding to the same message.
        /// </summary>
        private readonly HashSet<UInt256> knownHashes = new HashSet<UInt256>();

        public ConsensusService(IActorRef localNode, IActorRef taskManager, Store store, Wallet wallet)
            : this(localNode, taskManager, store, new ConsensusContext(wallet))
        {
            this.wallet = wallet;
        }

        public ConsensusService(IActorRef localNode, IActorRef taskManager, Store store, IConsensusContext context)
        {
            this.localNode = localNode;
            this.taskManager = taskManager;
            this.store = store;
            this.context = context;
        }

        private bool AddTransaction(Transaction tx, bool verify)
        {
            if (verify && !tx.Verify(context.Snapshot, context.Transactions.Values))
            {
                Log($"Invalid transaction: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}", LogLevel.Warning);
                RequestChangeView();
                return false;
            }
            if (!Plugin.CheckPolicy(tx))
            {
                Log($"reject tx: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}", LogLevel.Warning);
                RequestChangeView();
                return false;
            }
            context.Transactions[tx.Hash] = tx;
            if (context.TransactionHashes.Length == context.Transactions.Count)
            {
                if (context.VerifyRequest())
                {
                    context.State |= ConsensusState.ResponseSent;

                    // if we are the primary for this view, but acting as a backup because we recovered our own
                    // previously sent prepare request, then we don't want to send a prepare response.
                    if (context.MyIndex == context.PrimaryIndex) return true;

                    Log($"send prepare response");
                    var payload = context.MakePrepareResponse(context.PreparationPayloads[context.PrimaryIndex].Hash);
                    context.PreparationPayloads[context.MyIndex] = payload;
                    localNode.Tell(new LocalNode.SendDirectly { Inventory = payload });
                    CheckPreparations();
                }
                else
                {
                    RequestChangeView();
                    return false;
                }
            }
            return true;
        }

        private void ChangeTimer(TimeSpan delay)
        {
            timer_token.CancelIfNotNull();
            timer_token = Context.System.Scheduler.ScheduleTellOnceCancelable(delay, Self, new Timer
            {
                Height = context.BlockIndex,
                ViewNumber = context.ViewNumber
            }, ActorRefs.NoSender);
        }

        private void CheckCommits()
        {
            if (context.Commits.Count(p => p != null) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
            {
                Block block = context.CreateBlock();
                Log($"relay block: {block.Hash}");
                localNode.Tell(new LocalNode.Relay { Inventory = block });
                context.State |= ConsensusState.BlockSent;
            }
        }

        private void CheckExpectedView(byte viewNumber)
        {
            if (context.ViewNumber == viewNumber) return;
            if (context.ChangeViewPayloads.Count(p => p.GetDeserializedMessage<ChangeView>().NewViewNumber == viewNumber) >= context.M)
                InitializeConsensus(viewNumber);
        }

        private void CheckPreparations()
        {
            if (context.PreparationPayloads.Count(p => p != null) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
            {
                ConsensusPayload payload = context.MakeCommit();
                Log($"send commit");
                context.State |= ConsensusState.CommitSent;
                store.Put(ContextSerializationPrefix, new byte[0], context.ToArray());
                localNode.Tell(new LocalNode.SendDirectly { Inventory = payload });
                // Set timer, so we will resend the commit in case of a networking issue
                ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock));
                CheckCommits();
            }
        }

        private void InitializeConsensus(byte viewNumber)
        {
            context.Reset(viewNumber);
            if (context.MyIndex < 0) return;
            if (viewNumber > 0)
                Log($"changeview: view={viewNumber} primary={context.Validators[context.GetPrimaryIndex((byte)(viewNumber - 1u))]}", LogLevel.Warning);
            Log($"initialize: height={context.BlockIndex} view={viewNumber} index={context.MyIndex} role={(context.MyIndex == context.PrimaryIndex ? ConsensusState.Primary : ConsensusState.Backup)}");
            if (context.MyIndex == context.PrimaryIndex)
            {
                context.State |= ConsensusState.Primary;
                TimeSpan span = TimeProvider.Current.UtcNow - block_received_time;
                if (span >= Blockchain.TimePerBlock)
                    ChangeTimer(TimeSpan.Zero);
                else
                    ChangeTimer(Blockchain.TimePerBlock - span);
            }
            else
            {
                context.State = ConsensusState.Backup;
                ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (viewNumber + 1)));
            }
        }

        private void Log(string message, LogLevel level = LogLevel.Info)
        {
            Plugin.Log(nameof(ConsensusService), level, message);
        }

        private void OnChangeViewReceived(ConsensusPayload payload, ChangeView message)
        {
            // Node in commit receiving ChangeView should always send the recovery message, to restore
            // nodes that may have counted their view number past the view that has commit set.
            bool shouldSendRecovery = context.State.HasFlag(ConsensusState.CommitSent);
            if (shouldSendRecovery || message.NewViewNumber < context.ViewNumber)
            {
                if (!shouldSendRecovery)
                {
                    // Limit recovery to sending from `f` nodes when the request is from a lower view number.
                    int allowedRecoveryNodeCount = context.F;
                    for (int i = 0; i < allowedRecoveryNodeCount; i++)
                    {
                        var eligibleResponders = context.Validators.Length - 1;
                        var chosenIndex = (payload.ValidatorIndex + i + message.NewViewNumber) % eligibleResponders;
                        if (chosenIndex >= payload.ValidatorIndex) chosenIndex++;
                        if (chosenIndex != context.MyIndex) continue;
                        shouldSendRecovery = true;
                        break;
                    }
                }

                // We keep track of the payload hashes received in this block, and don't respond with recovery
                // in response to the same payload that we already responded to previously.
                // ChangeView messages include a Timestamp when the change view is sent, thus if a node restarts
                // and issues a change view for the same view, it will have a different hash and will correctly respond
                // again; however replay attacks of the ChangeView message from arbitrary nodes will not trigger an
                // additonal recovery message response.
                if (!shouldSendRecovery || knownHashes.Contains(payload.Hash)) return;
                knownHashes.Add(payload.Hash);

                Log($"send recovery from view: {message.ViewNumber} to view: {context.ViewNumber}");
                localNode.Tell(new LocalNode.SendDirectly {Inventory = context.MakeRecoveryMessage()});
                return;
            }

            var expectedView = GetLastExpectedView(payload.ValidatorIndex);
            if (message.NewViewNumber <= expectedView)
                return;

            Log($"{nameof(OnChangeViewReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} nv={message.NewViewNumber}");
            context.ChangeViewPayloads[payload.ValidatorIndex] = payload;
            CheckExpectedView(message.NewViewNumber);
        }

        private void OnCommitReceived(ConsensusPayload payload, Commit commit)
        {
            if (context.Commits[payload.ValidatorIndex] != null) return;
            Log($"{nameof(OnCommitReceived)}: height={payload.BlockIndex} view={commit.ViewNumber} index={payload.ValidatorIndex}");
            byte[] hashData = context.MakeHeader()?.GetHashData();
            if (hashData == null)
            {
                context.Commits[payload.ValidatorIndex] = commit.Signature;
            }
            else if (Crypto.Default.VerifySignature(hashData, commit.Signature, context.Validators[payload.ValidatorIndex].EncodePoint(false)))
            {
                context.Commits[payload.ValidatorIndex] = commit.Signature;
                CheckCommits();
            }
        }

        private bool PerformBasicConsensusPayloadPreChecks(ConsensusPayload payload)
        {
            if (payload.PrevHash != context.PrevHash || payload.BlockIndex != context.BlockIndex)
            {
                if (context.BlockIndex < payload.BlockIndex)
                {
                    Log($"chain sync: expected={payload.BlockIndex} current={context.BlockIndex - 1} nodes={LocalNode.Singleton.ConnectedCount}", LogLevel.Warning);
                }
                return false;
            }
            if (payload.ValidatorIndex >= context.Validators.Length) return false;
            return true;
        }

        private void OnConsensusPayload(ConsensusPayload payload)
        {
            if (context.State.HasFlag(ConsensusState.BlockSent)) return;
            if (payload.Version != ConsensusContext.Version)
                return;
            if (payload.ValidatorIndex == context.MyIndex) return;
            if (!PerformBasicConsensusPayloadPreChecks(payload)) return;
            ConsensusMessage message = payload.ConsensusMessage;
            if (message.ViewNumber != context.ViewNumber && message.Type != ConsensusMessageType.ChangeView &&
                                                            message.Type != ConsensusMessageType.RecoveryMessage)
                return;
            switch (message)
            {
                case ChangeView view:
                    OnChangeViewReceived(payload, view);
                    break;
                case RecoveryMessage regeneration:
                    OnRecoveryMessageReceived(payload, regeneration);
                    break;
                case PrepareRequest request:
                    OnPrepareRequestReceived(payload, request);
                    break;
                case PrepareResponse response:
                    OnPrepareResponseReceived(payload, response);
                    break;
                case Commit commit:
                    OnCommitReceived(payload, commit);
                    break;
            }
        }

        private void OnPersistCompleted(Block block)
        {
            Log($"persist block: {block.Hash}");
            block_received_time = TimeProvider.Current.UtcNow;
            knownHashes.Clear();
            InitializeConsensus(0);
        }

        private bool ReverifyPrepareRequest(ConsensusContext consensusContext, RecoveryMessage message,
            out ConsensusPayload prepareRequestPayload, out PrepareRequest prepareRequest)
        {
            if (message.PrepareRequestMessage != null)
            {
                prepareRequest = message.PrepareRequestMessage;
                prepareRequestPayload = consensusContext.RegenerateSignedPayload(
                    prepareRequest, (ushort) consensusContext.PrimaryIndex,
                    message.PreparationMessages[(ushort)consensusContext.PrimaryIndex].InvocationScript);

                if (prepareRequestPayload.Verify(context.Snapshot) && PerformBasicConsensusPayloadPreChecks(prepareRequestPayload))
                    return true;
            }

            prepareRequestPayload = null;
            prepareRequest = null;
            return false;
        }

        private void RestoreCommits(RecoveryMessage message)
        {
            bool addedCommits = false;
            var header = context.MakeHeader();
            for (ushort i = 0; i < context.Validators.Length; i++)
            {
                if (context.Commits[i] != null) continue;
                if (!message.CommitMessages.ContainsKey(i)) continue;
                var signature = message.CommitMessages[i].Signature;
                if (!Crypto.Default.VerifySignature(header.GetHashData(), signature,
                    context.Validators[i].EncodePoint(false)))
                    continue;
                context.Commits[i] = signature;
                addedCommits = true;
            }
            if (addedCommits) CheckCommits();
        }

        private void HandleRecoveryInCurrentView(RecoveryMessage message)
        {
            // If we are already on the right view number we want to accept more preparation messages and also accept
            // any commit signatures in the payload.

            bool recoveryHasPrepareRequest = message.PrepareRequestMessage != null && message.PreparationMessages.ContainsKey((ushort)context.PrimaryIndex);

            if (!context.State.HasFlag(ConsensusState.CommitSent))
            {
                UInt256 preparationHash = null;
                bool myIndexIsPrimary = context.MyIndex == context.PrimaryIndex;
                if (myIndexIsPrimary)
                {
                    preparationHash = context.PreparationPayloads[context.PrimaryIndex]?.Hash;
                    if (preparationHash == null && !recoveryHasPrepareRequest) return;
                }

                if (preparationHash == null)
                {
                    if (recoveryHasPrepareRequest && ReverifyPrepareRequest((ConsensusContext) context, message,
                            out var prepareRequestPayload, out var prepareRequest))
                    {
                        if (myIndexIsPrimary)
                        {
                            // In this case we are primary, but we haven't sent a prepare request, but we received a
                            // recovery message containing our own previous prepare request; so we now are acting as
                            // a backup and accepting our own previous request.
                            context.State |= ConsensusState.Backup;
                            // Since our Primary flag is still set, we must act as though we already sent the request.
                            context.State |= ConsensusState.RequestSent;

                            ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (context.ViewNumber + 1)));
                        }
                        OnPrepareRequestReceived(prepareRequestPayload, prepareRequest);
                        preparationHash = prepareRequestPayload.Hash;
                    }
                    else
                    {
                        // Can't use anything from the recovery message if we are primary and haven't sent a prepare
                        // request and no prepare request was present in the recovery message.
                        if (myIndexIsPrimary) return;

                        // If we have no `Preparation` hash we can't regenerate any of the `PrepareRequest` messages.
                        if (message.PreparationHash == null) return;
                        preparationHash = message.PreparationHash;
                    }
                }

                for (ushort i = 0; i < context.Validators.Length; i++)
                {
                    // If we are missing this preparation.
                    if (context.PreparationPayloads[i] != null) continue;
                    if (i == context.PrimaryIndex) continue;
                    // If the recovery message has this preparations
                    if (!message.PreparationMessages.ContainsKey(i)) continue;
                    var prepareResponseMsg = new PrepareResponse {PreparationHash = preparationHash};
                    prepareResponseMsg.ViewNumber = context.ViewNumber;
                    var regeneratedPrepareResponse = ((ConsensusContext) context).RegenerateSignedPayload(
                        prepareResponseMsg, i, message.PreparationMessages[i].InvocationScript);
                    if (regeneratedPrepareResponse.Verify(context.Snapshot) &&
                        PerformBasicConsensusPayloadPreChecks(regeneratedPrepareResponse))
                        OnPrepareResponseReceived(regeneratedPrepareResponse, prepareResponseMsg);
                }
            }

            if (!context.State.HasFlag(ConsensusState.CommitSent)) return;

            RestoreCommits(message);
        }

        private void OnRecoveryMessageReceived(ConsensusPayload payload, RecoveryMessage message)
        {
            Log(
                $"{nameof(OnRecoveryMessageReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
            if (context.ViewNumber == message.ViewNumber)
            {
                HandleRecoveryInCurrentView(message);
                return;
            }

            // Commited nodes cannot change view or it can lead to a potential block spork, since their block signature
            // could potentially be used twice.
            if (context.State.HasFlag(ConsensusState.CommitSent)) return;

            var tempContext = new ConsensusContext(wallet);
            // Have to Reset to 0 first to handle initializion of the context
            tempContext.Reset(0, context.Snapshot);
            if (message.ViewNumber != 0)
                tempContext.Reset(message.ViewNumber);
            if (message.PrepareRequestMessage != null)
            {
                var prepareRequest = message.PrepareRequestMessage;
                tempContext.Nonce = prepareRequest.Nonce;
                tempContext.TransactionHashes = prepareRequest.TransactionHashes;
                tempContext.NextConsensus = prepareRequest.NextConsensus;
                tempContext.Transactions = new Dictionary<UInt256, Transaction>
                {
                    [prepareRequest.TransactionHashes[0]] = prepareRequest.MinerTransaction
                };
                tempContext.Timestamp = prepareRequest.Timestamp;
            }

            ConsensusPayload prepareRequestPayload = null;
            PrepareRequest prepareRequestMessage = null;

            UInt256 preparationHash;
            if (message.PreparationMessages.ContainsKey((int) tempContext.PrimaryIndex)
                && ReverifyPrepareRequest(tempContext, message, out prepareRequestPayload, out prepareRequestMessage))
                preparationHash = prepareRequestPayload.Hash;
            else
                preparationHash = message.PreparationHash;

            var prepareResponses = new List<(ConsensusPayload, PrepareResponse)>();
            var prepareResponseMsg = new PrepareResponse
            {
                ViewNumber = tempContext.ViewNumber,
                PreparationHash = preparationHash
            };
            bool canRestoreView = false;
            if (preparationHash != null)
            {
                for (ushort i = 0; i < context.Validators.Length; i++)
                {
                    if (i == tempContext.PrimaryIndex) continue;
                    if (!message.PreparationMessages.ContainsKey(i)) continue;
                    Log($" considering prepare request {i}");
                    var regeneratedPrepareResponse = tempContext.RegenerateSignedPayload(prepareResponseMsg, i,
                        message.PreparationMessages[i].InvocationScript);
                    if (!regeneratedPrepareResponse.Verify(context.Snapshot) ||
                        !PerformBasicConsensusPayloadPreChecks(regeneratedPrepareResponse)) continue;
                    prepareResponses.Add((regeneratedPrepareResponse, prepareResponseMsg));
                    Log($" verified prepare {i}");
                    // Verify that there are M valid preparations, 1 Prepare Request + (M-1) Prepare responses
                    if (prepareRequestMessage == null || prepareResponses.Count < context.M - 1) continue;
                    canRestoreView = true;
                    break;
                }
            }

            byte[][] commitSignaturesIfMovingToLowerView = null;
            // Only accept recovery from lower views if there were at least M valid prepare requests
            if (message.ViewNumber < context.ViewNumber)
            {
                if (!canRestoreView) return;

                int commitCount = 0;
                commitSignaturesIfMovingToLowerView = new byte[context.Validators.Length][];
                var header = context.MakeHeader();
                for (ushort i = 0; i < context.Validators.Length; i++)
                {
                    if (!message.CommitMessages.ContainsKey(i)) continue;
                    var signature = message.CommitMessages[i].Signature;
                    if (!Crypto.Default.VerifySignature(header.GetHashData(), signature,
                        context.Validators[i].EncodePoint(false)))
                        continue;
                    commitCount++;
                    commitSignaturesIfMovingToLowerView[i] = signature;
                }

                if (commitCount < context.M) return;
            }


            var verifiedChangeViewPayloads = new ConsensusPayload[context.Validators.Length];
            if (!canRestoreView && message.ChangeViewMessages.Count >= context.M)
            {
                var changeViewMsg = new ChangeView {NewViewNumber = message.ViewNumber};
                int validChangeViewCount = 0;
                for (ushort i = 0; i < context.Validators.Length; i++)
                {
                    if (!message.ChangeViewMessages.ContainsKey(i)) continue;

                    changeViewMsg.ViewNumber = message.ChangeViewMessages[i].OriginalViewNumber;
                    changeViewMsg.Timestamp = message.ChangeViewMessages[i].Timestamp;
                    // Regenerate the ChangeView message
                    var regeneratedChangeView = tempContext.RegenerateSignedPayload(changeViewMsg, i,
                        message.ChangeViewMessages[i].InvocationScript);
                    if (!regeneratedChangeView.Verify(context.Snapshot) ||
                        !PerformBasicConsensusPayloadPreChecks(regeneratedChangeView)) continue;
                    verifiedChangeViewPayloads[i] = regeneratedChangeView;
                    validChangeViewCount++;
                    if (validChangeViewCount < context.M) continue;
                    canRestoreView = true;
                    break;
                }
            }

            if (!canRestoreView) return;
            // We had enough valid change view messages or preparations to safely change the view number.
            Log($"regenerating view: {message.ViewNumber}");

            if (tempContext.PrimaryIndex == context.MyIndex && prepareRequestPayload != null)
            {
                // If we are the primary and there was a valid prepare request payload, we will accept our own prepare
                // request, so avoid the normal IntializeConsensus behavior.
                context.Reset(message.ViewNumber);
                // Since we won't want to fill the context, we will behave like a backup even though we have the primary
                // index; we set the backup state so we can call OnPrepareRequestReceived below.
                context.State |= ConsensusState.Primary | ConsensusState.RequestSent | ConsensusState.Backup;
                // We set the timer in the same way a Backup sets their timer in this case.
                Log(
                    $"initialize: height={context.BlockIndex} view={message.ViewNumber} index={context.MyIndex} role={ConsensusState.Primary}");
                ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (message.ViewNumber + 1)));
            }
            else
            {
                if (block_received_time == null)
                {
                    block_received_time = TimeProvider.Current.UtcNow - TimeSpan.FromSeconds(
                                              Blockchain.TimePerBlock.TotalSeconds * Math.Pow(2, message.ViewNumber));
                }

                InitializeConsensus(message.ViewNumber);
            }

            for (int i = 0; i < context.Validators.Length; i++)
                if (verifiedChangeViewPayloads[i] != null)
                    context.ChangeViewPayloads[i] = verifiedChangeViewPayloads[i];

            if (prepareRequestPayload != null)
            {
                Log($"regenerating prepare request");
                // Note: If our node is the primary this will accept its own previously sent prepare request here.
                OnPrepareRequestReceived(prepareRequestPayload, prepareRequestMessage);
            }

            if (prepareResponses.Count > 0)
            {
                Log($"regenerating preparations: {prepareResponses.Count}");
                foreach (var (prepareRespPayload, prepareResp) in prepareResponses)
                    if (prepareRespPayload.ValidatorIndex != context.MyIndex)
                        OnPrepareResponseReceived(prepareRespPayload, prepareResp);
            }

            if (commitSignaturesIfMovingToLowerView is null)
            {
                RestoreCommits(message);
                return;
            }

            // Restore commits from moving to a lower view
            for (int i = 0; i < context.Validators.Length; i++)
                context.Commits[i] = commitSignaturesIfMovingToLowerView[i];
            CheckCommits();
        }

        private void OnPrepareRequestReceived(ConsensusPayload payload, PrepareRequest message)
        {
            if (context.State.HasFlag(ConsensusState.RequestReceived)) return;
            if (payload.ValidatorIndex != context.PrimaryIndex) return;
            Log($"{nameof(OnPrepareRequestReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} tx={message.TransactionHashes.Length}");
            if (!context.State.HasFlag(ConsensusState.Backup)) return;
            if (message.Timestamp <= context.PrevHeader.Timestamp || message.Timestamp > TimeProvider.Current.UtcNow.AddMinutes(10).ToTimestamp())
            {
                Log($"Timestamp incorrect: {message.Timestamp}", LogLevel.Warning);
                return;
            }
            if (message.TransactionHashes.Any(p => context.Snapshot.ContainsTransaction(p)))
            {
                Log($"Invalid request: transaction already exists", LogLevel.Warning);
                return;
            }
            context.State |= ConsensusState.RequestReceived;
            context.Timestamp = message.Timestamp;
            context.Nonce = message.Nonce;
            context.NextConsensus = message.NextConsensus;
            context.TransactionHashes = message.TransactionHashes;
            context.Transactions = new Dictionary<UInt256, Transaction>();
            for (int i = 0; i < context.PreparationPayloads.Length; i++)
                if (context.PreparationPayloads[i] != null)
                    if (!context.PreparationPayloads[i].Hash.Equals(payload.Hash))
                        context.PreparationPayloads[i] = null;
            context.PreparationPayloads[payload.ValidatorIndex] = payload;
            byte[] hashData = context.MakeHeader().GetHashData();
            for (int i = 0; i < context.Commits.Length; i++)
                if (context.Commits[i] != null)
                    if (!Crypto.Default.VerifySignature(hashData, context.Commits[i], context.Validators[i].EncodePoint(false)))
                        context.Commits[i] = null;
            Dictionary<UInt256, Transaction> mempoolVerified = Blockchain.Singleton.MemPool.GetVerifiedTransactions().ToDictionary(p => p.Hash);

            List<Transaction> unverified = new List<Transaction>();
            foreach (UInt256 hash in context.TransactionHashes.Skip(1))
            {
                if (mempoolVerified.TryGetValue(hash, out Transaction tx))
                {
                    if (!AddTransaction(tx, false))
                        return;
                }
                else
                {
                    if (Blockchain.Singleton.MemPool.TryGetValue(hash, out tx))
                        unverified.Add(tx);
                }
            }
            foreach (Transaction tx in unverified)
                if (!AddTransaction(tx, true))
                    return;
            if (!AddTransaction(message.MinerTransaction, true)) return;
            if (context.Transactions.Count < context.TransactionHashes.Length)
            {
                UInt256[] hashes = context.TransactionHashes.Where(i => !context.Transactions.ContainsKey(i)).ToArray();
                taskManager.Tell(new TaskManager.RestartTasks
                {
                    Payload = InvPayload.Create(InventoryType.TX, hashes)
                });
            }
        }

        private void OnPrepareResponseReceived(ConsensusPayload payload, PrepareResponse message)
        {
            if (context.PreparationPayloads[payload.ValidatorIndex] != null) return;
            if (context.PreparationPayloads[context.PrimaryIndex] != null && !message.PreparationHash.Equals(context.PreparationPayloads[context.PrimaryIndex].Hash))
                return;
            Log($"{nameof(OnPrepareResponseReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
            if (context.State.HasFlag(ConsensusState.CommitSent)) return;
            context.PreparationPayloads[payload.ValidatorIndex] = payload;
            if (context.State.HasFlag(ConsensusState.RequestSent) || context.State.HasFlag(ConsensusState.RequestReceived))
                CheckPreparations();
        }

        protected override void OnReceive(object message)
        {
            if (message is Start)
            {
                if (started) return;
                OnStart();
            }
            else
            {
                if (!started) return;
                switch (message)
                {
                    case SetViewNumber setView:
                        InitializeConsensus(setView.ViewNumber);
                        break;
                    case Timer timer:
                        OnTimer(timer);
                        break;
                    case ConsensusPayload payload:
                        OnConsensusPayload(payload);
                        break;
                    case Transaction transaction:
                        OnTransaction(transaction);
                        break;
                    case Blockchain.PersistCompleted completed:
                        OnPersistCompleted(completed.Block);
                        break;
                }
            }
        }

        private void OnStart()
        {
            Log("OnStart");
            started = true;
            byte[] data = store.Get(ContextSerializationPrefix, new byte[0]);
            if (data != null)
            {
                using (MemoryStream ms = new MemoryStream(data, false))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    context.Deserialize(reader);
                }
            }
            if (context.State.HasFlag(ConsensusState.CommitSent) && context.BlockIndex == Blockchain.Singleton.Height + 1)
                CheckPreparations();
            else
            {
                InitializeConsensus(0);
                // Issue a ChangeView with NewViewNumber of 0 to request recovery messages on start-up.
                if (context.BlockIndex == Blockchain.Singleton.HeaderHeight + 1)
                    localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeChangeView(0) });
            }
        }

        private void OnTimer(Timer timer)
        {
            if (context.State.HasFlag(ConsensusState.BlockSent)) return;
            if (timer.Height != context.BlockIndex || timer.ViewNumber != context.ViewNumber) return;
            Log($"timeout: height={timer.Height} view={timer.ViewNumber} state={context.State}");
            if (context.State.HasFlag(ConsensusState.Primary) && !context.State.HasFlag(ConsensusState.RequestSent))
            {
                Log($"send prepare request: height={timer.Height} view={timer.ViewNumber}");
                context.Fill();
                ConsensusPayload prepareRequestPayload = context.MakePrepareRequest();
                localNode.Tell(new LocalNode.SendDirectly { Inventory = prepareRequestPayload });
                context.State |= ConsensusState.RequestSent;
                context.PreparationPayloads[context.MyIndex] = prepareRequestPayload;

                if (context.TransactionHashes.Length > 1)
                {
                    foreach (InvPayload payload in InvPayload.CreateGroup(InventoryType.TX, context.TransactionHashes.Skip(1).ToArray()))
                        localNode.Tell(Message.Create("inv", payload));
                }
                ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (timer.ViewNumber + 1)));
            }
            else if ((context.State.HasFlag(ConsensusState.Primary) && context.State.HasFlag(ConsensusState.RequestSent)) || context.State.HasFlag(ConsensusState.Backup))
            {
                if (context.State.HasFlag(ConsensusState.CommitSent))
                {
                    // Re-send commit periodically by sending recover message in case of a network issue.
                    Log($"send recovery to resend commit");
                    localNode.Tell(new LocalNode.SendDirectly {Inventory = context.MakeRecoveryMessage()});
                    ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << 1));
                }
                else
                {
                    RequestChangeView();
                }
            }
        }

        private void OnTransaction(Transaction transaction)
        {
            if (transaction.Type == TransactionType.MinerTransaction) return;
            if (!context.State.HasFlag(ConsensusState.Backup) || !context.State.HasFlag(ConsensusState.RequestReceived) || context.State.HasFlag(ConsensusState.ResponseSent) || context.State.HasFlag(ConsensusState.ViewChanging) || context.State.HasFlag(ConsensusState.BlockSent))
                return;
            if (context.Transactions.ContainsKey(transaction.Hash)) return;
            if (!context.TransactionHashes.Contains(transaction.Hash)) return;
            AddTransaction(transaction, true);
        }

        protected override void PostStop()
        {
            Log("OnStop");
            started = false;
            context.Dispose();
            base.PostStop();
        }

        public static Props Props(IActorRef localNode, IActorRef taskManager, Store store, Wallet wallet)
        {
            return Akka.Actor.Props.Create(() => new ConsensusService(localNode, taskManager, store, wallet)).WithMailbox("consensus-service-mailbox");
        }

        private byte GetLastExpectedView(int validatorIndex)
        {
            var lastPreparationPayload = context.PreparationPayloads[validatorIndex];
            if (lastPreparationPayload != null)
                return lastPreparationPayload.GetDeserializedMessage<ConsensusMessage>().ViewNumber;

            return context.ChangeViewPayloads[validatorIndex]?.GetDeserializedMessage<ChangeView>().NewViewNumber ?? (byte) 0;
        }

        private void RequestChangeView()
        {
            context.State |= ConsensusState.ViewChanging;
            byte expectedView = GetLastExpectedView(context.MyIndex);
            expectedView++;
            Log($"request change view: height={context.BlockIndex} view={context.ViewNumber} nv={expectedView} state={context.State}");
            ChangeTimer(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (expectedView + 1)));
            var changeViewPayload = context.MakeChangeView(expectedView);
            context.ChangeViewPayloads[context.MyIndex] = changeViewPayload;
            localNode.Tell(new LocalNode.SendDirectly { Inventory = changeViewPayload });
            CheckExpectedView(expectedView);
        }
    }

    internal class ConsensusServiceMailbox : PriorityMailbox
    {
        public ConsensusServiceMailbox(Akka.Actor.Settings settings, Config config)
            : base(settings, config)
        {
        }

        protected override bool IsHighPriority(object message)
        {
            switch (message)
            {
                case ConsensusPayload _:
                case ConsensusService.SetViewNumber _:
                case ConsensusService.Timer _:
                case Blockchain.PersistCompleted _:
                    return true;
                default:
                    return false;
            }
        }
    }
}
