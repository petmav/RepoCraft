using System.Collections.Generic;
using UnityEngine;

namespace RepoCraft.Render
{
	/// <summary>
	/// Minecraft arrows that hit a REPO enemy stay stuck in it where they hit, pinned to the nearest
	/// part of its body so they follow its animation. Minecraft itself removes arrows that hit a mob.
	/// </summary>
	internal sealed unsafe class StuckArrows
	{
		public static readonly StuckArrows Instance = new StuckArrows();

		private const int Max = 64;

		private struct Stuck
		{
			public Transform Bone;
			public Vector3 LocalPos, LocalDir;
		}

		private readonly LinkedList<Stuck> arrows = new LinkedList<Stuck>();

		/// <summary>An arrow stuck in <paramref name="body"/> at hit (MC coords), flying at yaw/pitch (MC degrees).</summary>
		public void Stick(Transform body, Vector3 hitMc, float yawDeg, float pitchDeg)
		{
			if (body == null)
			{
				return;
			}
			Vector3 at = Coords.ToUnity(hitMc);
			float yr = yawDeg * Mathf.Deg2Rad, pr = pitchDeg * Mathf.Deg2Rad;
			var dirMc = new Vector3(Mathf.Sin(yr) * Mathf.Cos(pr), Mathf.Sin(pr), Mathf.Cos(yr) * Mathf.Cos(pr));
			Vector3 dir = Coords.DirToUnity(dirMc);
			// The nearest transform of the body (its renderers' bones or pieces).
			Transform best = body;
			float bestD = float.MaxValue;
			foreach (var t in body.GetComponentsInChildren<Transform>())
			{
				float d = (t.position - at).sqrMagnitude;
				if (d < bestD)
				{
					bestD = d;
					best = t;
				}
			}
			arrows.AddLast(new Stuck { Bone = best, LocalPos = best.InverseTransformPoint(at), LocalDir = best.InverseTransformDirection(dir) });
			while (arrows.Count > Max)
			{
				arrows.RemoveFirst();
			}
		}

		public void Clear() => arrows.Clear();

		public void Build(EntityRenderer r)
		{
			if (!r.HaveArrowUv)
			{
				return;
			}
			fixed (float* uv = r.LastArrowUv)
			{
				for (var node = arrows.First; node != null;)
				{
					var next = node.Next;
					var s = node.Value;
					if (s.Bone == null || !s.Bone.gameObject.activeInHierarchy)
					{
						arrows.Remove(node);
						node = next;
						continue;
					}
					Vector3 p = Coords.ToMcF(s.Bone.TransformPoint(s.LocalPos));
					Vector3 d = Coords.DirToMc(s.Bone.TransformDirection(s.LocalDir)).normalized;
					r.Arrow(p, d, uv, uv + 4, false);
					node = next;
				}
			}
		}
	}

	/// <summary>
	/// The Minecraft player's body, in parts, kept up to date while alive (kRenRagdoll) so that when
	/// the player dies the body can fall as a ragdoll where they stood.
	/// </summary>
	internal sealed unsafe class Ragdoll
	{
		public static readonly Ragdoll Instance = new Ragdoll();

		private byte[] last;
		private uint lastBytes;
		private readonly List<GameObject> parts = new List<GameObject>();

		public void OnRagdoll(byte* data, uint bytes)
		{
			if (last == null || last.Length < bytes)
			{
				last = new byte[bytes];
			}
			for (uint i = 0; i < bytes; i++)
			{
				last[i] = data[i];
			}
			lastBytes = bytes;
		}

		/// <summary>The player died at feet (Unity), facing yaw (MC degrees): drop the body's parts.</summary>
		public void Drop(Vector3 feet, float mcYaw, Vector3 push)
		{
			Clear();
			if (last == null || lastBytes < 8)
			{
				return;
			}
			fixed (byte* data = last)
			{
				uint batches = *(uint*)data, verts = *(uint*)(data + 4);
				if (lastBytes < 8 + batches * (ulong)Link.Proto.RenBatchBytes + verts * (ulong)Link.Proto.RenVertexBytes)
				{
					return;
				}
				byte* b0 = data + 8;
				byte* v0 = b0 + batches * Link.Proto.RenBatchBytes;
				// One rigid body per part (RenBatch flags bits 8-11), all batches of a part together.
				var byPart = new Dictionary<uint, List<(int first, int count, uint tex, uint flags)>>();
				for (uint i = 0; i < batches; i++)
				{
					byte* b = b0 + i * Link.Proto.RenBatchBytes;
					uint tex = *(uint*)b, first = *(uint*)(b + 4), count = *(uint*)(b + 8), flags = *(uint*)(b + 12);
					uint part = (flags >> 8) & 0xF;
					if (!byPart.TryGetValue(part, out var list))
					{
						byPart[part] = list = new List<(int, int, uint, uint)>();
					}
					list.Add(((int)first, (int)count, tex, flags));
				}
				var facing = Quaternion.Euler(0, Coords.YawToUnity(mcYaw) + 180f, 0);
				foreach (var kv in byPart)
				{
					var go = new GameObject($"Minecraft body part {kv.Key}");
					go.transform.position = feet;
					go.transform.rotation = facing;
					var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
					var ranges = new List<(int, int)>();
					var mats = new List<Material>();
					foreach (var r in kv.Value)
					{
						var tex = BlockRenderer.Instance.TextureFor(r.tex);
						if (tex == null)
						{
							continue;
						}
						ranges.Add((r.first, r.count));
						mats.Add(Materials.Get((r.flags & 1) != 0 ? Materials.Kind.Translucent : Materials.Kind.Cutout, tex));
					}
					if (ranges.Count == 0)
					{
						Object.Destroy(go);
						continue;
					}
					MeshBuilder.FillBatches(mesh, v0, (int)verts, ranges);
					go.AddComponent<MeshFilter>().sharedMesh = mesh;
					go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
					var col = go.AddComponent<BoxCollider>();
					col.center = mesh.bounds.center;
					col.size = Vector3.Max(mesh.bounds.size, Vector3.one * 0.05f);
					var rb = go.AddComponent<Rigidbody>();
					rb.mass = 2f;
					rb.velocity = push + Random.insideUnitSphere * 0.5f;
					rb.angularVelocity = Random.insideUnitSphere * 3f;
					parts.Add(go);
				}
			}
		}

		public void Clear()
		{
			foreach (var p in parts)
			{
				if (p != null)
				{
					var mf = p.GetComponent<MeshFilter>();
					if (mf != null && mf.sharedMesh != null)
					{
						Object.Destroy(mf.sharedMesh);
					}
					Object.Destroy(p);
				}
			}
			parts.Clear();
		}
	}
}
