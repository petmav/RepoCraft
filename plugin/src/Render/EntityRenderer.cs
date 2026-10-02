using System.Collections.Generic;
using RepoCraft.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace RepoCraft.Render
{
	/// <summary>
	/// Minecraft things REPO draws each frame from the world-entity table: flying and stuck arrows,
	/// tridents, dropped items (sprites) and blocks (spinning cubes), block-breaking cracks, and the
	/// targeted block's outline. Geometry as SkyCraft's WorldRender builds it, in Minecraft space,
	/// then mirrored into Unity's. Quads are emitted double-sided (items are seen from both sides).
	/// </summary>
	internal sealed unsafe class EntityRenderer
	{
		public static readonly EntityRenderer Instance = new EntityRenderer();

		private const float ArrowScale = 0.55f;
		private readonly byte[] table = new byte[(int)(Proto.WeRecords + Proto.WorldEntityBytes * Proto.MaxWorldEntities)];
		private GameObject solidGo, crackGo, outlineGo;
		private Mesh solidMesh, crackMesh, outlineMesh;
		private MeshRenderer solidMr, crackMr, outlineMr;
		private readonly List<Vector3> pos = new List<Vector3>();
		private readonly List<Vector2> uvs = new List<Vector2>();
		private readonly List<Color32> cols = new List<Color32>();
		private readonly List<Vector3> nrms = new List<Vector3>();
		private readonly List<int> tris = new List<int>();
		private readonly List<Vector3> cpos = new List<Vector3>();
		private readonly List<Vector2> cuvs = new List<Vector2>();
		private readonly List<Color32> ccols = new List<Color32>();
		private readonly List<Vector3> cnrms = new List<Vector3>();
		private readonly List<int> ctris = new List<int>();
		private bool toCracks;

		/// <summary>Where Minecraft's stuck-arrow look comes from (the last flying arrow it showed).</summary>
		public float[] LastArrowUv { get; } = new float[8];
		public bool HaveArrowUv { get; private set; }

		/// <summary>Contact shadows Minecraft asked for this frame (feet positions, MC coords; w = width).</summary>
		public readonly List<Vector4> Shadows = new List<Vector4>();

		private void Ensure(Transform parent)
		{
			if (solidGo != null)
			{
				return;
			}
			(solidGo, solidMesh, solidMr) = Make("Minecraft items", parent);
			(crackGo, crackMesh, crackMr) = Make("Minecraft cracks", parent);
			(outlineGo, outlineMesh, outlineMr) = Make("Minecraft outline", parent);
			outlineMr.shadowCastingMode = ShadowCastingMode.Off;
			crackMr.shadowCastingMode = ShadowCastingMode.Off;
		}

		private static (GameObject, Mesh, MeshRenderer) Make(string name, Transform parent)
		{
			var go = new GameObject(name);
			go.transform.SetParent(parent, false);
			var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
			mesh.MarkDynamic();
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.lightProbeUsage = LightProbeUsage.Off;
			mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
			mr.enabled = false;
			return (go, mesh, mr);
		}

		/// <summary>Main thread, every frame.</summary>
		public void Frame(Transform parent, Texture atlas, bool show)
		{
			Ensure(parent);
			Shadows.Clear();
			int count = show ? SharedLink.Instance.ReadWorldEntities(table) : -1;
			if (count < 0 || atlas == null)
			{
				if (!show || atlas == null)
				{
					solidMr.enabled = crackMr.enabled = outlineMr.enabled = false;
				}
				return; // torn read: keep last frame's
			}
			pos.Clear(); uvs.Clear(); cols.Clear(); nrms.Clear(); tris.Clear();
			cpos.Clear(); cuvs.Clear(); ccols.Clear(); cnrms.Clear(); ctris.Clear();

			fixed (byte* t = table)
			{
				for (int i = 0; i < count; i++)
				{
					byte* e = t + Proto.WeRecords + i * Proto.WorldEntityBytes;
					uint kind = *(uint*)e;
					float x = *(float*)(e + 8), y = *(float*)(e + 12), z = *(float*)(e + 16);
					float yaw = *(float*)(e + 20), pitch = *(float*)(e + 24), scale = *(float*)(e + 28);
					float* ext = (float*)(e + 32);
					float* uv = (float*)(e + 44);
					uint tint = *(uint*)(e + 92);
					switch (kind)
					{
						case Proto.WeShadow:
							Shadows.Add(new Vector4(x, y, z, scale));
							break;
						case Proto.WeBlock:
						{
							float s = scale;
							Box(x - s * 0.5f, y - s * 0.5f, z - s * 0.5f, s, s, s, yaw * Mathf.Deg2Rad, uv, uv + 4, uv + 8, tint, true);
							break;
						}
						case Proto.WeCrack:
							toCracks = true;
							Box(x, y, z, ext[0], ext[1], ext[2], 0f, uv, uv, uv, 0, false);
							toCracks = false;
							break;
						case Proto.WeArrow:
						case Proto.WeTrident:
						{
							float yr = yaw * Mathf.Deg2Rad, pr = pitch * Mathf.Deg2Rad;
							var d = new Vector3(Mathf.Sin(yr) * Mathf.Cos(pr), Mathf.Sin(pr), Mathf.Cos(yr) * Mathf.Cos(pr));
							if (kind == Proto.WeArrow)
							{
								for (int k = 0; k < 8; k++)
								{
									LastArrowUv[k] = uv[k];
								}
								HaveArrowUv = true;
							}
							Arrow(new Vector3(x, y, z), d, uv, uv + 4, kind == Proto.WeTrident);
							break;
						}
						case Proto.WeItem:
						{
							float spin = yaw * Mathf.Deg2Rad, half = scale * 0.5f;
							float rx = Mathf.Cos(spin) * half, rz = Mathf.Sin(spin) * half;
							Quad(new Vector3(x - rx, y + half, z - rz), new Vector3(x + rx, y + half, z + rz),
								new Vector3(x + rx, y - half, z + rz), new Vector3(x - rx, y - half, z - rz), uv, White, Vector3.up);
							break;
						}
					}
				}
				StuckArrows.Instance.Build(this);
				Upload(solidMesh, solidMr, pos, uvs, cols, nrms, tris, Materials.Get(Materials.Kind.Cutout, atlas));
				Upload(crackMesh, crackMr, cpos, cuvs, ccols, cnrms, ctris, Materials.Get(Materials.Kind.Translucent, atlas));
				Outline(t);
			}
		}

		private static readonly Color32 White = new Color32(255, 255, 255, 255);

		private static void Upload(Mesh mesh, MeshRenderer mr, List<Vector3> p, List<Vector2> u, List<Color32> c, List<Vector3> n, List<int> t, Material m)
		{
			mesh.Clear();
			if (t.Count == 0)
			{
				mr.enabled = false;
				return;
			}
			mesh.SetVertices(p);
			mesh.SetUVs(0, u);
			mesh.SetColors(c);
			mesh.SetNormals(n);
			mesh.SetTriangles(t, 0, true);
			mr.sharedMaterial = m;
			mr.enabled = true;
		}

		private bool hasSelection;
		private Bounds selection; // Unity space

		/// <summary>Distance (Unity metres) from <paramref name="eye"/> to the block Minecraft has targeted, or +inf.</summary>
		public float SelectionDistance(Vector3 eye)
		{
			if (!hasSelection)
			{
				return float.PositiveInfinity;
			}
			return Vector3.Distance(eye, selection.ClosestPoint(eye));
		}

		private void Outline(byte* t)
		{
			hasSelection = *(uint*)(t + Proto.WeHasSelection) != 0;
			if (!hasSelection)
			{
				outlineMr.enabled = false;
				return;
			}
			float* lo = (float*)(t + Proto.WeSelMin);
			float* hi = (float*)(t + Proto.WeSelMax);
			var a = Coords.ToUnity(lo[0], lo[1], lo[2]);
			var b = Coords.ToUnity(hi[0], hi[1], hi[2]);
			selection = new Bounds((a + b) * 0.5f, new Vector3(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
			const float g = 0.002f;
			var corners = new Vector3[8];
			for (int i = 0; i < 8; i++)
			{
				var mc = new Vector3((i & 1) != 0 ? hi[0] + g : lo[0] - g, (i & 2) != 0 ? hi[1] + g : lo[1] - g, (i & 4) != 0 ? hi[2] + g : lo[2] - g);
				corners[i] = Coords.ToUnity(mc);
			}
			int[] edges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
			var colors = new Color32[8];
			for (int i = 0; i < 8; i++)
			{
				colors[i] = new Color32(0, 0, 0, 115); // black, 45% (Minecraft's outline)
			}
			outlineMesh.Clear();
			outlineMesh.vertices = corners;
			outlineMesh.colors32 = colors;
			outlineMesh.SetIndices(edges, MeshTopology.Lines, 0);
			outlineMr.sharedMaterial = Materials.Lines();
			outlineMr.enabled = true;
		}

		// ---- geometry (Minecraft space in, Unity space out) --------------------------------------

		/// <summary>One quad TL, TR, BR, BL (Minecraft space), double-sided.</summary>
		public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float* uv, Color32 color, Vector3 mcNormal)
		{
			var P = toCracks ? cpos : pos;
			var U = toCracks ? cuvs : uvs;
			var C = toCracks ? ccols : cols;
			var N = toCracks ? cnrms : nrms;
			var T = toCracks ? ctris : tris;
			int i = P.Count;
			P.Add(Coords.ToUnity(a)); P.Add(Coords.ToUnity(b)); P.Add(Coords.ToUnity(c)); P.Add(Coords.ToUnity(d));
			U.Add(new Vector2(uv[0], uv[1])); U.Add(new Vector2(uv[2], uv[1])); U.Add(new Vector2(uv[2], uv[3])); U.Add(new Vector2(uv[0], uv[3]));
			var n = Coords.DirToUnity(mcNormal);
			for (int k = 0; k < 4; k++)
			{
				C.Add(color);
				N.Add(n);
			}
			T.Add(i); T.Add(i + 1); T.Add(i + 2); T.Add(i); T.Add(i + 2); T.Add(i + 3);
			T.Add(i); T.Add(i + 2); T.Add(i + 1); T.Add(i); T.Add(i + 3); T.Add(i + 2);
		}

		private void Box(float minX, float minY, float minZ, float sx, float sy, float sz, float yaw, float* side, float* top, float* bottom, uint topTint, bool shaded)
		{
			float cx = minX + sx * 0.5f, cz = minZ + sz * 0.5f;
			float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
			Vector3 Corner(int i)
			{
				float lx = ((i & 1) != 0 ? 0.5f : -0.5f) * sx, lz = ((i & 4) != 0 ? 0.5f : -0.5f) * sz;
				return new Vector3(cx + lx * c - lz * s, minY + ((i & 2) != 0 ? sy : 0f), cz + lx * s + lz * c);
			}
			int[,] faces = { { 6, 7, 5, 4 }, { 3, 2, 0, 1 }, { 7, 3, 1, 5 }, { 2, 6, 4, 0 }, { 2, 3, 7, 6 }, { 4, 5, 1, 0 } };
			Vector3[] normals = { new Vector3(0, 0, 1), new Vector3(0, 0, -1), Vector3.right, Vector3.left, Vector3.up, Vector3.down };
			for (int f = 0; f < 6; f++)
			{
				float* uv = f == 4 ? top : f == 5 ? bottom : side;
				Color32 color = White;
				if (shaded && f == 4 && topTint != 0)
				{
					color = new Color32((byte)topTint, (byte)(topTint >> 8), (byte)(topTint >> 16), 255);
				}
				var n = new Vector3(normals[f].x * c - normals[f].z * s, normals[f].y, normals[f].x * s + normals[f].z * c);
				Quad(Corner(faces[f, 0]), Corner(faces[f, 1]), Corner(faces[f, 2]), Corner(faces[f, 3]), uv, color, n);
			}
		}

		/// <summary>Minecraft's arrow model (or a trident's icon) at p, flying along d (unit, Minecraft axes).</summary>
		public void Arrow(Vector3 p, Vector3 d, float* uvSide, float* uvBack, bool trident)
		{
			var s = new Vector3(d.z, 0f, -d.x);
			float sl = Mathf.Sqrt(s.x * s.x + s.z * s.z);
			if (sl < 1e-3f)
			{
				s = new Vector3(1, 0, 0);
				sl = 1f;
			}
			s /= sl;
			var u = Vector3.Cross(s, d);
			const float r = 0.70710678f;
			Vector3[] fins = { (u + s) * r, (u - s) * r };
			Vector3 At(float along, Vector3 q, float side, Vector3 q2, float side2) => p + d * along + q * side + q2 * side2;
			if (!trident)
			{
				const float k = 0.9f / 16f * ArrowScale;
				foreach (var q in fins)
				{
					Quad(At(-12 * k, q, -2 * k, Vector3.zero, 0), At(4 * k, q, -2 * k, Vector3.zero, 0), At(4 * k, q, 2 * k, Vector3.zero, 0),
						At(-12 * k, q, 2 * k, Vector3.zero, 0), uvSide, White, Vector3.Cross(q, d));
				}
				Quad(At(-11 * k, fins[0], -2 * k, fins[1], -2 * k), At(-11 * k, fins[0], 2 * k, fins[1], -2 * k), At(-11 * k, fins[0], 2 * k, fins[1], 2 * k),
					At(-11 * k, fins[0], -2 * k, fins[1], 2 * k), uvBack, White, -d);
			}
			else
			{
				const float h = 0.9f;
				foreach (var q in fins)
				{
					Quad(At(0, q, h, Vector3.zero, 0), At(h, q, 0, Vector3.zero, 0), At(0, q, -h, Vector3.zero, 0), At(-h, q, 0, Vector3.zero, 0), uvSide, White,
						Vector3.Cross(q, d));
				}
			}
		}
	}
}
