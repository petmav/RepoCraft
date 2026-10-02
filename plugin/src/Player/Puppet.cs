using RepoCraft.Link;
using UnityEngine;

namespace RepoCraft.Player
{
	/// <summary>
	/// REPO's local player as Minecraft's puppet. Minecraft is authoritative for where the player is:
	/// REPO's own movement code is switched off (Patches.PlayerControllerPatches) and its Rigidbody is
	/// moved kinematically to Minecraft's feet, so it still pushes doors and loot, is what enemies
	/// see, chase and hit, and is what other players see over the network. The camera rig goes to
	/// Minecraft's eye with Minecraft's look, FOV, view bob and F5 camera.
	/// </summary>
	internal sealed class Puppet
	{
		public static readonly Puppet Instance = new Puppet();

		/// <summary>Minecraft drives the player (read by the Harmony patches).</summary>
		public static bool Active;

		private bool wasActive;
		private PlayerController pc;
		private CapsuleCollider capsule;
		private float savedRadius, savedHeight;
		private Vector3 savedCenter;
		private RigidbodyInterpolation savedInterp;

		// This frame's pose (Unity space).
		public Vector3 Feet { get; private set; }
		public Vector3 Eye { get; private set; }
		public float Yaw { get; private set; }    // MC degrees
		public float Pitch { get; private set; }  // MC degrees
		private Vector3 lastFeet;
		private Vector3 velocity;
		private bool haveLast;
		private float zoom;
		private bool flashlightHidden;
		private uint zoomMode;

		/// <summary>Where we last put the body: REPO moving it further than this from here is REPO teleporting the player.</summary>
		public Vector3 LastSet { get; private set; }
		public bool HaveLastSet { get; private set; }

		public void Release()
		{
			if (!wasActive)
			{
				return;
			}
			wasActive = false;
			Active = false;
			HaveLastSet = false;
			haveLast = false;
			if (pc != null && pc.rb != null)
			{
				pc.rb.isKinematic = false;
				pc.rb.interpolation = savedInterp;
			}
			if (capsule != null)
			{
				capsule.radius = savedRadius;
				capsule.height = savedHeight;
				capsule.center = savedCenter;
			}
			if (pc != null && pc.PlayerCollision != null)
			{
				pc.PlayerCollision.transform.localScale = Vector3.one;
			}
			Log.Info("puppet off: REPO drives its player again");
		}

		/// <summary>Update: the interpolated Minecraft pose for this frame.</summary>
		public void Frame(McState mc, TickInterpolator ticks, float yaw, float pitch, float dt)
		{
			pc = PlayerController.instance;
			if (pc == null)
			{
				Active = false;
				return;
			}
			if (!wasActive)
			{
				wasActive = true;
				capsule = pc.col as CapsuleCollider;
				if (capsule != null)
				{
					savedRadius = capsule.radius;
					savedHeight = capsule.height;
					savedCenter = capsule.center;
				}
				savedInterp = pc.rb.interpolation;
				pc.rb.interpolation = RigidbodyInterpolation.Interpolate;
				Log.Info("puppet on: Minecraft drives the player");
			}
			Active = true;
			Yaw = yaw;
			Pitch = pitch;
			Feet = Coords.ToUnity(ticks.FeetX, ticks.FeetY, ticks.FeetZ);
			Eye = Feet + Vector3.up * (float)(ticks.EyeHeight / Coords.K);
			if (haveLast && dt > 0f)
			{
				velocity = Vector3.Lerp(velocity, (Feet - lastFeet) / dt, 1f - Mathf.Exp(-dt / 0.08f));
			}
			lastFeet = Feet;
			haveLast = true;

			// What REPO reads about its player: crouching (enemy vision height, footsteps), moving,
			// sprinting, local velocity (other players' walk animation), stamina.
			bool sneak = mc.Has(Proto.McSneaking);
			bool crawl = mc.Has(Proto.McSwimming) && !mc.Has(Proto.McFlying);
			bool sprint = mc.Has(Proto.McSprinting);
			var flat = new Vector3(velocity.x, 0f, velocity.z);
			bool moving = flat.magnitude > 0.3f;
			if (pc.Crouching != (sneak || crawl) || pc.Crawling != crawl || pc.sprinting != sprint || pc.moving != moving)
			{
				pc.Crouching = sneak || crawl;
				pc.Crawling = crawl;
				pc.sprinting = sprint;
				pc.moving = moving;
				pc.ChangeState();
			}
			pc.VelocityRelative = pc.transform.InverseTransformDirection(velocity);
			pc.Velocity = velocity;
			pc.InputDirection = moving ? pc.transform.InverseTransformDirection(flat.normalized) : Vector3.zero;
			pc.EnergyCurrent = Mathf.Max(pc.EnergyCurrent, 1f);
			if (pc.tumbleInputDisableTimer > 0f)
			{
				pc.tumbleInputDisableTimer -= dt;
			}
			pc.CollisionController?.ResetFalling();
			if (pc.CollisionGrounded != null)
			{
				pc.CollisionGrounded.Grounded = mc.Has(Proto.McOnGround);
			}
			if (pc.playerAvatarScript != null)
			{
				pc.playerAvatarScript.rbVelocityRaw = velocity;
			}
		}

		/// <summary>FixedUpdate: the body follows the latest pose (kinematic, so it pushes what it walks into).</summary>
		public void FixedStep()
		{
			if (!Active || pc == null || pc.rb == null)
			{
				return;
			}
			// Timers REPO counts down in the updates we skip: without these, one menu (or a revive)
			// would block grabbing and item use for good.
			if (pc.InputDisableTimer > 0f)
			{
				pc.InputDisableTimer -= Time.fixedDeltaTime;
			}
			var rb = pc.rb;
			rb.isKinematic = true;
			rb.MovePosition(Feet);
			rb.MoveRotation(Quaternion.Euler(0f, Coords.YawToUnity(Yaw), 0f));
			LastSet = Feet;
			HaveLastSet = true;
		}

		/// <summary>LateUpdate: hitbox and camera, after all of REPO's own camera scripts have run.</summary>
		public void LateFrame(McState mc, TickInterpolator ticks, float dt)
		{
			if (!Active || pc == null)
			{
				return;
			}
			// Minecraft's hitbox: 0.6 x 1.8 blocks standing, 1.5 sneaking, 0.6 crawling/swimming.
			if (capsule != null)
			{
				float k = Coords.K;
				float h = mc.Has(Proto.McSwimming) ? 0.6f : mc.Has(Proto.McSneaking) ? 1.5f : 1.8f;
				capsule.radius = 0.3f / k;
				capsule.height = h / k;
				capsule.center = new Vector3(0f, h * 0.5f / k, 0f);
				if (pc.PlayerCollision != null)
				{
					pc.PlayerCollision.transform.localScale = Vector3.one; // REPO squashes it when crouching
				}
			}
			if (SpectateCamera.instance != null)
			{
				return; // REPO's death camera owns the rig
			}
			// REPO's first-person flashlight model would float in front of Minecraft's F5 camera: its
			// light stays, the model goes while the camera is out of the head.
			var flashlight = FlashlightController.Instance;
			bool detachedNow = mc.CameraMode != 0;
			if (flashlight != null && flashlight.mesh != null && (detachedNow || flashlightHidden))
			{
				flashlight.mesh.enabled = !detachedNow;
				flashlightHidden = detachedNow;
			}
			DriveCamera(mc, ticks, dt);
		}

		private void DriveCamera(McState mc, TickInterpolator ticks, float dt)
		{
			var aim = CameraAim.Instance;
			var position = CameraPosition.instance;
			var cam = Repo.Game.MainCamera;
			if (aim == null || position == null || cam == null)
			{
				return;
			}
			var gm = GameplayManager.instance;
			if (gm != null)
			{
				gm.OverrideCameraAnimation(0f, 0.2f);
				gm.OverrideCameraNoise(0f, 0.2f);
			}

			// Minecraft's walk bob (GameRenderer.bobView), as a camera offset: sway sideways, lift,
			// and dip and roll the view.
			float phase = ticks.BobPhase * Mathf.PI;
			float bob = ticks.BobAmount;
			float side = -Mathf.Sin(phase) * bob * 0.5f;
			float lift = Mathf.Abs(Mathf.Cos(phase) * bob);
			float bobPitch = Mathf.Abs(Mathf.Cos(phase - 0.2f) * bob) * 5f;
			float bobRoll = Mathf.Sin(phase) * bob * 3f;

			float yaw = Yaw, pitch = Pitch;
			Vector3 eye = Eye;
			// Minecraft's F5: behind the player, or in front looking back, pulled in where Minecraft's
			// own zoom collision stopped it; eased back out after it pulls in.
			bool detached = mc.CameraMode != 0 && mc.CameraDistance > 0f;
			if (!detached || zoomMode != mc.CameraMode || mc.CameraDistance < zoom)
			{
				zoom = detached ? mc.CameraDistance : 0f;
			}
			else
			{
				zoom += (mc.CameraDistance - zoom) * (1f - Mathf.Exp(-Mathf.Max(dt, 0f) / 0.2f));
			}
			zoomMode = mc.CameraMode;
			if (mc.CameraMode == 2)
			{
				yaw += 180f;
				pitch = -pitch;
			}
			Quaternion look = Coords.Look(yaw, pitch);
			if (detached)
			{
				eye -= look * Vector3.forward * (zoom / Coords.K);
			}

			var posT = position.transform;
			posT.position = eye;
			posT.localRotation = Quaternion.identity;
			if (CameraCrouchPosition.instance != null)
			{
				CameraCrouchPosition.instance.transform.localPosition = Vector3.zero;
			}
			if (CameraCrawlPosition.instance != null)
			{
				CameraCrawlPosition.instance.transform.localPosition = Vector3.zero;
			}
			aim.SetPlayerAim(look, true);
			// Everything between the aim and the lens is REPO's animation (crouch dip, bob, noise,
			// intro look-down, jump, tilt): zeroed, except its screen shakes.
			var aimT = aim.transform;
			var shake = GameDirector.instance != null ? GameDirector.instance.CameraShake : null;
			var impact = GameDirector.instance != null ? GameDirector.instance.CameraImpact : null;
			for (Transform t = cam.transform.parent; t != null && t != aimT; t = t.parent)
			{
				if ((shake != null && t == shake.transform) || (impact != null && t == impact.transform))
				{
					continue;
				}
				t.localPosition = Vector3.zero;
				t.localRotation = Quaternion.identity;
			}
			cam.transform.localPosition = (Vector3.right * side + Vector3.up * lift) / Coords.K;
			cam.transform.localRotation = Quaternion.Euler(bobPitch, 0f, bobRoll);

			// Minecraft's FOV is vertical, like Unity's (sprint and fluid FOV effects included).
			if (mc.FovDeg > 1f && CameraZoom.Instance != null)
			{
				foreach (var c in CameraZoom.Instance.cams)
				{
					if (c != null)
					{
						c.fieldOfView = mc.FovDeg;
					}
				}
			}
		}

		/// <summary>How fast the Minecraft player moves (Unity m/s), for REPO's own systems.</summary>
		public Vector3 Velocity => velocity;
	}
}
