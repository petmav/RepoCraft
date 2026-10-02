using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RepoCraft.Link;
using RepoCraft.World;

namespace RepoCraft
{
	internal static class Log
	{
		public static void Info(string m) => Console.WriteLine("[info] " + m);

		public static void Warn(string m) => Console.WriteLine("[warn] " + m);

		public static void Error(string m) => Console.WriteLine("[error] " + m);
	}
}

namespace FakeRepo
{
	using RepoCraft;

	/// <summary>
	/// Stand-in for REPO: creates the link, sends a small test level (a floor, a wall, a step), waits
	/// for the Minecraft player to arrive, then walks, jumps, places a block, opens the inventory, and
	/// dumps Minecraft's overlay frames and atlas as BMPs. Run with REPO closed (same link name).
	///   dotnet run --project tools/FakeRepo -- [output dir]
	/// </summary>
	internal static unsafe class Program
	{
		private static readonly Dictionary<uint, int> RenderCounts = new Dictionary<uint, int>();
		private static long renderBytes;
		private static int sections, sectionVertices;
		private static string outDir;

		private static int Main(string[] args)
		{
			if (args.Length > 0 && args[0] == "--guest")
			{
				return Guest.Run(args);
			}
			outDir = args.Length > 0 ? args[0] : "fake-out";
			Directory.CreateDirectory(outDir);
			var link = SharedLink.Instance;
			if (!link.Create())
			{
				return 1;
			}
			var worker = new CollisionWorker();
			worker.Start();
			uint epoch = 1, seq = 12345;
			worker.Reset(epoch);
			var mc = new McState();
			var clock = Stopwatch.StartNew();
			bool linked = false, sentWorld = false, arrived = false;
			double arrivedAt = 0;
			float yaw = 0f, pitch = 0f;
			var script = new List<(double at, string what, Action act)>();
			int frame = 0;
			double lastLog = 0;
			while (clock.Elapsed.TotalSeconds < 150)
			{
				double t = clock.Elapsed.TotalSeconds;
				link.Heartbeat();
				bool alive = link.McAlive() && link.ReadMcState(mc);
				if (alive && !linked)
				{
					linked = true;
					Log.Info($"Minecraft linked (pid {link.McPid()}) at {t:0.0}s");
					link.ResetOverlay();
				}
				link.WriteHostState(new HostState
				{
					Flags = Proto.HostInGame,
					WorldId = 7,
					CollisionEpoch = epoch,
					PosX = 0.5, PosY = 1.0, PosZ = 0.5,
					Yaw = yaw, Pitch = pitch,
					TeleportSeq = seq,
					ViewportW = 1280, ViewportH = 720,
					GameHour = 13f,
				});
				if (alive && !sentWorld)
				{
					sentWorld = true;
					SendWorld(worker, epoch);
				}
				if (alive && !arrived && (mc.Flags & Proto.McInWorld) != 0 && mc.TeleportAck == seq)
				{
					arrived = true;
					arrivedAt = t;
					Log.Info($"Minecraft player arrived at {mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00} (t {t:0.0}s)");
					Plan(script, t, link, v => yaw = v, v => pitch = v);
				}
				for (int i = 0; i < script.Count;)
				{
					if (script[i].at <= t)
					{
						Log.Info($"t {t:0.0}: {script[i].what}   mc=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) ground={(mc.Flags & Proto.McOnGround) != 0}");
						script[i].act();
						script.RemoveAt(i);
					}
					else
					{
						i++;
					}
				}
				link.DrainRender(OnRender, 48L << 20);
				while (link.PopEvent(out McEvent ev))
				{
					Log.Info($"event {ev.Type} id {ev.FormId} a {ev.A}");
				}
				if (link.AcquireOverlayFrame() && frame++ % 120 == 0)
				{
					DumpOverlay(link, Path.Combine(outDir, $"overlay-{frame:0000}.bmp"));
				}
				if (t - lastLog > 5)
				{
					lastLog = t;
					Log.Info($"t {t:0}s alive={alive} flags={mc.Flags:X} ack={mc.TeleportAck}/{seq} pos=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) sections={sections} render={Fmt(RenderCounts)} ({renderBytes >> 10} KB) collision pending={worker.Pending}");
				}
				if (arrived && t - arrivedAt > 25 && script.Count == 0)
				{
					break;
				}
				Thread.Sleep(16);
			}
			DumpOverlay(link, Path.Combine(outDir, "overlay-final.bmp"));
			Log.Info($"done: arrived={arrived} final pos=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) sections={sections} render={Fmt(RenderCounts)}");
			worker.Stop();
			return arrived ? 0 : 2;
		}

		private static string Fmt(Dictionary<uint, int> d)
		{
			var parts = new List<string>();
			foreach (var kv in d)
			{
				parts.Add($"{kv.Key}:{kv.Value}");
			}
			return string.Join(",", parts);
		}

		/// <summary>The test level: a 40x40 floor (top at y 1), a stone wall east of the start, and a step south.</summary>
		private static void SendWorld(CollisionWorker worker, uint epoch)
		{
			for (int rx = -5; rx < 5; rx++)
			{
				for (int rz = -5; rz < 5; rz++)
				{
					for (int ry = -1; ry <= 1; ry++)
					{
						var job = new CollisionJob { Rx = rx, Ry = ry, Rz = rz, Epoch = epoch };
						job.Boxes.Add(Box(0, 0.5f, 0, 40, 0.5f, 40, Proto.TriDiggable | ((uint)Proto.DigPlanks << Proto.TriMaterialShift)));
						job.Boxes.Add(Box(6, 3f, 0.5f, 0.05f, 2f, 4f, Proto.TriDiggable | ((uint)Proto.DigStone << Proto.TriMaterialShift)));
						job.Boxes.Add(Box(0.5f, 1.25f, -6, 2f, 0.25f, 1f, 0));
						worker.Enqueue(job);
					}
				}
			}
			Log.Info("sent the test level's collision (300 regions)");
		}

		private static Obb Box(float cx, float cy, float cz, float hx, float hy, float hz, uint flags) => new Obb
		{
			Cx = cx, Cy = cy, Cz = cz, A0x = 1, A1y = 1, A2z = 1, H0 = hx, H1 = hy, H2 = hz, Flags = flags,
		};

		private static void Plan(List<(double, string, Action)> s, double t0, SharedLink link, Action<float> setYaw, Action<float> setPitch)
		{
			void Key(double at, string name, ushort sdl, int ms)
			{
				s.Add((t0 + at, $"press {name}", () => link.PushInput(Proto.InKey, sdl, 1)));
				s.Add((t0 + at + ms / 1000.0, $"release {name}", () => link.PushInput(Proto.InKey, sdl, 0)));
			}
			s.Add((t0 + 1, "snapshot", () => { }));
			Key(2, "W (walk south 2 s)", 26, 2000);
			s.Add((t0 + 4.5, "after walking", () => { }));
			Key(5, "Space (jump)", 44, 200);
			s.Add((t0 + 5.3, "mid jump", () => { }));
			s.Add((t0 + 6.5, "after landing; face east toward the wall", () => setYaw(-90f)));
			Key(7, "W (walk east into the wall 3 s)", 26, 3000);
			s.Add((t0 + 10.5, "against the wall?", () => { }));
			s.Add((t0 + 11, "look ahead and down; hotbar 5 (planks)", () => setPitch(40f)));
			Key(11.2, "5", 34, 100);
			s.Add((t0 + 12, "right-click: place a block", () => link.PushInput(Proto.InMouseButton, 3, 1)));
			s.Add((t0 + 12.15, "release right", () => link.PushInput(Proto.InMouseButton, 3, 0)));
			s.Add((t0 + 16, "E inventory", () => link.PushInput(Proto.InKey, 8, 1)));
			s.Add((t0 + 16.1, "release E", () => link.PushInput(Proto.InKey, 8, 0)));
			s.Add((t0 + 18, "overlay with inventory", () => DumpOverlay(link, Path.Combine(outDir, "overlay-inventory.bmp"))));
			s.Add((t0 + 18.5, "Esc close", () => link.PushInput(Proto.InKey, 41, 1)));
			s.Add((t0 + 18.6, "release Esc", () => link.PushInput(Proto.InKey, 41, 0)));
			s.Add((t0 + 20, "end", () => { }));
		}

		private static void OnRender(uint type, byte* data, uint bytes)
		{
			RenderCounts.TryGetValue(type, out int n);
			RenderCounts[type] = n + 1;
			renderBytes += bytes;
			if (type == Proto.RenSection)
			{
				uint count = *(uint*)(data + 12);
				if (count > 0)
				{
					sections++;
					sectionVertices += (int)count;
					Log.Info($"section {*(int*)data} {*(int*)(data + 4)} {*(int*)(data + 8)}: {count} vertices");
				}
			}
			else if (type == Proto.RenAtlas)
			{
				int w = *(int*)data, h = *(int*)(data + 4);
				Log.Info($"atlas {w}x{h}");
				WriteBmp(Path.Combine(outDir, "atlas.bmp"), data + 8, w, h, false);
			}
		}

		private static void DumpOverlay(SharedLink link, string path)
		{
			link.FrontHeader(out int w, out int h, out bool bottomUp, out long id);
			if (w <= 0 || h <= 0 || w > Proto.MaxOverlayW || h > Proto.MaxOverlayH)
			{
				return;
			}
			WriteBmp(path, link.FrontPixels, w, h, bottomUp);
			Log.Info($"overlay frame {id} {w}x{h} -> {path}");
		}

		/// <summary>RGBA pixels shown over grey, as a 24-bit BMP.</summary>
		private static void WriteBmp(string path, byte* rgba, int w, int h, bool bottomUp)
		{
			int row = (w * 3 + 3) & ~3;
			using var f = new BinaryWriter(File.Create(path));
			f.Write((byte)'B');
			f.Write((byte)'M');
			f.Write(54 + row * h);
			f.Write(0);
			f.Write(54);
			f.Write(40);
			f.Write(w);
			f.Write(h);
			f.Write((short)1);
			f.Write((short)24);
			f.Write(0);
			f.Write(row * h);
			f.Write(2835);
			f.Write(2835);
			f.Write(0);
			f.Write(0);
			var line = new byte[row];
			for (int y = 0; y < h; y++)
			{
				int src = bottomUp ? y : h - 1 - y; // BMP rows go bottom-up
				byte* p = rgba + (long)src * w * 4;
				for (int x = 0; x < w; x++)
				{
					int a = p[x * 4 + 3];
					const int bg = 96;
					line[x * 3 + 2] = (byte)Math.Min(255, p[x * 4] + bg * (255 - a) / 255);
					line[x * 3 + 1] = (byte)Math.Min(255, p[x * 4 + 1] + bg * (255 - a) / 255);
					line[x * 3 + 0] = (byte)Math.Min(255, p[x * 4 + 2] + bg * (255 - a) / 255);
				}
				f.Write(line);
			}
		}
	}
}
