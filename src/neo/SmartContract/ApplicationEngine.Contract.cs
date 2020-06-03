using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.SmartContract.Manifest;
using Neo.SmartContract.Native;
using Neo.VM;
using System;
using System.Linq;
using Array = Neo.VM.Types.Array;

namespace Neo.SmartContract
{
    partial class ApplicationEngine
    {
        public const int MaxContractLength = 1024 * 1024;

        public static readonly InteropDescriptor System_Contract_Create = Register("System.Contract.Create", nameof(CreateContract), 0, TriggerType.Application, CallFlags.AllowModifyStates);
        public static readonly InteropDescriptor System_Contract_Update = Register("System.Contract.Update", nameof(UpdateContract), 0, TriggerType.Application, CallFlags.AllowModifyStates);
        public static readonly InteropDescriptor System_Contract_Destroy = Register("System.Contract.Destroy", nameof(DestroyContract), 0_01000000, TriggerType.Application, CallFlags.AllowModifyStates);
        public static readonly InteropDescriptor System_Contract_Call = Register("System.Contract.Call", nameof(CallContract), 0_01000000, TriggerType.System | TriggerType.Application, CallFlags.AllowCall);
        public static readonly InteropDescriptor System_Contract_CallEx = Register("System.Contract.CallEx", nameof(CallContractEx), 0_01000000, TriggerType.System | TriggerType.Application, CallFlags.AllowCall);
        public static readonly InteropDescriptor System_Contract_IsStandard = Register("System.Contract.IsStandard", nameof(IsStandardContract), 0_00030000, TriggerType.All, CallFlags.None);
        public static readonly InteropDescriptor System_Contract_GetCallFlags = Register("System.Contract.GetCallFlags", nameof(GetCallFlags), 0_00030000, TriggerType.All, CallFlags.None);
        /// <summary>
        /// Calculate corresponding account scripthash for given public key
        /// Warning: check first that input public key is valid, before creating the script.
        /// </summary>
        public static readonly InteropDescriptor System_Contract_CreateStandardAccount = Register("System.Contract.CreateStandardAccount", nameof(CreateStandardAccount), 0_00010000, TriggerType.All, CallFlags.None);

        internal ContractState CreateContract(byte[] script, byte[] manifest)
        {
            if (script.Length == 0 || script.Length > MaxContractLength || manifest.Length == 0 || manifest.Length > ContractManifest.MaxLength)
                throw new ArgumentException();

            if (!AddGas(StoragePrice * (script.Length + manifest.Length)))
                throw new InvalidOperationException();

            UInt160 hash = script.ToScriptHash();
            ContractState contract = Snapshot.Contracts.TryGet(hash);
            if (contract != null) throw new InvalidOperationException();
            contract = new ContractState
            {
                Id = Snapshot.ContractId.GetAndChange().NextId++,
                Script = script.ToArray(),
                Manifest = ContractManifest.Parse(manifest)
            };

            if (!contract.Manifest.IsValid(hash)) throw new InvalidOperationException();

            Snapshot.Contracts.Add(hash, contract);
            return contract;
        }

        internal void UpdateContract(byte[] script, byte[] manifest)
        {
            if (!AddGas(StoragePrice * (script?.Length ?? 0 + manifest?.Length ?? 0)))
                throw new InvalidOperationException();

            var contract = Snapshot.Contracts.TryGet(CurrentScriptHash);
            if (contract is null) throw new InvalidOperationException();

            if (script != null)
            {
                if (script.Length == 0 || script.Length > MaxContractLength)
                    throw new ArgumentException();
                UInt160 hash_new = script.ToScriptHash();
                if (hash_new.Equals(CurrentScriptHash) || Snapshot.Contracts.TryGet(hash_new) != null)
                    throw new InvalidOperationException();
                contract = new ContractState
                {
                    Id = contract.Id,
                    Script = script.ToArray(),
                    Manifest = contract.Manifest
                };
                contract.Manifest.Abi.Hash = hash_new;
                Snapshot.Contracts.Add(hash_new, contract);
                Snapshot.Contracts.Delete(CurrentScriptHash);
            }
            if (manifest != null)
            {
                if (manifest.Length == 0 || manifest.Length > ContractManifest.MaxLength)
                    throw new ArgumentException();
                contract = Snapshot.Contracts.GetAndChange(contract.ScriptHash);
                contract.Manifest = ContractManifest.Parse(manifest);
                if (!contract.Manifest.IsValid(contract.ScriptHash))
                    throw new InvalidOperationException();
                if (!contract.HasStorage && Snapshot.Storages.Find(BitConverter.GetBytes(contract.Id)).Any())
                    throw new InvalidOperationException();
            }
        }

        internal void DestroyContract()
        {
            UInt160 hash = CurrentScriptHash;
            ContractState contract = Snapshot.Contracts.TryGet(hash);
            if (contract == null) return;
            Snapshot.Contracts.Delete(hash);
            if (contract.HasStorage)
                foreach (var (key, _) in Snapshot.Storages.Find(BitConverter.GetBytes(contract.Id)))
                    Snapshot.Storages.Delete(key);
        }

        internal void CallContract(UInt160 contractHash, ContractParameterType returnType, string method, Array args)
        {
            CallContractInternal(contractHash, returnType, method, args, CallFlags.All);
        }

        internal void CallContractEx(UInt160 contractHash, ContractParameterType returnType, string method, Array args, CallFlags callFlags)
        {
            if ((callFlags & ~CallFlags.All) != 0)
                throw new ArgumentOutOfRangeException(nameof(callFlags));
            CallContractInternal(contractHash, returnType, method, args, callFlags);
        }

        private void CallContractInternal(UInt160 contractHash, ContractParameterType returnType, string method, Array args, CallFlags flags)
        {
            if (method.StartsWith('_')) throw new ArgumentException();

            ContractState contract = Snapshot.Contracts.TryGet(contractHash);
            if (contract is null) throw new InvalidOperationException();

            ContractManifest currentManifest = Snapshot.Contracts.TryGet(CurrentScriptHash)?.Manifest;

            if (currentManifest != null && !currentManifest.CanCall(contract.Manifest, method))
                throw new InvalidOperationException();

            if (invocationCounter.TryGetValue(contract.ScriptHash, out var counter))
            {
                invocationCounter[contract.ScriptHash] = counter + 1;
            }
            else
            {
                invocationCounter[contract.ScriptHash] = 1;
            }

            ExecutionContextState state = CurrentContext.GetState<ExecutionContextState>();
            UInt160 callingScriptHash = state.ScriptHash;
            CallFlags callingFlags = state.CallFlags;

            ContractMethodDescriptor md = contract.Manifest.Abi.GetMethod(method);
            if (md is null) throw new InvalidOperationException();
            if (md.ReturnType != returnType) throw new InvalidOperationException();
            ExecutionContext context_new = LoadScript(contract.Script);
            state = context_new.GetState<ExecutionContextState>();
            state.CallingScriptHash = callingScriptHash;
            state.CallFlags = flags & callingFlags;
            state.ReturnType = returnType;

            if (NativeContract.IsNative(contractHash))
            {
                context_new.EvaluationStack.Push(args);
                context_new.EvaluationStack.Push(method);
            }
            else
            {
                for (int i = args.Count - 1; i >= 0; i--)
                    context_new.EvaluationStack.Push(args[i]);
                context_new.InstructionPointer = md.Offset;
            }

            md = contract.Manifest.Abi.GetMethod("_initialize");
            if (md != null) LoadClonedContext(md.Offset);
        }

        internal bool IsStandardContract(UInt160 hash)
        {
            ContractState contract = Snapshot.Contracts.TryGet(hash);
            return contract is null || contract.Script.IsStandardContract();
        }

        internal CallFlags GetCallFlags()
        {
            var state = CurrentContext.GetState<ExecutionContextState>();
            return state.CallFlags;
        }

        internal UInt160 CreateStandardAccount(ECPoint pubKey)
        {
            return Contract.CreateSignatureRedeemScript(pubKey).ToScriptHash();
        }
    }
}
