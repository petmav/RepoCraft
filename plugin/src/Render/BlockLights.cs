using System.Collections.Generic;
using RepoCraft.Link;
using UnityEngine;

namespace RepoCraft.Render
{
	/// <summary>
	/// Minecraft's light-emitting blocks (torches, lanterns, lava, glowstone...) light REPO's level:
	/// a pool of Unity point lights goes to the brightest emitters near the player (merged per small
	/// cell so a wall of torches doesn't take every light). Also answers "what does this block do to
	/// whoever stands in it" (lava, fire, magma) for enemies.
	/// </summary>
	internal sealed unsafe class BlockLights
	{
		public static readonly BlockLights Instance = new BlockLights();

		private const int CellBlocks = 3;
		private const int MaxLights = 16;
		private const float RangeBlocks = 48f;
		private const float UpdateSeconds = 0.25f;

		private struct Emitter
		{
			public int X, Y, Z;
			public byte Level;
			public uint Color; // RGB8, top byte: LightKind bits 0-3, BlockHazard bits 4-7
		}

		private readonly Dictionary<long, Emitter[]> sections = new Dictionary<long, Emitter[]>();
		private readonly Dictionary<long, byte> hazards = new Dictionary<long, byte>();
		private readonly Light[] pool = new Light[MaxLights];
		private readonly float[] baseIntensity = new float[MaxLights];
		private readonly uint[] kind = new uint[MaxLights];
		private float timer;
		private float clock;

		public int Emitters
		{
			get
			{
				int n = 0;
				foreach (var l in sections.Values)
				{
					n += l.Length;
				}
				return n;
			}
		}

		public int LightsOn
		{
			get
			{
				int n = 0;
				foreach (var l in pool)
				{
					if (l != null && l.enabled)
					{
						n++;
					}
				}
				return n;
			}
		}

		public void OnLights(byte* data, uint bytes)
		{
			int sx = *(int*)data, sy = *(int*)(data + 4), sz = *(int*)(data + 8);
			uint count = *(uint*)(data + 12);
			long key = World.Clip.Key(sx, sy, sz);
			if (sections.TryGetValue(key, out Emitter[] old))
			{
				foreach (var e in old)
				{
					hazards.Remove(World.Clip.Key(e.X, e.Y, e.Z));
				}
			}
			if (count == 0 || bytes < 16 + count * 8)
			{
				sections.Remove(key);
				timer = 0;
				return;
			}
			var list = new Emitter[count];
			for (int i = 0; i < count; i++)
			{
				byte* l = data + 16 + i * 8;
				list[i] = new Emitter { X = sx * 16 + l[0], Y = sy * 16 + l[1], Z = sz * 16 + l[2], Level = l[3], Color = *(uint*)(l + 4) };
				byte hazard = (byte)((list[i].Color >> 28) & 0xF);
				if (hazard != Proto.HazardNone)
				{
					hazards[World.Clip.Key(list[i].X, list[i].Y, list[i].Z)] = hazard;
				}
			}
			sections[key] = list;
			timer = 0;
		}

		public void Clear()
		{
			sections.Clear();
			hazards.Clear();
			foreach (var l in pool)
			{
				if (l != null)
				{
					l.enabled = false;
				}
			}
		}

		/// <summary>Proto.Hazard* of the Minecraft block at (x, y, z).</summary>
		public byte HazardAt(int x, int y, int z) => hazards.TryGetValue(World.Clip.Key(x, y, z), out byte h) ? h : (byte)Proto.HazardNone;

		/// <summary>Main thread, every frame. player: Minecraft coords, or null to turn every light off.</summary>
		public void Update(Vector3? player, float dt)
		{
			clock += dt;
			if (player == null)
			{
				foreach (var l in pool)
				{
					if (l != null)
					{
						l.enabled = false;
					}
				}
				return;
			}
			Flicker();
			timer -= dt;
			if (timer > 0)
			{
				return;
			}
			timer = UpdateSeconds;
			Vector3 p = player.Value;

			// Merge emitters per cell: the brightest one's colour, the cell's centre of light.
			var cells = new Dictionary<long, (Vector3 sum, float weight, int level, uint color)>();
			float range2 = RangeBlocks * RangeBlocks;
			foreach (var list in sections.Values)
			{
				foreach (var e in list)
				{
					var c = new Vector3(e.X + 0.5f, e.Y + 0.5f, e.Z + 0.5f);
					if ((c - p).sqrMagnitude > range2 || e.Level < 6)
					{
						continue;
					}
					long key = World.Clip.Key(Mathf.FloorToInt(c.x / CellBlocks), Mathf.FloorToInt(c.y / CellBlocks), Mathf.FloorToInt(c.z / CellBlocks));
					cells.TryGetValue(key, out var acc);
					acc.sum += c * e.Level;
					acc.weight += e.Level;
					if (e.Level > acc.level)
					{
						acc.level = e.Level;
						acc.color = e.Color;
					}
					cells[key] = acc;
				}
			}
			var chosen = new List<(Vector3 pos, int level, uint color, float score)>();
			foreach (var c in cells.Values)
			{
				Vector3 at = c.sum / c.weight;
				chosen.Add((at, c.level, c.color, (at - p).magnitude - c.level));
			}
			chosen.Sort((a, b) => a.score.CompareTo(b.score));
			for (int i = 0; i < MaxLights; i++)
			{
				if (i >= chosen.Count)
				{
					if (pool[i] != null)
					{
						pool[i].enabled = false;
					}
					continue;
				}
				var c = chosen[i];
				Light light = pool[i] ??= NewLight(i);
				light.transform.position = Coords.ToUnity(c.pos);
				light.color = new Color32((byte)c.color, (byte)(c.color >> 8), (byte)(c.color >> 16), 255);
				// Minecraft light falls off one level per block: a torch (14) reaches about 13 blocks.
				light.range = c.level * 0.9f / Coords.K;
				baseIntensity[i] = 0.6f + c.level / 15f * 1.4f;
				kind[i] = (c.color >> 24) & 0xF;
				light.intensity = baseIntensity[i];
				light.enabled = true;
			}
		}

		private Light NewLight(int i)
		{
			var go = new GameObject($"Minecraft light {i}");
			Object.DontDestroyOnLoad(go);
			var l = go.AddComponent<Light>();
			l.type = LightType.Point;
			l.shadows = i < 2 ? LightShadows.Soft : LightShadows.None;
			l.renderMode = LightRenderMode.ForcePixel;
			return l;
		}

		/// <summary>Flames flicker, lava glows slowly, steady lights stay put.</summary>
		private void Flicker()
		{
			for (int i = 0; i < MaxLights; i++)
			{
				var l = pool[i];
				if (l == null || !l.enabled)
				{
					continue;
				}
				float f = 1f;
				if (kind[i] == Proto.LightFlame)
				{
					f = 0.88f + 0.12f * Mathf.PerlinNoise(clock * 6f, i * 3.1f);
				}
				else if (kind[i] == Proto.LightLava)
				{
					f = 0.85f + 0.15f * Mathf.Sin(clock * 1.3f + i);
				}
				l.intensity = baseIntensity[i] * f;
			}
		}
	}
}
