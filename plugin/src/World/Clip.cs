using System;
using System.Collections.Generic;

namespace RepoCraft.World
{
	/// <summary>
	/// Cutting dug Minecraft blocks (unit cubes) out of REPO triangles: what's left of a triangle
	/// outside every cube, as convex polygons. Used for the collision sent to Minecraft and for the
	/// meshes REPO draws. Minecraft coordinates. Port of SkyCraft's Clip.h.
	/// </summary>
	internal static class Clip
	{
		public struct Vert
		{
			public float X, Y, Z;     // position
			public float B0, B1, B2;  // weights of the source triangle's corners (for the other vertex data)

			public float this[int axis]
			{
				get => axis == 0 ? X : axis == 1 ? Y : Z;
				set
				{
					if (axis == 0) X = value;
					else if (axis == 1) Y = value;
					else Z = value;
				}
			}
		}

		/// <summary>Dug blocks merged into boxes (a TNT crater is a few boxes, not hundreds of cubes).</summary>
		public struct Box
		{
			public float LoX, LoY, LoZ, HiX, HiY, HiZ;

			public float Lo(int axis) => axis == 0 ? LoX : axis == 1 ? LoY : LoZ;

			public float Hi(int axis) => axis == 0 ? HiX : axis == 1 ? HiY : HiZ;
		}

		/// <summary>Faces lying exactly on a dug block's face go with the block.</summary>
		public const float Slop = 1.0e-4f;

		public static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

		private static Vert Lerp(in Vert a, in Vert b, float t) => new Vert
		{
			X = a.X + (b.X - a.X) * t,
			Y = a.Y + (b.Y - a.Y) * t,
			Z = a.Z + (b.Z - a.Z) * t,
			B0 = a.B0 + (b.B0 - a.B0) * t,
			B1 = a.B1 + (b.B1 - a.B1) * t,
			B2 = a.B2 + (b.B2 - a.B2) * t,
		};

		/// <summary>The parts of <paramref name="input"/> below and above the plane p[axis] = value.</summary>
		private static void Split(List<Vert> input, int axis, float value, List<Vert> below, List<Vert> above)
		{
			below.Clear();
			above.Clear();
			int n = input.Count;
			for (int i = 0; i < n; i++)
			{
				Vert a = input[i];
				Vert b = input[(i + 1) % n];
				float da = a[axis] - value;
				float db = b[axis] - value;
				if (da <= 0f)
				{
					below.Add(a);
				}
				if (da >= 0f)
				{
					above.Add(a);
				}
				if ((da < 0f && db > 0f) || (da > 0f && db < 0f))
				{
					Vert m = Lerp(a, b, da / (da - db));
					m[axis] = value;
					below.Add(m);
					above.Add(m);
				}
			}
			if (below.Count < 3)
			{
				below.Clear();
			}
			if (above.Count < 3)
			{
				above.Clear();
			}
		}

		public static float Area2(List<Vert> poly)
		{
			float nx = 0, ny = 0, nz = 0;
			Vert o = poly[0];
			for (int i = 1; i + 1 < poly.Count; i++)
			{
				float ux = poly[i].X - o.X, uy = poly[i].Y - o.Y, uz = poly[i].Z - o.Z;
				float wx = poly[i + 1].X - o.X, wy = poly[i + 1].Y - o.Y, wz = poly[i + 1].Z - o.Z;
				nx += uy * wz - uz * wy;
				ny += uz * wx - ux * wz;
				nz += ux * wy - uy * wx;
			}
			return (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
		}

		/// <summary>Greedy merge of cubes (min corners) into boxes. up: how far a box reaches above its top.</summary>
		public static List<Box> Merge(IReadOnlyList<(int x, int y, int z)> cubes, float up = 0f)
		{
			var output = new List<Box>();
			if (cubes.Count == 0)
			{
				return output;
			}
			var left = new HashSet<long>();
			foreach (var c in cubes)
			{
				left.Add(Key(c.x, c.y, c.z));
			}
			var order = new List<(int x, int y, int z)>(cubes);
			order.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
			foreach (var c in order)
			{
				if (!left.Contains(Key(c.x, c.y, c.z)))
				{
					continue;
				}
				int x1 = c.x, z1 = c.z, y1 = c.y;
				while (left.Contains(Key(x1 + 1, c.y, c.z)))
				{
					x1++;
				}
				for (bool grow = true; grow;)
				{
					for (int x = c.x; x <= x1 && grow; x++)
					{
						grow = left.Contains(Key(x, c.y, z1 + 1));
					}
					z1 += grow ? 1 : 0;
				}
				for (bool grow = true; grow;)
				{
					for (int z = c.z; z <= z1 && grow; z++)
					{
						for (int x = c.x; x <= x1 && grow; x++)
						{
							grow = left.Contains(Key(x, y1 + 1, z));
						}
					}
					y1 += grow ? 1 : 0;
				}
				for (int y = c.y; y <= y1; y++)
				{
					for (int z = c.z; z <= z1; z++)
					{
						for (int x = c.x; x <= x1; x++)
						{
							left.Remove(Key(x, y, z));
						}
					}
				}
				output.Add(new Box
				{
					LoX = c.x - Slop, LoY = c.y - Slop, LoZ = c.z - Slop,
					HiX = x1 + 1 + Slop, HiY = y1 + 1 + up + Slop, HiZ = z1 + 1 + Slop,
				});
			}
			return output;
		}

		private static bool Touches(List<Vert> poly, in Box box)
		{
			for (int axis = 0; axis < 3; axis++)
			{
				float lo = float.MaxValue, hi = float.MinValue;
				foreach (var v in poly)
				{
					float p = v[axis];
					lo = Math.Min(lo, p);
					hi = Math.Max(hi, p);
				}
				if (hi < box.Lo(axis) || lo > box.Hi(axis))
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// <paramref name="input"/> minus every box, as convex pieces appended to <paramref name="output"/>.
		/// Returns false if nothing was cut (output then holds the input unchanged).
		/// </summary>
		public static bool Subtract(List<Vert> input, List<Box> boxes, List<List<Vert>> output)
		{
			var pieces = new List<List<Vert>> { input };
			var next = new List<List<Vert>>();
			var below = new List<Vert>();
			var above = new List<Vert>();
			bool cut = false;
			foreach (var box in boxes)
			{
				next.Clear();
				foreach (var piece in pieces)
				{
					if (!Touches(piece, box))
					{
						next.Add(piece);
						continue;
					}
					cut = true;
					var rest = piece;
					for (int axis = 0; axis < 3; axis++)
					{
						Split(rest, axis, box.Lo(axis), below, above);
						if (below.Count > 0)
						{
							next.Add(new List<Vert>(below));
						}
						if (above.Count == 0)
						{
							rest = null;
							break;
						}
						rest = new List<Vert>(above);
						Split(rest, axis, box.Hi(axis), below, above);
						if (above.Count > 0)
						{
							next.Add(new List<Vert>(above));
						}
						if (below.Count == 0)
						{
							rest = null;
							break;
						}
						rest = new List<Vert>(below);
					}
					// Whatever is left is inside the box: gone.
				}
				var swap = pieces;
				pieces = next;
				next = swap;
				if (pieces.Count == 0)
				{
					break;
				}
			}
			foreach (var piece in pieces)
			{
				if (!cut || Area2(piece) > 1.0e-7f)
				{
					output.Add(piece);
				}
			}
			return cut;
		}

		public static List<Vert> FromTriangle(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz) => new List<Vert>(3)
		{
			new Vert { X = ax, Y = ay, Z = az, B0 = 1 },
			new Vert { X = bx, Y = by, Z = bz, B1 = 1 },
			new Vert { X = cx, Y = cy, Z = cz, B2 = 1 },
		};
	}
}
