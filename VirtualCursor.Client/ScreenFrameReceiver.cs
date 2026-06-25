using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualCursor.Common;

namespace VirtualCursor.Client
{
	/// <summary>
	/// Приёмник и декодер кадров экрана
	/// </summary>
	public class ScreenFrameReceiver : IDisposable
	{
		// Текущее состояние приёма
		private ushort _currentFrameNumber;
		private int _screenWidthBlocks;
		private int _screenHeightBlocks;
		private int _totalBlocks;
		private Dictionary<int, byte[]> _pendingBlocks = new();
		private int _expectedBlocks;

		// Хранилище декодированных блоков
		private WriteableBitmap _screenBitmap;
		private IntPtr _backBuffer;
		private int _bufferStride;

		// Параметры отображения
		public event Action<WriteableBitmap> OnFrameReady;
		public event Action<int, int> OnFrameStats; // (fps, blocksReceived)

		// Статистика
		private int _framesReceived = 0;
		private int _blocksReceived = 0;
		private DateTime _lastStatsTime = DateTime.Now;

		public ScreenFrameReceiver(int initialWidth = 800, int initialHeight = 600)
		{
			// Инициализируем битмап с начальными размерами
			CreateBitmap(initialWidth, initialHeight);
		}

		private void CreateBitmap(int width, int height)
		{
			_screenBitmap = new WriteableBitmap(
			width, height, 96, 96,
			PixelFormats.Bgra32, null);

			_backBuffer = _screenBitmap.BackBuffer;
			_bufferStride = _screenBitmap.BackBufferStride;
		}

		/// <summary>
		/// Обработка входящих данных
		/// </summary>
		public void ProcessData(byte[] data)
		{
			if (data == null || data.Length < 2) return;

			byte protocolVersion = data[0];
			byte messageType = data[1];

			if (protocolVersion != ScreenFrameProtocol.PROTOCOL_VERSION)
			{
				Debug.WriteLine($"Неизвестная версия протокола: {protocolVersion}");
				return;
			}

			switch (messageType)
			{
				case ScreenFrameProtocol.MSG_FRAME_HEADER:
					ProcessHeader(data);
					break;

				case ScreenFrameProtocol.MSG_BLOCK_DATA:
					ProcessBlockData(data);
					break;

				case ScreenFrameProtocol.MSG_FRAME_END:
					ProcessFrameEnd(data);
					break;

				case ScreenFrameProtocol.MSG_KEEPALIVE:
					// Игнорируем
					break;

				default:
					Debug.WriteLine($"Неизвестный тип сообщения: {messageType}");
					break;
			}
		}

		private void ProcessHeader(byte[] data)
		{
			try
			{
				var header = BytesToStruct<ScreenFrameProtocol.FrameHeader>(data);
				if (header.FrameNumber != _currentFrameNumber + 1 ||
				header.ScreenWidth != _screenWidthBlocks ||
				header.ScreenHeight != _screenHeightBlocks)
				{
					// Новый кадр или изменилось разрешение
					_currentFrameNumber = header.FrameNumber;
					_screenWidthBlocks = header.ScreenWidth;
					_screenHeightBlocks = header.ScreenHeight;
					_totalBlocks = _screenWidthBlocks * _screenHeightBlocks;
					_expectedBlocks = header.TotalBlocks;
					_pendingBlocks.Clear();

					// Создаём новый битмап если размер изменился
					int pixelWidth = _screenWidthBlocks * ScreenFrameProtocol.BLOCK_SIZE;
					int pixelHeight = _screenHeightBlocks * ScreenFrameProtocol.BLOCK_SIZE;

					if (_screenBitmap == null ||
					_screenBitmap.PixelWidth != pixelWidth ||
					_screenBitmap.PixelHeight != pixelHeight)
					{
						CreateBitmap(pixelWidth, pixelHeight);
					}

					Debug.WriteLine("Новый кадр #{_currentFrameNumber}: {_screenWidthBlocks}x{_screenHeightBlocks} блоков, ожидается {_expectedBlocks} блоков");
				}
				else
				{
					// Пропущенный кадр? Сброс
					_pendingBlocks.Clear();
					_expectedBlocks = header.TotalBlocks;
					Debug.WriteLine("Пропущенный кадр, сброс. Ожидается {_expectedBlocks} блоков");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"ProcessHeader Error: {ex.Message}");
			}
		}

		private void ProcessBlockData(byte[] data)
		{
			try
			{
				var packetHeader = BytesToStruct<ScreenFrameProtocol.BlockPacketHeader>(data);
				int offset = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ScreenFrameProtocol.BlockPacketHeader));

				for (int i = 0; i < packetHeader.BlockCount; i++)
				{
					if (offset + 4 > data.Length) break;

					var blockHeader = BytesToStruct<ScreenFrameProtocol.BlockData>(data, offset);
					offset += System.Runtime.InteropServices.Marshal.SizeOf(typeof(ScreenFrameProtocol.BlockData));

					if (blockHeader.DataSize > 0 && offset + blockHeader.DataSize <= data.Length)
					{
						byte[] blockData = new byte[blockHeader.DataSize];
						Array.Copy(data, offset, blockData, 0, blockHeader.DataSize);
						_pendingBlocks[blockHeader.BlockIndex] = blockData;
						offset += blockHeader.DataSize;
					}
					else
					{
						break;
					}
				}

				// Если собрали все блоки, рендерим
				if (_pendingBlocks.Count >= _expectedBlocks && _expectedBlocks > 0)
				{
					RenderFrame();
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"ProcessBlockData Error: {ex.Message}");
			}
		}

		private void ProcessFrameEnd(byte[] data)
		{
			if (data.Length >= 4)
			{
				ushort frameNumber = (ushort)((data[2] << 8) | data[3]);
				if (frameNumber == _currentFrameNumber)
				{
					// Принудительный рендеринг если есть блоки
					if (_pendingBlocks.Count > 0 && _expectedBlocks > 0)
					{
						RenderFrame();
					}
				}
			}
		}

		private void RenderFrame()
		{
			try
			{
				_screenBitmap.Lock();
				_backBuffer = _screenBitmap.BackBuffer;
				_bufferStride = _screenBitmap.BackBufferStride;

				int blockSize = ScreenFrameProtocol.BLOCK_SIZE;

				foreach (var kvp in _pendingBlocks)
				{
					int blockIdx = kvp.Key;
					byte[] jpegData = kvp.Value;

					// Декодируем JPEG в Bitmap
					using var ms = new MemoryStream(jpegData);
					using var blockBitmap = new Bitmap(ms);

					// Получаем координаты блока
					var (blockX, blockY, blockW, blockH) = ScreenFrameProtocol.GetBlockBounds(
					blockIdx, _screenWidthBlocks * blockSize, _screenHeightBlocks * blockSize);

					// Копируем данные в WriteableBitmap
					var bmpData = blockBitmap.LockBits(
					new Rectangle(0, 0, blockW, blockH),
					ImageLockMode.ReadOnly,
					System.Drawing.Imaging.PixelFormat.Format32bppArgb);

					IntPtr srcPtr = bmpData.Scan0;
					int srcStride = bmpData.Stride;

					for (int y = 0; y < blockH; y++)
					{
						IntPtr dstPtr = _backBuffer + (blockY + y) * _bufferStride + blockX * 4;
						IntPtr srcRowPtr = srcPtr + y * srcStride;
						byte[] buffer = new byte[blockW * 4];
						Marshal.Copy(srcRowPtr, buffer, 0, blockW * 4);
						Marshal.Copy(buffer, 0, dstPtr, blockW * 4);
					}

					blockBitmap.UnlockBits(bmpData);
				}

				_screenBitmap.AddDirtyRect(new Int32Rect(0, 0, _screenBitmap.PixelWidth, _screenBitmap.PixelHeight));
				_screenBitmap.Unlock();

				// Статистика
				_framesReceived++;
				_blocksReceived += _pendingBlocks.Count;

				if ((DateTime.Now - _lastStatsTime).TotalSeconds >= 1)
				{
					OnFrameStats?.Invoke(_framesReceived, _blocksReceived);
					_framesReceived = 0;
					_blocksReceived = 0;
					_lastStatsTime = DateTime.Now;
				}

				// Вызываем событие о готовности кадра
				OnFrameReady?.Invoke(_screenBitmap);

				// Очищаем для следующего кадра
				_pendingBlocks.Clear();
				_expectedBlocks = 0;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"RenderFrame Error: {ex.Message}");
				_screenBitmap.Unlock();
			}
		}

		private T BytesToStruct<T>(byte[] bytes, int offset = 0) where T : struct
		{
			int size = System.Runtime.InteropServices.Marshal.SizeOf<T>();
			IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);

			try
			{
				System.Runtime.InteropServices.Marshal.Copy(bytes, offset, ptr, size);
				return System.Runtime.InteropServices.Marshal.PtrToStructure<T>(ptr);
			}
			finally
			{
				System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
			}
		}

		public WriteableBitmap GetCurrentBitmap()
		{
			return _screenBitmap;
		}

		public void Dispose()
		{
			// Очистка ресурсов
		}
	}
}