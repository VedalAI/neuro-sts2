#nullable enable

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using NeuroSdk.Actions;
using NeuroSdk.Messages.Outgoing;
using NeuroSdk.Websocket;
using Sts2Agent;

namespace NeuroSdk
{

    [HarmonyPatch(typeof(NGame), nameof(NGame._Notification))]
    public static class NGamePatch
    {
        public static bool OnlyOnce = false;
        [HarmonyPostfix]
        public static void PostFix(int what)
        {

            switch (what)
            {
                case (int)Godot.Node.NotificationReady:
                    var tree = (Engine.GetMainLoop() as SceneTree).Root.GetNode("Game");
                    tree.SetProcessInternal(true);
                    tree.SetProcess(true);
                    NeuroSdkSetup.Initialize(tree, "Slay The Spire 2");
                    Plugin.LogDebug("NeuroSDKReady");
                    OnlyOnce = true;
                    break;

                case (int)Godot.Node.NotificationInternalProcess:
                    NeuroSdkSetup.WebsocketInstance.Loop();
                    break;
                case (int)1006:
                    NeuroSdkSetup.ActionHandleInstance?.Quit();
                    break;
                default:
                    break;
            }
        }
    }
    public static partial class NeuroSdkSetup
    {
        public static CommandHandler CommandInstance;
        public static NeuroActionHandler ActionHandleInstance;
        public static WebsocketConnection WebsocketInstance;
        public static void Initialize(Node Root, string game)
        {
            var neuroSDKNode = new Node();

            var MainLoop = Engine.GetMainLoop();
            // Engine.RegisterSingleton("neurosdk", neuroSDKNode);
            if (MainLoop is SceneTree sceneTree)
            {
                // await sceneTree.ToSignal(sceneTree, SceneTree.SignalName.ProcessFrame);
                Root.AddChild(neuroSDKNode);
                Plugin.LogDebug("Added NeuroSDK to sceneTree");
            }
            else
            {
                Plugin.LogError("MainLoop isn't a SceneTree!!");
            }
            WebsocketConnection connection = new();
            neuroSDKNode.AddChild(connection);
            WebsocketInstance = connection;
            connection.game = game;


            var commandHandler = new CommandHandler();
            connection.AddChild(commandHandler);
            CommandInstance = commandHandler;
            connection.commandHandler = commandHandler;

            var messageQueue = new MessageQueue();
            connection.messageQueue = messageQueue;

            var neuroActionHandler = new NeuroActionHandler();
            connection.AddChild(neuroActionHandler);


            NeuroSdkSetup.CommandInstance.Initialize();
            NeuroSdkSetup.WebsocketInstance.Startup();


            Context.Send("You are Playing Slay the Spire 2");


        }
    }
}
