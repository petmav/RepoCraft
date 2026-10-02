using BepInEx.Configuration;

namespace RepoCraft
{
	/// <summary>BepInEx/config/dev.repocraft.cfg.</summary>
	internal static class Config
	{
		private static ConfigEntry<bool> startWithRepo;
		private static ConfigEntry<string> launcher;
		private static ConfigEntry<string> launcherArgs;
		private static ConfigEntry<float> blocksPerMeter;
		private static ConfigEntry<bool> destruction;
		private static ConfigEntry<float> damageToEnemies;
		private static ConfigEntry<float> damageToPlayer;
		private static ConfigEntry<bool> diagnostics;
		private static ConfigEntry<bool> hideRepoHud;
		private static ConfigEntry<bool> shareWorld;

		public static bool StartWithRepo => startWithRepo.Value;
		public static string Launcher => launcher.Value;
		public static string LauncherArgs => launcherArgs.Value;
		/// <summary>Minecraft blocks per Unity unit (metre). 1 block = 1 m keeps doorways and the robots in proportion.</summary>
		public static float BlocksPerMeter => blocksPerMeter.Value;
		public static bool Destruction { get => destruction.Value; set => destruction.Value = value; }
		public static float DamageToEnemies => damageToEnemies.Value;
		public static float DamageToPlayer => damageToPlayer.Value;
		public static bool Diagnostics => diagnostics?.Value ?? false;
		public static bool HideRepoHud => hideRepoHud.Value;
		public static bool ShareWorld => shareWorld?.Value ?? true;

		public static void Bind(ConfigFile file)
		{
			startWithRepo = file.Bind("Minecraft", "StartWithRepo", true,
				"Start Minecraft (hidden) when REPO starts, and close it when REPO closes. Off: start the RepoCraft Minecraft instance yourself.");
			launcher = file.Bind("Minecraft", "Launcher", "",
				"Empty: Prism Launcher from its usual install folder. Or the full path of your launcher (Prism, MultiMC, or a .bat file).");
			launcherArgs = file.Bind("Minecraft", "LauncherArguments", "--launch RepoCraft",
				"What to pass to the launcher. For Prism: --launch and the name of the RepoCraft instance.");
			blocksPerMeter = file.Bind("World", "BlocksPerMeter", 1.0f,
				"Minecraft blocks per REPO metre. Bigger makes the Minecraft player smaller relative to REPO.");
			destruction = file.Bind("World", "Destruction", true,
				"Minecraft can dig into and blow up REPO's level (walls, floors, props). Holes already dug stay when you turn it off.");
			damageToEnemies = file.Bind("Combat", "DamageToEnemies", 4.0f,
				"REPO damage per point of Minecraft damage dealt to an enemy (a diamond sword does 7).");
			damageToPlayer = file.Bind("Combat", "DamageToPlayer", 0.2f,
				"Minecraft damage (half hearts) per point of REPO damage taken (REPO's player has 100 health).");
			hideRepoHud = file.Bind("Display", "HideRepoHud", true,
				"Hide REPO's health/stamina HUD while Minecraft drives the player (Minecraft's own HUD is shown).");
			shareWorld = file.Bind("Multiplayer", "ShareWorld", true,
				"In a REPO lobby, everyone with RepoCraft plays in one Minecraft world, the host's (opened to the lobby over e4mc), and sees the same blocks, holes and Minecraft players. Off: your own world (hosting: the lobby's guests keep theirs too).");
			diagnostics = file.Bind("Debug", "Diagnostics", false,
				"Detailed timing, camera, combat and collision logs (several lines a second).");
		}
	}
}
