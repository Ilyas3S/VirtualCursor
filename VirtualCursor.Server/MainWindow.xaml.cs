using Open.Nat;
using System.Diagnostics;
using System.Linq;   // добавлено
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VirtualCursor.Common;

namespace VirtualCursor.Server
{
	public partial class MainWindow : Window
	{
		private const int GWL_EXSTYLE = -20;
		private const int WS_EX_TRANSPARENT = 0x20;
		private const int WS_EX_LAYERED = 0x80000;

		private HolePunchUdpClient _udpClient;
		private SignalingClient _signalingClient;
		private readonly string _mySessionId = GenerateShortSessionId();

		private double _spriteWidth, _spriteHeight;
		private double _maxX, _maxY;

		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out POINT lpPoint);
		[DllImport("user32.dll")]
		private static extern bool SetCursorPos(int x, int y);
		[StructLayout(LayoutKind.Sequential)]
		public struct POINT { public int X; public int Y; }
		[DllImport("user32.dll")]
		private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
		private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
		private const uint MOUSEEVENTF_LEFTUP = 0x0004;

		private static readonly Random _random = new Random();
		private static string GenerateShortSessionId() =>
			new string(Enumerable.Range(0, 6).Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[_random.Next(36)]).ToArray());

		public MainWindow()
		{
			InitializeComponent();
			Loaded += OnLoaded;
			SourceInitialized += OnSourceInitialized;
			Closed += OnClosed;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			SessionIdText.Text = $"ID: {_mySessionId}";
			_spriteWidth = Sprite.ActualWidth;
			_spriteHeight = Sprite.ActualHeight;
			_maxX = SystemParameters.PrimaryScreenWidth - _spriteWidth;
			_maxY = SystemParameters.PrimaryScreenHeight - _spriteHeight;
			double startX = (SystemParameters.PrimaryScreenWidth - _spriteWidth) / 2;
			double startY = (SystemParameters.PrimaryScreenHeight - _spriteHeight) / 2;
			SetSpritePosition(startX, startY);
			_ = SetupNetworkingAsync();
		}

		private void OnSourceInitialized(object sender, EventArgs e)
		{
			var hwnd = new WindowInteropHelper(this).Handle;
			int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
			SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
		}

		private void SetSpritePosition(double x, double y)
		{
			x = Math.Max(0, Math.Min(x, _maxX));
			y = Math.Max(0, Math.Min(y, _maxY));
			Dispatcher.Invoke(() =>
			{
				Canvas.SetLeft(Sprite, x);
				Canvas.SetTop(Sprite, y);
			});
		}

		private void PerformClickAt(int x, int y)
		{
			GetCursorPos(out POINT originalPos);
			SetCursorPos(x, y);
			mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
			mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
			SetCursorPos(originalPos.X, originalPos.Y);
		}

		private async Task SetupNetworkingAsync()
		{
			ConfigureFirewall();
			await AutoPortForwardAsync(9050, Protocol.Udp);

			// Создаём UDP-клиент с hole punching
			_udpClient = new HolePunchUdpClient(9050); // фиксированный порт для сервера
			await _udpClient.InitializeWithStunAsync();
			_udpClient.DataReceived += OnUdpDataReceived;

			// Подключаемся к signaling
			string gatewayUrl = "wss://d5d0j8gkda3jfh88v0e9.avjje9e3.apigw.yandexcloud.net";
			_signalingClient = new SignalingClient(gatewayUrl, _mySessionId);
			_signalingClient.OnSignalReceived += OnSignalReceived;
			await _signalingClient.ConnectAsync();

			// Сообщаем свой публичный адрес (через broadcast, чтобы клиенты знали)
			string publicAddr = $"{_udpClient.PublicEndPoint.Address}:{_udpClient.PublicEndPoint.Port}";
			await _signalingClient.SendSignalAsync("broadcast", "server_info", publicAddr);
			Dispatcher.Invoke(() => StatusTextBlock.Text = $"Сервер готов. Публичный адрес: {publicAddr}");
		}

		private async Task OnSignalReceived(string type, string data)
		{
			if (type == "connect_request")
			{
				string clientSessionId = data;
				if (_udpClient.PublicEndPoint == null)
				{
					Debug.WriteLine("Сервер: публичный адрес не получен от STUN");
					return;
				}
				// Отправляем клиенту наш публичный UDP-адрес
				string publicAddr = $"{_udpClient.PublicEndPoint.Address}:{_udpClient.LocalEndPoint.Port}";
				await _signalingClient.SendCandidateAsync(clientSessionId, publicAddr);
				// Также сообщаем клиенту, что можем принять пакеты
				Dispatcher.Invoke(() => StatusTextBlock.Text = $"Отправил клиенту {clientSessionId} адрес {publicAddr}");
			}
			else if (type == "candidate")
			{
				// Клиент прислал свой публичный адрес – начинаем hole punching
				var parts = data.Split(':');
				if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out int port))
				{
					// ПРОВЕРКА НА ЛОКАЛЬНОСТЬ
					if (ip.Equals(_udpClient.PublicEndPoint.Address) || ip.ToString() == "127.0.0.1" || ip.ToString().StartsWith("192.168."))
					{
						ip = IPAddress.Loopback;
						Debug.WriteLine("Local mode: using 127.0.0.1");
					}

					var clientEndPoint = new IPEndPoint(ip, port);
					_udpClient.SetRemotePublicEndPoint(clientEndPoint);
					Dispatcher.Invoke(() => StatusTextBlock.Text = $"Начинаем hole punching с клиентом {clientEndPoint}");
				}
			}
		}

		private void OnUdpDataReceived(IPEndPoint remote, byte[] data)
		{
			string json = Encoding.UTF8.GetString(data);
			Debug.WriteLine($"Server received: {json}");  // важно: смотрим, что приходит
			var cmd = JsonSerializer.Deserialize<CursorCommand>(json);
			if (cmd == null)
			{
				Debug.WriteLine("Deserialization failed");
				return;
			}

			if (cmd.Type == "MOVE")
				Dispatcher.Invoke(() => SetSpritePosition(cmd.X, cmd.Y));
			else if (cmd.Type == "CLICK")
				Dispatcher.Invoke(() => PerformClickAt((int)cmd.X, (int)cmd.Y));
		}

		private async void OnClosed(object sender, EventArgs e)
		{
			RemoveFirewallRule();
			_udpClient?.Dispose();
			if (_signalingClient != null) await _signalingClient.DisconnectAsync();
		}

		[DllImport("user32.dll")]
		private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
		[DllImport("user32.dll")]
		private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

		#region Port Forward & Firewall
		private async Task<bool> AutoPortForwardAsync(int port, Protocol protocol)
		{
			try
			{
				var discoverer = new NatDiscoverer();
				var cts = new CancellationTokenSource(5000);
				var device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);
				var existing = await device.GetAllMappingsAsync();
				bool exists = existing.Any(m => m.PrivatePort == port && m.Protocol == protocol);
				if (!exists)
				{
					var mapping = new Mapping(protocol, port, port, "VirtualCursor Server");
					await device.CreatePortMapAsync(mapping);
					await Task.Delay(500);
					var check = await device.GetSpecificMappingAsync(protocol, port);
					if (check != null)
					{
						Debug.WriteLine($"UPnP: порт {port} ({protocol}) успешно проброшен");
						return true;
					}
					Debug.WriteLine($"UPnP: не удалось подтвердить проброс порта {port}");
					return false;
				}
				Debug.WriteLine($"UPnP: порт {port} уже проброшен");
				return true;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"UPnP ошибка: {ex.Message}");
				return false;
			}
		}

		private void ConfigureFirewall()
		{
			try
			{
				// Добавляем правило для входящего UDP порта 9050
				var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
				{
					FileName = "netsh",
					Arguments = "advfirewall firewall add rule name=\"VirtualCursor Server\" dir=in action=allow protocol=UDP localport=9050",
					UseShellExecute = false,
					CreateNoWindow = true
				});
				process?.WaitForExit(1000);
				Debug.WriteLine("Брандмауэр: правило добавлено");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Ошибка настройки брандмауэра: {ex.Message}");
			}
		}

		private void RemoveFirewallRule()
		{
			try
			{
				var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
				{
					FileName = "netsh",
					Arguments = "advfirewall firewall delete rule name=\"VirtualCursor Server\"",
					UseShellExecute = false,
					CreateNoWindow = true
				});
				process?.WaitForExit(1000);
				Debug.WriteLine("Брандмауэр: правило удалено");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Ошибка удаления правила: {ex.Message}");
			}
		}
		#endregion
	}

	public record CursorCommand(
		[property: JsonPropertyName("Type")] string Type,
		[property: JsonPropertyName("X")] double X,
		[property: JsonPropertyName("Y")] double Y
	);
}