using BepInEx.Logging;

namespace RepoCraft
{
	internal static class Log
	{
		private static ManualLogSource source;

		public static void Init(ManualLogSource s) => source = s;

		public static void Info(string message) => source?.LogInfo(message);

		public static void Warn(string message) => source?.LogWarning(message);

		public static void Error(string message) => source?.LogError(message);

		/// <summary>Only with Diagnostics on (several lines a second at times).</summary>
		public static void Debug(string message)
		{
			if (Config.Diagnostics)
			{
				source?.LogInfo(message);
			}
		}
	}
}
