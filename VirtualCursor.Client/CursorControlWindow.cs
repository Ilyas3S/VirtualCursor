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
		private HolePunchUdpClient _udpClient;
		private double _spriteWidth, _spriteHeight;
		private double _maxX, _maxY;
		private Rectangle _sprite;

		private HashSet<VirtualKeys> _pressedKeys = new();
		private Vector _moveDirection = new Vector(0, 0);
		private double _currentSpeed = 0;
		private double _startSpeed = 0;
		private DateTime _currentDirectionStartTime;
		private const double MinSpeed = 150.0;
		private const double MaxSpeed = 800.0;
		private const double AccelerationTime = 0.8;
		private DateTime _lastUpdateTime;

		// В начало класса, после других импортов
		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int nIndex);
		private const int SM_CXSCREEN = 0;
		private const int SM_CYSCREEN = 1;

		// Поля в классе
		private double _screenWidth;
		private double _screenHeight;

		private enum VirtualKeys
		{
			VK_LEFT = 0x25,
			VK_UP = 0x26,
			VK_RIGHT = 0x27,
			VK_DOWN = 0x28,
			VK_RSHIFT = 0xA1,
			VK_RCONTROL = 0xA3,    // NEW: правый Ctrl
			VK_RMENU = 0xA5,      // NEW: правый Alt
			VK_OEM_MINUS = 0xBD,
			VK_OEM_PLUS = 0xBB,
			VK_LSHIFT = 0xA0,
			VK_LCONTROL = 0xA2,
			VK_LMENU = 0xA4,  // LAlt
			VK_SPACE = 0x20,
			VK_TAB = 0x09
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
			this.WindowStyle = WindowStyle.None;
			this.AllowsTransparency = true;
			this.Background = Brushes.Transparent;
			this.Topmost = true;
			this.WindowState = WindowState.Maximized;
			this.Title = "Virtual Cursor Client";

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
			this.Content = canvas;

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
			Canvas.SetLeft(_sprite, x);
			Canvas.SetTop(_sprite, y);

			SendCommandRel("MOVE", x, y);
		}

		// ---- Отправка команд для левой кнопки (захват) ----
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

		// ---- NEW: отправка команд для правой кнопки ----
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

		// ---- NEW: отправка команды Hover (кратковременное наведение) ----
		private void SendHover()
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel("HOVER", x, y);
		}

		private void SendWheel(int delta)
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			var cmd = new CursorCommand("WHEEL", 0, delta); // используем Y для дельты
			SendCommand(cmd);
		}

		private void SendKeyDown(string key)
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel(key + "_DOWN", x, y);
		}

		private void SendKeyUp(string key)
		{
			double x = Canvas.GetLeft(_sprite) + _spriteWidth / 2;
			double y = Canvas.GetTop(_sprite) + _spriteHeight / 2;
			SendCommandRel(key + "_UP", x, y);
		}

		private void SendCommandRel(string type, double x, double y)
		{
			// Преобразуем в относительные координаты (0..1)
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

		// ---- Логика движения (не меняется) ----
		private void UpdateMoveDirection()
		{
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
			if (_moveDirection.Length > 0)
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

		#region Keyboard Hook
		private IntPtr _hookId = IntPtr.Zero;
		private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
		private LowLevelKeyboardProc _proc;
		private const int WH_KEYBOARD_LL = 13;
		private const int WM_KEYDOWN = 0x0100;
		private const int WM_KEYUP = 0x0101;
		private const int WM_SYSKEYDOWN = 0x0104;
		private const int WM_SYSKEYUP = 0x0105;

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool UnhookWindowsHookEx(IntPtr hhk);

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

		[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern IntPtr GetModuleHandle(string lpModuleName);

		private void InstallKeyboardHook()
		{
			_proc = HookCallback;
			using (Process curProcess = Process.GetCurrentProcess())
			using (ProcessModule curModule = curProcess.MainModule)
			{
				_hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
			}
		}

		private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
		{
			if (nCode >= 0)
			{
				int vkCode = Marshal.ReadInt32(lParam);
				bool isKeyDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
				bool isKeyUp = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);

				if (isKeyDown)
				{
					VirtualKeys key = (VirtualKeys)vkCode;
					// Обработка стрелок для движения
					if (key == VirtualKeys.VK_LEFT || key == VirtualKeys.VK_RIGHT ||
						key == VirtualKeys.VK_UP || key == VirtualKeys.VK_DOWN)
					{
						_pressedKeys.Add(key);
						UpdateMoveDirection();
						return (IntPtr)1; // блокируем
					}
					// ---- NEW: правый Shift -> RIGHT_DOWN ----
					else if (key == VirtualKeys.VK_RSHIFT)
					{
						SendRightDown();
						return (IntPtr)1;
					}
					// ---- NEW: правый Ctrl -> LEFT_DOWN (захват) ----
					else if (key == VirtualKeys.VK_RCONTROL)
					{
						SendLeftDown();
						return (IntPtr)1;
					}
					// ---- NEW: правый Alt -> HOVER (однократно) ----
					else if (key == VirtualKeys.VK_RMENU)
					{
						SendHover();
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_OEM_MINUS)
					{
						SendWheel(-120); // прокрутка вниз
						return (IntPtr)1;
					}
					else if (key == VirtualKeys.VK_OEM_PLUS)
					{
						SendWheel(120); // прокрутка вверх
						return (IntPtr)1;
					}
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
					// Стрелки отпущены
					if (key == VirtualKeys.VK_LEFT || key == VirtualKeys.VK_RIGHT ||
						key == VirtualKeys.VK_UP || key == VirtualKeys.VK_DOWN)
					{
						_pressedKeys.Remove(key);
						UpdateMoveDirection();
						return (IntPtr)1;
					}
					// ---- NEW: правый Shift -> RIGHT_UP ----
					else if (key == VirtualKeys.VK_RSHIFT)
					{
						SendRightUp();
						return (IntPtr)1;
					}
					// ---- NEW: правый Ctrl -> LEFT_UP ----
					else if (key == VirtualKeys.VK_RCONTROL)
					{
						SendLeftUp();
						return (IntPtr)1;
					}
					// Для правого Alt ничего не делаем при отпускании
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
			return CallNextHookEx(_hookId, nCode, wParam, lParam);
		}
		#endregion

		protected override void OnClosed(EventArgs e)
		{
			CompositionTarget.Rendering -= OnRendering;
			if (_hookId != IntPtr.Zero)
			{
				UnhookWindowsHookEx(_hookId);
				_hookId = IntPtr.Zero;
			}

			// Освобождаем UDP-клиент
			_udpClient?.Dispose();

			base.OnClosed(e);

			// Завершаем приложение
			Application.Current?.Shutdown();
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
		[DllImport("user32.dll", SetLastError = true)]
		private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
		private const int GWL_EXSTYLE = -20;
		private const int WS_EX_TRANSPARENT = 0x20;
		private const int WS_EX_LAYERED = 0x80000;

		// Контракт для обмена данными (должен быть одинаковым на клиенте и сервере)
		public record CursorCommand(
			[property: JsonPropertyName("Type")] string Type,
			[property: JsonPropertyName("X")] double X,
			[property: JsonPropertyName("Y")] double Y
		);
	}
}