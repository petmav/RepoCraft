using BepInEx;
using HarmonyLib;
using UnityEngine;

// Compiled against a publicized Assembly-CSharp (tools\Publicizer); Mono honours this at runtime.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Assembly-CSharp")]

namespace System.Runtime.CompilerServices
{
	[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
	internal sealed class IgnoresAccessChecksToAttribute : Attribute
	{
		public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

		public string AssemblyName { get; }
	}
}

namespace RepoCraft
{
	/// <summary>
	/// RepoCraft: play R.E.P.O. as a Minecraft player. A hidden Minecraft (with the RepoCraft Fabric
	/// mod) runs the player's movement, inventory, combat and blocks; this plugin feeds it REPO's level
	/// geometry, enemies and input over shared memory and draws what Minecraft sends back inside
	/// REPO's frame. Neither game is rewritten: each runs its own logic, this only translates.
	/// </summary>
	[BepInPlugin(Guid, "RepoCraft", Version)]
	public sealed class Plugin : BaseUnityPlugin
	{
		public const string Guid = "dev.repocraft";
		public const string Version = "0.1.1";

		internal static Plugin Instance { get; private set; }
		internal static Harmony Harmony { get; private set; }

		private void Awake()
		{
			Instance = this;
			Log.Init(Logger);
			RepoCraft.Config.Bind(base.Config);
			Log.Info($"RepoCraft {Version} loading");

			if (!Link.SharedLink.Instance.Create())
			{
				Log.Error("couldn't create the shared memory: RepoCraft stays off");
				return;
			}
			Harmony = new Harmony(Guid);
			try
			{
				Harmony.PatchAll(typeof(Plugin).Assembly);
			}
			catch (System.Exception e)
			{
				Log.Error($"patching failed (a REPO update may have changed what RepoCraft hooks): {e}");
			}
			foreach (var m in Harmony.GetPatchedMethods())
			{
				Log.Debug($"patched {m.DeclaringType?.Name}.{m.Name}");
			}

			var host = new GameObject("RepoCraft");
			DontDestroyOnLoad(host);
			host.hideFlags = HideFlags.HideAndDontSave;
			host.AddComponent<Render.Overlay>();
			host.AddComponent<Driver>();

			Launcher.StartMinecraft();
		}
	}
}
