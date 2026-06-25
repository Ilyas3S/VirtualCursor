using System;
using System.Collections.Generic;

namespace VirtualCursor.Common
{
/// <summary>
/// Данные кадра экрана для передачи
/// </summary>
public class ScreenFrameData
{
public ushort FrameNumber { get; set; }
public int ScreenWidth { get; set; }
public int ScreenHeight { get; set; }
public int ScaleFactor { get; set; } // Коэффициент уменьшения
public byte Quality { get; set; } = 70;
public Dictionary<int, byte[]> ChangedBlocks { get; set; } = new();
public List<int> BlockIndices { get; set; } = new(); // Для быстрого доступа
public uint Timestamp { get; set; }
public bool HasChanges { get; set; }

public ScreenFrameData()
{
ChangedBlocks = new Dictionary<int, byte[]>();
BlockIndices = new List<int>();
}

public void AddBlock(int index, byte[] jpegData)
{
ChangedBlocks[index] = jpegData;
BlockIndices.Add(index);
}

public void Clear()
{
ChangedBlocks.Clear();
BlockIndices.Clear();
HasChanges = false;
}

public int BlockCount => ChangedBlocks.Count;
}
}