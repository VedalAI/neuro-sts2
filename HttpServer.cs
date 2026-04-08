using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sts2Agent;

// public class HttpServer
// {
//     private readonly int _port;
//     private HttpListener? _listener;
//     private CancellationTokenSource? _cts;
//     private readonly ManualResetEventSlim _decisionReady = new(false);
//     private readonly ManualResetEventSlim _actionStabilityReady = new(false);

//     public HttpServer(int port = 8080)
//     {
//         _port = port;
//     }

//     public void Start()
//     {
//         _cts = new CancellationTokenSource();
//         _listener = new HttpListener();
//         _listener.Prefixes.Add($"http://localhost:{_port}/");
//         _listener.Start();
//         Plugin.Log($"HTTP server listening on port {_port}");

//         Task.Run(() => ListenLoop(_cts.Token));
//     }

//     public void Stop()
//     {
//         _cts?.Cancel();
//         _listener?.Stop();
//         _listener?.Close();
//         _decisionReady.Set(); // unblock any waiting requests
//         _actionStabilityReady.Set(); // unblock any action waiting for stability
//         Plugin.Log("HTTP server stopped.");
//     }

//     /// <summary>
//     /// Signal that a decision point has been reached (turn start, room entered, etc.).
//     /// </summary>
//     public void SignalDecisionPoint()
//     {
//         Plugin.LogDebug("SignalDecisionPoint: setting _decisionReady and _actionStabilityReady");
//         _decisionReady.Set();
//         _actionStabilityReady.Set();
//     }

//     private async Task ListenLoop(CancellationToken ct)
//     {
//         while (!ct.IsCancellationRequested)
//         {
//             try
//             {
//                 var context = await _listener!.GetContextAsync().ConfigureAwait(false);
//                 _ = Task.Run(() => HandleRequest(context), ct);
//             }
//             catch (ObjectDisposedException)
//             {
//                 break;
//             }
//             catch (HttpListenerException)
//             {
//                 break;
//             }
//             catch (Exception e)
//             {
//                 Plugin.LogError($"HTTP listen error: {e.Message}");
//             }
//         }
//     }

//     private void HandleRequest(HttpListenerContext context)
//     {
//         var request = context.Request;
//         var response = context.Response;

//         try
//         {
//             var path = request.Url?.AbsolutePath ?? "";
//             var method = request.HttpMethod;

//             string responseBody;
//             int statusCode = 200;

//             switch (path)
//             {
//                 case "/state" when method == "GET":
//                     responseBody = "";
//                     break;

//                 case "/state/wait" when method == "GET":
//                     responseBody = HandleStateWait(request);
//                     break;

//                 case "/map" when method == "GET":
//                     responseBody = MapSerializer.Serialize();
//                     break;

//                 case "/action" when method == "POST":
//                     responseBody = HandleAction(request);
//                     statusCode = 200;
//                     break;

//                 case "/log-level" when method == "GET":
//                     responseBody = HandleLogLevel(request);
//                     break;

//                 default:
//                     statusCode = 404;
//                     responseBody = "{\"error\": \"Not found\"}";
//                     break;
//             }

//             response.StatusCode = statusCode;
//             response.ContentType = "application/json; charset=utf-8";
//             response.Headers.Add("Access-Control-Allow-Origin", "*");
//             var buffer = Encoding.UTF8.GetBytes(responseBody);
//             response.ContentLength64 = buffer.Length;
//             response.OutputStream.Write(buffer, 0, buffer.Length);
//         }
//         catch (Exception e)
//         {
//             Plugin.LogError($"HTTP request error: {e.Message}");
//             try
//             {
//                 response.StatusCode = 500;
//                 var errorBody = Encoding.UTF8.GetBytes($"{{\"error\": \"{EscapeJson(e.Message)}\"}}");
//                 response.ContentLength64 = errorBody.Length;
//                 response.OutputStream.Write(errorBody, 0, errorBody.Length);
//             }
//             catch
//             {
//                 // Response may already be closed
//             }
//         }
//         finally
//         {
//             try { response.Close(); } catch { }
//         }
//     }

//     private string HandleStateWait(HttpListenerRequest request)
//     {
//         // If the game is already stable, return immediately
//         if (GameStabilityDetector.IsStable())
//         {
//             Plugin.LogDebug("HandleStateWait: already stable, returning immediately");
//             // return GameStateSerializer.Serialize();
//         }

//         // Parse optional timeout query param (default 30s)
//         var timeoutStr = request.QueryString["timeout"];
//         var timeoutMs = 30000;
//         if (int.TryParse(timeoutStr, out var t) && t > 0)
//             timeoutMs = Math.Min(t, 120000);

//         // Reset to discard stale signals from previous decision points,
//         // then re-check stability to close the race window (game may have
//         // become stable between our first IsStable() check and the Reset).
//         _decisionReady.Reset();
//         if (GameStabilityDetector.IsStable())
//         {
//             Plugin.LogDebug("HandleStateWait: became stable after reset, returning");
//             // return GameStateSerializer.Serialize();
//         }

//         // Ensure a stability poll is running so we don't rely solely on events
//         GameStabilityDetector.ScheduleStabilityCheck();

//         Plugin.LogDebug($"HandleStateWait: waiting up to {timeoutMs}ms for stability signal...");
//         var signaled = _decisionReady.Wait(timeoutMs);
//         if (!signaled)
//         {
//             Plugin.LogDebug("HandleStateWait: TIMED OUT");
//             Plugin.LogDebug($"HandleStateWait: IsStable()={GameStabilityDetector.IsStable()}");
//             return "{\"timeout\": true}";
//         }

//         Plugin.LogDebug("HandleStateWait: signaled, returning state");
//         // return GameStateSerializer.Serialize();
//         return "";
//     }

//     private string HandleAction(HttpListenerRequest request)
//     {
//         using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
//         var body = reader.ReadToEnd();

//         if (string.IsNullOrWhiteSpace(body))
//         {
//             return "{\"error\": \"Empty request body\"}";
//         }

//         try
//         {
//             Plugin.Log($"Action received: {body}");

//             // Mark that an action is starting so stability detector re-signals
//             GameStabilityDetector.OnActionStarting();
//             _decisionReady.Reset();
//             _actionStabilityReady.Reset();
//             Plugin.LogDebug("HandleAction: starting ActionExecutor.Execute");

//             var task = ActionExecutor.Execute(body);
//             // Block the HTTP thread waiting for the action to complete (with timeout)
//             if (!task.Wait(TimeSpan.FromSeconds(15)))
//             {
//                 Plugin.LogError("HandleAction: ActionExecutor.Execute timed out after 15s");

//                 return "{\"error\": \"Action execution timed out\"}";
//             }

//             var result = task.Result;
//             Plugin.LogDebug($"HandleAction: action result={result}");

//             // If action succeeded, wait for game to stabilize and return new state
//             using var resultDoc = JsonDocument.Parse(result);
//             if (!resultDoc.RootElement.TryGetProperty("error", out _))
//             {
//                 // Reset signals to discard any stale ones, then schedule a fresh check.
//                 // CRITICAL: also reset _wasStable — during action execution, game action
//                 // events may have set _wasStable=true. If we don't reset it, the next
//                 // CheckStability sees stable=true && _wasStable=true → no-op, no event fires,
//                 // and _actionStabilityReady never gets set → 10s timeout.
//                 _decisionReady.Reset();
//                 _actionStabilityReady.Reset();
//                 GameStabilityDetector.ResetWasStable();

//                 Plugin.LogDebug("HandleAction: action succeeded, scheduling stability check");
//                 Plugin.LogDebug($"HandleAction: IsStable()={GameStabilityDetector.IsStable()}");
//                 // Schedule a stability check for actions that don't go through
//                 // the game's action queue (e.g., UI click actions like rewards, map, events)
//                 GameStabilityDetector.ScheduleStabilityCheck();
//                 Plugin.LogDebug("HandleAction: waiting for stability (up to 10s)...");
//                 var stable = _actionStabilityReady.Wait(10000);
//                 Plugin.LogDebug($"HandleAction: stability wait returned, stable={stable}");
//                 Plugin.LogDebug($"HandleAction: IsStable() after wait={GameStabilityDetector.IsStable()}");
//                 var stateJson = GameStateSerializer.Serialize();
//                 // Plugin.LogDebug($"HandleAction: serialized state length={stateJson.Length}");

//                 // Extract message from action result
//                 string message = "ok";
//                 try
//                 {
//                     using var doc = JsonDocument.Parse(result);
//                     message = doc.RootElement.GetProperty("message").GetString() ?? "ok";
//                 }
//                 catch { }

//                 return $"{{\"status\":\"ok\",\"message\":\"{EscapeJson(message)}\",\"stable\":{(stable ? "true" : "false")},\"state\":{stateJson}}}";
//             }

//             // Action failed — re-signal decision point so /state/wait doesn't hang
//             SignalDecisionPoint();
//             return result;
//         }
//         catch (AggregateException ae) when (ae.InnerException != null)
//         {
//             Plugin.LogError($"Action error: {ae.InnerException.Message}");
//             SignalDecisionPoint();
//             return $"{{\"error\": \"{EscapeJson(ae.InnerException.Message)}\"}}";
//         }
//         catch (Exception e)
//         {
//             Plugin.LogError($"Action error: {e.Message}");
//             SignalDecisionPoint();
//             return $"{{\"error\": \"{EscapeJson(e.Message)}\"}}";
//         }
//     }

//     private string HandleLogLevel(HttpListenerRequest request)
//     {
//         var level = request.QueryString["level"];
//         if (string.IsNullOrEmpty(level))
//             return $"{{\"level\": \"{Plugin.CurrentLogLevel.ToString().ToLower()}\"}}";

//         if (Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed))
//         {
//             Plugin.CurrentLogLevel = parsed;
//             Plugin.Log($"Log level changed to {parsed}");
//             return $"{{\"level\": \"{parsed.ToString().ToLower()}\"}}";
//         }

//         return "{\"error\": \"Invalid level. Use: debug, info, error\"}";
//     }

//     private static string EscapeJson(string s)
//     {
//         return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
//     }
// }
