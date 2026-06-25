using System.Diagnostics;
using VirtualCursor.Common;

namespace VirtualCursor.Server
{
	/// <summary>
	/// Отправитель кадров экрана через UDP
	/// </summary>
	public class ScreenFrameSender : IDisposable
	{
		private HolePunchUdpClient _udpClient;
		private ScreenCaptureService _captureService;
		private CancellationTokenSource _cts;
		private Task _sendLoopTask;

		private int _fps = 20;
		private bool _isRunning = false;
		private int _frameCounter = 0;
		private Stopwatch _performanceWatch = new Stopwatch();

		// Флаги для управления
		public event Action<int, int> OnFrameStats; // (fps, blocksSent)

		public ScreenFrameSender(HolePunchUdpClient udpClient, ScreenCaptureService captureService)
		{
			_udpClient = udpClient;
			_captureService = captureService;
			_cts = new CancellationTokenSource();
		}

		public void Start(int fps = 20)
		{
			if (_isRunning) return;

			_fps = Math.Clamp(fps, 5, 60);
			_isRunning = true;
			_cts = new CancellationTokenSource();
			_sendLoopTask = Task.Run(SendLoop);
			_performanceWatch.Start();

			Debug.WriteLine($"ScreenFrameSender: запущен с FPS={_fps}");
		}

		public void Stop()
		{
			if (!_isRunning) return;

			_isRunning = false;
			_cts?.Cancel();
			_sendLoopTask?.Wait(1000);
			_performanceWatch.Stop();

			Debug.WriteLine("ScreenFrameSender: остановлен");
		}

		private async Task SendLoop()
		{
			int frameInterval = 1000 / _fps;
			int lastFrameTime = 0;
			int framesSent = 0;
			int blocksSent = 0;
			DateTime lastStatsTime = DateTime.Now;

			while (!_cts.IsCancellationRequested && _isRunning)
			{
				int currentTime = Environment.TickCount;
				int elapsed = currentTime - lastFrameTime;

				if (elapsed >= frameInterval)
				{
					try
					{
						// Захватываем кадр
						var frameData = _captureService.CaptureFrame();

						// Отправляем только если есть изменения
						if (frameData.HasChanges && frameData.BlockCount > 0)
						{
							await SendFrameAsync(frameData);
							framesSent++;
							blocksSent += frameData.BlockCount;

							// Увеличиваем счётчик кадров в протоколе
							_frameCounter = (ushort)((_frameCounter + 1) % ushort.MaxValue);
						}

						// Статистика
						if ((DateTime.Now - lastStatsTime).TotalSeconds >= 1)
						{
							OnFrameStats?.Invoke(framesSent, blocksSent);
							framesSent = 0;
							blocksSent = 0;
							lastStatsTime = DateTime.Now;
						}

						lastFrameTime = currentTime;
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"SendLoop Error: {ex.Message}");
					}
				}

				await Task.Delay(1);
			}
		}

		private async Task SendFrameAsync(ScreenFrameData frameData)
		{
			try
			{
				var (blocksW, blocksH) = ScreenFrameProtocol.GetBlockDimensions(
				frameData.ScreenWidth, frameData.ScreenHeight);

				// Отправляем заголовок кадра
				var header = new ScreenFrameProtocol.FrameHeader
				{
					ProtocolVersion = ScreenFrameProtocol.PROTOCOL_VERSION,
					MessageType = ScreenFrameProtocol.MSG_FRAME_HEADER,
					FrameNumber = frameData.FrameNumber,
					ScreenWidth = (ushort)blocksW,
					ScreenHeight = (ushort)blocksH,
					TotalBlocks = (ushort)frameData.BlockCount,
					Timestamp = frameData.Timestamp,
					Quality = frameData.Quality,
					Flags = frameData.HasChanges ? (byte)1 : (byte)0
				};

				byte[] headerBytes = StructToBytes(header);
				await _udpClient.SendToRemoteAsync(headerBytes);

				// Отправляем блоки пачками
				var blockList = new List<int>(frameData.ChangedBlocks.Keys);
				int totalBlocks = blockList.Count;
				int sentBlocks = 0;

				while (sentBlocks < totalBlocks)
				{
					int batchSize = Math.Min(
					ScreenFrameProtocol.MAX_BLOCKS_PER_PACKET,
					totalBlocks - sentBlocks);

					// Формируем пакет с блоками
					using var ms = new System.IO.MemoryStream();
					var packetHeader = new ScreenFrameProtocol.BlockPacketHeader
					{
						ProtocolVersion = ScreenFrameProtocol.PROTOCOL_VERSION,
						MessageType = ScreenFrameProtocol.MSG_BLOCK_DATA,
						FrameNumber = frameData.FrameNumber,
						BlockCount = (ushort)batchSize
					};

					byte[] packetHeaderBytes = StructToBytes(packetHeader);
					ms.Write(packetHeaderBytes, 0, packetHeaderBytes.Length);

					for (int i = 0; i < batchSize; i++)
					{
						int blockIdx = blockList[sentBlocks + i];
						byte[] blockData = frameData.ChangedBlocks[blockIdx];

						if (blockData != null && blockData.Length > 0)
						{
							var blockHeader = new ScreenFrameProtocol.BlockData
							{
								BlockIndex = (ushort)blockIdx,
								DataSize = (ushort)Math.Min(blockData.Length, ushort.MaxValue)
							};

							byte[] blockHeaderBytes = StructToBytes(blockHeader);
							ms.Write(blockHeaderBytes, 0, blockHeaderBytes.Length);
							ms.Write(blockData, 0, blockData.Length);
						}
					}

					// Отправляем пакет
					byte[] packetData = ms.ToArray();
					await _udpClient.SendToRemoteAsync(packetData);
					sentBlocks += batchSize;
				}

				// Отправляем маркер конца кадра
				byte[] endMarker = new byte[] {
ScreenFrameProtocol.PROTOCOL_VERSION,
ScreenFrameProtocol.MSG_FRAME_END,
(byte)(frameData.FrameNumber >> 8),
(byte)(frameData.FrameNumber & 0xFF)
};
				await _udpClient.SendToRemoteAsync(endMarker);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"SendFrame Error: {ex.Message}");
			}
		}

		private byte[] StructToBytes<T>(T structure) where T : struct
		{
			int size = System.Runtime.InteropServices.Marshal.SizeOf(structure);
			byte[] bytes = new byte[size];
			IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);

			try
			{
				System.Runtime.InteropServices.Marshal.StructureToPtr(structure, ptr, false);
				System.Runtime.InteropServices.Marshal.Copy(ptr, bytes, 0, size);
				return bytes;
			}
			finally
			{
				System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
			}
		}

		public void Dispose()
		{
			Stop();
			_cts?.Dispose();
			_captureService?.Dispose();
		}
	}
}