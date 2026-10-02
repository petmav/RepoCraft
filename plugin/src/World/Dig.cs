using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;

namespace RepoCraft.World
{
	/// <summary>
	/// Digging into REPO's level. Minecraft decides what's dug (DugBlocks); in a dug block REPO's own
	/// geometry is gone:
	///  - its colliders: a cut wall/floor box is switched off and replaced by the boxes left around the
	///    holes (so loot, enemies and other players go through too);
	///  - what it draws: the wall or floor mesh is rebuilt without the dug blocks (MeshCutter);
	///  - under the land (a ground floor), the pit's sides and bottom are Minecraft's solid ground:
	///    REPO gets colliders there so nothing falls out into the void under the level;
	///  - the host rebuilds REPO's navigation mesh a moment later, so enemies path through holes.
	/// The original colliders still go to Minecraft as they were (it clips the dug blocks out itself
	/// and needs the original surfaces to know what's inside).
	/// </summary>
	internal sealed class Dig
	{
		public static readonly Dig Instance = new Dig();

		private sealed class CutCollider
		{
			public BoxCollider Original;
			public GameObject Pieces;
			public Bounds WorldBounds; // a switched-off collider has no bounds of its own
		}

		private readonly Dictionary<BoxCollider, CutCollider> cut = new Dictionary<BoxCollider, CutCollider>();
		private readonly HashSet<(int, int, int)> pending = new HashSet<(int, int, int)>();
		private readonly HashSet<long> groundCells = new HashSet<long>();
		private readonly Dictionary<long, GameObject> groundColliders = new Dictionary<long, GameObject>();
		private GameObject groundRoot;
		private float navTimer = -1f;
		private float meshTimer;
		private readonly Collider[] hits = new Collider[256];

		/// <summary>The cut REPO collider a dug piece stands in for.</summary>
		internal sealed class DugPiece : MonoBehaviour
		{
			public BoxCollider Original;

			public static Collider OriginalOf(Collider c)
			{
				if (c == null)
				{
					return null;
				}
				var piece = c.GetComponentInParent<DugPiece>();
				return piece != null ? piece.Original : c;
			}
		}

		/// <summary>Cut originals (switched off in REPO, which Minecraft must still be sent) overlapping a box.</summary>
		public void CutOriginalsIn(Bounds region, List<BoxCollider> output)
		{
			foreach (var kv in cut)
			{
				if (kv.Key != null && kv.Value.WorldBounds.Intersects(region))
				{
					output.Add(kv.Key);
				}
			}
		}

		public void Clear()
		{
			foreach (var c in cut.Values)
			{
				if (c.Pieces != null)
				{
					UnityEngine.Object.Destroy(c.Pieces);
				}
				if (c.Original != null)
				{
					c.Original.enabled = true;
				}
			}
			cut.Clear();
			pending.Clear();
			foreach (var g in groundColliders.Values)
			{
				if (g != null)
				{
					UnityEngine.Object.Destroy(g);
				}
			}
			groundColliders.Clear();
			groundCells.Clear();
			MeshCutter.Instance.Clear();
		}

		public void Changed(List<(int x, int y, int z)> blocks)
		{
			foreach (var b in blocks)
			{
				pending.Add(b);
			}
		}

		public void Frame(bool active, float dt)
		{
			if (!active)
			{
				return;
			}
			if (pending.Count > 0)
			{
				var batch = new List<(int x, int y, int z)>(pending);
				pending.Clear();
				Physics.SyncTransforms();
				MeshCutter.Instance.Dirty(batch);
				CutColliders(batch);
				UpdateGround(batch);
				navTimer = 1.5f;
			}
			meshTimer -= dt;
			if (meshTimer <= 0f)
			{
				meshTimer = 0.1f;
				MeshCutter.Instance.Work(4);
			}
			if (navTimer > 0f)
			{
				navTimer -= dt;
				if (navTimer <= 0f)
				{
					RebuildNavMesh();
				}
			}
		}

		// ---- colliders -----------------------------------------------------------------------------

		/// <summary>Is this REPO collider part of the level that can be dug (as CollisionExporter decides)?</summary>
		public static bool Diggable(Collider col)
		{
			if (col == null || col.isTrigger || col.attachedRigidbody != null || col.gameObject.layer != 0 || col.GetComponentInParent<RepoCraftOwned>() != null)
			{
				return false;
			}
			var lg = LevelGenerator.Instance;
			if (lg == null || lg.LevelParent == null || !col.transform.IsChildOf(lg.LevelParent.transform))
			{
				return false;
			}
			if (col.GetComponentInParent<StartRoom>() != null || col.GetComponent<InvisibleWall>() != null)
			{
				return false;
			}
			return InLevelGroup(col.transform);
		}

		/// <summary>
		/// The level's own walls, floors and ceilings, not its props. REPO's modules aren't consistent
		/// ("---- Level ----/Walls/...", "Room/Museum Wall 01.../Wall - 1x1 - Curve/..."), but the pieces
		/// are named for what they are, and props sit under a "Props" group.
		/// </summary>
		public static bool InLevelGroup(Transform t)
		{
			bool piece = false;
			for (var p = t; p != null; p = p.parent)
			{
				string n = p.name;
				if (n.IndexOf("Props", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return false;
				}
				if (n.StartsWith("---- Level", StringComparison.Ordinal))
				{
					return true;
				}
				if (n.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Floor", StringComparison.OrdinalIgnoreCase) >= 0
					|| n.IndexOf("Ceiling", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					piece = true;
				}
				if (p.GetComponent<Module>() != null || p.GetComponent<StartRoom>() != null)
				{
					break;
				}
			}
			return piece;
		}

		/// <summary>A floor (or the land): a thin horizontal piece that isn't a ceiling.</summary>
		public static bool IsFloorShape(Collider col, Bounds b)
		{
			if (col.CompareTag("Ceiling"))
			{
				return false;
			}
			return b.size.y < 0.6f && b.size.x > 0.4f && b.size.z > 0.4f;
		}

		private void CutColliders(List<(int x, int y, int z)> blocks)
		{
			var touched = new HashSet<BoxCollider>();
			foreach (var b in blocks)
			{
				Bounds cube = CubeBounds(b.x, b.y, b.z);
				int n = Physics.OverlapBoxNonAlloc(cube.center, cube.extents * 0.98f, hits, Quaternion.identity, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
				for (int i = 0; i < n; i++)
				{
					if (hits[i] is BoxCollider box && Diggable(box))
					{
						touched.Add(box);
					}
				}
				// Already cut: its pieces were hit, not it.
				foreach (var kv in cut)
				{
					if (kv.Key != null && kv.Value.WorldBounds.Intersects(cube))
					{
						touched.Add(kv.Key);
					}
				}
			}
			foreach (var box in touched)
			{
				Recut(box);
			}
		}

		/// <summary>The box minus every dug block it overlaps, as child boxes (in the box's own frame).</summary>
		private void Recut(BoxCollider box)
		{
			var t = box.transform;
			if (!cut.TryGetValue(box, out var state))
			{
				state = new CutCollider { Original = box };
				cut[box] = state;
			}
			if (state.Pieces != null)
			{
				UnityEngine.Object.Destroy(state.Pieces);
				state.Pieces = null;
			}
			Bounds wb = CollisionExporter.WorldBounds(box);
			state.WorldBounds = wb;
			Vector3 lo = Coords.ToMcF(wb.min), hi = Coords.ToMcF(wb.max);
			var cubes = new List<(int x, int y, int z)>();
			DugBlocks.Collect(Mathf.Min(lo.x, hi.x), Mathf.Min(lo.y, hi.y), Mathf.Min(lo.z, hi.z), Mathf.Max(lo.x, hi.x), Mathf.Max(lo.y, hi.y), Mathf.Max(lo.z, hi.z), cubes);
			if (cubes.Count == 0)
			{
				box.enabled = true; // filled back in
				cut.Remove(box);
				return;
			}
			// In the box's local frame: its own box, and each dug block's local bounding box.
			var pieces = new List<Bounds> { new Bounds(box.center, box.size) };
			foreach (var c in cubes)
			{
				Bounds w = CubeBounds(c.x, c.y, c.z);
				Bounds local = new Bounds(t.InverseTransformPoint(w.center), Vector3.zero);
				for (int i = 0; i < 8; i++)
				{
					var corner = new Vector3((i & 1) != 0 ? w.max.x : w.min.x, (i & 2) != 0 ? w.max.y : w.min.y, (i & 4) != 0 ? w.max.z : w.min.z);
					local.Encapsulate(t.InverseTransformPoint(corner));
				}
				pieces = Subtract(pieces, local);
			}
			box.enabled = false;
			var holder = new GameObject("RepoCraft dug pieces") { layer = box.gameObject.layer, tag = box.gameObject.tag };
			holder.transform.SetParent(t, false);
			holder.AddComponent<RepoCraftOwned>();
			holder.AddComponent<DugPiece>().Original = box;
			var surface = box.GetComponent<MaterialSurface>();
			foreach (var p in pieces)
			{
				if (p.size.x < 1e-3f || p.size.y < 1e-3f || p.size.z < 1e-3f)
				{
					continue;
				}
				var go = new GameObject("piece") { layer = box.gameObject.layer, tag = box.gameObject.tag };
				go.transform.SetParent(holder.transform, false);
				var bc = go.AddComponent<BoxCollider>();
				bc.center = p.center;
				bc.size = p.size;
				bc.sharedMaterial = box.sharedMaterial;
				if (surface != null)
				{
					go.AddComponent<MaterialSurface>().Type = surface.Type;
				}
				go.AddComponent<NavMeshModifier>();
			}
			state.Pieces = holder;
		}

		/// <summary>Each box minus the cutter, as up to six boxes per box.</summary>
		private static List<Bounds> Subtract(List<Bounds> boxes, Bounds cutter)
		{
			var output = new List<Bounds>();
			foreach (var b in boxes)
			{
				if (!b.Intersects(cutter) || Overlap(b, cutter) < 1e-6f)
				{
					output.Add(b);
					continue;
				}
				Vector3 min = b.min, max = b.max;
				for (int axis = 0; axis < 3; axis++)
				{
					if (cutter.min[axis] > min[axis])
					{
						var pmax = max;
						pmax[axis] = cutter.min[axis];
						output.Add(MinMax(min, pmax));
						min[axis] = cutter.min[axis];
					}
					if (cutter.max[axis] < max[axis])
					{
						var pmin = min;
						pmin[axis] = cutter.max[axis];
						output.Add(MinMax(pmin, max));
						max[axis] = cutter.max[axis];
					}
				}
				// What's left between min and max is inside the cutter: gone.
			}
			return output;
		}

		private static float Overlap(Bounds a, Bounds b)
		{
			Vector3 lo = Vector3.Max(a.min, b.min), hi = Vector3.Min(a.max, b.max);
			Vector3 d = hi - lo;
			return d.x > 0 && d.y > 0 && d.z > 0 ? d.x * d.y * d.z : 0f;
		}

		private static Bounds MinMax(Vector3 min, Vector3 max)
		{
			var b = new Bounds();
			b.SetMinMax(min, max);
			return b;
		}

		/// <summary>A Minecraft block (min corner) as a Unity box.</summary>
		public static Bounds CubeBounds(int x, int y, int z)
		{
			Vector3 a = Coords.ToUnity(x, y, z), b = Coords.ToUnity(x + 1, y + 1, z + 1);
			var bounds = new Bounds();
			bounds.SetMinMax(Vector3.Min(a, b), Vector3.Max(a, b));
			return bounds;
		}

		// ---- the ground under the land -------------------------------------------------------------

		/// <summary>
		/// Around a dug block under a ground floor, the blocks still in Minecraft's ground are solid in
		/// REPO too (a block-sized collider each), so what falls into the pit stays in it.
		/// </summary>
		private void UpdateGround(List<(int x, int y, int z)> blocks)
		{
			groundRoot ??= MakeGroundRoot();
			var check = new HashSet<(int, int, int)>();
			foreach (var b in blocks)
			{
				check.Add(b);
				check.Add((b.x + 1, b.y, b.z));
				check.Add((b.x - 1, b.y, b.z));
				check.Add((b.x, b.y + 1, b.z));
				check.Add((b.x, b.y - 1, b.z));
				check.Add((b.x, b.y, b.z + 1));
				check.Add((b.x, b.y, b.z - 1));
			}
			foreach (var (x, y, z) in check)
			{
				long key = Clip.Key(x, y, z);
				bool needed = !DugBlocks.IsDug(x, y, z) && UnderLand(x, y, z) && NextToDug(x, y, z);
				if (needed && !groundColliders.ContainsKey(key))
				{
					var go = new GameObject("RepoCraft ground") { layer = 0 };
					go.transform.SetParent(groundRoot.transform, false);
					Bounds cb = CubeBounds(x, y, z);
					go.transform.position = cb.center;
					go.AddComponent<BoxCollider>().size = cb.size;
					go.AddComponent<MaterialSurface>().Type = Materials.Type.Gravel;
					groundColliders[key] = go;
				}
				else if (!needed && groundColliders.TryGetValue(key, out var old))
				{
					if (old != null)
					{
						UnityEngine.Object.Destroy(old);
					}
					groundColliders.Remove(key);
				}
			}
		}

		private static GameObject MakeGroundRoot()
		{
			var g = new GameObject("RepoCraft ground");
			g.AddComponent<RepoCraftOwned>();
			return g; // part of the level scene: goes with it
		}

		private static bool NextToDug(int x, int y, int z) =>
			DugBlocks.IsDug(x + 1, y, z) || DugBlocks.IsDug(x - 1, y, z) || DugBlocks.IsDug(x, y + 1, z) || DugBlocks.IsDug(x, y - 1, z)
			|| DugBlocks.IsDug(x, y, z + 1) || DugBlocks.IsDug(x, y, z - 1);

		/// <summary>The block is below a ground floor (Minecraft's land there: solid ground).</summary>
		private static bool UnderLand(int x, int y, int z)
		{
			Bounds cb = CubeBounds(x, y, z);
			Vector3 p = cb.center;
			foreach (var h in Physics.RaycastAll(p, Vector3.up, 6f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
			{
				Collider c = h.collider;
				c = DugPiece.OriginalOf(c);
				if (c != null && CollisionExporter.Instance.IsLand(c))
				{
					return true;
				}
			}
			foreach (var kv in Instance.cut)
			{
				var fb = kv.Value.WorldBounds;
				if (kv.Key != null && CollisionExporter.Instance.IsLand(kv.Key) && p.x >= fb.min.x && p.x <= fb.max.x && p.z >= fb.min.z && p.z <= fb.max.z
					&& fb.max.y >= cb.max.y - 0.01f && fb.max.y - cb.max.y < 6f)
				{
					return true;
				}
			}
			return false;
		}

		// ---- navigation ----------------------------------------------------------------------------

		private static void RebuildNavMesh()
		{
			if (!Repo.Game.HasAuthority || LevelGenerator.Instance == null)
			{
				return; // enemies think on the host only
			}
			var surface = LevelGenerator.Instance.GetComponent<NavMeshSurface>();
			if (surface == null || surface.navMeshData == null)
			{
				return;
			}
			try
			{
				surface.UpdateNavMesh(surface.navMeshData);
				Log.Debug("navigation mesh rebuilt for dug blocks");
			}
			catch (Exception e)
			{
				Log.Warn($"navigation mesh: {e.Message}");
			}
		}
	}
}
