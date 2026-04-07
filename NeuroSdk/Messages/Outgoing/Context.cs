#nullable enable

using System.Text.Json.Serialization;
using NeuroSdk.Messages.API;
using NeuroSdk.Websocket;

namespace NeuroSdk.Messages.Outgoing
{
    public sealed class Context : OutgoingMessageBuilder
    {
        public Context(string message, bool silent = false)
        {
            Message = message;
            _silent = silent;
        }

        protected override string Command => "context";

        [JsonInclude, JsonPropertyName("message")]
        public readonly string Message;

        [JsonInclude, JsonPropertyName("silent")]
        private readonly bool _silent;

        public static void Send(string message, bool silent = false) => WebsocketConnection.Instance!.Send(new Context(message, silent));
    }
}
