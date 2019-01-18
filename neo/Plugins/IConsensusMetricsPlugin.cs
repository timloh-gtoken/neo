﻿using Neo.Network.P2P.Payloads;

namespace Neo.Plugins
{
    public interface IConsensusMetricsPlugin
    {
        void AnalyzePrepareRequest(ConsensusPayload payload);
        void AnalyzePrepareResponse(ConsensusPayload payload);
    }
}
