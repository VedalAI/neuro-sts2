using System;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using NeuroSdk;
using NeuroSdk.Messages.Outgoing;

namespace Sts2Agent;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Error = 2,
    Warning = 3,
}

[ModInitializer("Initialize")]
public static class Plugin
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "sts2agent.log");

    public static LogLevel CurrentLogLevel { get; set; } = LogLevel.Debug;

    private static Harmony? _harmony;

    public static void Initialize()
    {
        try
        {
            _harmony = new Harmony("neuro-sts2");
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(Assembly.GetExecutingAssembly());

            GameStabilityDetector.Initialize();
            GameStabilityDetector.OnBecameStable += STS2NeuroIntegration.NeuroIntegration.SignalDecisionPoint;

            RunManager.Instance.RoomEntered += OnRoomEntered;

            Log("Plugin initialized. Patches applied.");
        }
        catch (Exception e)
        {
            LogError($"Failed to initialize: {e}");
        }
    }

    private static void OnRoomEntered()
    {
        try
        {
            GameStabilityDetector.OnRoomEntered();
        }
        catch (Exception e)
        {
            LogError($"Error in OnRoomEntered: {e}");
        }
    }

    public static bool IsMultiplayer() => RunManager.Instance?.NetService?.Type.IsMultiplayer() ?? false;

    public static void Log(string message) => Log(LogLevel.Info, message);
    public static void LogDebug(string message) => Log(LogLevel.Debug, message);
    public static void LogError(string message) => Log(LogLevel.Error, message);
    public static void LogWarning(string message) => Log(LogLevel.Warning, message);

    public static void Log(LogLevel level, string message)
    {
        if (level < CurrentLogLevel) return;
        try
        {
            var prefix = level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARNING",
                _ => "INFO"
            };
            if (RunManager.Instance?.NetService?.Type.IsMultiplayer() ?? false)
            {
                message = $"[NetId: {RunManager.Instance.NetService.NetId}] " + message;
            }
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{prefix}] {message}\n";
            File.AppendAllText(LogPath, line, new UTF8Encoding(true));
        }
        catch
        {
            // Silently ignore logging failures
        }
    }
}
