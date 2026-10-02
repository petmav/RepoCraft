using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RepoCraft.Input;
using RepoCraft.Link;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RepoCraft
{
	/// <summary>
	/// Development: commands from BepInEx/repocraft-commands.txt (picked up and deleted within a
	/// quarter second), one per line, run in order (wait MS delays the rest):
	///   start                 start a singleplayer run on REPO's debug save (from the main menu)
	///   shot NAME             screenshot to BepInEx/repocraft-shots/NAME.png
	///   key KEY MS            hold a key in Minecraft (Input System key name: W, Space, LeftShift...)
	///   mouse N MS            hold a Minecraft mouse button (1 left, 2 middle, 3 right)
	///   repo INPUTKEY MS      hold one of REPO's own inputs (Grab, Interact, Inventory1...)
	///   look YAW PITCH        set the look (Minecraft degrees); turn DYAW DPITCH adds to it
	///   lclick MS             a left click the way a real one is decided (REPO grab or Minecraft)
	///   aim valuable|enemy    look at the nearest one; tp valuable|enemy [M] stand M metres from it
	///   tpat X Y Z            put REPO's player there (Minecraft follows); probe, near R: colliders
	///   scroll N              Minecraft wheel notches (+ up)
	///   text STRING           type into an open Minecraft screen (chat)
	///   state                 log what RepoCraft sees
	///   wait MS / quit
	/// </summary>
	internal static class DebugCommands
	{
		private static string FilePath => Path.Combine(BepInEx.Paths.BepInExRootPath, "repocraft-commands.txt");
		private static string ShotDir => Path.Combine(BepInEx.Paths.BepInExRootPath, "repocraft-shots");

		private static float pollTimer;
		private static readonly List<(float at, Action run)> queue = new List<(float, Action)>();
		public static readonly HashSet<InputKey> RepoHeld = new HashSet<InputKey>();

		public static void Frame(Func<string> state)
		{
			float now = Time.realtimeSinceStartup;
			for (int i = 0; i < queue.Count;)
			{
				if (queue[i].at <= now)
				{
					var run = queue[i].run;
					queue.RemoveAt(i);
					try
					{
						run();
					}
					catch (Exception e)
					{
						Log.Warn($"debug command: {e.Message}");
					}
				}
				else
				{
					i++;
				}
			}
			pollTimer -= Time.unscaledDeltaTime;
			if (pollTimer > 0f)
			{
				return;
			}
			pollTimer = 0.25f;
			string path = FilePath;
			if (!File.Exists(path))
			{
				return;
			}
			string[] lines;
			try
			{
				lines = File.ReadAllLines(path);
				File.Delete(path);
			}
			catch (IOException)
			{
				return; // still being written
			}
			float t = now;
			foreach (string raw in lines)
			{
				string line = raw.Trim();
				if (line.Length == 0 || line.StartsWith("#"))
				{
					continue;
				}
				string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
				string cmd = a[0].ToLowerInvariant();
				if (cmd == "wait")
				{
					t += Ms(a, 1, 1000) / 1000f;
					continue;
				}
				float at = t;
				queue.Add((at, () => Run(cmd, a, line, state)));
			}
		}

		private static void Run(string cmd, string[] a, string line, Func<string> state)
		{
			var link = SharedLink.Instance;
			Log.Info($"debug: {line}");
			switch (cmd)
			{
				case "start":
					if (RunManager.instance != null && SemiFunc.IsMainMenu())
					{
						RunManager.instance.localSingleplayerTest = true;
					}
					else
					{
						Log.Info("debug: not on REPO's main menu");
					}
					break;
				case "starthost":
					// REPO's "Private Game" host flow (Steam-authenticated, invite-only), on one fixed test save
					// (BepInEx's REPOCRAFT_TEST: delete its folder under saves to forget it).
					if (RunManager.instance != null && SemiFunc.IsMainMenu())
					{
						MenuManager.instance.PageCloseAll();
						GameManager.instance.localTest = false;
						RunManager.instance.ResetProgress();
						GameManager.instance.SetConnectRandom(false);
						GameManager.instance.SetLobbyType(GameManager.LobbyTypes.Private);
						StatsManager.instance.SaveFileCreate("REPOCRAFT_TEST", true);
						RunManager.instance.waitToChangeScene = true;
						RunManager.instance.ChangeLevel(true, false, RunManager.ChangeLevelType.LobbyMenu);
						MainMenuOpen.instance.NetworkConnect();
					}
					break;
				case "blast":
				{
					// What a Minecraft explosion (radius 4 blocks) does to REPO, at the nearest enemy (or the player).
					Vector3? at = Nearest(a.Length > 1 ? a[1].ToLowerInvariant() : "enemy");
					if (at != null)
					{
						Combat.Explosions.Blast(at.Value + Vector3.down * 0.5f, 4f / Coords.K);
					}
					break;
				}
				case "password":
				{
					// The host's lobby password page: confirm it empty (= Skip).
					var page = UnityEngine.Object.FindObjectOfType<MenuPagePassword>();
					if (page != null)
					{
						page.ConfirmButton();
					}
					break;
				}
				case "lobbystart":
				{
					var lobby = UnityEngine.Object.FindObjectOfType<MenuPageLobby>();
					if (lobby != null)
					{
						lobby.ButtonStart();
					}
					else
					{
						Log.Info("debug: no lobby page");
					}
					break;
				}
				case "navcheck":
				{
					// Is REPO's navigation mesh open straight ahead (as far as N metres)?
					var pc = PlayerController.instance;
					var cam = Repo.Game.MainCamera;
					if (pc == null || cam == null)
					{
						break;
					}
					float dist = a.Length > 1 ? F(a, 1) : 4f;
					Vector3 fwd = cam.transform.forward;
					fwd.y = 0f;
					fwd.Normalize();
					if (!UnityEngine.AI.NavMesh.SamplePosition(pc.transform.position, out var start, 1.5f, UnityEngine.AI.NavMesh.AllAreas))
					{
						Log.Info("debug: navcheck: no navmesh under the player");
						break;
					}
					bool blocked = UnityEngine.AI.NavMesh.Raycast(start.position, start.position + fwd * dist, out var hit, UnityEngine.AI.NavMesh.AllAreas);
					int obstacles = 0;
					foreach (var o in UnityEngine.Object.FindObjectsOfType<UnityEngine.AI.NavMeshObstacle>())
					{
						if (o.GetComponentInParent<World.RepoCraftOwned>() != null && o.enabled && o.carving)
						{
							obstacles++;
						}
					}
					var path = new UnityEngine.AI.NavMeshPath();
					bool pathOk = UnityEngine.AI.NavMesh.CalculatePath(start.position, start.position + fwd * dist, UnityEngine.AI.NavMesh.AllAreas, path);
					float length = 0f;
					for (int i = 1; i < path.corners.Length; i++)
					{
						length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
					}
					Log.Info($"debug: navcheck {dist:0.0} m ahead: {(blocked ? $"blocked at {hit.distance:0.00} m" : "open")}; path {path.status} {length:0.0} m via {path.corners.Length} corners; {obstacles} carving block obstacles");
					break;
				}
				case "shot":
				{
					Directory.CreateDirectory(ShotDir);
					string name = a.Length > 1 ? a[1] : DateTime.Now.ToString("HHmmss");
					ScreenCapture.CaptureScreenshot(Path.Combine(ShotDir, name + ".png"));
					break;
				}
				case "key":
				{
					if (a.Length < 2 || !Enum.TryParse(a[1], true, out Key key) || !KeyMap.ToSdl.TryGetValue(key, out ushort sdl))
					{
						Log.Warn($"debug: unknown key {(a.Length > 1 ? a[1] : "")}");
						break;
					}
					link.PushInput(Proto.InKey, sdl, 1);
					Later(Ms(a, 2, 100), () => link.PushInput(Proto.InKey, sdl, 0));
					break;
				}
				case "lclick":
				{
					// A left click through the same decision a real one makes: REPO's grab or Minecraft's attack.
					int ms = Ms(a, 1, 150);
					if (Arbiter.RepoGrabTarget())
					{
						Log.Info("debug: lclick -> REPO grab");
						RepoHeld.Add(InputKey.Grab);
						Later(ms, () => RepoHeld.Remove(InputKey.Grab));
					}
					else
					{
						Log.Info("debug: lclick -> Minecraft");
						link.PushInput(Proto.InMouseButton, 1, 1);
						Later(ms, () => link.PushInput(Proto.InMouseButton, 1, 0));
					}
					break;
				}
				case "aim":
				case "tp":
				{
					// aim|tp valuable|enemy [distance]: look at (or stand that far from) the nearest one.
					string what = a.Length > 1 ? a[1].ToLowerInvariant() : "valuable";
					Vector3? target = Nearest(what);
					if (target == null)
					{
						Log.Info($"debug: no {what} found");
						break;
					}
					Vector3 t = target.Value;
					var pc = PlayerController.instance;
					if (cmd == "tp" && pc != null)
					{
						float dist = a.Length > 2 ? F(a, 2) : 1.5f;
						Vector3? spot = FreeSpotNear(t, dist);
						if (spot == null)
						{
							Log.Info($"debug: no free spot near {what} at {t}");
							break;
						}
						pc.rb.position = spot.Value;
						pc.transform.position = spot.Value;
						Log.Info($"debug: tp to {spot.Value} near {what} at {t}");
					}
					Vector3 eye = Player.Puppet.Active ? Player.Puppet.Instance.Eye : (pc != null ? pc.transform.position + Vector3.up * 1.5f : t);
					if (cmd == "tp")
					{
						eye = (pc != null ? pc.transform.position : eye) + Vector3.up * 1.5f;
					}
					Vector3 d = t - eye;
					float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
					float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
					InputBridge.Instance.Yaw = Coords.YawToMc(yaw);
					InputBridge.Instance.Pitch = Mathf.Clamp(pitch, -89f, 89f);
					break;
				}
				case "mouse":
				{
					ushort button = (ushort)(a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 1);
					link.PushInput(Proto.InMouseButton, button, 1);
					Later(Ms(a, 2, 100), () => link.PushInput(Proto.InMouseButton, button, 0));
					break;
				}
				case "repo":
				{
					if (a.Length < 2 || !Enum.TryParse(a[1], true, out InputKey k))
					{
						break;
					}
					RepoHeld.Add(k);
					Later(Ms(a, 2, 100), () => RepoHeld.Remove(k));
					break;
				}
				case "look":
					InputBridge.Instance.Yaw = F(a, 1);
					InputBridge.Instance.Pitch = F(a, 2);
					break;
				case "turn":
					InputBridge.Instance.Yaw = Mathf.Repeat(InputBridge.Instance.Yaw + F(a, 1), 360f);
					InputBridge.Instance.Pitch = Mathf.Clamp(InputBridge.Instance.Pitch + F(a, 2), -90f, 90f);
					break;
				case "scroll":
					link.PushInput(Proto.InScroll, 0, (int)(F(a, 1) * 120));
					break;
				case "text":
				{
					string s = line.Substring(line.IndexOf(' ') + 1);
					foreach (char c in s)
					{
						link.PushInput(Proto.InText, 0, c);
					}
					break;
				}
				case "state":
					Log.Info(state());
					break;
				case "probe":
				{
					var cam = Repo.Game.MainCamera;
					Vector3 o = Player.Puppet.Active ? Player.Puppet.Instance.Eye : cam.transform.position;
					foreach (var h in Physics.RaycastAll(o, cam.transform.forward, 6f, ~0, QueryTriggerInteraction.Collide))
					{
						Log.Info($"  probe {h.distance:0.00} m: {Describe(h.collider)}");
					}
					break;
				}
				case "near":
				{
					float r = a.Length > 1 ? F(a, 1) : 2f;
					Vector3 p = PlayerController.instance != null ? PlayerController.instance.transform.position : Vector3.zero;
					foreach (var c in Physics.OverlapSphere(p + Vector3.up, r, ~0, QueryTriggerInteraction.Collide))
					{
						Log.Info($"  near: {Describe(c)}");
					}
					break;
				}
				case "tpat":
					if (PlayerController.instance != null)
					{
						var pc = PlayerController.instance;
						var to = new Vector3(F(a, 1), F(a, 2), F(a, 3));
						pc.rb.isKinematic = true;
						pc.rb.position = to;
						pc.transform.position = to;
					}
					break;
				case "quit":
					Application.Quit();
					break;
				default:
					Log.Warn($"debug: unknown command {cmd}");
					break;
			}
		}

		/// <summary>
		/// A floor spot about <paramref name="dist"/> from <paramref name="target"/> where the player fits
		/// and can see it (nothing of the level in between).
		/// </summary>
		private static Vector3? FreeSpotNear(Vector3 target, float dist)
		{
			int solid = LayerMask.GetMask("Default", "PlayerOnlyCollision");
			foreach (float d in new[] { dist, dist * 0.75f, dist * 1.4f, dist * 2f })
			{
				for (int i = 0; i < 16; i++)
				{
					float ang = i * Mathf.PI * 2f / 16f;
					Vector3 p = target + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * d;
					if (!Physics.Raycast(new Vector3(p.x, target.y + 1f, p.z), Vector3.down, out RaycastHit floor, 4f, solid, QueryTriggerInteraction.Ignore))
					{
						continue;
					}
					Vector3 feet = floor.point;
					if (Physics.CheckCapsule(feet + Vector3.up * 0.35f, feet + Vector3.up * 1.55f, 0.3f, solid, QueryTriggerInteraction.Ignore))
					{
						continue;
					}
					Vector3 eye = feet + Vector3.up * 1.5f;
					if (Physics.Linecast(eye, target, out RaycastHit block, solid, QueryTriggerInteraction.Ignore) && (block.point - target).magnitude > 0.3f)
					{
						continue;
					}
					return feet;
				}
			}
			return null;
		}

		/// <summary>Centre of the nearest valuable (loot) or living enemy to the player.</summary>
		private static Vector3? Nearest(string what)
		{
			var pc = PlayerController.instance;
			Vector3 p = pc != null ? pc.transform.position : Vector3.zero;
			Vector3? best = null;
			float bestD = float.MaxValue;
			if (what.StartsWith("wall"))
			{
				// The nearest point, at eye height, of a diggable wall (not a floor or ceiling).
				Vector3 eye = p + Vector3.up * 1.5f;
				foreach (var c in Physics.OverlapSphere(eye, 6f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
				{
					if (!World.Dig.Diggable(c) || World.Dig.IsFloorShape(c, c.bounds) || c.CompareTag("Ceiling") || !(c is BoxCollider))
					{
						continue;
					}
					Vector3 q = c.ClosestPoint(eye);
					float d = (q - eye).sqrMagnitude;
					if (d > 0.04f && d < bestD)
					{
						bestD = d;
						best = q;
					}
				}
				return best;
			}
			if (what.StartsWith("enemy"))
			{
				if (EnemyDirector.instance == null)
				{
					return null;
				}
				foreach (var ep in EnemyDirector.instance.enemiesSpawned)
				{
					if (ep == null || !ep.Spawned || ep.Enemy == null || !ep.EnableObject.activeInHierarchy || (ep.Enemy.HasHealth && ep.Enemy.Health.dead))
					{
						continue;
					}
					Vector3 c = Combat.Combat.BodyBounds(ep.Enemy).center;
					float d = (c - p).sqrMagnitude;
					if (d < bestD)
					{
						bestD = d;
						best = c;
					}
				}
				return best;
			}
			foreach (var v in UnityEngine.Object.FindObjectsOfType<ValuableObject>())
			{
				var pgo = v.GetComponent<PhysGrabObject>();
				Vector3 c = pgo != null ? pgo.centerPoint : v.transform.position;
				float d = (c - p).sqrMagnitude;
				if (d < bestD)
				{
					bestD = d;
					best = c;
				}
			}
			return best;
		}

		private static string Describe(Collider c)
		{
			string path = c.name;
			for (var t = c.transform.parent; t != null && path.Length < 160; t = t.parent)
			{
				path = t.name + "/" + path;
			}
			var b = c.bounds;
			return $"{c.GetType().Name} '{path}' layer {LayerMask.LayerToName(c.gameObject.layer)} tag {c.tag} trigger {c.isTrigger} enabled {c.enabled} rb {(c.attachedRigidbody != null ? (c.attachedRigidbody.isKinematic ? "kinematic" : "dynamic") : "none")} bounds {b.min} - {b.max}";
		}

		private static void Later(int ms, Action run) => queue.Add((Time.realtimeSinceStartup + ms / 1000f, run));

		private static int Ms(string[] a, int i, int fallback) => a.Length > i && int.TryParse(a[i], out int v) ? v : fallback;

		private static float F(string[] a, int i) => a.Length > i ? float.Parse(a[i], CultureInfo.InvariantCulture) : 0f;
	}
}
