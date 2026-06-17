using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VirtualCursor.Common
{
	public class HolePunchUdpClient : IDisposable
	{
		private UdpClient _udp;
		private CancellationTokenSource _cts;
		private Task _receiveTask;
		private Task _punchTask;
		public event Action<IPEndPoint, byte[]> DataReceived;

		public IPEndPoint LocalEndPoint { get; private set; }
		public IPEndPoint PublicEndPoint { get; private set; }

		private IPEndPoint _remotePublic;
		private bool _punchingActive;

		public HolePunchUdpClient(int localPort = 0)
		{
			_udp = new UdpClient(localPort);
			LocalEndPoint = (IPEndPoint)_udp.Client.LocalEndPoint;
		}

		public async Task<bool> InitializeWithStunAsync(string stunServer = "stun.sipnet.ru", int stunPort = 3478)
		{
			PublicEndPoint = await StunClient.GetPublicEndPointAsync(stunServer, stunPort);
			if (PublicEndPoint == null) return false;

			_cts = new CancellationTokenSource();
			_receiveTask = Task.Run(ReceiveLoop);
			return true;
		}

		public void SetRemotePublicEndPoint(IPEndPoint remote)
		{
			if (_remotePublic != null) return; // уже установлен
			_remotePublic = remote;
			StartPunching();
		}

		private void StartPunching()
		{
			_punchingActive = true;
			_punchTask = Task.Run(async () =>
			{
				byte[] punchData = Encoding.UTF8.GetBytes("PUNCH");
				while (_punchingActive && !_cts.IsCancellationRequested)
				{
					await _udp.SendAsync(punchData, punchData.Length, _remotePublic);
					await Task.Delay(50);
				}
			});

			Debug.WriteLine("Hole punching succeeded!");
		}

		private async Task ReceiveLoop()
		{
			while (!_cts.IsCancellationRequested)
			{
				try
				{
					var result = await _udp.ReceiveAsync(_cts.Token);
					// Если получили PUNCH от удалённого, не вызываем событие, просто игнорируем
					Debug.WriteLine($"[{LocalEndPoint}] Received from {result.RemoteEndPoint}: {BitConverter.ToString(result.Buffer)}");
					string text = Encoding.UTF8.GetString(result.Buffer);
					Debug.WriteLine($"[{LocalEndPoint}] Text: {text}");
					if (text == "PUNCH")
					{
						// Можно отправить подтверждение, но не обязательно
						// Ответим один раз, чтобы другая сторона узнала о нас
						await _udp.SendAsync(Encoding.UTF8.GetBytes("PUNCH-ACK"), result.Buffer.Length, result.RemoteEndPoint);
						continue;
					}
					DataReceived?.Invoke(result.RemoteEndPoint, result.Buffer);
				}
				catch (OperationCanceledException) { break; }
				catch (SocketException se) when (se.ErrorCode == 10054) { continue; }
				catch (Exception ex) { Debug.WriteLine($"Recv err: {ex.Message}"); }
			}
		}

		public async Task SendToAsync(byte[] data, IPEndPoint remote)
		{
			await _udp.SendAsync(data, data.Length, remote);
		}

		public async Task SendToRemoteAsync(byte[] data)
		{
			if (_remotePublic == null)
				throw new InvalidOperationException("Remote endpoint not set");

			await _udp.SendAsync(data, data.Length, _remotePublic);
		}

		public void Dispose()
		{
			_punchingActive = false;
			_cts?.Cancel();
			_udp?.Close();
			_udp?.Dispose();
		}
	}
}