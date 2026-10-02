using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RepoCraft.Render
{
	/// <summary>
	/// Materials for Minecraft's geometry, built from shaders REPO itself ships (there is no Unity
	/// editor in the loop to build our own). Preferred: Particles/Standard Surface, which is lit by
	/// REPO's lights (flashlight, lamps) and multiplies in the vertex colour Minecraft sends (biome
	/// tint, ambient occlusion). Fallbacks are tried in order and the choice is logged.
	/// </summary>
	internal static class Materials
	{
		public enum Kind { Opaque, Cutout, Translucent, Unlit, Overlay }

		private static readonly Dictionary<(Kind, Texture), Material> Cache = new Dictionary<(Kind, Texture), Material>();
		private static Shader lit, litCutout, unlit, premultiplied, colored;
		private static bool picked;
		public static bool LitHasVertexColor { get; private set; }

		private static Shader Find(params string[] names)
		{
			foreach (string n in names)
			{
				Shader s = Shader.Find(n);
				if (s != null && s.isSupported)
				{
					return s;
				}
			}
			return null;
		}

		public static void Pick()
		{
			if (picked)
			{
				return;
			}
			picked = true;
			lit = Find("Particles/Standard Surface");
			LitHasVertexColor = lit != null;
			lit ??= Find("Standard", "Legacy Shaders/Diffuse", "Legacy Shaders/VertexLit", "Mobile/Diffuse");
			litCutout = lit != null && LitHasVertexColor ? lit : Find("Legacy Shaders/Transparent/Cutout/Diffuse", "Unlit/Transparent Cutout");
			unlit = Find("Particles/Standard Unlit", "Sprites/Default", "Unlit/Transparent", "UI/Default");
			premultiplied = Find("Legacy Shaders/Particles/Alpha Blended Premultiply", "UI/Default", "Sprites/Default");
			colored = Find("Hidden/Internal-Colored", "Sprites/Default", "UI/Default");
			lit ??= unlit;
			litCutout ??= unlit;
			Log.Info($"shaders: lit {Name(lit)} (vertex colour {(LitHasVertexColor ? "yes" : "no")}), cutout {Name(litCutout)}, unlit {Name(unlit)}, overlay {Name(premultiplied)}, lines {Name(colored)}");
		}

		private static string Name(Shader s) => s != null ? s.name : "(none)";

		public static Material Get(Kind kind, Texture texture)
		{
			Pick();
			if (Cache.TryGetValue((kind, texture), out Material m) && m != null)
			{
				return m;
			}
			m = Create(kind, texture);
			Cache[(kind, texture)] = m;
			return m;
		}

		/// <summary>Untextured vertex-coloured lines and quads (block outline).</summary>
		public static Material Lines()
		{
			Pick();
			if (Cache.TryGetValue((Kind.Unlit, null), out Material m) && m != null)
			{
				return m;
			}
			m = new Material(colored) { name = "RepoCraft lines", hideFlags = HideFlags.DontSave };
			if (m.HasProperty("_ZWrite"))
			{
				m.SetInt("_ZWrite", 0);
			}
			if (m.HasProperty("_Cull"))
			{
				m.SetInt("_Cull", (int)CullMode.Off);
			}
			if (m.HasProperty("_ZTest"))
			{
				m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
			}
			m.renderQueue = 3100;
			Cache[(Kind.Unlit, null)] = m;
			return m;
		}

		private static Material Create(Kind kind, Texture texture)
		{
			Shader shader = kind switch
			{
				Kind.Opaque => lit,
				Kind.Cutout => litCutout,
				Kind.Translucent => lit,
				Kind.Overlay => premultiplied,
				_ => unlit,
			};
			var m = new Material(shader) { name = $"RepoCraft {kind}", hideFlags = HideFlags.DontSave, mainTexture = texture };
			if (m.HasProperty("_Glossiness"))
			{
				m.SetFloat("_Glossiness", 0f);
			}
			if (m.HasProperty("_Metallic"))
			{
				m.SetFloat("_Metallic", 0f);
			}
			if (m.HasProperty("_Color"))
			{
				m.SetColor("_Color", Color.white);
			}
			switch (kind)
			{
				case Kind.Opaque:
					SetMode(m, 0);
					break;
				case Kind.Cutout:
					SetMode(m, 1);
					if (m.HasProperty("_Cutoff"))
					{
						m.SetFloat("_Cutoff", 0.1f);
					}
					break;
				case Kind.Translucent:
				case Kind.Unlit:
					SetMode(m, 2);
					break;
				case Kind.Overlay:
					m.renderQueue = 4000;
					break;
			}
			return m;
		}

		/// <summary>Standard / Particles blend modes, as Unity's own shader GUIs set them up.</summary>
		private static void SetMode(Material m, int mode)
		{
			if (m.HasProperty("_Mode"))
			{
				m.SetFloat("_Mode", mode);
			}
			if (m.HasProperty("_LightingEnabled"))
			{
				m.SetFloat("_LightingEnabled", 1f);
			}
			m.DisableKeyword("_ALPHATEST_ON");
			m.DisableKeyword("_ALPHABLEND_ON");
			m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			switch (mode)
			{
				case 0: // opaque
					SetBlend(m, BlendMode.One, BlendMode.Zero, true);
					m.renderQueue = (int)RenderQueue.Geometry;
					m.SetOverrideTag("RenderType", "Opaque");
					break;
				case 1: // cutout
					SetBlend(m, BlendMode.One, BlendMode.Zero, true);
					m.EnableKeyword("_ALPHATEST_ON");
					m.renderQueue = (int)RenderQueue.AlphaTest;
					m.SetOverrideTag("RenderType", "TransparentCutout");
					break;
				default: // fade (straight alpha)
					SetBlend(m, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false);
					m.EnableKeyword("_ALPHABLEND_ON");
					m.renderQueue = (int)RenderQueue.Transparent;
					m.SetOverrideTag("RenderType", "Transparent");
					break;
			}
			if (m.HasProperty("_Cull"))
			{
				m.SetFloat("_Cull", (float)CullMode.Back);
			}
		}

		private static void SetBlend(Material m, BlendMode src, BlendMode dst, bool zwrite)
		{
			if (m.HasProperty("_SrcBlend"))
			{
				m.SetFloat("_SrcBlend", (float)src);
			}
			if (m.HasProperty("_DstBlend"))
			{
				m.SetFloat("_DstBlend", (float)dst);
			}
			if (m.HasProperty("_ZWrite"))
			{
				m.SetFloat("_ZWrite", zwrite ? 1f : 0f);
			}
		}

		public static void Forget(Texture texture)
		{
			var stale = new List<(Kind, Texture)>();
			foreach (var key in Cache.Keys)
			{
				if (key.Item2 == texture)
				{
					stale.Add(key);
				}
			}
			foreach (var key in stale)
			{
				Object.Destroy(Cache[key]);
				Cache.Remove(key);
			}
		}
	}
}
