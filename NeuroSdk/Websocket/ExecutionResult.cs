#nullable enable

namespace NeuroSdk.Websocket
{
    public sealed class ExecutionResult
    {
        public bool Successful { get; }
        public bool IsUnstable { get; }
        public string? Message { get; }

        private ExecutionResult(bool success, string? message, bool unstable = false)
        {
            Successful = success;
            Message = message;
            IsUnstable = unstable;
        }

        public static ExecutionResult Success(string? message = null) => new(true, message);
        public static ExecutionResult Failure(string reason) => new(false, reason);
        public static ExecutionResult Unstable(string reason) => new(false, reason, true);
        public static ExecutionResult VedalFailure(string reason) => Failure(reason + NeuroSdkStrings.VedalFaultSuffix);
        public static ExecutionResult ModFailure(string reason) => Failure(reason + NeuroSdkStrings.ModFaultSuffix);
    }
}
