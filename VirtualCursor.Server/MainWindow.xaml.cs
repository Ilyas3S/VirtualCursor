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


		// Размеры спрайтов (в пикселях)
		private double _spriteWidth, _spriteHeight;
		private double _maxX, _maxY;

		// -------------------- NEW: состояния курсоров --------------------
		// Позиции спрайтов в экранных координатах
		private Point _localCursorPos;    // позиция основного курсора (фантом)
		private Point _remoteCursorPos;   // позиция удалённого курсора

		// Состояние захвата
		private bool _isBlockedByRemote = false;
		private bool _isTakedByRemote = false;

		// -------------------- NEW: глобальный хук мыши --------------------
		private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
		private HookProc _mouseHookProc;
		private IntPtr _mouseHookId = IntPtr.Zero;
		private Point _mousePosPrev = new Point();

		// Поля
		private bool _needUpdateSprites = false;
		private object _updateLock = new object();
		

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern bool UnhookWindowsHookEx(IntPtr hhk);

		[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr GetModuleHandle(string lpModuleName);

		private const int WH_MOUSE_LL = 14;

		// Структура для получения координат из хука
		[StructLayout(LayoutKind.Sequential)]
		private struct POINTAPI
		{
			public int x;
			public int y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MSLLHOOKSTRUCT
		{
			public POINTAPI pt;
			public uint mouseData;
			public uint flags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		// -------------------- Остальные импорты --------------------
		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out POINT lpPoint);
		[DllImport("user32.dll")]
		private static extern bool SetCursorPos(int x, int y);
		[StructLayout(LayoutKind.Sequential)]
		public struct POINT { public int X; public int Y; }

		[DllImport("user32.dll")]
		private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

		private const uint MOUSEEVENTF_WHEEL = 0x0800;
		private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
		private const uint MOUSEEVENTF_LEFTUP = 0x0004;
		private const uint MOUSEEVENTF_MOVE = 0x0001;
		private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
		private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
		private const IntPtr EMULATED_MOUSE_FLAG = (IntPtr)0x12345678; // маркер

		private void mouse_event_emulated(uint dwFlags, uint dx, uint dy)
		{
			mouse_event(dwFlags, dx, dy, 0, (nuint)EMULATED_MOUSE_FLAG);
		}

		[DllImport("user32.dll")]
		static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

		private const uint KEYEVENTF_KEYDOWN = 0x0000;
		private const uint KEYEVENTF_KEYUP = 0x0002;

		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int nIndex);
		private const int SM_CXSCREEN = 0;
		private const int SM_CYSCREEN = 1;

		private double _screenWidth;
		private double _screenHeight;

		[DllImport("user32.dll")]
		private static extern short GetAsyncKeyState(int vKey);

		// Виртуальные коды для кнопок мыши
		private const int VK_LBUTTON = 0x01;
		private const int VK_RBUTTON = 0x02;
		private const int VK_MBUTTON = 0x04; // средняя кнопка (если понадобится)
		private bool IsMouseButtonPressed(int vKey)
		{
			// Старший бит (0x8000) показывает, зажата ли кнопка в данный момент
			return (GetAsyncKeyState(vKey) & 0x8000) != 0;
		}

		private static readonly Random _random = new Random();
		private static string GenerateShortSessionId() =>
			new string(Enumerable.Range(0, 6).Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[_random.Next(36)]).ToArray());

		public MainWindow()
		{
			InitializeComponent();
			Loaded += OnLoaded;
			SourceInitialized += OnSourceInitialized;
			Closed += OnClosed;
			// В конструкторе или OnLoaded подписываемся на рендеринг
			CompositionTarget.Rendering += OnRendering;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			SessionIdText.Text = $"ID: {_mySessionId}";

			_spriteWidth = RemoteSprite.ActualWidth;   // предполагаем, что оба спрайта одинакового размера
			_spriteHeight = RemoteSprite.ActualHeight;
			_maxX = SystemParameters.PrimaryScreenWidth - _spriteWidth;
			_maxY = SystemParameters.PrimaryScreenHeight - _spriteHeight;
			_screenWidth = GetSystemMetrics(SM_CXSCREEN);
			_screenHeight = GetSystemMetrics(SM_CYSCREEN);

			// Начальные позиции: основной в центре, удалённый чуть сбоку
			double startXLocal = (SystemParameters.PrimaryScreenWidth - _spriteWidth) / 2;
			double startYLocal = (SystemParameters.PrimaryScreenHeight - _spriteHeight) / 2;
			_localCursorPos = new Point(startXLocal, startYLocal);
			_remoteCursorPos = new Point(startXLocal + 50, startYLocal + 50); // сместим для наглядности

			UpdateSprites();

			// -------------------- NEW: запуск глобального хука мыши --------------------
			StartGlobalMouseHook();

			_ = SetupNetworkingAsync();
		}

		private void OnSourceInitialized(object sender, EventArgs e)
		{
			var hwnd = new WindowInteropHelper(this).Handle;
			int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
			SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
		}


		private void UpdateSprites()
		{
			// Просто устанавливаем флаг – рендеринг сам обновит позиции
			_needUpdateSprites = true;
		}
		private void OnRendering(object sender, EventArgs e)
		{
			if (_needUpdateSprites)
			{
				_needUpdateSprites = false;
				// Обновляем позиции напрямую (уже в UI-потоке)
				Canvas.SetLeft(RemoteSprite, _remoteCursorPos.X);
				Canvas.SetTop(RemoteSprite, _remoteCursorPos.Y);
				Canvas.SetLeft(LocalSprite, _localCursorPos.X);
				Canvas.SetTop(LocalSprite, _localCursorPos.Y);
			}
		}

		private Point ConverseNormalPoint(ushort x, ushort y)
		{
			double relX = x / 65535.0;
			double relY = y / 65535.0;
			return new Point(relX * _screenWidth, relY * _screenHeight);
		}

		// -------------------- Обработка команд от клиента --------------------
		private void OnUdpDataReceived(IPEndPoint remote, byte[] data)
		{
			string json = Encoding.UTF8.GetString(data);
			Debug.WriteLine($"Server received: {json}");
			var cmd = JsonSerializer.Deserialize<CursorCommand>(json);
			if (cmd == null)
			{
				Debug.WriteLine("Deserialization failed");
				return;
			}

			Dispatcher.Invoke(() =>
			{
				switch (cmd.Type)
				{
					case "MOVE":
						_remoteCursorPos = ConverseNormalPoint(cmd.X, cmd.Y);
						if (_isTakedByRemote)
						{
							// Двигаем системный курсор за удалённым курсором
							SetSystemCursorPosition(_remoteCursorPos);
							// Синхронизируем _mousePosPrev, чтобы следующий дельта в хуке считался от новой позиции
						}
						UpdateSprites();
						break;

					case "LEFT_DOWN":
						if (true) //!_isDraggingByRemote)
						{
							// Начинаем захват
							_ = StartRemoteDrag();
						}
						break;

					case "LEFT_UP":
						if (_isTakedByRemote)
						{
							// Завершаем захват
							StopRemoteDrag();
						}
						break;

					case "RIGHT_DOWN":
						if (true) //!_isDraggingByRemote)
						{
							_ = PerformRightButtonDown();
						}
						break;

					case "RIGHT_UP":
						if (_isTakedByRemote)
						{
							PerformRightButtonUp();
						}
						break;

					case "HOVER":
						if (!_isTakedByRemote)
						{
							_ = PerformHoverAsync(); // асинхронный запуск, не ждём
						}
						break;

					case "WHEEL":
						int delta = (int)cmd.Y;
						PerformWheel(delta);
						break;
					case "LSHIFT_DOWN":
						keybd_event((byte)0xA0, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
						break;
					case "LSHIFT_UP":
						keybd_event((byte)0xA0, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
						break;
					case "LCTRL_DOWN":
						keybd_event((byte)0xA2, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
						break;
					case "LCTRL_UP":
						keybd_event((byte)0xA2, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
						break;
					case "LALT_DOWN":
						keybd_event((byte)0xA4, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
						break;
					case "LALT_UP":
						keybd_event((byte)0xA4, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
						break;
					case "SPACE_DOWN":
						keybd_event((byte)0x20, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
						break;
					case "SPACE_UP":
						keybd_event((byte)0x20, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
						break;
					case "TAB_DOWN":
						keybd_event((byte)0x09, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
						break;
					case "TAB_UP":
						keybd_event((byte)0x09, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
						break;
				}
			});
		}

		private void SetSystemCursorPosition(Point pos)
		{
			SetCursorPos((int)pos.X, (int)pos.Y);
			_mousePosPrev = pos;
		}

		// -------------------- NEW: методы управления --------------------
		private void PerformWheel(int delta)
		{
			// Перемещаем курсор в позицию удалённого курсора (опционально, чтобы колесо прокручивалось там)
			SetSystemCursorPosition(_remoteCursorPos);
			// эмулируем колесо
			mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, (UIntPtr)EMULATED_MOUSE_FLAG);
		}
		private async Task StartRemoteDrag()
		{
			// Сначала принудительно отпускаем все кнопки мыши
			if (IsMouseButtonPressed(VK_LBUTTON))
				mouse_event_emulated(MOUSEEVENTF_LEFTUP, 0, 0);
			if (IsMouseButtonPressed(VK_RBUTTON))
				mouse_event_emulated(MOUSEEVENTF_RIGHTUP, 0, 0);

			// Перемещаем системный курсор на позицию удалённого курсора
			SetSystemCursorPosition(_remoteCursorPos);
			_isTakedByRemote = true;
			await Task.Delay(30);
			mouse_event_emulated(MOUSEEVENTF_LEFTDOWN, 0, 0);
			_isBlockedByRemote = true;
		}

		private void StopRemoteDrag()
		{
			mouse_event_emulated(MOUSEEVENTF_LEFTUP, 0, 0);
			_isTakedByRemote = false;
			_isBlockedByRemote = false;
			SetSystemCursorPosition(_localCursorPos);
		}

		private void PerformClickAt(int x, int y)
		{
			GetCursorPos(out POINT originalPos);
			SetSystemCursorPosition(new Point(x, y)); // временно
			mouse_event_emulated(MOUSEEVENTF_LEFTDOWN, 0, 0);
			mouse_event_emulated(MOUSEEVENTF_LEFTUP, 0, 0);
			SetSystemCursorPosition(new Point(originalPos.X, originalPos.Y));
		}

		private async Task PerformRightButtonDown()
		{
			// Сначала принудительно отпускаем все кнопки мыши
			if (IsMouseButtonPressed(VK_LBUTTON))
				mouse_event_emulated(MOUSEEVENTF_LEFTUP, 0, 0);
			if (IsMouseButtonPressed(VK_RBUTTON))
				mouse_event_emulated(MOUSEEVENTF_RIGHTUP, 0, 0);

			// Перемещаем системный курсор на позицию удалённого курсора
			SetSystemCursorPosition(_remoteCursorPos);
			_isTakedByRemote = true;
			await Task.Delay(30);
			mouse_event_emulated(MOUSEEVENTF_RIGHTDOWN, 0, 0);
			_isBlockedByRemote = true;
		}

		private void PerformRightButtonUp()
		{
			mouse_event_emulated(MOUSEEVENTF_RIGHTUP, 0, 0);
			_isTakedByRemote = false;
			_isBlockedByRemote = false;
			SetSystemCursorPosition(_localCursorPos);
		}

		private async Task PerformHoverAsync()
		{
			// Сначала принудительно отпускаем все кнопки мыши
			if (IsMouseButtonPressed(VK_LBUTTON))
				mouse_event_emulated(MOUSEEVENTF_LEFTUP, 0, 0);
			if (IsMouseButtonPressed(VK_RBUTTON))
				mouse_event_emulated(MOUSEEVENTF_RIGHTUP, 0, 0);

			SetSystemCursorPosition(_remoteCursorPos);
			_isTakedByRemote = true;
			await Task.Delay(1000);
			_isTakedByRemote = false;
			SetSystemCursorPosition(_localCursorPos);
		}

		// -------------------- NEW: глобальный хук мыши --------------------
		private void StartGlobalMouseHook()
		{
			_mouseHookProc = MouseHookCallback;
			using (Process curProcess = Process.GetCurrentProcess())
			using (ProcessModule curModule = curProcess.MainModule)
			{
				_mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc,
					GetModuleHandle(curModule.ModuleName), 0);
				if (_mouseHookId == IntPtr.Zero)
				{
					Debug.WriteLine("Не удалось установить хук мыши");
				}
			}
		}

		private void StopGlobalMouseHook()
		{
			if (_mouseHookId != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_mouseHookId);
				_mouseHookId = IntPtr.Zero;
			}
		}

		private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
		{
			if (nCode >= 0)
			{
				MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				// Если событие от нас – пропускаем без блокировки
				if (hookStruct.dwExtraInfo == EMULATED_MOUSE_FLAG)
					return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

				Point mousePos = new Point(hookStruct.pt.x, hookStruct.pt.y);
				Vector delta = mousePos - _mousePosPrev;

				if (_isTakedByRemote)
				{
					// Обновляем фантом только при движении мыши (wParam == WM_MOUSEMOVE)
					// Чтобы не обрабатывать нажатия кнопок как движения
					if (wParam == (IntPtr)0x0200) // WM_MOUSEMOVE
					{
						_localCursorPos += delta;
						UpdateSprites();
					}
					if (_isBlockedByRemote)
					// Блокируем все события (кроме наших), чтобы реальная мышь не влияла на системный курсор
						return (IntPtr)1;
					else
						return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
				}
				else
				{
					// Свободный режим: обновляем фантом, системный курсор управляется реальной мышью
					if (wParam == (IntPtr)0x0200)
					{
						_localCursorPos = mousePos;
						_mousePosPrev = mousePos;
						UpdateSprites();
					}
					
					return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
				}
			}
			return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
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
		[property: JsonPropertyName("X")] ushort X,  // было double
		[property: JsonPropertyName("Y")] ushort Y   // было double
	);
}