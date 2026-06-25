using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using VirtualCursor.Common;

namespace VirtualCursor.Server
{
	/// <summary>
	/// Сервис захвата и кодирования экрана с детекцией изменений
	/// </summary>
	public class ScreenCaptureService : IDisposable
	{
		// Настройки захвата
		private int _captureWidth;
		private int _captureHeight;
		private int _scaleFactor; // 1 = оригинал, 2 = 50%, 4 = 25%
		private int _blocksW;
		private int _blocksH;
		private int _totalBlocks;

		// Хранилище предыдущего кадра
		private byte[][,] _previousBlocks;

		// Счётчик кадров
		private ushort _frameCounter = 0;

		// Настройки качества JPEG
		private byte _jpegQuality = 70;

		// Порог изменений (0-255, чем меньше, тем чувствительнее)
		private const int CHANGE_THRESHOLD = 30;

		// Кэш для преобразования цветового пространства
		private byte[] _pixelBuffer;
		private GCHandle _pixelBufferHandle;

		// Кэш для JPEG энкодера
		private ImageCodecInfo _jpegCodec;
		private EncoderParameters _encoderParams;

		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int nIndex);
		private const int SM_CXSCREEN = 0;
		private const int SM_CYSCREEN = 1;

		public ScreenCaptureService(int scaleFactor = 2, byte jpegQuality = 70)
		{
			_scaleFactor = Math.Max(1, scaleFactor);
			_jpegQuality = Math.Clamp(jpegQuality, (byte)10, (byte)100);

			// Настройка экрана
			UpdateScreenDimensions();

			// Инициализация буферов
			int bufferSize = _captureWidth * _captureHeight * 4; // RGBA
			_pixelBuffer = new byte[bufferSize];
			_pixelBufferHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);

			// Настройка JPEG энкодера
			var codecs = ImageCodecInfo.GetImageEncoders();
			foreach (var codec in codecs)
			{
				if (codec.MimeType == "image/jpeg")
				{
					_jpegCodec = codec;
					break;
				}
			}

			_encoderParams = new EncoderParameters(1);
			_encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_jpegQuality);

			// Инициализация предыдущих блоков
			_previousBlocks = new byte[_totalBlocks][,];
			for (int i = 0; i < _totalBlocks; i++)
			{
				_previousBlocks[i] = new byte[ScreenFrameProtocol.BLOCK_SIZE, ScreenFrameProtocol.BLOCK_SIZE];
			}

			Debug.WriteLine($"ScreenCaptureService: {_captureWidth}x{_captureHeight}, масштаб 1:{_scaleFactor}, блоков: {_blocksW}x{_blocksH} = {_totalBlocks}");
		}

		private void UpdateScreenDimensions()
		{
			int originalWidth = GetSystemMetrics(SM_CXSCREEN);
			int originalHeight = GetSystemMetrics(SM_CYSCREEN);

			_captureWidth = originalWidth / _scaleFactor;
			_captureHeight = originalHeight / _scaleFactor;

			(_blocksW, _blocksH) = ScreenFrameProtocol.GetBlockDimensions(_captureWidth, _captureHeight);
			_totalBlocks = _blocksW * _blocksH;
		}

		/// <summary>
		/// Захват экрана и кодирование изменений
		/// </summary>
		public ScreenFrameData CaptureFrame()
		{
			try
			{
				// 1. Захват экрана в Bitmap
				using var bitmap = CaptureScreen();

				// 2. Масштабирование если нужно
				using var scaledBitmap = (_scaleFactor > 1) ? ScaleBitmap(bitmap) : bitmap;

				// 3. Получение данных пикселей
				var bitmapData = scaledBitmap.LockBits(
				new Rectangle(0, 0, scaledBitmap.Width, scaledBitmap.Height),
				ImageLockMode.ReadOnly,
				System.Drawing.Imaging.PixelFormat.Format32bppArgb);

				int stride = bitmapData.Stride;
				int height = scaledBitmap.Height;
				int width = scaledBitmap.Width;

				// 4. Копирование данных в буфер
				IntPtr scan0 = bitmapData.Scan0;
				unsafe
				{
					byte* src = (byte*)scan0.ToPointer();
					fixed (byte* dst = _pixelBuffer)
					{
						for (int y = 0; y < height; y++)
						{
							int srcRow = y * stride;
							int dstRow = y * width * 4;
							Buffer.MemoryCopy(src + srcRow, dst + dstRow, (width * 4), (width * 4));
						}
					}
				}

				scaledBitmap.UnlockBits(bitmapData);

				// 5. Сравнение блоков с предыдущим кадром и кодирование изменённых
				var frameData = new ScreenFrameData
				{
					FrameNumber = ++_frameCounter,
					ScreenWidth = _captureWidth,
					ScreenHeight = _captureHeight,
					ScaleFactor = _scaleFactor,
					Quality = _jpegQuality,
					Timestamp = (uint)Environment.TickCount
				};

				// Создаём битмап для кодирования изменённых блоков
				using var blockBitmap = new Bitmap(ScreenFrameProtocol.BLOCK_SIZE, ScreenFrameProtocol.BLOCK_SIZE,
				System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				using var graphics = Graphics.FromImage(blockBitmap);

				for (int blockIdx = 0; blockIdx < _totalBlocks; blockIdx++)
				{
					var (blockX, blockY, blockW, blockH) = ScreenFrameProtocol.GetBlockBounds(
					blockIdx, _captureWidth, _captureHeight);

					// Копируем блок из буфера для сравнения
					byte[,] currentBlock = new byte[ScreenFrameProtocol.BLOCK_SIZE, ScreenFrameProtocol.BLOCK_SIZE];
					for (int y = 0; y < blockH; y++)
					{
						int srcRow = (blockY + y) * _captureWidth * 4;
						for (int x = 0; x < blockW; x++)
						{
							int srcIdx = srcRow + (blockX + x) * 4;
							// Берём яркость (среднее RGB)
							byte r = _pixelBuffer[srcIdx + 2];
							byte g = _pixelBuffer[srcIdx + 1];
							byte b = _pixelBuffer[srcIdx];
							byte gray = (byte)((r * 30 + g * 59 + b * 11) / 100);
							currentBlock[y, x] = gray;
						}
					}

					// Сравниваем с предыдущим
					bool hasChanged = IsBlockChanged(currentBlock, _previousBlocks[blockIdx], blockW, blockH);

					if (hasChanged)
					{
						// Сохраняем для следующего сравнения
						for (int y = 0; y < blockH; y++)
						{
							for (int x = 0; x < blockW; x++)
							{
								_previousBlocks[blockIdx][y, x] = currentBlock[y, x];
							}
						}

						// Кодируем блок в JPEG
						byte[] jpegData = EncodeBlockToJpeg(_pixelBuffer, blockX, blockY, blockW, blockH);
						if (jpegData != null && jpegData.Length > 0)
						{
							frameData.AddBlock(blockIdx, jpegData);
						}
					}
				}

				frameData.HasChanges = frameData.BlockCount > 0;
				return frameData;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"ScreenCapture Error: {ex.Message}");
				return new ScreenFrameData { HasChanges = false };
			}
		}

		private Bitmap CaptureScreen()
		{
			int screenWidth = GetSystemMetrics(SM_CXSCREEN);
			int screenHeight = GetSystemMetrics(SM_CYSCREEN);
			var bitmap = new Bitmap(screenWidth, screenHeight);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight));
			return bitmap;
		}

		private Bitmap ScaleBitmap(Bitmap original)
		{
			int newWidth = original.Width / _scaleFactor;
			int newHeight = original.Height / _scaleFactor;

			var scaled = new Bitmap(newWidth, newHeight);
			using var graphics = Graphics.FromImage(scaled);
			graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
			graphics.DrawImage(original, 0, 0, newWidth, newHeight);
			return scaled;
		}

		private bool IsBlockChanged(byte[,] current, byte[,] previous, int blockW, int blockH)
		{
			int changedPixels = 0;
			int totalPixels = blockW * blockH;
			int thresholdPixels = (int)(totalPixels * 0.02); // 2% пикселей изменились

			for (int y = 0; y < blockH; y++)
			{
				for (int x = 0; x < blockW; x++)
				{
					if (Math.Abs(current[y, x] - previous[y, x]) > CHANGE_THRESHOLD)
					{
						changedPixels++;
						if (changedPixels > thresholdPixels)
							return true;
					}
				}
			}
			return changedPixels > thresholdPixels;
		}

		private byte[] EncodeBlockToJpeg(byte[] pixelData, int blockX, int blockY, int blockW, int blockH)
		{
			try
			{
				using var blockBitmap = new Bitmap(ScreenFrameProtocol.BLOCK_SIZE, ScreenFrameProtocol.BLOCK_SIZE,
				System.Drawing.Imaging.PixelFormat.Format32bppArgb);

				var bitmapData = blockBitmap.LockBits(
				new Rectangle(0, 0, ScreenFrameProtocol.BLOCK_SIZE, ScreenFrameProtocol.BLOCK_SIZE),
				ImageLockMode.WriteOnly,
				System.Drawing.Imaging.PixelFormat.Format32bppArgb);

				int stride = bitmapData.Stride;
				IntPtr scan0 = bitmapData.Scan0;

				unsafe
				{
					byte* dst = (byte*)scan0.ToPointer();
					for (int y = 0; y < blockH; y++)
					{
						int srcRow = (blockY + y) * _captureWidth * 4;
						int dstRow = y * stride;
						for (int x = 0; x < blockW; x++)
						{
							int srcIdx = srcRow + (blockX + x) * 4;
							int dstIdx = dstRow + x * 4;

							// Копируем RGBA
							dst[dstIdx] = pixelData[srcIdx];         // B
							dst[dstIdx + 1] = pixelData[srcIdx + 1]; // G
							dst[dstIdx + 2] = pixelData[srcIdx + 2]; // R
							dst[dstIdx + 3] = pixelData[srcIdx + 3]; // A
						}
					}
				}

				blockBitmap.UnlockBits(bitmapData);

				// Сохраняем в JPEG
				using var ms = new MemoryStream();
				blockBitmap.Save(ms, _jpegCodec, _encoderParams);
				return ms.ToArray();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"EncodeBlock Error: {ex.Message}");
				return null;
			}
		}

		public void Dispose()
		{
			if (_pixelBufferHandle.IsAllocated)
				_pixelBufferHandle.Free();

			_encoderParams?.Dispose();
		}

		/// <summary>
		/// Изменение настроек качества
		/// </summary>
		public void SetQuality(byte quality)
		{
			_jpegQuality = Math.Clamp(quality, (byte)10, (byte)100);
			_encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_jpegQuality);
		}

		/// <summary>
		/// Изменение масштаба
		/// </summary>
		public void SetScaleFactor(int scaleFactor)
		{
			if (scaleFactor != _scaleFactor)
			{
				_scaleFactor = Math.Max(1, scaleFactor);
				UpdateScreenDimensions();

				// Пересоздаём буфер предыдущих блоков
				_previousBlocks = new byte[_totalBlocks][,];
				for (int i = 0; i < _totalBlocks; i++)
				{
					_previousBlocks[i] = new byte[ScreenFrameProtocol.BLOCK_SIZE, ScreenFrameProtocol.BLOCK_SIZE];
				}

				// Пересоздаём буфер пикселей
				int bufferSize = _captureWidth * _captureHeight * 4;
				_pixelBuffer = new byte[bufferSize];

				Debug.WriteLine($"ScreenCaptureService: масштаб изменён на 1:{_scaleFactor}");
			}
		}
	}
}