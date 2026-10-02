using System;
using System.Collections.Generic;
using System.Threading;
using RepoCraft.Link;

namespace RepoCraft.World
{
	/// <summary>One triangle in Minecraft space. Flags: Proto.Tri* (diggable, material in bits 8-15).</summary>
	internal struct Tri
	{
		public float Ax, Ay, Az, Bx, By, Bz, Cx, Cy, Cz;
		public uint Flags;

		public Tri(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, uint flags)
		{
			Ax = ax; Ay = ay; Az = az; Bx = bx; By = by; Bz = bz; Cx = cx; Cy = cy; Cz = cz; Flags = flags;
		}
	}

	/// <summary>An oriented box in Minecraft space: centre, unit axes, half extents (blocks).</summary>
	internal struct Obb
	{
		public float Cx, Cy, Cz;
		public float A0x, A0y, A0z, A1x, A1y, A1z, A2x, A2y, A2z;
		public float H0, H1, H2;
		public uint Flags;
	}

	internal struct Capsule
	{
		public float Ax, Ay, Az, Bx, By, Bz, R;
		public uint Flags;
	}

	/// <summary>What the main thread harvested for one 8x8x8-block region.</summary>
	internal sealed class CollisionJob
	{
		public int Rx, Ry, Rz;
		public uint Epoch;
		public bool Clear;
		public readonly List<Tri> Tris = new List<Tri>();
		public readonly List<Obb> Boxes = new List<Obb>();
		public readonly List<Capsule> Capsules = new List<Capsule>();
	}

	/// <summary>
	/// Off the main thread: turns harvested REPO geometry into what Minecraft's physics uses: the
	/// exact triangles (the player's smooth collider) and 1/8-block voxels (everything else), and
	/// writes both to the collision ring. Port of the worker half of SkyCraft's Collision.cpp.
	/// </summary>
	internal sealed class CollisionWorker
	{
		public const int RegionSize = 8;           // blocks per region edge (must match Java)
		/// <summary>Box/capsule flag (never sent): fill as voxels but emit no triangles for it.</summary>
		public const uint NoShell = 1u << 31;
		private const int G = RegionSize * 8;      // voxels per region edge
		private const float SteepMin = 0.1f;       // |n.y| below this is a wall: keep it fine-grained
		private const float SteepMax = 0.643f;     // |n.y| below this (steeper than ~50 deg) gets block-coarsened
		private const float PrimMargin = 0.5f;     // voxels; lets thin shapes still register

		private readonly object queueLock = new object();
		private readonly Queue<CollisionJob> queue = new Queue<CollisionJob>();
		private Thread thread;
		private volatile uint epoch;
		private volatile bool running;

		public int Pending
		{
			get
			{
				lock (queueLock)
				{
					return queue.Count;
				}
			}
		}

		public uint Epoch => epoch;

		public void Start()
		{
			if (thread != null)
			{
				return;
			}
			running = true;
			thread = new Thread(Loop) { IsBackground = true, Name = "RepoCraft collision" };
			thread.Start();
		}

		public void Stop() => running = false;

		/// <summary>Drops everything queued; Minecraft clears its store when it sees the new epoch.</summary>
		public void Reset(uint newEpoch)
		{
			epoch = newEpoch;
			lock (queueLock)
			{
				queue.Clear();
				queue.Enqueue(new CollisionJob { Clear = true, Epoch = newEpoch });
				Monitor.Pulse(queueLock);
			}
		}

		public void Enqueue(CollisionJob job)
		{
			lock (queueLock)
			{
				queue.Enqueue(job);
				Monitor.Pulse(queueLock);
			}
		}

		private void Loop()
		{
			while (running)
			{
				CollisionJob job;
				lock (queueLock)
				{
					while (queue.Count == 0 && running)
					{
						Monitor.Wait(queueLock, 500);
					}
					if (!running)
					{
						return;
					}
					job = queue.Dequeue();
				}
				try
				{
					if (job.Clear)
					{
						Send(BitConverter.GetBytes(job.Epoch), 4, Proto.ColClear);
					}
					else if (job.Epoch == epoch)
					{
						var solid = new List<Tri>(job.Tris.Count + job.Boxes.Count * 12);
						Triangulate(job, solid);
						SendTriangles(job, solid);
						Voxelize(job, solid);
					}
				}
				catch (Exception e)
				{
					Log.Error($"collision worker: {e}");
				}
			}
		}

		private void Send(byte[] payload, int bytes, uint type)
		{
			var link = SharedLink.Instance;
			for (int attempt = 0; attempt < 2000 && running; attempt++)
			{
				if (link.WriteCollision(type, payload, bytes))
				{
					return;
				}
				Thread.Sleep(1); // ring full: Minecraft is behind (or not running)
			}
			Log.Warn("collision ring stayed full; dropped a message");
		}

		// ---- triangles ---------------------------------------------------------------------------

		/// <summary>The job's triangles plus its boxes and capsules as outward-facing triangles.</summary>
		private static void Triangulate(CollisionJob job, List<Tri> output)
		{
			output.AddRange(job.Tris);
			var corner = new float[8, 3];
			foreach (var b in job.Boxes)
			{
				if ((b.Flags & CollisionWorker.NoShell) != 0)
				{
					continue; // voxels only (a land box: its top went as terrain triangles)
				}
				EmitBox(b.Cx, b.Cy, b.Cz, b.A0x, b.A0y, b.A0z, b.A1x, b.A1y, b.A1z, b.A2x, b.A2y, b.A2z, b.H0, b.H1, b.H2, b.Flags, corner, output);
			}
			foreach (var c in job.Capsules)
			{
				if ((c.Flags & CollisionWorker.NoShell) != 0)
				{
					continue; // its round surface was sent as triangles already
				}
				// As its bounding box along the axis (what SkyCraft does too: close enough for walls).
				float abx = c.Bx - c.Ax, aby = c.By - c.Ay, abz = c.Bz - c.Az;
				float len = (float)Math.Sqrt(abx * abx + aby * aby + abz * abz);
				float zx = 0, zy = 1, zz = 0;
				if (len > 1e-4f)
				{
					zx = abx / len; zy = aby / len; zz = abz / len;
				}
				float rx = Math.Abs(zy) < 0.9f ? 0f : 1f, ry = Math.Abs(zy) < 0.9f ? 1f : 0f, rz = 0f;
				Cross(rx, ry, rz, zx, zy, zz, out float xx, out float xy, out float xz);
				float l0 = (float)Math.Sqrt(xx * xx + xy * xy + xz * xz);
				xx /= l0; xy /= l0; xz /= l0;
				Cross(zx, zy, zz, xx, xy, xz, out float yx, out float yy, out float yz);
				EmitBox((c.Ax + c.Bx) * 0.5f, (c.Ay + c.By) * 0.5f, (c.Az + c.Bz) * 0.5f, xx, xy, xz, yx, yy, yz, zx, zy, zz, c.R, c.R, len * 0.5f + c.R, c.Flags, corner, output);
			}
		}

		private static void EmitBox(float cx, float cy, float cz, float a0x, float a0y, float a0z, float a1x, float a1y, float a1z, float a2x, float a2y, float a2z,
			float h0, float h1, float h2, uint flags, float[,] corner, List<Tri> output)
		{
			for (int i = 0; i < 8; i++)
			{
				float sx = (i & 1) != 0 ? 1f : -1f, sy = (i & 2) != 0 ? 1f : -1f, sz = (i & 4) != 0 ? 1f : -1f;
				corner[i, 0] = cx + a0x * h0 * sx + a1x * h1 * sy + a2x * h2 * sz;
				corner[i, 1] = cy + a0y * h0 * sx + a1y * h1 * sy + a2y * h2 * sz;
				corner[i, 2] = cz + a0z * h0 * sx + a1z * h1 * sy + a2z * h2 * sz;
			}
			void Emit(int a, int b, int c)
			{
				var t = new Tri(corner[a, 0], corner[a, 1], corner[a, 2], corner[b, 0], corner[b, 1], corner[b, 2], corner[c, 0], corner[c, 1], corner[c, 2], flags);
				// Outward = away from the centre.
				Cross(t.Bx - t.Ax, t.By - t.Ay, t.Bz - t.Az, t.Cx - t.Ax, t.Cy - t.Ay, t.Cz - t.Az, out float nx, out float ny, out float nz);
				float dx = (t.Ax + t.Bx + t.Cx) / 3f - cx, dy = (t.Ay + t.By + t.Cy) / 3f - cy, dz = (t.Az + t.Bz + t.Cz) / 3f - cz;
				if (nx * dx + ny * dy + nz * dz < 0f)
				{
					t = new Tri(t.Ax, t.Ay, t.Az, t.Cx, t.Cy, t.Cz, t.Bx, t.By, t.Bz, flags);
				}
				output.Add(t);
			}
			void Quad(int a, int b, int c, int d)
			{
				Emit(a, b, c);
				Emit(a, c, d);
			}
			Quad(0, 1, 3, 2);
			Quad(4, 5, 7, 6);
			Quad(0, 1, 5, 4);
			Quad(2, 3, 7, 6);
			Quad(0, 2, 6, 4);
			Quad(1, 3, 7, 5);
		}

		private void SendTriangles(CollisionJob job, List<Tri> solid)
		{
			float loX = job.Rx * RegionSize - 0.5f, loY = job.Ry * RegionSize - 0.5f, loZ = job.Rz * RegionSize - 0.5f;
			float hiX = loX + RegionSize + 1f, hiY = loY + RegionSize + 1f, hiZ = loZ + RegionSize + 1f;
			var output = new List<Tri>(solid.Count);
			void Add(in Tri t, uint flags)
			{
				float tlx = Min3(t.Ax, t.Bx, t.Cx), tly = Min3(t.Ay, t.By, t.Cy), tlz = Min3(t.Az, t.Bz, t.Cz);
				float thx = Max3(t.Ax, t.Bx, t.Cx), thy = Max3(t.Ay, t.By, t.Cy), thz = Max3(t.Az, t.Bz, t.Cz);
				if (tlx <= hiX && thx >= loX && tly <= hiY && thy >= loY && tlz <= hiZ && thz >= loZ && Finite(t))
				{
					var copy = t;
					copy.Flags = flags;
					output.Add(copy);
				}
			}

			// The dug blocks around this region's diggable triangles, merged into boxes once.
			List<Clip.Box> boxes = null;
			if (DugBlocks.Any())
			{
				float dlx = float.MaxValue, dly = float.MaxValue, dlz = float.MaxValue, dhx = float.MinValue, dhy = float.MinValue, dhz = float.MinValue;
				foreach (var t in solid)
				{
					if ((t.Flags & Proto.TriDiggable) == 0)
					{
						continue;
					}
					dlx = Math.Min(dlx, Min3(t.Ax, t.Bx, t.Cx)); dly = Math.Min(dly, Min3(t.Ay, t.By, t.Cy)); dlz = Math.Min(dlz, Min3(t.Az, t.Bz, t.Cz));
					dhx = Math.Max(dhx, Max3(t.Ax, t.Bx, t.Cx)); dhy = Math.Max(dhy, Max3(t.Ay, t.By, t.Cy)); dhz = Math.Max(dhz, Max3(t.Az, t.Bz, t.Cz));
				}
				if (dlx <= dhx)
				{
					var cubes = new List<(int, int, int)>();
					DugBlocks.Collect(dlx, dly, dlz, dhx, dhy, dhz, cubes);
					if (cubes.Count > 0)
					{
						boxes = Clip.Merge(cubes);
					}
				}
			}
			var nearBoxes = new List<Clip.Box>();
			var pieces = new List<List<Clip.Vert>>();
			foreach (var t in solid)
			{
				if ((t.Flags & Proto.TriDiggable) != 0 && boxes != null)
				{
					float tlx = Min3(t.Ax, t.Bx, t.Cx), tly = Min3(t.Ay, t.By, t.Cy), tlz = Min3(t.Az, t.Bz, t.Cz);
					float thx = Max3(t.Ax, t.Bx, t.Cx), thy = Max3(t.Ay, t.By, t.Cy), thz = Max3(t.Az, t.Bz, t.Cz);
					nearBoxes.Clear();
					foreach (var b in boxes)
					{
						if (thx >= b.LoX && tlx <= b.HiX && thy >= b.LoY && tly <= b.HiY && thz >= b.LoZ && tlz <= b.HiZ)
						{
							nearBoxes.Add(b);
						}
					}
					if (nearBoxes.Count > 0)
					{
						Add(t, t.Flags | Proto.TriGhost); // the surface as it was: what's behind it is solid
						pieces.Clear();
						Clip.Subtract(Clip.FromTriangle(t.Ax, t.Ay, t.Az, t.Bx, t.By, t.Bz, t.Cx, t.Cy, t.Cz), nearBoxes, pieces);
						foreach (var piece in pieces)
						{
							for (int v = 1; v + 1 < piece.Count; v++)
							{
								var part = new Tri(piece[0].X, piece[0].Y, piece[0].Z, piece[v].X, piece[v].Y, piece[v].Z, piece[v + 1].X, piece[v + 1].Y, piece[v + 1].Z, t.Flags);
								Add(part, part.Flags);
							}
						}
						continue;
					}
				}
				Add(t, t.Flags);
			}

			byte[] payload = new byte[Proto.ColRegionBytes + output.Count * Proto.ColTriBytes];
			WriteRegionHeader(payload, job, (uint)output.Count);
			int o = Proto.ColRegionBytes;
			foreach (var t in output)
			{
				PutF(payload, ref o, t.Ax); PutF(payload, ref o, t.Ay); PutF(payload, ref o, t.Az);
				PutF(payload, ref o, t.Bx); PutF(payload, ref o, t.By); PutF(payload, ref o, t.Bz);
				PutF(payload, ref o, t.Cx); PutF(payload, ref o, t.Cy); PutF(payload, ref o, t.Cz);
				PutU(payload, ref o, t.Flags);
			}
			Send(payload, payload.Length, Proto.ColTris);
		}

		// ---- voxels ------------------------------------------------------------------------------

		private void Voxelize(CollisionJob job, List<Tri> tris)
		{
			var solid = new ulong[G * G];
			var steep = new ulong[G * G];
			var digSolid = new ulong[G * G];
			var digSteep = new ulong[G * G];
			float ox = job.Rx * RegionSize, oy = job.Ry * RegionSize, oz = job.Rz * RegionSize;
			var a = new float[3];
			var b = new float[3];
			var c = new float[3];
			var n = new float[3];
			var lo = new float[3];
			var hi = new float[3];
			var cen = new float[3];
			var p = new int[3];

			foreach (var tri in tris)
			{
				if ((tri.Flags & Proto.TriStairHelper) != 0)
				{
					continue;
				}
				a[0] = (tri.Ax - ox) * 8f; a[1] = (tri.Ay - oy) * 8f; a[2] = (tri.Az - oz) * 8f;
				b[0] = (tri.Bx - ox) * 8f; b[1] = (tri.By - oy) * 8f; b[2] = (tri.Bz - oz) * 8f;
				c[0] = (tri.Cx - ox) * 8f; c[1] = (tri.Cy - oy) * 8f; c[2] = (tri.Cz - oz) * 8f;
				for (int i = 0; i < 3; i++)
				{
					lo[i] = Min3(a[i], b[i], c[i]);
					hi[i] = Max3(a[i], b[i], c[i]);
				}
				if (hi[0] < 0 || hi[1] < 0 || hi[2] < 0 || lo[0] > G || lo[1] > G || lo[2] > G)
				{
					continue;
				}
				Cross(b[0] - a[0], b[1] - a[1], b[2] - a[2], c[0] - a[0], c[1] - a[1], c[2] - a[2], out n[0], out n[1], out n[2]);
				float len = (float)Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
				if (len < 1e-9f)
				{
					continue;
				}
				n[0] /= len; n[1] /= len; n[2] /= len;
				float ny = Math.Abs(n[1]);
				bool flat = ny >= SteepMax || ny < SteepMin;
				bool dig = (tri.Flags & Proto.TriDiggable) != 0;
				ulong[] grid = dig ? (flat ? digSolid : digSteep) : (flat ? solid : steep);

				int dom = 0;
				if (Math.Abs(n[1]) > Math.Abs(n[dom])) dom = 1;
				if (Math.Abs(n[2]) > Math.Abs(n[dom])) dom = 2;
				int u = (dom + 1) % 3, v = (dom + 2) % 3;
				float d = n[0] * a[0] + n[1] * a[1] + n[2] * a[2];
				float r = 0.5f * (Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]));
				int iu0 = ClampLo(lo[u]), iu1 = ClampHi(hi[u]), iv0 = ClampLo(lo[v]), iv1 = ClampHi(hi[v]);
				int id0 = ClampLo(lo[dom]), id1 = ClampHi(hi[dom]);
				for (int iu = iu0; iu <= iu1; iu++)
				{
					for (int iv = iv0; iv <= iv1; iv++)
					{
						float cu = iu + 0.5f, cv = iv + 0.5f;
						float s0 = (d - r - n[u] * cu - n[v] * cv) / n[dom];
						float s1 = (d + r - n[u] * cu - n[v] * cv) / n[dom];
						int a0 = Math.Max(id0, (int)Math.Floor(Math.Min(s0, s1) - 0.5f));
						int a1 = Math.Min(id1, (int)Math.Ceiling(Math.Max(s0, s1) - 0.5f));
						for (int id = a0; id <= a1; id++)
						{
							cen[dom] = id + 0.5f;
							cen[u] = cu;
							cen[v] = cv;
							if (TriBoxOverlap(cen, 0.5f, a, b, c, n))
							{
								p[dom] = id; p[u] = iu; p[v] = iv;
								grid[p[1] * G + p[2]] |= 1UL << p[0];
							}
						}
					}
				}
			}

			// Boxes and capsules: fill their insides too (their triangles only made the shell).
			const float m = PrimMargin / 8f;
			foreach (var box in job.Boxes)
			{
				float ex = Math.Abs(box.A0x) * box.H0 + Math.Abs(box.A1x) * box.H1 + Math.Abs(box.A2x) * box.H2;
				float ey = Math.Abs(box.A0y) * box.H0 + Math.Abs(box.A1y) * box.H1 + Math.Abs(box.A2y) * box.H2;
				float ez = Math.Abs(box.A0z) * box.H0 + Math.Abs(box.A1z) * box.H1 + Math.Abs(box.A2z) * box.H2;
				var bx = box;
				Fill(box.Cx - ex, box.Cy - ey, box.Cz - ez, box.Cx + ex, box.Cy + ey, box.Cz + ez, (box.Flags & Proto.TriDiggable) != 0 ? digSolid : solid, ox, oy, oz,
					(px, py, pz) =>
					{
						float dx = px - bx.Cx, dy = py - bx.Cy, dz = pz - bx.Cz;
						return Math.Abs(dx * bx.A0x + dy * bx.A0y + dz * bx.A0z) <= bx.H0 + m
							&& Math.Abs(dx * bx.A1x + dy * bx.A1y + dz * bx.A1z) <= bx.H1 + m
							&& Math.Abs(dx * bx.A2x + dy * bx.A2y + dz * bx.A2z) <= bx.H2 + m;
					});
			}
			foreach (var cap in job.Capsules)
			{
				var cp = cap;
				Fill(Math.Min(cap.Ax, cap.Bx) - cap.R, Math.Min(cap.Ay, cap.By) - cap.R, Math.Min(cap.Az, cap.Bz) - cap.R,
					Math.Max(cap.Ax, cap.Bx) + cap.R, Math.Max(cap.Ay, cap.By) + cap.R, Math.Max(cap.Az, cap.Bz) + cap.R,
					(cap.Flags & Proto.TriDiggable) != 0 ? digSolid : solid, ox, oy, oz,
					(px, py, pz) =>
					{
						float abx = cp.Bx - cp.Ax, aby = cp.By - cp.Ay, abz = cp.Bz - cp.Az;
						float apx = px - cp.Ax, apy = py - cp.Ay, apz = pz - cp.Az;
						float len2 = abx * abx + aby * aby + abz * abz;
						float t = len2 > 0 ? Math.Max(0f, Math.Min(1f, (apx * abx + apy * aby + apz * abz) / len2)) : 0f;
						float qx = cp.Ax + abx * t - px, qy = cp.Ay + aby * t - py, qz = cp.Az + abz * t - pz;
						return qx * qx + qy * qy + qz * qz <= (cp.R + m) * (cp.R + m);
					});
			}

			Coarsen(steep, solid);
			Coarsen(digSteep, digSolid);

			if (DugBlocks.Any())
			{
				var dug = new List<(int x, int y, int z)>();
				DugBlocks.Collect(ox + 0.5f, oy + 0.5f, oz + 0.5f, ox + RegionSize - 0.5f, oy + RegionSize - 0.5f, oz + RegionSize - 0.5f, dug);
				foreach (var cube in dug)
				{
					int bx = cube.x - (int)ox, by = cube.y - (int)oy, bz = cube.z - (int)oz;
					if (bx < 0 || by < 0 || bz < 0 || bx >= RegionSize || by >= RegionSize || bz >= RegionSize)
					{
						continue;
					}
					ulong keep = ~(0xFFUL << (bx * 8));
					for (int y = by * 8; y < by * 8 + 8; y++)
					{
						for (int z = bz * 8; z < bz * 8 + 8; z++)
						{
							digSolid[y * G + z] &= keep;
						}
					}
				}
			}
			for (int i = 0; i < solid.Length; i++)
			{
				solid[i] |= digSolid[i];
			}

			var blocks = new List<(int x, int y, int z, ulong[] bits)>(64);
			for (int by = 0; by < RegionSize; by++)
			{
				for (int bz = 0; bz < RegionSize; bz++)
				{
					for (int bx = 0; bx < RegionSize; bx++)
					{
						ulong[] bits = null;
						for (int sy = 0; sy < 8; sy++)
						{
							ulong layer = 0;
							for (int sz = 0; sz < 8; sz++)
							{
								ulong row = (solid[(by * 8 + sy) * G + (bz * 8 + sz)] >> (bx * 8)) & 0xFF;
								layer |= row << (sz * 8);
							}
							if (layer != 0)
							{
								bits ??= new ulong[8];
								bits[sy] = layer;
							}
						}
						if (bits != null)
						{
							blocks.Add((job.Rx * RegionSize + bx, job.Ry * RegionSize + by, job.Rz * RegionSize + bz, bits));
						}
					}
				}
			}

			byte[] payload = new byte[Proto.ColRegionBytes + blocks.Count * Proto.ColBlockBytes];
			WriteRegionHeader(payload, job, (uint)blocks.Count);
			int o = Proto.ColRegionBytes;
			foreach (var blk in blocks)
			{
				PutI(payload, ref o, blk.x);
				PutI(payload, ref o, blk.y);
				PutI(payload, ref o, blk.z);
				PutU(payload, ref o, 0);
				for (int i = 0; i < 8; i++)
				{
					PutL(payload, ref o, blk.bits[i]);
				}
			}
			Send(payload, payload.Length, Proto.ColRegion);
		}

		private delegate bool Inside(float x, float y, float z);

		private static void Fill(float loX, float loY, float loZ, float hiX, float hiY, float hiZ, ulong[] grid, float ox, float oy, float oz, Inside inside)
		{
			float lx = (loX - ox) * 8f, ly = (loY - oy) * 8f, lz = (loZ - oz) * 8f;
			float hx = (hiX - ox) * 8f, hy = (hiY - oy) * 8f, hz = (hiZ - oz) * 8f;
			if (hx < 0 || hy < 0 || hz < 0 || lx > G || ly > G || lz > G)
			{
				return;
			}
			for (int y = ClampLo(ly - 1); y <= ClampHi(hy + 1); y++)
			{
				for (int z = ClampLo(lz - 1); z <= ClampHi(hz + 1); z++)
				{
					for (int x = ClampLo(lx - 1); x <= ClampHi(hx + 1); x++)
					{
						if (inside(ox + (x + 0.5f) / 8f, oy + (y + 0.5f) / 8f, oz + (z + 0.5f) / 8f))
						{
							grid[y * G + z] |= 1UL << x;
						}
					}
				}
			}
		}

		/// <summary>Steep surfaces become whole-block walls so Minecraft's step-up refuses to climb them.</summary>
		private static void Coarsen(ulong[] steep, ulong[] solid)
		{
			for (int by = 0; by < RegionSize; by++)
			{
				for (int bz = 0; bz < RegionSize; bz++)
				{
					for (int bx = 0; bx < RegionSize; bx++)
					{
						ulong xmask = 0xFFUL << (bx * 8);
						int minY = 99, maxY = -1;
						for (int y = by * 8; y < by * 8 + 8; y++)
						{
							for (int z = bz * 8; z < bz * 8 + 8; z++)
							{
								if ((steep[y * G + z] & xmask) != 0)
								{
									minY = Math.Min(minY, y);
									maxY = Math.Max(maxY, y);
								}
							}
						}
						if (maxY < 0)
						{
							continue;
						}
						for (int y = minY; y <= maxY; y++)
						{
							for (int z = bz * 8; z < bz * 8 + 8; z++)
							{
								solid[y * G + z] |= xmask;
							}
						}
					}
				}
			}
		}

		// ---- helpers -----------------------------------------------------------------------------

		private static void WriteRegionHeader(byte[] payload, CollisionJob job, uint count)
		{
			int o = 0;
			int minX = job.Rx * RegionSize, minY = job.Ry * RegionSize, minZ = job.Rz * RegionSize;
			PutI(payload, ref o, minX);
			PutI(payload, ref o, minY);
			PutI(payload, ref o, minZ);
			PutI(payload, ref o, minX + RegionSize - 1);
			PutI(payload, ref o, minY + RegionSize - 1);
			PutI(payload, ref o, minZ + RegionSize - 1);
			PutU(payload, ref o, job.Epoch);
			PutU(payload, ref o, count);
		}

		private static unsafe void PutF(byte[] b, ref int o, float v)
		{
			fixed (byte* p = &b[o])
			{
				*(float*)p = v;
			}
			o += 4;
		}

		private static unsafe void PutI(byte[] b, ref int o, int v)
		{
			fixed (byte* p = &b[o])
			{
				*(int*)p = v;
			}
			o += 4;
		}

		private static void PutU(byte[] b, ref int o, uint v) => PutI(b, ref o, (int)v);

		private static unsafe void PutL(byte[] b, ref int o, ulong v)
		{
			fixed (byte* p = &b[o])
			{
				*(ulong*)p = v;
			}
			o += 8;
		}

		private static int ClampLo(float v) => Math.Max(0, Math.Min(G - 1, (int)Math.Floor(v)));

		private static int ClampHi(float v) => Math.Max(0, Math.Min(G - 1, (int)Math.Ceiling(v) - 1));

		private static float Min3(float a, float b, float c) => Math.Min(a, Math.Min(b, c));

		private static float Max3(float a, float b, float c) => Math.Max(a, Math.Max(b, c));

		private static bool Finite(in Tri t)
		{
			bool F(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 1e6f;
			return F(t.Ax) && F(t.Ay) && F(t.Az) && F(t.Bx) && F(t.By) && F(t.Bz) && F(t.Cx) && F(t.Cy) && F(t.Cz);
		}

		private static void Cross(float ax, float ay, float az, float bx, float by, float bz, out float x, out float y, out float z)
		{
			x = ay * bz - az * by;
			y = az * bx - ax * bz;
			z = ax * by - ay * bx;
		}

		private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

		private static readonly float[][] Units = { new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f } };

		[ThreadStatic] private static float[] tv0, tv1, tv2, te0, te1, te2, tax;

		/// <summary>Triangle / axis-aligned cube overlap (Akenine-Moller SAT), voxel units.</summary>
		private static bool TriBoxOverlap(float[] c, float h, float[] ta, float[] tb, float[] tc, float[] n)
		{
			tv0 ??= new float[3]; tv1 ??= new float[3]; tv2 ??= new float[3];
			te0 ??= new float[3]; te1 ??= new float[3]; te2 ??= new float[3]; tax ??= new float[3];
			float[] v0 = tv0, v1 = tv1, v2 = tv2;
			for (int i = 0; i < 3; i++)
			{
				v0[i] = ta[i] - c[i];
				v1[i] = tb[i] - c[i];
				v2[i] = tc[i] - c[i];
				float mn = Min3(v0[i], v1[i], v2[i]), mx = Max3(v0[i], v1[i], v2[i]);
				if (mn > h || mx < -h)
				{
					return false;
				}
			}
			float d = Dot(n, v0);
			float r = h * (Math.Abs(n[0]) + Math.Abs(n[1]) + Math.Abs(n[2]));
			if (Math.Abs(d) > r)
			{
				return false;
			}
			for (int i = 0; i < 3; i++)
			{
				te0[i] = v1[i] - v0[i];
				te1[i] = v2[i] - v1[i];
				te2[i] = v0[i] - v2[i];
			}
			float[][] edges = { te0, te1, te2 };
			foreach (var e in edges)
			{
				foreach (var unit in Units)
				{
					tax[0] = e[1] * unit[2] - e[2] * unit[1];
					tax[1] = e[2] * unit[0] - e[0] * unit[2];
					tax[2] = e[0] * unit[1] - e[1] * unit[0];
					float p0 = Dot(v0, tax), p1 = Dot(v1, tax), p2 = Dot(v2, tax);
					float mn = Min3(p0, p1, p2), mx = Max3(p0, p1, p2);
					float rr = h * (Math.Abs(tax[0]) + Math.Abs(tax[1]) + Math.Abs(tax[2]));
					if (mn > rr || mx < -rr)
					{
						return false;
					}
				}
			}
			return true;
		}
	}
}
