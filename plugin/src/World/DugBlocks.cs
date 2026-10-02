using System.Collections.Generic;
using System.Threading;

namespace RepoCraft.World
{
	/// <summary>
	/// Blocks Minecraft has dug out of REPO's level (Minecraft keeps the set; it arrives per section as
	/// kRenDug). In a dug block REPO's own geometry is gone: not drawn, not in the collision Minecraft
	/// sees, not under REPO's enemies. Thread-safe.
	/// </summary>
	internal static class DugBlocks
	{
		private static readonly object Lock = new object();
		// section key -> 512-byte bitset (bit x + 16z + 256y)
		private static readonly Dictionary<long, byte[]> Sections = new Dictionary<long, byte[]>();
		private static readonly List<(int x, int y, int z)> Changed = new List<(int, int, int)>();
		private static uint worldId;
		private static int total;
		private static long generation;

		public static bool Any() => Volatile.Read(ref total) > 0;

		public static long Generation => Interlocked.Read(ref generation);

		public static void SetWorld(uint id)
		{
			lock (Lock)
			{
				if (id != worldId)
				{
					worldId = id;
					ClearLocked();
				}
			}
		}

		public static void Clear()
		{
			lock (Lock)
			{
				ClearLocked();
			}
		}

		private static void ClearLocked()
		{
			foreach (var kv in Sections)
			{
				AddAll(kv.Key, kv.Value);
			}
			Sections.Clear();
			Volatile.Write(ref total, 0);
			Interlocked.Increment(ref generation);
		}

		private static void AddAll(long key, byte[] bits)
		{
			Unpack(key, out int sx, out int sy, out int sz);
			for (int i = 0; i < 4096; i++)
			{
				if ((bits[i >> 3] & (1 << (i & 7))) != 0)
				{
					Changed.Add((sx * 16 + (i & 15), sy * 16 + (i >> 8), sz * 16 + ((i >> 4) & 15)));
				}
			}
		}

		/// <summary>Render message: RenDug {sx, sy, sz, count, worldId, pad} + 512-byte bitset (none when count is 0).</summary>
		public static unsafe void OnDug(byte* data, uint bytes)
		{
			if (bytes < 24)
			{
				return;
			}
			int sx = *(int*)data, sy = *(int*)(data + 4), sz = *(int*)(data + 8);
			uint count = *(uint*)(data + 12);
			uint world = *(uint*)(data + 16);
			long key = SectionKey(sx, sy, sz);
			lock (Lock)
			{
				if (world != worldId)
				{
					return; // dug in another level
				}
				Sections.TryGetValue(key, out byte[] old);
				byte[] now = null;
				if (count > 0 && bytes >= 24 + 512)
				{
					now = new byte[512];
					for (int i = 0; i < 512; i++)
					{
						now[i] = data[24 + i];
					}
				}
				for (int i = 0; i < 512; i++)
				{
					int diff = (old?[i] ?? 0) ^ (now?[i] ?? 0);
					if (diff == 0)
					{
						continue;
					}
					for (int bit = 0; bit < 8; bit++)
					{
						if ((diff & (1 << bit)) != 0)
						{
							int idx = i * 8 + bit;
							Changed.Add((sx * 16 + (idx & 15), sy * 16 + (idx >> 8), sz * 16 + ((idx >> 4) & 15)));
						}
					}
				}
				int delta = -Popcount(old) + Popcount(now);
				if (now != null)
				{
					Sections[key] = now;
				}
				else
				{
					Sections.Remove(key);
				}
				Volatile.Write(ref total, total + delta);
				Interlocked.Increment(ref generation);
			}
		}

		private static int Popcount(byte[] bits)
		{
			if (bits == null)
			{
				return 0;
			}
			int n = 0;
			foreach (byte b in bits)
			{
				int v = b;
				while (v != 0)
				{
					n += v & 1;
					v >>= 1;
				}
			}
			return n;
		}

		public static bool IsDug(int x, int y, int z)
		{
			if (!Any())
			{
				return false;
			}
			lock (Lock)
			{
				if (!Sections.TryGetValue(SectionKey(x >> 4, y >> 4, z >> 4), out byte[] bits))
				{
					return false;
				}
				int i = (x & 15) + 16 * (z & 15) + 256 * (y & 15);
				return (bits[i >> 3] & (1 << (i & 7))) != 0;
			}
		}

		/// <summary>Dug blocks whose cube overlaps [lo, hi] (Minecraft coords).</summary>
		public static void Collect(float loX, float loY, float loZ, float hiX, float hiY, float hiZ, List<(int x, int y, int z)> output)
		{
			if (!Any())
			{
				return;
			}
			int x0 = Floor(loX), y0 = Floor(loY), z0 = Floor(loZ);
			int x1 = Floor(hiX), y1 = Floor(hiY), z1 = Floor(hiZ);
			lock (Lock)
			{
				for (int sy = y0 >> 4; sy <= y1 >> 4; sy++)
				{
					for (int sz = z0 >> 4; sz <= z1 >> 4; sz++)
					{
						for (int sx = x0 >> 4; sx <= x1 >> 4; sx++)
						{
							if (!Sections.TryGetValue(SectionKey(sx, sy, sz), out byte[] bits))
							{
								continue;
							}
							for (int i = 0; i < 4096; i++)
							{
								if ((bits[i >> 3] & (1 << (i & 7))) == 0)
								{
									continue;
								}
								int x = sx * 16 + (i & 15), y = sy * 16 + (i >> 8), z = sz * 16 + ((i >> 4) & 15);
								if (x >= x0 && x <= x1 && y >= y0 && y <= y1 && z >= z0 && z <= z1)
								{
									output.Add((x, y, z));
								}
							}
						}
					}
				}
			}
		}

		/// <summary>Blocks dug (or no longer dug) since the last call. Main thread, once a frame.</summary>
		public static void TakeChanged(List<(int x, int y, int z)> output)
		{
			lock (Lock)
			{
				output.AddRange(Changed);
				Changed.Clear();
			}
		}

		private static int Floor(float v) => (int)System.Math.Floor(v);

		private static long SectionKey(int x, int y, int z) => Clip.Key(x, y, z);

		private static void Unpack(long key, out int x, out int y, out int z)
		{
			x = Sign21((int)((key >> 42) & 0x1FFFFF));
			y = Sign21((int)((key >> 21) & 0x1FFFFF));
			z = Sign21((int)(key & 0x1FFFFF));
		}

		private static int Sign21(int v) => v >= 0x100000 ? v - 0x200000 : v;
	}
}
