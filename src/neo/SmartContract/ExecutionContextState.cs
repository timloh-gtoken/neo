namespace Neo.SmartContract
{
    internal class ExecutionContextState
    {
        /// <summary>
        /// Script hash
        /// </summary>
        public UInt160 ScriptHash { get; set; }

        /// <summary>
        /// Calling script hash
        /// </summary>
        public UInt160 CallingScriptHash { get; set; }

        /// <summary>
        /// Execution context rights
        /// </summary>
        public CallFlags CallFlags { get; set; } = CallFlags.All;

        public ContractParameterType ReturnType { get; set; } = ContractParameterType.Void;
    }
}
