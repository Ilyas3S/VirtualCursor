using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VirtualCursor.Common
{
	public static class StunClient
	{
		private static readonly byte[] StunMagicCookie = { 0x21, 0x12, 0xA4, 0x42 };
		private const ushort StunBindingRequest = 0x0001;
		private const ushort StunAttributeMappedAddress = 0x0001;
		private const ushort StunAttributeXorMappedAddress = 0x0020;

		public static async Task<IPEndPoint> GetPublicEndPointAsync(string stunServer = "stun.sipnet.ru", int stunPort = 3478)
		{
			using var udp = new UdpClient();
			udp.Connect(stunServer, stunPort);

			// Формируем STUN Binding Request
			byte[] request = new byte[20];
			// тип: Binding Request
			request[0] = (byte)(StunBindingRequest >> 8);
			request[1] = (byte)(StunBindingRequest & 0xFF);
			// длина: 0
			// Magic cookie
			Array.Copy(StunMagicCookie, 0, request, 4, 4);
			// Transaction ID (12 байт случайных)
			new Random().NextBytes(request.AsSpan(8, 12));

			await udp.SendAsync(request, request.Length);
			var result = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

			// Парсим ответ (упрощённо: ищем XOR-MAPPED-ADDRESS)
			byte[] response = result.Buffer;
			if (response.Length < 20) return null;

			int offset = 20;
			while (offset + 4 <= response.Length)
			{
				ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
				ushort attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
				if (offset + 4 + attrLen > response.Length) break;

				if (attrType == StunAttributeXorMappedAddress)
				{
					// Формат: 1 байт (0), семейство (1 байт), порт XOR, адрес XOR
					byte family = response[offset + 5];
					if (family == 0x01) // IPv4
					{
						ushort xorPort = (ushort)((response[offset + 6] << 8) | response[offset + 7]);
						int xorPortResult = xorPort ^ (StunMagicCookie[0] << 8 | StunMagicCookie[1]);

						byte[] xorIp = new byte[4];
						for (int i = 0; i < 4; i++)
							xorIp[i] = (byte)(response[offset + 8 + i] ^ StunMagicCookie[i]);

						IPAddress ip = new IPAddress(xorIp);
						return new IPEndPoint(ip, xorPortResult);
					}
				}
				offset += 4 + attrLen;
			}
			return null;
		}
	}
}