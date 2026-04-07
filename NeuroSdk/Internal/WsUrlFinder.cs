#nullable enable

using System;
using System.Collections;

namespace NeuroSdk.Internal
{
    internal static class WsUrlFinder
    {
        public static void FindWsUrl(Action<string> callback)
        {
            string? url = null;
            TryGetWsUrlFromEnvironment(ref url);

            callback(url ?? "");
        }
        private static void TryGetWsUrlFromEnvironment(ref string? url)
        {
            if (url is not null && url is not "") return;

            url = Environment.GetEnvironmentVariable("NEURO_SDK_WS_URL", EnvironmentVariableTarget.Process);
            if (url is not null && url is not "") return;

            url = Environment.GetEnvironmentVariable("NEURO_SDK_WS_URL", EnvironmentVariableTarget.User);
            if (url is not null && url is not "") return;

            url = Environment.GetEnvironmentVariable("NEURO_SDK_WS_URL", EnvironmentVariableTarget.Machine);
        }
    }
}
