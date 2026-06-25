using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VirtualCursor.Common
{
	/// <summary>
	/// Протокол передачи кадров экрана через UDP
	/// </summary>
	public static class ScreenFrameProtocol
	{
		// Константы протокола
		public const int BLOCK_SIZE = 32;          // Размер блока в пикселях
		public const int MAX_BLOCKS_PER_PACKET = 20; // Максимум блоков в одном UDP пакете
		public const int MAX_PACKET_SIZE = 64000;   // Максимальный размер UDP пакета (с запасом)
		public const byte PROTOCOL_VERSION = 1;

		// Типы сообщений
		public const byte MSG_FRAME_HEADER = 0x01;
		public const byte MSG_BLOCK_DATA = 0x02;
		public const byte MSG_FRAME_END = 0x03;
		public const byte MSG_KEEPALIVE = 0x04;

		/// <summary>
		/// Заголовок кадра
		/// </summary>
		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		public struct FrameHeader
		{
			public byte ProtocolVersion;    // Версия протокола
			public byte MessageType;        // MSG_FRAME_HEADER
			public ushort FrameNumber;      // Номер кадра (для синхронизации)
			public ushort ScreenWidth;      // Ширина экрана в блоках
			public ushort ScreenHeight;     // Высота экрана в блоках
			public ushort TotalBlocks;      // Общее количество блоков в кадре
			public uint Timestamp;          // Временная метка (мс)
			public byte Quality;            // Качество JPEG (1-100)
			public byte Flags;              // Флаги (бит 0: есть изменения)
		}

		/// <summary>
		/// Данные одного блока
		/// </summary>
		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		public struct BlockData
		{
			public ushort BlockIndex;       // Индекс блока в сетке
			public ushort DataSize;         // Размер сжатых данных (в байтах)
											// Далее следует сжатый JPEG
		}

		/// <summary>
		/// Заголовок пакета с блоками
		/// </summary>
		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		public struct BlockPacketHeader
		{
			public byte ProtocolVersion;
			public byte MessageType;        // MSG_BLOCK_DATA
			public ushort FrameNumber;
			public ushort BlockCount;
		}

		/// <summary>
		/// Преобразование координат пикселя в индекс блока
		/// </summary>
		public static int GetBlockIndex(int x, int y, int screenWidth)
		{
			int blockX = x / BLOCK_SIZE;
			int blockY = y / BLOCK_SIZE;
			int blocksPerRow = (screenWidth + BLOCK_SIZE - 1) / BLOCK_SIZE;
			return blockY * blocksPerRow + blockX;
		}

		/// <summary>
		/// Получение количества блоков по ширине и высоте
		/// </summary>
		public static (int blocksW, int blocksH) GetBlockDimensions(int width, int height)
		{
			int blocksW = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
			int blocksH = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
			return (blocksW, blocksH);
		}

		/// <summary>
		/// Получение границ блока по его индексу
		/// </summary>
		public static (int x, int y, int w, int h) GetBlockBounds(int index, int screenWidth, int screenHeight)
		{
			(int blocksW, int blocksH) = GetBlockDimensions(screenWidth, screenHeight);
			int blockX = index % blocksW;
			int blockY = index / blocksW;

			int x = blockX * BLOCK_SIZE;
			int y = blockY * BLOCK_SIZE;
			int w = Math.Min(BLOCK_SIZE, screenWidth - x);
			int h = Math.Min(BLOCK_SIZE, screenHeight - y);

			return (x, y, w, h);
		}
	}
}