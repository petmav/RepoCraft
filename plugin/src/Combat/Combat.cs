using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using RepoCraft.Link;
using UnityEngine;

namespace RepoCraft.Combat
{
	/// <summary>
	/// REPO's enemies inside Minecraft and Minecraft's weapons inside REPO.
	///  - Every spawned enemy goes to Minecraft as an invisible, hittable stand-in (the actor table),
	///    sized from its own colliders, so Minecraft's swords, bows, tridents and TNT work on it with
	///    vanilla Minecraft code.
	///  - Minecraft's hits come back as events and are applied through REPO's own code: stun, freeze,
	///    knock-back and EnemyHealth.Hurt, exactly what REPO's own melee weapons do. REPO enemies only
	///    take damage on the host; a Minecraft player in someone else's lobby sends its hits to the
	///    host's RepoCraft (a Photon event).
	///  - REPO hurting the player is cancelled in REPO and sent to Minecraft (Patches.CombatPatches),
	///    where armour, shields and totems apply; Minecraft's health is the player's health.
	/// </summary>
	internal sealed class Combat
	{
		public static readonly Combat Instance = new Combat();

		public const byte HitEventCode = 87; // REPO uses 123 and 124; Photon reserves 200+

		private readonly ActorRecord[] records = new ActorRecord[Proto.MaxActors];
		private readonly Dictionary<Enemy, uint> ids = new Dictionary<Enemy, uint>();
		private readonly Dictionary<uint, Enemy> byId = new Dictionary<uint, Enemy>();
		private uint nextId = 1;
		private bool eventsHooked;

		/// <summary>The enemy whose attack is landing right now (set around HurtCollider.PlayerHurt).</summary>
		public static Enemy HurtSource;
		public static Vector3 HurtFrom;
		/// <summary>Our own call into REPO's damage code: let it through.</summary>
		public static bool Bypass;

		public void Clear()
		{
			ids.Clear();
			byId.Clear();
		}

		public uint IdOf(Enemy e)
		{
			if (e == null)
			{
				return 0;
			}
			if (!ids.TryGetValue(e, out uint id))
			{
				// In multiplayer the enemy's PhotonView id is the same on every client (hits go to the host by it).
				id = Repo.Game.Multiplayer && e.PhotonView != null && e.PhotonView.ViewID > 0 ? (uint)e.PhotonView.ViewID : 0x40000000u | nextId++;
				ids[e] = id;
				byId[id] = e;
			}
			return id;
		}

		public Enemy Find(uint id)
		{
			if (byId.TryGetValue(id, out Enemy e) && e != null)
			{
				return e;
			}
			if (Repo.Game.Multiplayer && id < 0x40000000u)
			{
				var view = PhotonView.Find((int)id);
				return view != null ? view.GetComponent<Enemy>() : null;
			}
			return null;
		}

		/// <summary>
		/// Main thread, every frame: actor table out, Minecraft's events in. {@code serving}: our
		/// Minecraft world is the lobby's, so the others hit the stand-ins too, alive or dead.
		/// </summary>
		public void Frame(bool puppet, bool serving)
		{
			HookEvents();
			var link = SharedLink.Instance;
			int count = puppet || serving ? BuildActors() : 0;
			link.WriteActors(records, count);
			while (link.PopEvent(out McEvent ev))
			{
				if (!puppet && !serving && ev.Type != Proto.EvPlayerDied)
				{
					continue; // stale hits don't land when control resumes
				}
				switch (ev.Type)
				{
					case Proto.EvHitActor:
						OnHit(ev);
						break;
					case Proto.EvPlayerDied:
						if (puppet)
						{
							KillRepoPlayer();
						}
						break;
					case Proto.EvExplosion:
						Explosions.Blast(Coords.ToUnity(ev.A, ev.B, ev.C), ev.D / Coords.K);
						break;
					case Proto.EvArrowStuck:
					{
						var e = Find(ev.FormId);
						var body = e != null && e.HasRigidbody ? e.Rigidbody.transform : e != null ? e.transform : null;
						Render.StuckArrows.Instance.Stick(body, new Vector3(ev.A, ev.B, ev.C), ev.D, System.BitConverter.ToSingle(System.BitConverter.GetBytes(ev.Flags), 0));
						break;
					}
				}
			}
		}

		private int BuildActors()
		{
			var director = EnemyDirector.instance;
			if (director == null || director.enemiesSpawned == null)
			{
				return 0;
			}
			int n = 0;
			foreach (var ep in director.enemiesSpawned)
			{
				if (n >= records.Length)
				{
					break;
				}
				if (ep == null || !ep.Spawned || ep.EnableObject == null || !ep.EnableObject.activeInHierarchy || ep.Enemy == null)
				{
					continue;
				}
				var e = ep.Enemy;
				bool dead = e.HasHealth && e.Health.dead;
				Bounds b = BodyBounds(e);
				float k = Coords.K;
				Coords.ToMc(new Vector3(b.center.x, b.min.y, b.center.z), out double x, out double y, out double z);
				float yaw = e.HasRigidbody ? e.Rigidbody.transform.eulerAngles.y : e.transform.eulerAngles.y;
				uint flags = Proto.ActorHostile;
				if (dead)
				{
					flags |= Proto.ActorDead;
				}
				if (e.CurrentState == EnemyState.Chase || e.CurrentState == EnemyState.ChaseBegin || e.CurrentState == EnemyState.ChaseSlow)
				{
					flags |= Proto.ActorInCombat;
				}
				float hp = 1f;
				if (e.HasHealth && e.Health.health > 0 && Repo.Game.HasAuthority)
				{
					hp = Mathf.Clamp01((float)e.Health.healthCurrent / e.Health.health);
				}
				records[n++] = new ActorRecord
				{
					FormId = IdOf(e),
					Flags = flags,
					X = (float)x,
					Y = (float)y,
					Z = (float)z,
					Yaw = Coords.YawToMc(yaw),
					Width = Mathf.Max(0.3f, Mathf.Max(b.size.x, b.size.z) * k),
					Height = Mathf.Max(0.3f, b.size.y * k),
					HealthFrac = hp,
					Level = (ushort)(1 + (int)ep.difficulty * 10),
					Name = ep.enemyName,
				};
			}
			return n;
		}

		/// <summary>The enemy's body as its own non-trigger colliders bound it (world, Unity).</summary>
		public static Bounds BodyBounds(Enemy e)
		{
			if (e.HasRigidbody && e.Rigidbody.physGrabObject != null)
			{
				var pgo = e.Rigidbody.physGrabObject;
				bool any = false;
				var b = new Bounds();
				foreach (var c in e.Rigidbody.GetComponentsInChildren<Collider>())
				{
					if (c.isTrigger || !c.enabled || c.gameObject.layer == 18 /* PlayerOnlyCollision */)
					{
						continue;
					}
					if (!any)
					{
						b = c.bounds;
						any = true;
					}
					else
					{
						b.Encapsulate(c.bounds);
					}
				}
				if (any)
				{
					return b;
				}
				return new Bounds(pgo.centerPoint, pgo.boundingBox);
			}
			var centre = e.CenterTransform != null ? e.CenterTransform.position : e.transform.position;
			return new Bounds(centre, Vector3.one * 1.5f); // Peeper (no rigidbody)
		}

		// ---- Minecraft hits a REPO enemy ---------------------------------------------------------

		private void OnHit(McEvent ev)
		{
			var e = Find(ev.FormId);
			if (e == null)
			{
				return;
			}
			int damage = Mathf.Max(1, Mathf.RoundToInt(ev.A * Config.DamageToEnemies));
			var dir = new Vector3(ev.B, 0f, -ev.C);
			if (dir.sqrMagnitude < 1e-6f)
			{
				dir = Player.Puppet.Instance.Feet != Vector3.zero ? (e.transform.position - Player.Puppet.Instance.Feet) : Vector3.forward;
				dir.y = 0f;
			}
			dir = dir.normalized;
			// Crits, sweeps and knock-back stagger; ordinary swings only flinch (Minecraft's attack rate
			// would otherwise stun-lock everything).
			bool stagger = (ev.Flags & (Proto.HitCritical | Proto.HitSweep)) != 0 || ev.D > 0.6f;
			float stun = stagger ? 1.5f : 0f;
			float force = 4f + ev.D * 10f;
			Log.Debug($"hit {e.EnemyParent?.enemyName} for {damage} (MC {ev.A:0.0}, knockback {ev.D:0.00}, weapon {ev.Weapon})");
			if (Repo.Game.HasAuthority)
			{
				ApplyHit(e, damage, dir, force, stun);
			}
			else if (e.PhotonView != null)
			{
				PhotonNetwork.RaiseEvent(HitEventCode, new object[] { e.PhotonView.ViewID, damage, dir, force, stun },
					new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
			}
		}

		/// <summary>What REPO's own melee weapons do to an enemy (HurtCollider.EnemyHurt). Host only.</summary>
		public static void ApplyHit(Enemy e, int damage, Vector3 dir, float force, float stun)
		{
			if (e == null || (e.HasHealth && e.Health.dead))
			{
				return;
			}
			if (e.HasStateStunned && stun > 0f)
			{
				e.StateStunned.Set(stun);
			}
			const float freeze = 0.1f;
			e.Freeze(freeze);
			if (e.HasRigidbody && e.Rigidbody.physGrabObject != null)
			{
				var pgo = e.Rigidbody.physGrabObject;
				pgo.mediumBreakImpulse = true;
				Vector3 f = dir * force, t = Vector3.Cross(dir, -pgo.transform.up) * force;
				e.Rigidbody.FreezeForces(f, t);
			}
			if (damage > 0 && e.HasHealth)
			{
				e.Health.Hurt(damage, dir);
			}
		}

		private void HookEvents()
		{
			if (eventsHooked || PhotonNetwork.NetworkingClient == null)
			{
				return;
			}
			eventsHooked = true;
			PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;
		}

		/// <summary>Host: a Minecraft player in our lobby hit one of our enemies.</summary>
		private static void OnPhotonEvent(EventData data)
		{
			if (data.Code != HitEventCode || !SemiFunc.IsMasterClientOrSingleplayer() || !(data.CustomData is object[] args) || args.Length < 5)
			{
				return;
			}
			var view = PhotonView.Find((int)args[0]);
			var e = view != null ? view.GetComponent<Enemy>() : null;
			ApplyHit(e, (int)args[1], (Vector3)args[2], (float)args[3], (float)args[4]);
		}

		// ---- REPO hurts the player -----------------------------------------------------------------

		/// <summary>REPO damage on the player, re-routed to Minecraft (amount in REPO health points).</summary>
		public void PlayerHurt(int damage, int enemyIndex)
		{
			var source = HurtSource != null ? HurtSource : SemiFunc.EnemyGetFromIndex(enemyIndex);
			uint attacker = source != null ? IdOf(source) : 0;
			ushort kind = source != null ? Proto.HurtMelee : Proto.HurtOther;
			// REPO's damage scaled to Minecraft's: the protocol carries "host damage * 100", and the
			// Minecraft side divides by its own factor (HostCombat.HOST_TO_MC_DAMAGE, 5): pre-scale so
			// the config's DamageToPlayer is the real ratio.
			float mcDamage = damage * Config.DamageToPlayer;
			int wire = Mathf.RoundToInt(mcDamage * 5f * 100f);
			SharedLink.Instance.PushInput(Proto.InHurt, kind, wire, (int)attacker, 0);
			Log.Debug($"REPO hurt the player for {damage} ({source?.EnemyParent?.enemyName ?? "no enemy"}): Minecraft takes {mcDamage:0.0}");
		}

		/// <summary>Minecraft's player died: REPO's player dies too (its death camera, head and revive follow).</summary>
		public void KillRepoPlayer()
		{
			var av = PlayerAvatar.instance;
			if (av == null || av.deadSet)
			{
				return;
			}
			Log.Info("Minecraft's player died: killing REPO's");
			Render.Ragdoll.Instance.Drop(Player.Puppet.Instance.Feet, Player.Puppet.Instance.Yaw, Player.Puppet.Instance.Velocity);
			Bypass = true;
			try
			{
				av.PlayerDeath(-1);
			}
			finally
			{
				Bypass = false;
			}
		}
	}
}
