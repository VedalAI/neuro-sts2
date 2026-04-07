#nullable enable

using System;
using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using NativeWebSocket;
using NeuroSdk.Internal;
using NeuroSdk.Messages.API;
using Sts2Agent;

namespace NeuroSdk.Websocket
{
    public partial class WebsocketConnection : Node
    {
        private const float RECONNECT_INTERVAL = 3;

        private static bool _checkingSelf;

        private static WebsocketConnection? _instance;
        public static WebsocketConnection? Instance
        {
            get
            {
                if (_instance is null && !_checkingSelf) Plugin.LogWarning("Accessed WebsocketConnection.Instance without an instance being present");
                _checkingSelf = false;
                return _instance;
            }
            private set => _instance = value;
        }

        private static WebSocket? _socket;

        public string game = "";
        public MessageQueue messageQueue = null!;
        public CommandHandler commandHandler = null!;

        public Action? onConnected;
        public Action<string>? onError;
        public Action<WebSocketCloseCode>? onDisconnected;

        public void Startup()
        {
            _checkingSelf = true;
            if (Instance is not null)
            {
                Plugin.Log("Destroying duplicate WebsocketConnection instance");
                QueueFree();
                return;
            }

            Instance = this;

            Plugin.Log("NeuroSdk WebsocketConnection is now awake");
            _ = StartWs();
        }

        // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method

        private async Task Reconnect()
        {
            await Task.Delay((int)(RECONNECT_INTERVAL * 1000));
            await StartWs();
        }

        private async Task StartWs()
        {
            if (MainThreadUtil.Instance == null) MainThreadUtil.Setup();

            try
            {
                if (_socket?.State is WebSocketState.Open or WebSocketState.Connecting) _ = _socket.Close();
            }
            catch
            {
                // ignored
            }

            string? websocketUrl = null;
            WsUrlFinder.FindWsUrl(result => websocketUrl = result);


            if (websocketUrl is null or "")
            {
                string errMessage = "Could not retrieve websocket URL.";
#if EDITOR
                errMessage += " You should set the NEURO_SDK_WS_URL environment variable.";
#endif
                Plugin.LogError(errMessage);
                return;
            }

            // Websocket callbacks get run on separate threads! Watch out
            _socket = new WebSocket(websocketUrl);
            _socket.OnOpen += () =>
            {
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                coroutine();
                return;

                async void coroutine()
                {
                    Plugin.LogDebug(GetTree()?.ToString() ?? "TREE NOT FOUND ON CONNECTION");
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    onConnected?.Invoke();
                }
            };
            _socket.OnMessage += bytes =>
            {
                string message = Encoding.UTF8.GetString(bytes);
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                ReceiveMessage(message);
            };
            _socket.OnError += error =>
            {
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                coroutine();
                return;

                async void coroutine()
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

                    onError?.Invoke(error);
                    if (error != "Unable to connect to the remote server")
                    {
                        Plugin.LogError("Websocket connection has encountered an error!");
                        Plugin.LogError(error);
                    }
                }
            };
            _socket.OnClose += code =>
            {
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                _ = coroutine();
                return;

                async Task coroutine()
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

                    onDisconnected?.Invoke(code);
                    if (code != WebSocketCloseCode.Abnormal) Plugin.LogWarning($"Websocket connection has been closed with code {code}!");
                    // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                    _ = Reconnect();
                }
            };

            _ = _socket.Connect();

        }
        public void Loop()
        {
            if (_socket?.State is not WebSocketState.Open) return;

            while (messageQueue.Count > 0)
            {
                OutgoingMessageBuilder builder = messageQueue.Dequeue()!;
                // ReSharper disable once ArrangeThisQualifier -- Il2Cpp has this as an extension method
                _ = SendTask(builder);
            }
            _socket.DispatchMessageQueue();
        }

        private async Task SendTask(OutgoingMessageBuilder builder)
        {
            string message = Jason.Serialize(builder.GetWsMessage());

            Plugin.Log($"Sending ws message {message}");

            Task task = _socket!.SendText(message);
            // ReSharper disable once RedundantDelegateCreation
            await task;

            if (task.IsCanceled || task.IsFaulted)
            {
                Plugin.LogError($"Failed to send ws message {message}");
                messageQueue.Enqueue(builder);
            }
        }

        public void Send(OutgoingMessageBuilder messageBuilder) => messageQueue.Enqueue(messageBuilder);

        public void SendImmediate(OutgoingMessageBuilder messageBuilder)
        {
            string message = Jason.Serialize(messageBuilder.GetWsMessage());

            if (_socket?.State is not WebSocketState.Open)
            {
                Plugin.LogError($"WS not open - failed to send immediate ws message {message}");
                return;
            }

            Plugin.Log($"Sending immediate ws message {message}");

            _socket.SendText(message);
        }

        private async void ReceiveMessage(string msgData)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            try
            {

                Plugin.Log("Received ws message " + msgData);

                JsonObject message = (JsonObject)JsonObject.Parse(msgData);
                string? command = message["command"]?.GetValue<string>();
                MessageJData data = new(message["data"]);

                if (command == null)
                {
                    Plugin.LogError("Received command that could not be deserialized. Wtf are you doing?");
                    return;
                }

                commandHandler.Handle(command, data);
            }
            catch (Exception e)
            {
                Plugin.LogError("Received invalid message");
                Plugin.LogError(e.ToString());
            }
        }
    }
}
