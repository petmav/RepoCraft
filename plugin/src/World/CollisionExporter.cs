using System;
using System.Collections.Generic;
using RepoCraft.Link;
using UnityEngine;

namespace RepoCraft.World
{
	/// <summary>
	/// Streams REPO's static level collision around the player to Minecraft, one 8x8x8-block region
	/// at a time: exact triangles for the player's smooth collider and 1/8-block voxels for everything
	/// else (CollisionWorker does that part off the main thread).
	///
	/// REPO's levels are ~97% BoxColliders (walls 0.1 m thick, floors, ceilings), which go over exactly
	/// as oriented boxes; capsules and spheres as what they are; mesh colliders (stairs, ramps, curved
	/// walls) as their triangles, or probed with ray casts when the mesh can't be read. Only what
	/// stays put: anything on a rigidbody (doors, loot, carts, enemies) is pushed by the player's own
	/// body in REPO instead.
	/// </summary>
	internal sealed class CollisionExporter
	{
		public static readonly CollisionExporter Instance = new CollisionExporter();

		private const int Radius = 5;    // regions around the player horizontally (arrows fly far)
		private const int Below = 3;
		private const int Above = 2;
		private const float RefreshNearSeconds = 1f; // re-send regions next to the player this often (the truck door...)
		private const int MaxPerFrame = 3;
		private const float FrameBudgetMs = 2.5f;

		private readonly CollisionWorker worker = new CollisionWorker();
		private readonly Dictionary<long, float> harvested = new Dictionary<long, float>();
		private readonly List<(int x, int y, int z)> offsets = new List<(int, int, int)>();
		private readonly List<(int x, int y, int z)> urgent = new List<(int, int, int)>();
		private readonly Collider[] hits = new Collider[2048];
		private readonly Dictionary<Mesh, (Vector3[] v, int[] t)> meshCache = new Dictionary<Mesh, (Vector3[], int[])>();
		private readonly HashSet<Mesh> unreadable = new HashSet<Mesh>();
		private readonly Dictionary<Collider, Info> infos = new Dictionary<Collider, Info>();
		private int mask;
		private bool started;
		private uint epoch;

		public uint Epoch => epoch;
		public int Pending => worker.Pending;

		/// <summary>What a collider is to Minecraft: whether it digs, what into, and whether it's the land.</summary>
		private struct Info
		{
			public bool Skip;
			public uint Flags;
		}

		public void Start()
		{
			if (started)
			{
				return;
			}
			started = true;
			for (int dx = -Radius; dx <= Radius; dx++)
			{
				for (int dz = -Radius; dz <= Radius; dz++)
				{
					for (int dy = -Below; dy <= Above; dy++)
					{
						offsets.Add((dx, dy, dz));
					}
				}
			}
			offsets.Sort((a, b) => (a.x * a.x + a.z * a.z + a.y * a.y * 2).CompareTo(b.x * b.x + b.z * b.z + b.y * b.y * 2));
			mask = LayerMask.GetMask("Default", "PlayerOnlyCollision");
			worker.Start();
		}

		/// <summary>Drops everything; Minecraft clears its store when it sees the new epoch.</summary>
		public void Reset(uint newEpoch)
		{
			epoch = newEpoch;
			harvested.Clear();
			signatures.Clear();
			urgent.Clear();
			infos.Clear();
			meshCache.Clear();
			unreadable.Clear();
			worker.Reset(newEpoch);
		}

		/// <summary>Blocks dug out of (or back into) REPO's level: the regions around them go again first.</summary>
		public void DigChanged(List<(int x, int y, int z)> blocks)
		{
			var seen = new HashSet<long>();
			foreach (var b in blocks)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					for (int dy = -1; dy <= 1; dy++)
					{
						for (int dz = -1; dz <= 1; dz++)
						{
							int rx = FloorDiv(b.x + dx, CollisionWorker.RegionSize), ry = FloorDiv(b.y + dy, CollisionWorker.RegionSize), rz = FloorDiv(b.z + dz, CollisionWorker.RegionSize);
							if (seen.Add(Clip.Key(rx, ry, rz)))
							{
								urgent.Add((rx, ry, rz));
							}
						}
					}
				}
			}
		}

		/// <summary>
		/// Once a frame with the player's position (Minecraft coords), and where the other players in
		/// our Minecraft world are (hosting a lobby's world: its server needs REPO's ground under them).
		/// </summary>
		public void Update(Vector3 playerMc, List<Vector3> others = null)
		{
			if (!started || worker.Pending > 64)
			{
				return; // the worker is behind (or Minecraft is not reading the ring)
			}
			var centre = RegionOf(playerMc);
			centres.Clear();
			centres.Add(centre);
			if (others != null)
			{
				foreach (var p in others)
				{
					centres.Add(RegionOf(p));
				}
			}
			float now = Time.realtimeSinceStartup;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			int done = 0;
			Physics.SyncTransforms();
			while (urgent.Count > 0 && done < MaxPerFrame * 2)
			{
				var r = urgent[urgent.Count - 1];
				urgent.RemoveAt(urgent.Count - 1);
				if (!NearAny(r))
				{
					harvested.Remove(Clip.Key(r.x, r.y, r.z)); // far away: sent again whenever it's needed
					continue;
				}
				Harvest(r.x, r.y, r.z, true);
				harvested[Clip.Key(r.x, r.y, r.z)] = now;
				done++;
			}
			foreach (var o in offsets)
			{
				int rx = centre.x + o.x, ry = centre.y + o.y, rz = centre.z + o.z;
				long key = Clip.Key(rx, ry, rz);
				bool near = Math.Abs(o.x) <= 1 && Math.Abs(o.z) <= 1 && o.y >= -1 && o.y <= 0;
				bool known = harvested.TryGetValue(key, out float at);
				if (known && !(near && now - at > RefreshNearSeconds))
				{
					continue;
				}
				// A periodic refresh is skipped if nothing changed; a region not known here always goes.
				Harvest(rx, ry, rz, !known);
				harvested[key] = now;
				if (++done >= MaxPerFrame || watch.Elapsed.TotalMilliseconds > FrameBudgetMs)
				{
					break;
				}
			}
			// Around the others: what isn't known yet, nearest first, with what's left of the budget.
			for (int i = 1; i < centres.Count && done < MaxPerFrame && watch.Elapsed.TotalMilliseconds <= FrameBudgetMs; i++)
			{
				var c = centres[i];
				foreach (var o in offsets)
				{
					if (Math.Abs(o.x) > OthersRadius || Math.Abs(o.z) > OthersRadius || o.y < -OthersBelow || o.y > Above)
					{
						continue;
					}
					long key = Clip.Key(c.x + o.x, c.y + o.y, c.z + o.z);
					if (harvested.ContainsKey(key))
					{
						continue;
					}
					Harvest(c.x + o.x, c.y + o.y, c.z + o.z, true);
					harvested[key] = now;
					if (++done >= MaxPerFrame || watch.Elapsed.TotalMilliseconds > FrameBudgetMs)
					{
						break;
					}
				}
			}
			if (harvested.Count > offsets.Count * (4 + 2 * (centres.Count - 1)))
			{
				harvested.Clear();
			}
			if (signatures.Count > offsets.Count * (8 + 2 * (centres.Count - 1)))
			{
				signatures.Clear();
			}
		}

		private const int OthersRadius = 3;
		private const int OthersBelow = 2;
		private readonly List<(int x, int y, int z)> centres = new List<(int, int, int)>();

		private static (int x, int y, int z) RegionOf(Vector3 mc) => (
			FloorDiv((int)Math.Floor(mc.x), CollisionWorker.RegionSize),
			FloorDiv((int)Math.Floor(mc.y), CollisionWorker.RegionSize),
			FloorDiv((int)Math.Floor(mc.z), CollisionWorker.RegionSize));

		private bool NearAny((int x, int y, int z) r)
		{
			for (int i = 0; i < centres.Count; i++)
			{
				var c = centres[i];
				int reach = i == 0 ? Radius : OthersRadius;
				int below = i == 0 ? Below : OthersBelow;
				if (Math.Abs(r.x - c.x) <= reach + 1 && Math.Abs(r.z - c.z) <= reach + 1 && r.y - c.y >= -below - 1 && r.y - c.y <= Above + 1)
				{
					return true;
				}
			}
			return false;
		}

		private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

		// ---- one region ----------------------------------------------------------------------------

		private readonly Dictionary<long, long> signatures = new Dictionary<long, long>();

		private void Harvest(int rx, int ry, int rz, bool force)
		{
			var job = new CollisionJob { Rx = rx, Ry = ry, Rz = rz, Epoch = epoch };
			int s = CollisionWorker.RegionSize;
			const float margin = 0.25f;
			// The region's box in Unity space (Z mirrored, X offset to this level's slot).
			Vector3 a = Coords.ToUnity(rx * s - margin, ry * s - margin, rz * s - margin);
			Vector3 b = Coords.ToUnity((rx + 1) * s + margin, (ry + 1) * s + margin, (rz + 1) * s + margin);
			Vector3 centre = (a + b) * 0.5f;
			Vector3 half = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z)) * 0.5f;
			int n = Physics.OverlapBoxNonAlloc(centre, half, hits, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
			var regionBounds = new Bounds(centre, half * 2f);
			// Nothing moved, appeared or went since this region was last sent: Minecraft has it already.
			long sig = n * 7919L;
			for (int i = 0; i < n; i++)
			{
				var c = hits[i];
				if (c == null || !c.enabled)
				{
					continue;
				}
				var tp = c.transform.position;
				sig = sig * 31 + c.GetInstanceID();
				sig = sig * 31 + Mathf.RoundToInt(tp.x * 200f) * 73856093L + Mathf.RoundToInt(tp.y * 200f) * 19349663L + Mathf.RoundToInt(tp.z * 200f) * 83492791L;
				sig = sig * 31 + Mathf.RoundToInt(c.transform.rotation.eulerAngles.y * 10f);
			}
			sig = sig * 31 + DugBlocks.Generation;
			long rkey = Clip.Key(rx, ry, rz);
			if (!force && signatures.TryGetValue(rkey, out long last) && last == sig)
			{
				return;
			}
			signatures[rkey] = sig;
			for (int i = 0; i < n; i++)
			{
				var col = hits[i];
				if (col == null || !col.enabled)
				{
					continue;
				}
				Info info = InfoFor(col);
				if (info.Skip)
				{
					continue;
				}
				try
				{
					Collect(col, info.Flags, job, regionBounds);
				}
				catch (Exception e)
				{
					Log.Debug($"collision: {col.name}: {e.Message}");
				}
			}
			// Cut walls and floors are switched off in REPO, but go to Minecraft as they were (it cuts
			// the dug blocks out itself, and needs the original surfaces to know what's inside).
			cutScratch.Clear();
			Dig.Instance.CutOriginalsIn(regionBounds, cutScratch);
			foreach (var box in cutScratch)
			{
				Info info = InfoFor(box);
				if (!info.Skip)
				{
					AddBox(box, info.Flags, job);
				}
			}
			worker.Enqueue(job);
		}

		private readonly List<BoxCollider> cutScratch = new List<BoxCollider>();

		/// <summary>A ground floor: Minecraft treats what's under it as solid land.</summary>
		public bool IsLand(Collider col) => col != null && (InfoFor(col).Flags & Proto.TriTerrain) != 0;

		/// <summary>A box collider's world bounds from its transform (works while it's switched off).</summary>
		public static Bounds WorldBounds(BoxCollider box)
		{
			var t = box.transform;
			var b = new Bounds(t.TransformPoint(box.center), Vector3.zero);
			Vector3 h = box.size * 0.5f;
			for (int i = 0; i < 8; i++)
			{
				b.Encapsulate(t.TransformPoint(box.center + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z)));
			}
			return b;
		}

		private Info InfoFor(Collider col)
		{
			if (infos.TryGetValue(col, out Info info))
			{
				return info;
			}
			info = new Info();
			if (col.isTrigger || col.attachedRigidbody != null || col.GetComponentInParent<RepoCraftOwned>() != null || col.gameObject.layer == 31)
			{
				info.Skip = true; // doors, loot, carts, enemies (pushed by the body), or our own blocks
			}
			else
			{
				info.Flags = DigFlags(col);
			}
			infos[col] = info;
			return info;
		}

		/// <summary>
		/// Walls, floors and ceilings of the level can be dug (not the truck, not invisible walls, not
		/// props). A floor with nothing under it is the land: Minecraft treats what's below it as solid
		/// ground (dirt, stone, bedrock), so you can dig down; upper floors are dug straight through.
		/// </summary>
		private static uint DigFlags(Collider col)
		{
			var lg = LevelGenerator.Instance;
			if (lg == null || lg.LevelParent == null || !col.transform.IsChildOf(lg.LevelParent.transform))
			{
				return 0;
			}
			if (col.GetComponentInParent<StartRoom>() != null || col.GetComponent<InvisibleWall>() != null || col.gameObject.layer != 0 || !Dig.InLevelGroup(col.transform))
			{
				return 0;
			}
			var surface = col.GetComponent<MaterialSurface>();
			Bounds b = col is BoxCollider bc ? WorldBounds(bc) : col.bounds;
			bool floor = Dig.IsFloorShape(col, b);
			byte material = Material(surface, !floor);
			uint flags = Proto.TriDiggable | ((uint)material << Proto.TriMaterialShift);
			if (floor && IsLandShape(col))
			{
				flags |= Proto.TriTerrain;
			}
			return flags;
		}

		/// <summary>A floor with no level geometry for a storey below it.</summary>
		private static bool IsLandShape(Collider col)
		{
			var b = col is BoxCollider bc ? WorldBounds(bc) : col.bounds;
			if (b.size.y > 1f || b.size.x < 0.5f || b.size.z < 0.5f)
			{
				return false;
			}
			var below = new Vector3(b.center.x, b.min.y - 0.05f, b.center.z);
			return !Physics.Raycast(below, Vector3.down, 6f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
		}

		private static byte Material(MaterialSurface surface, bool wallOrCeiling)
		{
			if (surface != null)
			{
				switch (surface.Type)
				{
					case Materials.Type.Wood: return Proto.DigPlanks;
					case Materials.Type.Beam: return Proto.DigOakLog;
					case Materials.Type.Rug:
					case Materials.Type.Tarp: return Proto.DigCloth;
					case Materials.Type.Tile:
					case Materials.Type.Stone: return Proto.DigStone;
					case Materials.Type.Rubble:
					case Materials.Type.Brokentiles: return Proto.DigCobble;
					case Materials.Type.Catwalk:
					case Materials.Type.Metal:
					case Materials.Type.Wetmetal:
					case Materials.Type.Vent: return Proto.DigMetal;
					case Materials.Type.Snow: return Proto.DigSnow;
					case Materials.Type.Gravel: return Proto.DigGravel;
					case Materials.Type.Grass: return Proto.DigGrass;
					case Materials.Type.Garbage: return Proto.DigOrganic;
				}
			}
			// Walls and ceilings by the level they're in.
			string level = Repo.Game.LevelName;
			if (level.Contains("Manor")) return Proto.DigPlanks;
			if (level.Contains("Wizard")) return Proto.DigCobble;
			if (level.Contains("Arctic")) return Proto.DigMetal;
			return Proto.DigStone;
		}

		private void Collect(Collider col, uint flags, CollisionJob job, Bounds region)
		{
			switch (col)
			{
				case BoxCollider box:
					AddBox(box, flags, job);
					break;
				case CapsuleCollider cap:
					AddCapsule(cap, flags, job);
					break;
				case SphereCollider sph:
					AddSphere(sph, flags, job);
					break;
				case MeshCollider mc when mc.sharedMesh != null:
					AddMesh(mc, flags, job, region);
					break;
				default:
					if (col.GetType().Name != "TerrainCollider" && col.GetType().Name != "WheelCollider")
					{
						var bb = col.bounds;
						AddObb(bb.center, Vector3.right, Vector3.up, Vector3.forward, bb.extents, flags, job);
					}
					break;
			}
		}

		private static void AddBox(BoxCollider box, uint flags, CollisionJob job)
		{
			var t = box.transform;
			Vector3 c = t.TransformPoint(box.center);
			Vector3 s = Vector3.Scale(box.size, Abs(t.lossyScale)) * 0.5f;
			if ((flags & Proto.TriTerrain) != 0)
			{
				// The land: only its top surface (Minecraft's "below it is solid" takes care of the rest),
				// plus the box itself as voxels for everything else.
				AddObb(c, t.right, t.up, t.forward, s, flags & ~Proto.TriTerrain, job, shellOnly: false, noShell: true);
				Vector3 up = Vector3.up;
				// The face whose normal points most nearly up.
				Vector3[] axes = { t.right, t.up, t.forward };
				float[] ext = { s.x, s.y, s.z };
				int best = 0;
				float bestDot = 0f;
				for (int i = 0; i < 3; i++)
				{
					float d = Vector3.Dot(axes[i], up);
					if (Mathf.Abs(d) > Mathf.Abs(bestDot))
					{
						bestDot = d;
						best = i;
					}
				}
				Vector3 n = axes[best] * Mathf.Sign(bestDot);
				Vector3 u = axes[(best + 1) % 3] * ext[(best + 1) % 3], v = axes[(best + 2) % 3] * ext[(best + 2) % 3];
				Vector3 top = c + n * ext[best];
				AddQuad(top - u - v, top + u - v, top + u + v, top - u + v, n, flags, job);
				return;
			}
			AddObb(c, t.right, t.up, t.forward, s, flags, job);
		}

		private static void AddObb(Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, Vector3 half, uint flags, CollisionJob job, bool shellOnly = false, bool noShell = false)
		{
			float k = Coords.K;
			Vector3 mc = Coords.ToMcF(c);
			Vector3 x = Coords.DirToMc(ax.normalized), y = Coords.DirToMc(ay.normalized), z = Coords.DirToMc(az.normalized);
			if (noShell)
			{
				// Voxels only (CollisionWorker fills boxes; its triangles come from Triangulate, skipped
				// here by passing it as a capsule-free fill): add as a box with a flag the worker reads.
				job.Boxes.Add(new Obb
				{
					Cx = mc.x, Cy = mc.y, Cz = mc.z, A0x = x.x, A0y = x.y, A0z = x.z, A1x = y.x, A1y = y.y, A1z = y.z, A2x = z.x, A2y = z.y, A2z = z.z,
					H0 = half.x * k, H1 = half.y * k, H2 = half.z * k, Flags = flags | CollisionWorker.NoShell,
				});
				return;
			}
			job.Boxes.Add(new Obb
			{
				Cx = mc.x, Cy = mc.y, Cz = mc.z, A0x = x.x, A0y = x.y, A0z = x.z, A1x = y.x, A1y = y.y, A1z = y.z, A2x = z.x, A2y = z.y, A2z = z.z,
				H0 = half.x * k, H1 = half.y * k, H2 = half.z * k, Flags = flags,
			});
		}

		private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, uint flags, CollisionJob job)
		{
			AddTri(a, b, c, outward, flags, job);
			AddTri(a, c, d, outward, flags, job);
		}

		/// <summary>A Unity-space triangle, wound so its Minecraft normal faces <paramref name="outward"/> (Unity).</summary>
		private static void AddTri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, uint flags, CollisionJob job)
		{
			Vector3 ma = Coords.ToMcF(a), mb = Coords.ToMcF(b), mcc = Coords.ToMcF(c);
			Vector3 n = Vector3.Cross(mb - ma, mcc - ma);
			if (Vector3.Dot(n, Coords.DirToMc(outward)) < 0f)
			{
				(mb, mcc) = (mcc, mb);
			}
			job.Tris.Add(new Tri(ma.x, ma.y, ma.z, mb.x, mb.y, mb.z, mcc.x, mcc.y, mcc.z, flags));
		}

		private static void AddCapsule(CapsuleCollider cap, uint flags, CollisionJob job)
		{
			var t = cap.transform;
			Vector3 ls = Abs(t.lossyScale);
			int dir = cap.direction;
			float axisScale = dir == 0 ? ls.x : dir == 1 ? ls.y : ls.z;
			float radiusScale = dir == 0 ? Mathf.Max(ls.y, ls.z) : dir == 1 ? Mathf.Max(ls.x, ls.z) : Mathf.Max(ls.x, ls.y);
			float r = cap.radius * radiusScale;
			float h = Mathf.Max(cap.height * axisScale, 2f * r);
			Vector3 axis = dir == 0 ? t.right : dir == 1 ? t.up : t.forward;
			Vector3 c = t.TransformPoint(cap.center);
			Vector3 pa = c - axis * (h * 0.5f - r), pb = c + axis * (h * 0.5f - r);
			AddRoundShell(pa, pb, r, flags, job);
		}

		private static void AddSphere(SphereCollider sph, uint flags, CollisionJob job)
		{
			var t = sph.transform;
			Vector3 ls = Abs(t.lossyScale);
			float r = sph.radius * Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
			Vector3 c = t.TransformPoint(sph.center);
			AddRoundShell(c, c, r, flags, job);
		}

		/// <summary>A capsule (or sphere, a == b): its surface as triangles, its inside as voxels.</summary>
		private static void AddRoundShell(Vector3 a, Vector3 b, float r, uint flags, CollisionJob job)
		{
			float k = Coords.K;
			Vector3 ma = Coords.ToMcF(a), mb = Coords.ToMcF(b);
			job.Capsules.Add(new Capsule { Ax = ma.x, Ay = ma.y, Az = ma.z, Bx = mb.x, By = mb.y, Bz = mb.z, R = r * k, Flags = flags | CollisionWorker.NoShell });
			Vector3 axis = b - a;
			float len = axis.magnitude;
			Vector3 up = len > 1e-4f ? axis / len : Vector3.up;
			Vector3 side = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
			Vector3 fwd = Vector3.Cross(side, up);
			const int seg = 10, rings = 4;
			// Rings from the bottom pole to the top pole: lower hemisphere around a, upper around b.
			var ringPts = new List<Vector3[]>();
			for (int i = 0; i <= rings * 2; i++)
			{
				float phi = Mathf.PI * i / (rings * 2) - Mathf.PI / 2; // -90..90
				Vector3 centre = i <= rings ? a : b;
				var pts = new Vector3[seg];
				for (int j = 0; j < seg; j++)
				{
					float th = 2 * Mathf.PI * j / seg;
					Vector3 d = (side * Mathf.Cos(th) + fwd * Mathf.Sin(th)) * Mathf.Cos(phi) + up * Mathf.Sin(phi);
					pts[j] = centre + d * r;
				}
				ringPts.Add(pts);
				if (i == rings && len > 1e-4f)
				{
					// The cylinder between the two hemispheres.
					var top = new Vector3[seg];
					for (int j = 0; j < seg; j++)
					{
						top[j] = pts[j] + axis;
					}
					ringPts.Add(top);
				}
			}
			Vector3 mid = (a + b) * 0.5f;
			for (int i = 0; i + 1 < ringPts.Count; i++)
			{
				var lo = ringPts[i];
				var hi = ringPts[i + 1];
				for (int j = 0; j < seg; j++)
				{
					int jn = (j + 1) % seg;
					Vector3 o = ((lo[j] + hi[jn]) * 0.5f - mid).normalized;
					AddTri(lo[j], lo[jn], hi[jn], o, flags, job);
					AddTri(lo[j], hi[jn], hi[j], o, flags, job);
				}
			}
		}

		private void AddMesh(MeshCollider mc, uint flags, CollisionJob job, Bounds region)
		{
			Mesh mesh = mc.sharedMesh;
			if (!meshCache.TryGetValue(mesh, out var data))
			{
				if (mesh.isReadable && !unreadable.Contains(mesh))
				{
					data = (mesh.vertices, mesh.triangles);
					meshCache[mesh] = data;
				}
				else
				{
					if (unreadable.Add(mesh))
					{
						Log.Debug($"collision: mesh {mesh.name} isn't readable; probing it with rays");
					}
					Probe(mc, flags, job, region);
					return;
				}
			}
			var m = mc.transform.localToWorldMatrix;
			var (v, tris) = data;
			for (int i = 0; i + 2 < tris.Length; i += 3)
			{
				Vector3 a = m.MultiplyPoint3x4(v[tris[i]]), b = m.MultiplyPoint3x4(v[tris[i + 1]]), c = m.MultiplyPoint3x4(v[tris[i + 2]]);
				var tb = new Bounds(a, Vector3.zero);
				tb.Encapsulate(b);
				tb.Encapsulate(c);
				if (!tb.Intersects(region))
				{
					continue;
				}
				// Unity's winding is clockwise from the front: the face normal is (b - a) x (c - a) reversed
				// in Minecraft's mirrored space, which AddTri sorts out from the Unity normal.
				Vector3 n = Vector3.Cross(b - a, c - a);
				if (n.sqrMagnitude < 1e-12f)
				{
					continue;
				}
				AddTri(a, b, c, n, flags, job);
			}
		}

		/// <summary>
		/// A mesh collider whose mesh can't be read: its walkable surfaces as a height field, ray cast
		/// straight down onto it on a quarter-block grid (stairs and ramps, which is what REPO's mesh
		/// colliders mostly are), and its sides from horizontal rays as voxels.
		/// </summary>
		private static void Probe(MeshCollider mc, uint flags, CollisionJob job, Bounds region)
		{
			var b = mc.bounds;
			if (!b.Intersects(region))
			{
				return;
			}
			var area = new Bounds();
			area.SetMinMax(Vector3.Max(b.min, region.min), Vector3.Min(b.max, region.max));
			float step = 0.25f / Coords.K;
			int nx = Mathf.Clamp(Mathf.CeilToInt(area.size.x / step) + 1, 1, 64), nz = Mathf.Clamp(Mathf.CeilToInt(area.size.z / step) + 1, 1, 64);
			var h = new float[nx, nz];
			var hit = new bool[nx, nz];
			float top = b.max.y + 0.1f, depth = b.size.y + 0.2f;
			for (int i = 0; i < nx; i++)
			{
				for (int j = 0; j < nz; j++)
				{
					var origin = new Vector3(area.min.x + i * step, top, area.min.z + j * step);
					if (mc.Raycast(new Ray(origin, Vector3.down), out RaycastHit rh, depth))
					{
						h[i, j] = rh.point.y;
						hit[i, j] = true;
					}
				}
			}
			float maxStep = 0.6f / Coords.K;
			for (int i = 0; i + 1 < nx; i++)
			{
				for (int j = 0; j + 1 < nz; j++)
				{
					if (!hit[i, j] || !hit[i + 1, j] || !hit[i, j + 1] || !hit[i + 1, j + 1])
					{
						continue;
					}
					float lo = Mathf.Min(Mathf.Min(h[i, j], h[i + 1, j]), Mathf.Min(h[i, j + 1], h[i + 1, j + 1]));
					float hi = Mathf.Max(Mathf.Max(h[i, j], h[i + 1, j]), Mathf.Max(h[i, j + 1], h[i + 1, j + 1]));
					var p00 = new Vector3(area.min.x + i * step, h[i, j], area.min.z + j * step);
					var p10 = new Vector3(area.min.x + (i + 1) * step, h[i + 1, j], area.min.z + j * step);
					var p01 = new Vector3(area.min.x + i * step, h[i, j + 1], area.min.z + (j + 1) * step);
					var p11 = new Vector3(area.min.x + (i + 1) * step, h[i + 1, j + 1], area.min.z + (j + 1) * step);
					if (hi - lo > maxStep)
					{
						// A step: a flat top at the highest corner's height, and the riser as a wall.
						float y = hi;
						p00.y = p10.y = p01.y = p11.y = y;
						Vector3 c = (p00 + p11) * 0.5f;
						AddObb(new Vector3(c.x, (y + lo) * 0.5f, c.z), Vector3.right, Vector3.up, Vector3.forward,
							new Vector3(step * 0.5f, (y - lo) * 0.5f, step * 0.5f), flags & ~Proto.TriTerrain, job);
						continue;
					}
					AddTri(p00, p10, p11, Vector3.up, flags & ~Proto.TriTerrain, job);
					AddTri(p00, p11, p01, Vector3.up, flags & ~Proto.TriTerrain, job);
				}
			}
		}

		private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
	}

	/// <summary>Marks GameObjects RepoCraft made (Minecraft's own block colliders): never sent back to Minecraft as REPO geometry.</summary>
	internal sealed class RepoCraftOwned : MonoBehaviour
	{
	}
}
