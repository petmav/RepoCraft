using System.Collections.Generic;
using RepoCraft.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace RepoCraft.Render
{
	/// <summary>
	/// RenVertex triangle lists (Minecraft space) -> Unity meshes. Positions mirror Z and scale by
	/// the block size, so every triangle's winding is reversed to stay front-facing.
	/// RenVertex: float x, y, z, u, v; u32 colour (RGBA8, r low); u32 light; u32 flags
	/// (bit0 cutout, bit1 translucent, bits 4-6 face normal as MC Direction ordinal + 1).
	/// </summary>
	internal static unsafe class MeshBuilder
	{
		// Minecraft's Direction order: DOWN, UP, NORTH, SOUTH, WEST, EAST; in Unity axes (Z mirrored).
		// No normal (plants, cross quads) is lit as if facing up, like Minecraft shades them.
		// 7: lit by the triangle's own normal (entities), filled in per triangle (zero until then).
		private static readonly Vector3[] Normals =
		{
			Vector3.up, Vector3.down, Vector3.up, new Vector3(0, 0, 1), new Vector3(0, 0, -1), Vector3.left, Vector3.right, Vector3.zero,
		};

		/// <summary>Triangle (a, b, c) as emitted (Unity winding): give vertices still without a normal the face's.</summary>
		private static void FaceNormal(int a, int b, int c)
		{
			if (nrm[a] != Vector3.zero && nrm[b] != Vector3.zero && nrm[c] != Vector3.zero)
			{
				return;
			}
			Vector3 n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
			n = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
			if (nrm[a] == Vector3.zero) nrm[a] = n;
			if (nrm[b] == Vector3.zero) nrm[b] = n;
			if (nrm[c] == Vector3.zero) nrm[c] = n;
		}

		[System.ThreadStatic] private static List<Vector3> pos;
		[System.ThreadStatic] private static List<Vector3> nrm;
		[System.ThreadStatic] private static List<Vector2> uv;
		[System.ThreadStatic] private static List<Color32> col;
		[System.ThreadStatic] private static List<int>[] subs;

		private static void Begin(int capacity)
		{
			pos ??= new List<Vector3>(capacity);
			nrm ??= new List<Vector3>(capacity);
			uv ??= new List<Vector2>(capacity);
			col ??= new List<Color32>(capacity);
			subs ??= new[] { new List<int>(), new List<int>(), new List<int>() };
			pos.Clear();
			nrm.Clear();
			uv.Clear();
			col.Clear();
			foreach (var s in subs)
			{
				s.Clear();
			}
		}

		private static void AddVertex(byte* v, float k)
		{
			float x = *(float*)v, y = *(float*)(v + 4), z = *(float*)(v + 8);
			pos.Add(new Vector3(x / k, y / k, -z / k));
			uv.Add(new Vector2(*(float*)(v + 12), *(float*)(v + 16)));
			uint c = *(uint*)(v + 20);
			col.Add(new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)));
			uint flags = *(uint*)(v + 28);
			nrm.Add(Normals[(flags >> 4) & 7]);
		}

		/// <summary>A block section: submesh 0 opaque, 1 cutout, 2 translucent.</summary>
		public static Mesh FromVertices(byte* data, int count, Mesh into)
		{
			count -= count % 3;
			if (count <= 0)
			{
				return null;
			}
			Begin(count);
			float k = Coords.K;
			for (int i = 0; i < count; i += 3)
			{
				byte* v0 = data + i * Proto.RenVertexBytes;
				uint flags = *(uint*)(v0 + 28);
				int sub = (flags & 2) != 0 ? 2 : (flags & 1) != 0 ? 1 : 0;
				int b = pos.Count;
				AddVertex(v0, k);
				AddVertex(v0 + Proto.RenVertexBytes, k);
				AddVertex(v0 + 2 * Proto.RenVertexBytes, k);
				var list = subs[sub];
				list.Add(b);
				list.Add(b + 2);
				list.Add(b + 1);
				FaceNormal(b, b + 2, b + 1);
			}
			Mesh mesh = into ?? new Mesh();
			mesh.Clear();
			mesh.indexFormat = pos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			mesh.SetVertices(pos);
			mesh.SetNormals(nrm);
			mesh.SetUVs(0, uv);
			mesh.SetColors(col);
			mesh.subMeshCount = 3;
			for (int s = 0; s < 3; s++)
			{
				mesh.SetTriangles(subs[s], s, false);
			}
			mesh.RecalculateBounds();
			return mesh;
		}

		/// <summary>Entity batches: one submesh per (first, count) vertex range.</summary>
		public static void FillBatches(Mesh mesh, byte* verts, int vertexCount, List<(int first, int count)> ranges)
		{
			Begin(vertexCount);
			float k = Coords.K;
			for (int i = 0; i < vertexCount; i++)
			{
				AddVertex(verts + i * Proto.RenVertexBytes, k);
			}
			foreach (var (first, count) in ranges)
			{
				for (int i = first; i + 2 < first + count; i += 3)
				{
					FaceNormal(i, i + 2, i + 1);
				}
			}
			for (int i = 0; i < nrm.Count; i++)
			{
				if (nrm[i] == Vector3.zero)
				{
					nrm[i] = Vector3.up;
				}
			}
			mesh.Clear();
			mesh.SetVertices(pos);
			mesh.SetNormals(nrm);
			mesh.SetUVs(0, uv);
			mesh.SetColors(col);
			mesh.subMeshCount = ranges.Count;
			var tris = new List<int>();
			for (int s = 0; s < ranges.Count; s++)
			{
				tris.Clear();
				var (first, count) = ranges[s];
				count -= count % 3;
				for (int i = first; i < first + count; i += 3)
				{
					tris.Add(i);
					tris.Add(i + 2);
					tris.Add(i + 1);
				}
				mesh.SetTriangles(tris, s, false);
			}
			mesh.RecalculateBounds();
		}
	}
}
