using RepoCraft.Input;
using RepoCraft.Link;
using RepoCraft.Player;
using RepoCraft.Render;
using RepoCraft.Repo;
using RepoCraft.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RepoCraft
{
	/// <summary>
	/// The per-frame glue between REPO and Minecraft (SkyCraft's Game.cpp PerFrame, for REPO): reads
	/// Minecraft's state, decides who drives the player, streams REPO's world to Minecraft and draws
	/// Minecraft's world in REPO's.
	/// </summary>
	internal sealed class Driver : MonoBehaviour
	{
		private const float SettleSeconds = 1.0f;      // REPO's level settles (props switch, doors spawn) before collision goes
		private const float TeleportThreshold = 3.0f;  // metres: further than this is REPO moving the player

		private readonly McState mc = new McState();
		private readonly TickInterpolator ticks = new TickInterpolator();
		private bool mcWasAlive;
		private uint lastMcPid;
		private uint teleportSeq = (uint)System.Environment.TickCount | 1u;
		private bool teleportPending = true;
		private uint epoch;
		private LevelGenerator lastLevel;
		private bool levelEntered;
		private float settle = SettleSeconds;
		private const float VoidY = -60f;              // metres: no REPO level goes this far down
		private Vector3 lastGround;
		private bool haveGround;
		private float slotWait;
		private float enteredScale;
		private readonly System.Collections.Generic.List<Vector3> othersMc = new System.Collections.Generic.List<Vector3>();
		private float holdMismatch;
		private bool puppetPrev;
		private float puppetFor;
		private bool mcInWorld;
		private bool mcScreenOpen;
		private float waterTimer;
		private float noteTimer = 8f;
		private bool told;
		private readonly float[] noWater = new float[Proto.WaterGridSize * Proto.WaterGridSize];
		private readonly System.Collections.Generic.List<(int x, int y, int z)> dugChanged = new System.Collections.Generic.List<(int, int, int)>();

		private void Start()
		{
			for (int i = 0; i < noWater.Length; i++)
			{
				noWater[i] = Proto.NoWater;
			}
			CollisionExporter.Instance.Start();
			InputSystem.onAfterUpdate += OnInputUpdate;
		}

		private void OnDestroy()
		{
			InputSystem.onAfterUpdate -= OnInputUpdate;
		}

		/// <summary>Right after the Input System updates, before any of REPO's scripts read input this frame.</summary>
		private void OnInputUpdate()
		{
			bool route = Puppet.Active && !Game.MenuOpen && Application.isFocused;
			InputBridge.Instance.Frame(route, mcScreenOpen, Screen.width, Screen.height, mc.Sensitivity);
		}

		private void Update()
		{
			var link = SharedLink.Instance;
			if (!link.Valid)
			{
				return;
			}
			link.Heartbeat();
			Lobby.Frame(link);
			float dt = Time.unscaledDeltaTime;

			bool mcAlive = link.McAlive();
			bool haveMc = mcAlive && link.ReadMcState(mc);
			uint pid = link.McPid();
			bool newProcess = mcAlive && pid != 0 && pid != lastMcPid;
			if (mcAlive)
			{
				lastMcPid = pid;
			}
			if (mcAlive && (!mcWasAlive || newProcess))
			{
				Log.Info("Minecraft connected");
				link.ResetOverlay();
				ResetWorld();
				ticks.Reset();
			}
			if (!mcAlive && mcWasAlive)
			{
				Log.Info("Minecraft disconnected");
			}
			mcWasAlive = mcAlive;
			mcInWorld = haveMc && mc.Has(Proto.McInWorld);
			mcScreenOpen = haveMc && mc.Has(Proto.McScreenOpen);

			// A new level (REPO reloads its scene for each one).
			var level = LevelGenerator.Instance;
			if (level != lastLevel)
			{
				lastLevel = level;
				levelEntered = false;
				slotWait = 0f;
				Puppet.Instance.Release();
			}
			// In the lobby host's Minecraft world, but this level was mapped before the host said where
			// (its world came up late), or at another scale: map it again.
			if (levelEntered && (Lobby.SlotStale || Coords.K != enteredScale))
			{
				Log.Info("lobby: mapping this level to the host's Minecraft slot");
				levelEntered = false;
				slotWait = 0f;
			}
			if (!levelEntered && level != null && level.Generated && PlayerAvatar.instance != null
				&& Lobby.PickSlot(slotWait += dt, out int hostSlot, out uint hostWorld))
			{
				levelEntered = true;
				enteredScale = Coords.K;
				if (hostSlot >= 0)
				{
					LevelSlots.Use(hostSlot, hostWorld);
				}
				else
				{
					LevelSlots.Enter();
				}
				Lobby.HostEntered();
				DugBlocks.SetWorld(LevelSlots.WorldId);
				ResetWorld();
				Combat.Combat.Instance.Clear();
				StuckArrows.Instance.Clear();
				Ragdoll.Instance.Clear();
				Dig.Instance.Clear();
				haveGround = false;
			}

			bool inLevel = Game.InLevel && levelEntered;
			bool loading = Game.Loading || !inLevel;
			bool menu = Game.MenuOpen;
			var pc = PlayerController.instance;
			Vector3 repoFeet = pc != null ? pc.transform.position : Vector3.zero;
			if (loading)
			{
				teleportPending = true;
				settle = SettleSeconds;
			}
			else if (Puppet.Active && puppetFor > 0.5f && pc != null && Puppet.Instance.HaveLastSet && (pc.rb.position - Puppet.Instance.LastSet).magnitude > TeleportThreshold)
			{
				// Where physics has the body, against where we last put it: anything else moved it.
				Log.Info($"REPO moved the player ({(pc.rb.position - Puppet.Instance.LastSet).magnitude:0.0} m); resyncing Minecraft");
				teleportPending = true;
				Puppet.Instance.Release();
			}
			bool inPlay = Game.InPlay;
			if (teleportPending && !loading && inPlay)
			{
				teleportSeq++;
				teleportPending = false;
				// Minecraft picks up REPO's look along with its position.
				var cam = Game.MainCamera;
				if (cam != null)
				{
					var e = cam.transform.eulerAngles;
					InputBridge.Instance.Yaw = Coords.YawToMc(e.y);
					InputBridge.Instance.Pitch = Mathf.Clamp(e.x > 180f ? e.x - 360f : e.x, -90f, 90f);
				}
			}

			// Minecraft waiting somewhere REPO's player isn't (its ground never arrives): send it again.
			bool arriving = haveMc && mcInWorld && !loading && inPlay && mc.TeleportAck != teleportSeq;
			if (arriving)
			{
				Coords.ToMc(repoFeet, out double hx, out double hy, out double hz);
				double gap = System.Math.Sqrt((hx - mc.X) * (hx - mc.X) + (hy - mc.Y) * (hy - mc.Y) + (hz - mc.Z) * (hz - mc.Z));
				holdMismatch = gap > 8.0 ? holdMismatch + dt : 0f;
				if (holdMismatch > 1.5f)
				{
					Log.Info($"Minecraft is waiting {gap:0} blocks from REPO's player; teleporting it again");
					teleportPending = true;
					holdMismatch = 0f;
				}
			}
			else
			{
				holdMismatch = 0f;
			}

			bool puppet = haveMc && mcInWorld && mc.TeleportAck == teleportSeq && !loading && inPlay;
			// Minecraft fell out of REPO's level (a gap in the collision, or a server that put it
			// somewhere else): back to where it last stood, REPO's player with it, before the void
			// below kills both.
			if (puppet && pc != null)
			{
				Vector3 mcFeet = Coords.ToUnity(mc.X, mc.Y, mc.Z);
				if (mc.Has(Proto.McOnGround) && !mc.Has(Proto.McDead) && mcFeet.y > VoidY)
				{
					lastGround = mcFeet;
					haveGround = true;
				}
				else if (haveGround && mcFeet.y < VoidY)
				{
					Log.Warn($"Minecraft fell out of the level (y {mcFeet.y:0} m); back to where it last stood");
					pc.rb.isKinematic = true;
					pc.rb.position = lastGround;
					pc.transform.position = lastGround;
					repoFeet = lastGround;
					teleportPending = true;
					puppet = false;
				}
			}
			puppetFor = puppet ? puppetFor + dt : 0f;
			if (puppet != puppetPrev)
			{
				Log.Info(puppet ? "Minecraft drives the player" : "REPO drives the player");
				if (puppet)
				{
					ticks.Reset(); // nothing from before Minecraft arrived here
				}
				if (!puppet)
				{
					Puppet.Instance.Release();
				}
				puppetPrev = puppet;
			}
			if (puppet)
			{
				ticks.Sample(mc);
				Puppet.Instance.Frame(mc, ticks, InputBridge.Instance.Yaw, InputBridge.Instance.Pitch, dt);
			}

			// Tell Minecraft where REPO's player is and where they look.
			Coords.ToMc(repoFeet, out double px, out double py, out double pz);
			var host = new HostState
			{
				Flags = (inLevel ? Proto.HostInGame : 0u) | (menu ? Proto.HostMenuOpen : 0u) | (loading ? Proto.HostLoading : 0u),
				WorldId = LevelSlots.WorldId,
				CollisionEpoch = epoch,
				PosX = px,
				PosY = py,
				PosZ = pz,
				Yaw = InputBridge.Instance.Yaw,
				Pitch = InputBridge.Instance.Pitch,
				TeleportSeq = teleportSeq,
				ViewportW = (uint)Mathf.Min(Screen.width, Proto.MaxOverlayW),
				ViewportH = (uint)Mathf.Min(Screen.height, Proto.MaxOverlayH),
				GameHour = Game.IsRunLevel ? 17.5f : 13f, // a dim afternoon in the facilities: Minecraft's hand isn't black
			};
			link.WriteHostState(host);

			// REPO's world to Minecraft.
			settle -= dt;
			Vector3 centreMc = puppet ? new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z) : new Vector3((float)px, (float)py, (float)pz);
			if (haveMc && !loading && settle <= 0f)
			{
				// Hosting the lobby's world: Minecraft's server also needs REPO's ground under the others
				// (their dropped items, arrows, TNT).
				Lobby.OthersInWorld(othersMc);
				CollisionExporter.Instance.Update(centreMc, Lobby.Hosting ? othersMc : null);
			}
			waterTimer -= dt;
			if (haveMc && waterTimer <= 0f)
			{
				waterTimer = 0.5f;
				link.WriteWaterGrid(Mathf.FloorToInt(centreMc.x) - Proto.WaterGridSize / 2, Mathf.FloorToInt(centreMc.z) - Proto.WaterGridSize / 2, LevelSlots.WorldId, noWater);
			}

			// Minecraft's world to REPO.
			BlockRenderer.Instance.Drain();
			dugChanged.Clear();
			DugBlocks.TakeChanged(dugChanged);
			if (dugChanged.Count > 0)
			{
				CollisionExporter.Instance.DigChanged(dugChanged);
				Dig.Instance.Changed(dugChanged);
			}
			Dig.Instance.Frame(inLevel && !loading, dt);
			BlockLights.Instance.Update(haveMc && mcInWorld && inLevel ? centreMc : (Vector3?)null, dt);
			EntityRenderer.Instance.Frame(BlockRenderer.Instance.Root.transform, BlockRenderer.Instance.Atlas, haveMc && mcInWorld && inLevel);
			// Hosting the lobby's world, the monsters' stand-ins are everyone's: they stay while we're dead.
			Combat.Combat.Instance.Frame(puppet, Lobby.Hosting && Lobby.InLobbyWorld && inLevel && !loading);
			Hud.Frame(puppet);

			var overlay = Overlay.Instance;
			overlay.Pull();
			overlay.Show = haveMc && mcInWorld && puppet && !menu;
			overlay.ShowCursor = mcScreenOpen;
			overlay.Cursor = new Vector2Int(InputBridge.Instance.CursorX, InputBridge.Instance.CursorY);

			ReportMinecraft(mcAlive, inLevel && !menu, dt);
			DebugCommands.Frame(StateText);
		}

		private string StateText()
		{
			var pc = PlayerController.instance;
			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"state: level '{Game.LevelName}' generated={LevelGenerator.Instance?.Generated} inPlay={Game.InPlay} loading={Game.Loading} menu={Game.MenuOpen} slot={LevelSlots.Current} world={LevelSlots.WorldId:X8}");
			sb.AppendLine($"  minecraft alive={SharedLink.Instance.McAlive()} inWorld={mcInWorld} screen={mcScreenOpen} pos=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) yaw={mc.Yaw:0} pitch={mc.Pitch:0} ack={mc.TeleportAck} seq={teleportSeq} flags={mc.Flags:X} fov={mc.FovDeg:0} cam={mc.CameraMode}");
			if (pc != null)
			{
				Coords.ToMc(pc.transform.position, out double x, out double y, out double z);
				sb.AppendLine($"  repo player unity={pc.transform.position} mc=({x:0.00} {y:0.00} {z:0.00}) kinematic={pc.rb.isKinematic} puppet={Puppet.Active} routing={InputBridge.Routing}");
			}
			sb.AppendLine($"  sections={BlockRenderer.Instance.SectionCount} atlas={(BlockRenderer.Instance.Atlas != null ? BlockRenderer.Instance.Atlas.width + "x" + BlockRenderer.Instance.Atlas.height : "none")} overlay frames={Overlay.Instance.FramesShown} collision epoch={epoch} pending={CollisionExporter.Instance.Pending} dug={DugBlocks.Any()} emitters={BlockLights.Instance.Emitters} lights={BlockLights.Instance.LightsOn}");
			sb.AppendLine("  " + Lobby.StateText);
			sb.Append($"  enemies={(EnemyDirector.instance != null ? EnemyDirector.instance.enemiesSpawned.Count : 0)} interp delay={ticks.RenderDelayMs:0.0}ms late={ticks.LateFrames} focused={Application.isFocused} screen={Screen.width}x{Screen.height}");
			return sb.ToString();
		}

		private void FixedUpdate()
		{
			Puppet.Instance.FixedStep();
		}

		private void LateUpdate()
		{
			if (Puppet.Active)
			{
				Puppet.Instance.LateFrame(mc, ticks, Time.unscaledDeltaTime);
			}
			bool thirdPerson = Puppet.Active && mc.CameraMode != 0;
			BlockRenderer.Instance.PlaceAvatar(Puppet.Instance.Feet, thirdPerson);
		}

		/// <summary>Everything Minecraft has of REPO's world is stale: resend it from a new epoch.</summary>
		private void ResetWorld()
		{
			epoch++;
			CollisionExporter.Instance.Reset(epoch);
			teleportPending = true;
			settle = SettleSeconds;
		}

		/// <summary>While Minecraft isn't connected, say so in REPO (once, then every couple of minutes).</summary>
		private void ReportMinecraft(bool connected, bool inGame, float dt)
		{
			if (connected)
			{
				if (told)
				{
					Notify("RepoCraft: Minecraft is ready.");
					told = false;
				}
				noteTimer = 8f;
				return;
			}
			if (!inGame)
			{
				return;
			}
			noteTimer -= dt;
			if (noteTimer > 0f)
			{
				return;
			}
			noteTimer = 120f;
			told = true;
			switch (Launcher.Current)
			{
				case Launcher.Status.NoLauncher:
					Notify("RepoCraft: no Minecraft launcher found. Install Prism Launcher (see BepInEx/config/dev.repocraft.cfg).");
					break;
				case Launcher.Status.Failed:
					Notify("RepoCraft: couldn't start Minecraft. See BepInEx/LogOutput.log.");
					break;
				case Launcher.Status.Off:
					Notify("RepoCraft: start the RepoCraft Minecraft instance to play as a Minecraft player.");
					break;
				case Launcher.Status.Unpacking:
					Notify("RepoCraft: setting up Minecraft (first start)...");
					noteTimer = 15f;
					break;
				default:
					Notify(Launcher.Bundled
						? "RepoCraft: starting Minecraft. First time: Alt-Tab to Prism Launcher and sign in with the Microsoft account that owns Minecraft (it then downloads Minecraft, a few minutes)."
						: "RepoCraft: waiting for Minecraft... (first start: sign in to Prism Launcher with your Microsoft account)");
					break;
			}
		}

		internal static void Notify(string text)
		{
			Log.Info(text);
			try
			{
				SemiFunc.UIFocusText(text, Color.white, new Color(0.4f, 1f, 0.4f), 5f);
			}
			catch (System.Exception)
			{
				// REPO's HUD isn't up (menus): the log has it.
			}
		}
	}
}
