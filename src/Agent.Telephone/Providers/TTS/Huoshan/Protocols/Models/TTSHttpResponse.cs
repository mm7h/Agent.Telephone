using System.Text.Json.Nodes;

namespace Agent.Telephone.Providers.TTS.Huoshan.Protocols.Models
{
    internal record TTSHttpResponse(string Reqid, int Code, string Operation, string Message, int Sequence, string? Data, JsonObject? Addition);
}
