using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using RepoCraft;
using RepoCraft.Link;
using RepoCraft.World;

namespace FakeRepo
{
	/// <summary>
	/// A stand-in REPO lobby guest, on the same PC as a real REPO hosting a lobby: its Minecraft (a
	/// second client on its own link) joins the host's world at the given address and stands at the
	/// given spot (Minecraft coordinates in the host's level slot, and the host's world id) on a flat
	/// floor, then walks a little, and reports what Minecraft says about the lobby's world.
	///   set REPOCRAFT_LINK=Local\RepoCraft_guest            (here and for the second Minecraft)
	///   dotnet run --project tools/FakeRepo -- --guest address worldIdHex x y z [seconds]
	/// </summary>
	internal static unsafe class Guest
	{
		public static int Run(string[] args)
		{
			if (Proto.MappingName == "Local\\RepoCraft_v1")
			{
				Log.Error("set REPOCRAFT_LINK=Local\\RepoCraft_guest first: the real REPO has the default link");
				return 1;
			}
			if (args.Length < 6)
			{
				Log.Error("usage: --guest address worldIdHex x y z [seconds]");
				return 1;
			}
			address = args[1];
			uint world = Convert.ToUInt32(args[2], 16);
			x = double.Parse(args[3], CultureInfo.InvariantCulture);
			y = double.Parse(args[4], CultureInfo.InvariantCulture);
			z = double.Parse(args[5], CultureInfo.InvariantCulture);
			double seconds = args.Length > 6 ? double.Parse(args[6], CultureInfo.InvariantCulture) : 150;

			var link = SharedLink.Instance;
			if (!link.Create())
			{
				return 1;
			}
			var worker = new CollisionWorker();
			worker.Start();
			epoch = 1;
			seq = (uint)Environment.TickCount | 1u;
			worker.Reset(epoch);
			var mc = new McState();
			var clock = Stopwatch.StartNew();
			bool linked = false, sent = false;
			double arrivedAt = -1, lastLog = 0;
			uint lastState = uint.MaxValue;
			float yaw = 0f, pitch = 0f;
			int step = 0;
			while (clock.Elapsed.TotalSeconds < seconds)
			{
				double t = clock.Elapsed.TotalSeconds;
				link.Heartbeat();
				link.WriteMpRequest(address.Length > 0 ? Proto.MpJoin : 0u, address);
				bool alive = link.McAlive() && link.ReadMcState(mc);
				if (alive && !linked)
				{
					linked = true;
					Log.Info($"guest Minecraft linked (pid {link.McPid()}) at {t:0.0}s");
					link.ResetOverlay();
				}
				link.WriteHostState(new HostState
				{
					Flags = inGame ? Proto.HostInGame : 0u,
					WorldId = world,
					CollisionEpoch = epoch,
					PosX = x, PosY = y, PosZ = z,
					Yaw = yaw, Pitch = pitch,
					TeleportSeq = seq,
					ViewportW = 1280, ViewportH = 720,
					GameHour = 13f,
				});
				if (alive && !sent)
				{
					sent = true;
					SendFloor(worker, epoch, x, y, z);
				}
				uint state = link.ReadMpState(out string share, out string friend);
				if (state != lastState)
				{
					lastState = state;
					Log.Info($"t {t:0.0}: Minecraft lobby state {state} ({Name(state)}) friendWorld='{friend}' shareLink='{share}'");
				}
				bool inWorld = alive && (mc.Flags & Proto.McInWorld) != 0 && mc.TeleportAck == seq && state == Proto.MpsInFriend;
				if (inWorld && arrivedAt < 0)
				{
					arrivedAt = t;
					Log.Info($"t {t:0.0}: guest player arrived in the host's world at {mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}");
				}
				if (arrivedAt >= 0)
				{
					double since = t - arrivedAt;
					if (step == 0 && since > 6)
					{
						step++;
						Log.Info($"t {t:0.0}: walk forward 1.5 s   mc=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00})");
						link.PushInput(Proto.InKey, 26, 1);
					}
					else if (step == 1 && since > 7.5)
					{
						step++;
						link.PushInput(Proto.InKey, 26, 0);
					}
					else if (step == 2 && since > 9)
					{
						step++;
						Log.Info($"t {t:0.0}: after walking   mc=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) ground={(mc.Flags & Proto.McOnGround) != 0}");
						yaw = 180f;
					}
				}
				Commands(link, worker, t, ref yaw, ref pitch, mc);
				link.DrainRender((type, data, bytes) => { }, 48L << 20);
				while (link.PopEvent(out McEvent ev))
				{
					Log.Info($"event {ev.Type} id {ev.FormId}");
				}
				link.AcquireOverlayFrame();
				if (t - lastLog > 5)
				{
					lastLog = t;
					Log.Info($"t {t:0}s alive={alive} flags={mc.Flags:X} ack={mc.TeleportAck}/{seq} pos=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) lobby={Name(state)}");
				}
				Thread.Sleep(16);
			}
			Log.Info($"done: in the host's world={arrivedAt >= 0} final pos=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00})");
			worker.Stop();
			return arrivedAt >= 0 ? 0 : 2;
		}

		private static readonly System.Collections.Generic.List<(double at, Action run)> Pending = new System.Collections.Generic.List<(double, Action)>();

		/// <summary>
		/// fake-guest-commands.txt (picked up and deleted): "look YAW PITCH", "key SCANCODE MS",
		/// "mouse BUTTON MS" (1 left, 3 right), "tp X Y Z" (a new floor there), "join ADDRESS" ("join -": leave the lobby), "ingame 0/1" (REPO in a menu / in a level), "wait MS", "pos".
		/// </summary>
		private static double x, y, z;
		private static string address;
		private static bool inGame = true;
		private static uint epoch, seq;

		private static void Commands(SharedLink link, CollisionWorker worker, double t, ref float yaw, ref float pitch, McState mc)
		{
			for (int i = 0; i < Pending.Count;)
			{
				if (Pending[i].at <= t)
				{
					var run = Pending[i].run;
					Pending.RemoveAt(i);
					run();
				}
				else
				{
					i++;
				}
			}
			const string file = "fake-guest-commands.txt";
			if (!System.IO.File.Exists(file))
			{
				return;
			}
			string[] lines;
			try
			{
				lines = System.IO.File.ReadAllLines(file);
				System.IO.File.Delete(file);
			}
			catch (System.IO.IOException)
			{
				return;
			}
			double at = t;
			foreach (string raw in lines)
			{
				var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (p.Length == 0)
				{
					continue;
				}
				Log.Info($"guest command: {raw.Trim()}");
				switch (p[0])
				{
					case "look":
						yaw = float.Parse(p[1], CultureInfo.InvariantCulture);
						pitch = float.Parse(p[2], CultureInfo.InvariantCulture);
						break;
					case "key":
					case "mouse":
					{
						ushort type = p[0] == "key" ? Proto.InKey : Proto.InMouseButton;
						ushort code = ushort.Parse(p[1]);
						double ms = p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : 100;
						Pending.Add((at, () => link.PushInput(type, code, 1)));
						Pending.Add((at + ms / 1000.0, () => link.PushInput(type, code, 0)));
						break;
					}
					case "wait":
						at += double.Parse(p[1], CultureInfo.InvariantCulture) / 1000.0;
						break;
					case "ingame":
						inGame = p.Length > 1 && p[1] != "0";
						break;
					case "join":
						address = p.Length > 1 && p[1] != "-" ? p[1] : "";
						break;
					case "tp":
						x = double.Parse(p[1], CultureInfo.InvariantCulture);
						y = double.Parse(p[2], CultureInfo.InvariantCulture);
						z = double.Parse(p[3], CultureInfo.InvariantCulture);
						epoch++;
						seq++;
						worker.Reset(epoch);
						SendFloor(worker, epoch, x, y, z);
						break;
					case "pos":
						Pending.Add((at, () => Log.Info($"guest at ({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) yaw {mc.Yaw:0} ground={(mc.Flags & Proto.McOnGround) != 0}")));
						break;
				}
			}
		}

		private static string Name(uint state) => state switch
		{
			Proto.MpsOwn => "own world",
			Proto.MpsSharing => "sharing",
			Proto.MpsJoining => "joining",
			Proto.MpsInFriend => "in friend's world",
			Proto.MpsJoinFailed => "join failed",
			_ => "?",
		};

		/// <summary>A 40x40 floor whose top is at the feet.</summary>
		private static void SendFloor(CollisionWorker worker, uint epoch, double x, double y, double z)
		{
			int s = CollisionWorker.RegionSize;
			int n = 0;
			for (int rx = (int)Math.Floor((x - 20) / s); rx <= (int)Math.Floor((x + 20) / s); rx++)
			{
				for (int rz = (int)Math.Floor((z - 20) / s); rz <= (int)Math.Floor((z + 20) / s); rz++)
				{
					for (int ry = (int)Math.Floor((y - 1) / s) - 1; ry <= (int)Math.Floor(y / s) + 1; ry++)
					{
						var job = new CollisionJob { Rx = rx, Ry = ry, Rz = rz, Epoch = epoch };
						job.Boxes.Add(new Obb
						{
							Cx = (float)x, Cy = (float)(y - 0.5), Cz = (float)z, A0x = 1, A1y = 1, A2z = 1, H0 = 20, H1 = 0.5f, H2 = 20,
							Flags = Proto.TriDiggable | ((uint)Proto.DigPlanks << Proto.TriMaterialShift),
						});
						worker.Enqueue(job);
						n++;
					}
				}
			}
			Log.Info($"sent a floor at y {y:0.00} around ({x:0.0}, {z:0.0}): {n} regions");
		}
	}
}
