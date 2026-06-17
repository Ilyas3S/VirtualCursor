using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VirtualCursor.Common
{
	public class SignalingClient : IDisposable
	{
		private ClientWebSocket _ws;
		private readonly string _gatewayUrl;
		private readonly string _sessionId;
		private CancellationTokenSource _cts;
		private bool _disposed;

		public event Func<string, string, Task> OnSignalReceived;
		public event Action<byte[]> OnRawDataReceived;

		public SignalingClient(string gatewayUrl, string sessionId = null)
		{
			_gatewayUrl = gatewayUrl;
			_sessionId = sessionId;
		}

		public async Task ConnectAsync()
		{
			_ws = new ClientWebSocket();
			_cts = new CancellationTokenSource();

			if (!string.IsNullOrEmpty(_sessionId))
				_ws.Options.SetRequestHeader("x-session-id", _sessionId);

			var uri = new Uri(_gatewayUrl);
			await _ws.ConnectAsync(uri, _cts.Token);
			_ = ReceiveLoop();
		}

		private async Task ReceiveLoop()
		{
			var buffer = new byte[8192];
			while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
			{
				try
				{
					var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
					if (result.MessageType == WebSocketMessageType.Close)
					{
						await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
						break;
					}

					var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
					System.Diagnostics.Debug.WriteLine($"Signaling received: {json}");

					// Игнорируем не-JSON сообщения (например, "Target not connected")
					if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{"))
					{
						System.Diagnostics.Debug.WriteLine($"Ignoring non-JSON message: {json}");
						continue;
					}

					using var doc = JsonDocument.Parse(json);
					var root = doc.RootElement;
					if (root.TryGetProperty("type", out var typeProp))
					{
						string type = typeProp.GetString();
						if (type == "raw" && root.TryGetProperty("data", out var dataProp))
						{
							var raw = Convert.FromBase64String(dataProp.GetString());
							OnRawDataReceived?.Invoke(raw);
						}
						else if (root.TryGetProperty("data", out var dataProp2))
						{
							string data = dataProp2.GetString();
							if (OnSignalReceived != null)
								await OnSignalReceived.Invoke(type, data);
						}
					}
					else if (root.TryGetProperty("sessionId", out _))
					{
						// Просто уведомление о сессии – игнорируем
						System.Diagnostics.Debug.WriteLine($"Session info: {json}");
					}
				}
				catch (JsonException je)
				{
					System.Diagnostics.Debug.WriteLine($"JSON parse error: {je.Message}");
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"Receive error: {ex.Message}");
				}
			}
		}

		public async Task SendSignalAsync(string targetSessionId, string type, string data)
		{
			if (_ws?.State != WebSocketState.Open)
				throw new InvalidOperationException("WebSocket not connected");

			var msg = new { targetSessionId, type, data };
			var json = JsonSerializer.Serialize(msg);
			var bytes = Encoding.UTF8.GetBytes(json);
			await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
		}

		public async Task DisconnectAsync()
		{
			_cts?.Cancel();
			if (_ws?.State == WebSocketState.Open)
				await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
			_ws?.Dispose();
		}

		public void Dispose()
		{
			if (!_disposed)
			{
				DisconnectAsync().GetAwaiter().GetResult();
				_cts?.Dispose();
				_disposed = true;
			}
		}

		public async Task SendCandidateAsync(string targetSessionId, string addressPort) // address:port
		{
			await SendSignalAsync(targetSessionId, "candidate", addressPort);
		}
	}
}