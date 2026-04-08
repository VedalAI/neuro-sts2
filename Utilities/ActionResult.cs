using System.Text.Json;

namespace Sts2Agent.Utilities;

public static class ActionResult
{
    public struct Result
    {
        public bool ok;
        public string message;

    }
    public static Result Ok(string message)
    {
        return new()
        {
            ok = true,
            message = message
        };
    }

    public static Result Error(string message)
    {
        Plugin.LogError($"Action error: {message}");
        return new()
        {
            ok = false,
            message = message
        };
    }
}
