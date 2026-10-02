using System.Collections.Generic;
using UnityEngine;

namespace RepoCraft.World
{
	/// <summary>
	/// Minecraft's solid blocks are solid in REPO too: per section, a static collider per run of
	/// solid blocks (greedy-merged boxes) on REPO's level layer, so enemies, loot, carts and the
	/// other players bump into and stand on what the Minecraft player builds. A NavMeshObstacle on
	/// each box carves REPO's navigation mesh so enemies path around walls.
	/// </summary>
	internal sealed unsafe class BlockSolids
	{
		public static readonly BlockSolids Instance = new BlockSolids();

		private readonly Dictionary<long, (GameObject go, byte[] bits)> sections = new Dictionary<long, (GameObject, byte[])>();
		private GameObject root;
		private int generation;

		/// <summary>Layer the colliders go on (REPO's static level geometry layer, set at startup).</summary>
		public static int Layer = 0;

		public int Generation => generation;

		private Transform Root
		{
			get
			{
				if (root == null)
				{
					root = new GameObject("RepoCraft solids");
					root.AddComponent<RepoCraftOwned>();
					Object.DontDestroyOnLoad(root);
				}
				return root.transform;
			}
		}

		/// <summary>Render message: RenSolids {sx, sy, sz, count} + 512-byte bitset (bit x + 16z + 256y).</summary>
		public void OnSolids(byte* data, uint bytes)
		{
			int sx = *(int*)data, sy = *(int*)(data + 4), sz = *(int*)(data + 8);
			uint count = *(uint*)(data + 12);
			long key = Clip.Key(sx, sy, sz);
			if (sections.TryGetValue(key, out var old))
			{
				if (old.go != null)
				{
					Object.Destroy(old.go);
				}
				sections.Remove(key);
			}
			generation++;
			if (count == 0 || bytes < 16 + 512)
			{
				return;
			}
			var bits = new byte[512];
			for (int i = 0; i < 512; i++)
			{
				bits[i] = data[16 + i];
			}
			var go = new GameObject($"solids {sx} {sy} {sz}") { layer = Layer };
			go.transform.SetParent(Root, false);
			go.transform.position = Coords.ToUnity(sx * 16.0, sy * 16.0, sz * 16.0);
			foreach (var box in Merge(bits))
			{
				// Box in section block coords [x0, x1) etc. -> Unity local (Z mirrored).
				var child = new GameObject("box") { layer = Layer };
				child.transform.SetParent(go.transform, false);
				float k = Coords.K;
				var size = new Vector3((box.x1 - box.x0) / k, (box.y1 - box.y0) / k, (box.z1 - box.z0) / k);
				var centre = new Vector3((box.x0 + box.x1) * 0.5f / k, (box.y0 + box.y1) * 0.5f / k, -(box.z0 + box.z1) * 0.5f / k);
				child.transform.localPosition = centre;
				var col = child.AddComponent<BoxCollider>();
				col.size = size;
				var obstacle = child.AddComponent<UnityEngine.AI.NavMeshObstacle>();
				obstacle.shape = UnityEngine.AI.NavMeshObstacleShape.Box;
				obstacle.size = size;
				obstacle.carving = true;
				obstacle.carveOnlyStationary = true;
			}
			sections[key] = (go, bits);
		}

		public void Clear()
		{
			foreach (var s in sections.Values)
			{
				if (s.go != null)
				{
					Object.Destroy(s.go);
				}
			}
			sections.Clear();
			generation++;
		}

		public bool SolidAt(int x, int y, int z)
		{
			if (!sections.TryGetValue(Clip.Key(x >> 4, y >> 4, z >> 4), out var s))
			{
				return false;
			}
			int i = (x & 15) + 16 * (z & 15) + 256 * (y & 15);
			return (s.bits[i >> 3] & (1 << (i & 7))) != 0;
		}

		private static bool Bit(byte[] bits, int x, int y, int z)
		{
			int i = x + 16 * z + 256 * y;
			return (bits[i >> 3] & (1 << (i & 7))) != 0;
		}

		/// <summary>Greedy boxes over a 16^3 bitset: rows along x, then z, then y.</summary>
		private static List<(int x0, int y0, int z0, int x1, int y1, int z1)> Merge(byte[] bits)
		{
			var left = new bool[4096];
			for (int i = 0; i < 4096; i++)
			{
				left[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
			}
			bool L(int x, int y, int z) => x < 16 && y < 16 && z < 16 && left[x + 16 * z + 256 * y];
			var boxes = new List<(int, int, int, int, int, int)>();
			for (int y = 0; y < 16; y++)
			{
				for (int z = 0; z < 16; z++)
				{
					for (int x = 0; x < 16; x++)
					{
						if (!L(x, y, z))
						{
							continue;
						}
						int x1 = x;
						while (L(x1 + 1, y, z))
						{
							x1++;
						}
						int z1 = z;
						for (bool grow = true; grow;)
						{
							for (int xx = x; xx <= x1 && grow; xx++)
							{
								grow = L(xx, y, z1 + 1);
							}
							z1 += grow ? 1 : 0;
						}
						int y1 = y;
						for (bool grow = true; grow;)
						{
							for (int zz = z; zz <= z1 && grow; zz++)
							{
								for (int xx = x; xx <= x1 && grow; xx++)
								{
									grow = L(xx, y1 + 1, zz);
								}
							}
							y1 += grow ? 1 : 0;
						}
						for (int yy = y; yy <= y1; yy++)
						{
							for (int zz = z; zz <= z1; zz++)
							{
								for (int xx = x; xx <= x1; xx++)
								{
									left[xx + 16 * zz + 256 * yy] = false;
								}
							}
						}
						boxes.Add((x, y, z, x1 + 1, y1 + 1, z1 + 1));
					}
				}
			}
			return boxes;
		}
	}
}
