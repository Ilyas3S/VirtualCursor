using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using VirtualCursor.Common;

namespace VirtualCursor.Client
{
	public class CursorControlWindow : Window
	{
		// ---- UDP и графика ----
		private HolePunchUdpClient _udpClient;
		private double _spriteWidth, _spriteHeight;
		private double _maxX, _maxY;
		private Rectangle _sprite;

		// ---- Движение с клавиатуры ----
		private HashSet<VirtualKeys> _pressedKeys = new();
		private Vector _moveDirection = new Vector(0, 0);
		private double _currentSpeed = 0;
		private double _startSpeed = 0;
		private DateTime _currentDirectionStartTime;
		private const double MinSpeed = 150.0;
		private const double MaxSpeed = 800.0;
		private const double AccelerationTime = 0.8;
		private DateTime _lastUpdateTime;

		// ---- Разрешение экрана ----
		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int nIndex);
		private const int SM_CXSCREEN = 0;
		private const int SM_CYSCREEN = 1;
		private double _screenWidth, _screenHeight;

		// ---- Режимы ----
		private enum ControlMode { Keyboard, Mouse }
		private ControlMode _currentMode = ControlMode.Keyboard;

		// ---- Хуки ----
		private IntPtr _keyboardHookId = IntPtr.Zero;
		private IntPtr _mouseHookId = IntPtr.Zero;
		private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
		private LowLevelKeyboardProc _keyboardProc;
		private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
		private LowLevelMouseProc _mouseProc;

		private const int WH_KEYBOARD_LL = 13;
		private const int WH_MOUSE_LL = 14;
		private const int WM_KEYDOWN = 0x0100;
		private const int WM_KEYUP = 0x0101;
		private const int WM_SYSKEYDOWN = 0x0104;
		private const int WM_SYSKEYUP = 0x0105;
		private const int WM_MOUSEMOVE = 0x0200;
		private const int WM_LBUTTONDOWN = 0x0201;
		private const int WM_LBUTTONUP = 0x0202;
		private const int WM_RBUTTONDOWN = 0x0204;
		private const int WM_RBUTTONUP = 0x0205;
		private const int WM_MBUTTONDOWN = 0x0207;
		private const int WM_MBUTTONUP = 0x0208;
		private const int WM_MOUSEWHEEL = 0x020A;

		// Структура для мышиного хука
		[StructLayout(LayoutKind.Sequential)]
		private struct POINT
		{
			public int x;
			public int y;
		}
		[StructLayout(LayoutKind.Sequential)]
		private struct MSLLHOOKSTRUCT
		{
			public POINT pt;
			public uint mouseData;
			public uint flags;
			public uint time;
			public IntPtr dwExtraInfo;
		}

		// ---- Клавиши ----
		private enum VirtualKeys
		{
			VK_LEFT = 0x25,
			VK_UP = 0x26,
			VK_RIGHT = 0x27,
			VK_DOWN = 0x28,
			VK_RSHIFT = 0xA1,
			VK_RCONTROL = 0xA3,
			VK_RMENU = 0xA5,
			VK_OEM_MINUS = 0xBD,
			VK_OEM_PLUS = 0xBB,
			VK_LSHIFT = 0xA0,
			VK_LCONTROL = 0xA2,
			VK_LMENU = 0xA4,
			VK_SPACE = 0x20,
			VK_TAB = 0x09,
			VK_F12 = 0x7B
		}

		public CursorControlWindow(HolePunchUdpClient udpClient)
		{
			_udpClient = udpClient;
			InitializeWindow();
			Loaded += OnLoaded;
			SourceInitialized += OnSourceInitialized;
		}

		private void InitializeWindow()
		{
			WindowStyle = WindowStyle.None;
			AllowsTransparency = true;
			Background = Brushes.Transparent;
			Topmost = true;
			WindowState = WindowState.Maximized;
			Title = "Virtual Cursor Client - Mode: Keyboard";

			var canvas = new Canvas();
			_sprite = new Rectangle
			{
				Width = 32,
				Height = 32,
				Fill = Brushes.Red,
				Stroke = Brushes.Black,
				StrokeThickness = 1
			};
			canvas.Children.Add(_sprite);
			Content = canvas;

			Debug.WriteLine("CursorControlWindow initialized");
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			_spriteWidth = _sprite.ActualWidth;
			_spriteHeight = _sprite.ActualHeight;
			_maxX = SystemParameters.PrimaryScreenWidth - _spriteWidth;
			_maxY = SystemParameters.PrimaryScreenHeight - _spriteHeight;
			double startX = (SystemParameters.PrimaryScreenWidth - _spriteWidth) / 2;
			double startY = (SystemParameters.PrimaryScreenHeight - _spriteHeight) / 2;
			_screenWidth = GetSystemMetrics(SM_CXSCREEN);
			_screenHeight = GetSystemMetrics(SM_CYSCREEN);
			SetSpritePosition(startX, startY);

			_lastUpdateTime = DateTime.Now;
			CompositionTarget.Rendering += OnRendering;
			InstallKeyboardHook();
			InstallMouseHook(); // всегда установлен, но обрабатываем только в режиме Mouse
		}

		private void OnSourceInitialized(object sender, EventArgs e)
		{
			var hwnd = new WindowInteropHelper(this).Handle;
			int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
			SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
		}

		// ---- Установка позиции спрайта ----
		private void SetSpritePosition(double x, double y)
		{
			x = Math.Max(0, Math.Min(x, _maxX));
			y = Math.Max(0, Math.Min(y, _maxY));
			Canvas.SetLeft(_sprite, x);
			Canvas.SetTop(_sprite, y);

			// В режиме Keyboard отправляем MOVE (в режиме Mouse отправка происходит из хука)
			if (_currentMode == ControlMode.Keyboard)
				SendMove(x, y);
		}

		// ---- Отправка команд (относительные координаты) ----
		private void SendMove(double x, double y)
		{
			double relX = x / _screenWidth;
			double relY = y / _screenHeight;
			var cmd = new CursorCommand("MOVE", relX, relY);
			SendCommand(cmd);
		}

		private void SendLeftDown()
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel("LEFT_DOWN", x, y);
		}

		private void SendLeftUp()
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel("LEFT_UP", x, y);
		}

		private void SendRightDown()
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel("RIGHT_DOWN", x, y);
		}

		private void SendRightUp()
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel("RIGHT_UP", x, y);
		}

		private void SendHover()
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel("HOVER", x, y);
		}

		private void SendWheel(int delta)
		{
			var cmd = new CursorCommand("WHEEL", 0, delta);
			SendCommand(cmd);
		}

		private void SendKeyDown(string key)
		{
			// Координаты не важны, но для совместимости передаём 0,0
			var cmd = new CursorCommand(key + "_DOWN", 0, 0);
			SendCommand(cmd);
		}

		private void SendKeyUp(string key)
		{
			var cmd = new CursorCommand(key + "_UP", 0, 0);
			SendCommand(cmd);
		}

		private void SendCommandRel(string type, double x, double y)
		{
			double relX = x / _screenWidth;
			double relY = y / _screenHeight;
			var cmd = new CursorCommand(type, relX, relY);
			SendCommand(cmd);
		}

		private void SendCommand(CursorCommand cmd)
		{
			string json = JsonSerializer.Serialize(cmd);
			byte[] data = Encoding.UTF8.GetBytes(json);
			_ = _udpClient.SendToRemoteAsync(data);
		}

		// ---- Переключение режима ----
		private void ToggleMode()
		{
			if (_currentMode == ControlMode.Keyboard)
			{
				_currentMode = ControlMode.Mouse;
				Title = "Virtual Cursor Client - Mode: Mouse";
				// Синхронизируем спрайт с текущей позицией мыши
				GetCursorPos(out POINT pt);
				SetSpritePosition(pt.x, pt.y);
				Debug.WriteLine("Switched to Mouse mode");
			}
			else
			{
				_currentMode = ControlMode.Keyboard;
				Title = "Virtual Cursor Client - Mode: Keyboard";
				Debug.WriteLine("Switched to Keyboard mode");
			}
		}

		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out POINT lpPoint);

		// ---- Движение с клавиатуры (работает только в режиме Keyboard) ----
		private void UpdateMoveDirection()
		{
			if (_currentMode != ControlMode.Keyboard)
			{
				_moveDirection = new Vector(0, 0);
				return;
			}

			double dx = 0, dy = 0;
			if (_pressedKeys.Contains(VirtualKeys.VK_LEFT)) dx -= 1;
			if (_pressedKeys.Contains(VirtualKeys.VK_RIGHT)) dx += 1;
			if (_pressedKeys.Contains(VirtualKeys.VK_UP)) dy -= 1;
			if (_pressedKeys.Contains(VirtualKeys.VK_DOWN)) dy += 1;

			if (dx != 0 || dy != 0)
			{
				double len = Math.Sqrt(dx * dx + dy * dy);
				dx /= len;
				dy /= len;
			}
			Vector newDirection = new Vector(dx, dy);

			if (newDirection != _moveDirection)
			{
				double oldSpeed = _currentSpeed;
				var oldDirection = _moveDirection;
				_moveDirection = newDirection;

				if (oldDirection.Length > 0 && newDirection.Length > 0)
				{
					double dot = oldDirection.X * newDirection.X + oldDirection.Y * newDirection.Y;
					dot = Math.Max(-1.0, Math.Min(1.0, dot));
					double angle = Math.Acos(dot);
					double preservation = (1 + Math.Cos(angle)) / 2;
					preservation = Math.Max(preservation, MinSpeed / MaxSpeed);
					double newSpeed = oldSpeed * preservation;
					newSpeed = Math.Max(MinSpeed, Math.Min(MaxSpeed, newSpeed));
					_startSpeed = newSpeed;
					_currentSpeed = newSpeed;
				}
				else if (newDirection.Length > 0)
				{
					_startSpeed = MinSpeed;
					_currentSpeed = MinSpeed;
				}

				_currentDirectionStartTime = DateTime.Now;
			}
		}

		private void UpdateCurrentSpeed()
		{
			if (_moveDirection.Length > 0 && _currentMode == ControlMode.Keyboard)
			{
				double elapsed = (DateTime.Now - _currentDirectionStartTime).TotalSeconds;
				double t = Math.Min(1.0, Math.Max(0, elapsed / AccelerationTime));
				_currentSpeed = _startSpeed + (MaxSpeed - _startSpeed) * t;
			}
			else
			{
				_currentSpeed = 0;
			}
		}

		private void OnRendering(object sender, EventArgs e)
		{
			// В режиме Mouse обновление позиции происходит через хук мыши
			if (_currentMode == ControlMode.Mouse)
				return;

			var now = DateTime.Now;
			double deltaSeconds = (now - _lastUpdateTime).TotalSeconds;
			_lastUpdateTime = now;
			if (deltaSeconds > 0.1) deltaSeconds = 0.1;

			UpdateCurrentSpeed();

			if (_moveDirection.Length > 0 && _currentSpeed > 0)
			{
				double offsetX = _moveDirection.X * _currentSpeed * deltaSeconds;
				double offsetY = _moveDirection.Y * _currentSpeed * deltaSeconds;

				double newX = Canvas.GetLeft(_sprite) + offsetX;
				double newY = Canvas.GetTop(_sprite) + offsetY;
				SetSpritePosition(newX, newY);
			}
		}

		// ---- Хуки ----
		private void InstallKeyboardHook()
		{
			_keyboardProc = KeyboardHookCallback;
			using (Process curProcess = Process.GetCurrentProcess())
			using (ProcessModule curModule = curProcess.MainModule)
			{
				_keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc,
					GetModuleHandle(curModule.ModuleName), 0);
			}
		}

		private void InstallMouseHook()
		{
			_mouseProc = MouseHookCallback;
			using (Process curProcess = Process.GetCurrentProcess())
			using (ProcessModule curModule = curProcess.MainModule)
			{
				_mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc,
					GetModuleHandle(curModule.ModuleName), 0);
			}
		}

		private void UninstallHooks()
		{
			if (_keyboardHookId != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_keyboardHookId);
				_keyboardHookId = IntPtr.Zero;
			}
			if (_mouseHookId != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_mouseHookId);
				_mouseHookId = IntPtr.Zero;
			}
		}

		// ---- Колбэк клавиатуры ----
		private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
		{
			if (nCode >= 0)
			{
				int vkCode = Marshal.ReadInt32(lParam);
				bool isKeyDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
				bool isKeyUp = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);

				// F12 переключает режим всегда
				if (vkCode == (int)VirtualKeys.VK_F12 && isKeyDown)
				{
					ToggleMode();
					return (IntPtr)1; // блокируем
				}

				if (_currentMode == ControlMode.Mouse)
				{
					// Пропускаем только стрелки и модификаторы, чтобы они не мешали
					VirtualKeys key = (VirtualKeys)vkCode;
					if (key == VirtualKeys.VK_LEFT || key == VirtualKeys.VK_RIGHT ||
						key == VirtualKeys.VK_UP || key == VirtualKeys.VK_DOWN ||
						key == VirtualKeys.VK_RSHIFT || key == VirtualKeys.VK_RCONTROL ||
						key == VirtualKeys.VK_RMENU ||
						key == VirtualKeys.VK_OEM_MINUS || key == VirtualKeys.VK_OEM_PLUS)
					{
						return (IntPtr)1; // блокируем все управляющие клавиши
					}
				}

				if (isKeyDown)
				{
					VirtualKeys key = (VirtualKeys)vkCode;
					// Стрелки
					if (key == VirtualKeys.VK_LEFT || key == VirtualKeys.VK_RIGHT ||
						key == VirtualKeys.VK_UP || key == VirtualKeys.VK_DOWN)
					{
						_pressedKeys.Add(key);
						UpdateMoveDirection();
						return (IntPtr)1;
					}
					// Правый Shift -> RIGHT_DOWN
					else if (key == VirtualKeys.VK_RSHIFT)
					{
						SendRightDown();
						return (IntPtr)1;
					}
					// Правый Ctrl -> LEFT_DOWN
					else if (key == VirtualKeys.VK_RCONTROL)
					{
						SendLeftDown();
						return (IntPtr)1;
					}
					// Правый Alt -> HOVER
					else if (key == VirtualKeys.VK_RMENU)
					{
						SendHover();
						return (IntPtr)1;
					}
					// Минус -> колесо вниз
					else if (key == VirtualKeys.VK_OEM_MINUS)
					{
						SendWheel(-120);
						return (IntPtr)1;
					}
					// Равно -> колесо вверх
					else if (key == VirtualKeys.VK_OEM_PLUS)
					{
						SendWheel(120);
						return (IntPtr)1;
					}
					// Модификаторы
					else if (key == VirtualKeys.VK_LSHIFT)
					{
						SendKeyDown("LSHIFT");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_LCONTROL)
					{
						SendKeyDown("LCTRL");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_LMENU)
					{
						SendKeyDown("LALT");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_SPACE)
					{
						SendKeyDown("SPACE");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_TAB)
					{
						SendKeyDown("TAB");
						return (IntPtr)1;
					}
				}
				else if (isKeyUp)
				{
					VirtualKeys key = (VirtualKeys)vkCode;
					if (key == VirtualKeys.VK_LEFT || key == VirtualKeys.VK_RIGHT ||
						key == VirtualKeys.VK_UP || key == VirtualKeys.VK_DOWN)
					{
						_pressedKeys.Remove(key);
						UpdateMoveDirection();
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_RSHIFT)
					{
						SendRightUp();
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_RCONTROL)
					{
						SendLeftUp();
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_RMENU)
					{
						// отпускание правого Alt игнорируем
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_LSHIFT)
					{
						SendKeyUp("LSHIFT");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_LCONTROL)
					{
						SendKeyUp("LCTRL");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_LMENU)
					{
						SendKeyUp("LALT");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_SPACE)
					{
						SendKeyUp("SPACE");
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_TAB)
					{
						SendKeyUp("TAB");
						return (IntPtr)1;
					}
				}
			}
			return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
		}

		// ---- Колбэк мыши ----
		private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
		{
			if (nCode >= 0 && _currentMode == ControlMode.Mouse)
			{
				MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
				int msg = (int)wParam;

				// Движение мыши
				if (msg == WM_MOUSEMOVE)
				{
					// Обновляем спрайт и отправляем MOVE
					double x = hookStruct.pt.x;
					double y = hookStruct.pt.y;
					// Ограничиваем, чтобы спрайт не выходил за экран
					x = Math.Max(0, Math.Min(x, _maxX));
					y = Math.Max(0, Math.Min(y, _maxY));
					Canvas.SetLeft(_sprite, x);
					Canvas.SetTop(_sprite, y);
					// Отправляем относительные координаты
					double relX = x / _screenWidth;
					double relY = y / _screenHeight;
					var cmd = new CursorCommand("MOVE", relX, relY);
					SendCommand(cmd);
					// Блокируем событие, чтобы мышь не двигала локальный курсор
					//return (IntPtr)1;
					return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
				}

				// Клики и колесо
				switch (msg)
				{
					case WM_LBUTTONDOWN:
						SendLeftDown();
						return (IntPtr)1;
					case WM_LBUTTONUP:
						SendLeftUp();
						return (IntPtr)1;
					case WM_RBUTTONDOWN:
						SendRightDown();
						return (IntPtr)1;
					case WM_RBUTTONUP:
						SendRightUp();
						return (IntPtr)1;
					case WM_MBUTTONDOWN:
						// Можно добавить среднюю кнопку, если нужно
						break;
					case WM_MBUTTONUP:
						break;
					case WM_MOUSEWHEEL:
						int delta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
						SendWheel(delta);
						return (IntPtr)1;
				}
			}
			return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
		}

		// ---- Завершение ----
		protected override void OnClosed(EventArgs e)
		{
			CompositionTarget.Rendering -= OnRendering;
			UninstallHooks();
			_udpClient?.Dispose();
			base.OnClosed(e);
			Application.Current?.Shutdown();
		}

		// ---- WinAPI ----
		[DllImport("user32.dll", SetLastError = true)]
		private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
		[DllImport("user32.dll", SetLastError = true)]
		private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
		private const int GWL_EXSTYLE = -20;
		private const int WS_EX_TRANSPARENT = 0x20;
		private const int WS_EX_LAYERED = 0x80000;

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);
		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool UnhookWindowsHookEx(IntPtr hhk);
		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
		[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr GetModuleHandle(string lpModuleName);

		// Контракт для обмена данными
		public record CursorCommand(
			[property: JsonPropertyName("Type")] string Type,
			[property: JsonPropertyName("X")] double X,
			[property: JsonPropertyName("Y")] double Y
		);
	}
}