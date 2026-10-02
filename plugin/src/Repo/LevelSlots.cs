using System;
using System.IO;

namespace RepoCraft.Repo
{
	/// <summary>
	/// Which stretch of the Minecraft world a REPO level maps to. REPO builds every level at the same
	/// coordinates, so each gets its own slot, 1024 blocks apart along X: the truck, each shop and
	/// each arena always the same slot (what you build there stays), run levels a fresh slot each time
	/// (cycling through 80, so a long time passes before one comes round again). Kept under ~100k
	/// blocks out: the protocol carries many positions as floats.
	/// </summary>
	internal static class LevelSlots
	{
		public const int SlotBlocks = 1024;
		private const int FirstRunSlot = 16;
		private const int RunSlots = 80;

		private static string StatePath => Path.Combine(BepInEx.Paths.ConfigPath, "repocraft-slots.txt");

		public static int Current { get; private set; } = -1;

		/// <summary>The protocol's worldId for the current level (0 = none).</summary>
		public static uint WorldId { get; private set; }

		/// <summary>A new level is up: pick its slot and move the coordinate mapping there.</summary>
		public static void Enter()
		{
			var rm = RunManager.instance;
			int slot;
			if (rm == null || rm.levelCurrent == null)
			{
				slot = 0;
			}
			else if (SemiFunc.RunIsLobby())
			{
				slot = 1;
			}
			else if (SemiFunc.RunIsShop())
			{
				slot = 2 + Math.Max(0, rm.levelShop.IndexOf(rm.levelCurrent)) % 6;
			}
			else if (SemiFunc.RunIsArena())
			{
				slot = 8 + Math.Max(0, rm.levelArena.IndexOf(rm.levelCurrent)) % 4;
			}
			else if (SemiFunc.RunIsTutorial())
			{
				slot = 12;
			}
			else if (SemiFunc.RunIsLevel())
			{
				slot = FirstRunSlot + NextRunCounter() % RunSlots;
			}
			else
			{
				slot = 13;
			}
			Current = slot;
			// The truck, shops and arenas are the same place every visit: same id, so what was dug there
			// stays dug. A run level gets a new id every time, even when its slot comes round again:
			// anything dug in that slot's last level belongs to a level that's gone.
			WorldId = slot >= FirstRunSlot ? (uint)(slot + 1) | ((uint)Environment.TickCount & 0x7FFF0000u) : (uint)(slot + 1);
			Coords.OffsetX = (double)slot * SlotBlocks;
			Log.Info($"level {Game.LevelName}: Minecraft slot {slot} (x {Coords.OffsetX:0}), world {WorldId:X8}");
		}

		/// <summary>A new level is up, in the lobby host's Minecraft world: the host's slot for it.</summary>
		public static void Use(int slot, uint worldId)
		{
			Current = slot;
			WorldId = worldId;
			Coords.OffsetX = (double)slot * SlotBlocks;
			Log.Info($"level {Game.LevelName}: the lobby host's Minecraft slot {slot} (x {Coords.OffsetX:0}), world {WorldId:X8}");
		}

		private static int NextRunCounter()
		{
			int n = 0;
			try
			{
				if (File.Exists(StatePath))
				{
					int.TryParse(File.ReadAllText(StatePath).Trim(), out n);
				}
				File.WriteAllText(StatePath, ((n + 1) % 100000).ToString());
			}
			catch (Exception e)
			{
				Log.Warn($"level slots: {e.Message}");
			}
			return n;
		}
	}
}
