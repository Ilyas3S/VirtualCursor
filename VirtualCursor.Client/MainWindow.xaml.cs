using System.Diagnostics;
using System.Net;
using System.Windows;
using VirtualCursor.Common;

namespace VirtualCursor.Client
{
	public partial class MainWindow : Window
	{
		private HolePunchUdpClient _udpClient;
		private SignalingClient _signalingClient;
		private readonly string _mySessionId = "client_" + Guid.NewGuid().ToString().Substring(0, 8);
		private string _targetServerSessionId;

		public MainWindow()
		{
			InitializeComponent();
		}

		private async void ConnectButton_Click(object sender, RoutedEventArgs e)
		{
			_targetServerSessionId = ServerSessionIdTextBox.Text;
			if (string.IsNullOrEmpty(_targetServerSessionId))
			{
				MessageBox.Show("Введите ID сервера.");
				return;
			}

			StatusTextBlock.Text = "Signaling: подключение...";

			// Инициализируем UDP-клиент и получаем публичный адрес
			_udpClient = new HolePunchUdpClient(); // случайный порт
			await _udpClient.InitializeWithStunAsync();
			_udpClient.DataReceived += OnUdpDataReceived;

			// Подключаемся к signaling
			string gatewayUrl = "wss://d5d0j8gkda3jfh88v0e9.avjje9e3.apigw.yandexcloud.net";
			_signalingClient = new SignalingClient(gatewayUrl, _mySessionId);
			_signalingClient.OnSignalReceived += OnSignalReceived;
			await _signalingClient.ConnectAsync();

			// Запрашиваем подключение к серверу
			StatusTextBlock.Text = "Отправка запроса серверу...";
			await _signalingClient.SendSignalAsync(_targetServerSessionId, "connect_request", _mySessionId);
		}

		private async Task OnSignalReceived(string type, string data)
		{
			Dispatcher.Invoke(() => StatusTextBlock.Text = $"Signal: {type}");

			if (type == "candidate")
			{
				// Получили публичный адрес сервера
				var parts = data.Split(':');
				if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out int port))
				{
					// ПРОВЕРКА НА ЛОКАЛЬНОСТЬ
					if (ip.Equals(_udpClient.PublicEndPoint.Address) || ip.ToString() == "127.0.0.1" || ip.ToString().StartsWith("192.168."))
					{
						ip = IPAddress.Loopback;
						Debug.WriteLine("Local mode: using 127.0.0.1");
					}
					
					Debug.WriteLine($"Client Punching to {ip}:{port}");
					var serverEndPoint = new IPEndPoint(ip, port);
					_udpClient.SetRemotePublicEndPoint(serverEndPoint);

					// Отправляем серверу наш публичный адрес (он может пригодиться для реальных удалённых запусков)
					string myPublic = $"{_udpClient.PublicEndPoint.Address}:{_udpClient.PublicEndPoint.Port}";
					await _signalingClient.SendCandidateAsync(_targetServerSessionId, myPublic);
					// Через некоторое время (после пробивки) откроем окно управления
					_ = Task.Run(async () =>
					{
						await Task.Delay(2000); // ждём, пока отверстие пробито
						await Dispatcher.InvokeAsync(() =>
						{
							var controlWindow = new CursorControlWindow(_udpClient);
							controlWindow.Show();
							//this.Hide();
							this.Close();
						});
					});
				}
			}
		}

		private void OnUdpDataReceived(IPEndPoint remote, byte[] data)
		{
			// Клиент не ожидает команд от сервера (только отправляет), но можно обработать
		}

		protected override void OnClosed(EventArgs e)
		{
			//_udpClient?.Dispose();
			//_signalingClient?.Dispose();
			base.OnClosed(e);
		}
	}
}