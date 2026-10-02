using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RepoCraft
{
	/// <summary>
	/// Starts Minecraft with REPO. The Fabric mod then waits hidden on its title screen until REPO is
	/// in a level, opens its mirror world by itself, and quits a few seconds after REPO does.
	///
	/// What it starts, in order: the launcher set in the config; the Minecraft RepoCraft ships
	/// (RepoCraft-Minecraft.zip next to the plugin: a portable Prism Launcher with a ready "RepoCraft"
	/// instance, unpacked to %LOCALAPPDATA%\RepoCraft; Prism asks for a Microsoft account sign-in the
	/// first time, then downloads Minecraft and Java itself); an installed Prism Launcher's "RepoCraft"
	/// instance.
	/// </summary>
	internal static class Launcher
	{
		public enum Status { Off, Running, Unpacking, Starting, NoLauncher, Failed }

		private static volatile Status current = Status.Off;

		public static Status Current => current;

		/// <summary>True when the bundled Minecraft was started (its first start needs a sign-in in Prism).</summary>
		public static bool Bundled { get; private set; }

		private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoCraft");

		private static string BundlePath => Path.Combine(Path.GetDirectoryName(typeof(Launcher).Assembly.Location) ?? ".", "RepoCraft-Minecraft.zip");

		/// <summary>A Minecraft with the RepoCraft mod holds this mutex while it runs (HostLink.announceRunning).</summary>
		public static bool MinecraftRunning()
		{
			IntPtr h = OpenMutexW(SYNCHRONIZE, false, Link.Proto.MappingName + "_minecraft");
			if (h == IntPtr.Zero)
			{
				return false;
			}
			CloseHandle(h);
			return true;
		}

		public static void StartMinecraft()
		{
			if (!Config.StartWithRepo)
			{
				current = Status.Off;
				return;
			}
			if (MinecraftRunning())
			{
				Log.Info("Minecraft (RepoCraft) is already running");
				current = Status.Running;
				return;
			}
			string configured = Config.Launcher?.Trim().Trim('"');
			if (string.IsNullOrEmpty(configured) && File.Exists(BundlePath))
			{
				// Off the main thread: unpacking takes a few seconds the first time.
				current = Status.Unpacking;
				Bundled = true;
				new Thread(() =>
				{
					string prism = EnsureBundle();
					if (prism == null)
					{
						current = Status.Failed;
						return;
					}
					Start(prism, "--launch RepoCraft");
				}) { IsBackground = true, Name = "RepoCraft launcher" }.Start();
				return;
			}
			string exe = FindLauncher(configured);
			if (exe == null)
			{
				Log.Warn("no Minecraft launcher found: install Prism Launcher, or set [Minecraft] Launcher in BepInEx/config/dev.repocraft.cfg");
				current = Status.NoLauncher;
				return;
			}
			Start(exe, Config.LauncherArgs);
		}

		private static void Start(string exe, string args)
		{
			try
			{
				var info = new ProcessStartInfo(exe, args)
				{
					UseShellExecute = true,
					WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
					WindowStyle = ProcessWindowStyle.Minimized,
				};
				if (exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
				{
					info = new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args}\"") { UseShellExecute = false, CreateNoWindow = true };
				}
				Process.Start(info);
				current = Status.Starting;
				Log.Info($"starting Minecraft: {exe} {args}");
			}
			catch (Exception e)
			{
				current = Status.Failed;
				Log.Error($"couldn't start Minecraft ({exe}): {e.Message}");
			}
		}

		private static string FindLauncher(string configured)
		{
			if (!string.IsNullOrEmpty(configured))
			{
				return File.Exists(configured) ? configured : null;
			}
			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			string[] candidates =
			{
				Path.Combine(local, "RepoCraft", "Prism", "prismlauncher.exe"),
				Path.Combine(local, "Programs", "PrismLauncher", "prismlauncher.exe"),
				@"C:\Program Files\PrismLauncher\prismlauncher.exe",
				Path.Combine(local, "Programs", "MultiMC", "MultiMC.exe"),
			};
			foreach (string c in candidates)
			{
				if (File.Exists(c))
				{
					return c;
				}
			}
			return null;
		}

		/// <summary>
		/// Unpacks the bundled Minecraft to %LOCALAPPDATA%\RepoCraft (outside the game folder) the first
		/// time, and again whenever this RepoCraft brings a different one. Prism's own data there (the
		/// signed-in account, the downloaded Minecraft and Java, the RepoCraft world) is kept; the
		/// instance and its RepoCraft and Fabric API jars are replaced, so both halves always match.
		/// Returns Prism's exe, or null.
		/// </summary>
		private static string EnsureBundle()
		{
			try
			{
				string dir = InstallDir;
				string prism = Path.Combine(dir, "Prism", "prismlauncher.exe");
				var bundle = new FileInfo(BundlePath);
				string stamp = $"{bundle.Length} {bundle.LastWriteTimeUtc.Ticks}";
				string stampFile = Path.Combine(dir, "bundle.stamp");
				if (File.Exists(prism) && File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp)
				{
					return prism;
				}
				Log.Info($"unpacking RepoCraft's Minecraft to {dir}");
				Directory.CreateDirectory(dir);
				string mods = Path.Combine(dir, "Prism", "instances", "RepoCraft", ".minecraft", "mods");
				if (Directory.Exists(mods))
				{
					foreach (string f in Directory.GetFiles(mods, "*.jar"))
					{
						string n = Path.GetFileName(f);
						if (n.StartsWith("repocraft-", StringComparison.OrdinalIgnoreCase) || n.StartsWith("fabric-api-", StringComparison.OrdinalIgnoreCase)
							|| n.StartsWith("e4mc-", StringComparison.OrdinalIgnoreCase))
						{
							File.Delete(f);
						}
					}
				}
				string tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
				var p = Process.Start(new ProcessStartInfo(tar, $"-xf \"{bundle.FullName}\" -C \"{dir}\"")
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = dir,
				});
				if (p == null || !p.WaitForExit(5 * 60 * 1000) || p.ExitCode != 0 || !File.Exists(prism))
				{
					Log.Warn($"unpacking the bundled Minecraft failed (tar exit code {(p != null && p.HasExited ? p.ExitCode : -1)})");
					return null;
				}
				// Prism's settings only the first time: after that they're the player's.
				string cfg = Path.Combine(dir, "Prism", "prismlauncher.cfg");
				string defaults = Path.Combine(dir, "defaults", "prismlauncher.cfg");
				if (!File.Exists(cfg) && File.Exists(defaults))
				{
					File.Copy(defaults, cfg);
				}
				File.WriteAllText(stampFile, stamp);
				Log.Info("bundled Minecraft ready (the first start asks you to sign in to Prism Launcher with a Microsoft account that owns Minecraft)");
				return prism;
			}
			catch (Exception e)
			{
				Log.Error($"couldn't set up the bundled Minecraft: {e.Message}");
				return null;
			}
		}

		private const uint SYNCHRONIZE = 0x00100000;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);
	}
}
