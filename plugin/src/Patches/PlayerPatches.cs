using HarmonyLib;
using RepoCraft.Input;
using RepoCraft.Player;
using UnityEngine;

namespace RepoCraft.Patches
{
	/// <summary>REPO's own movement code stands down while Minecraft drives the player.</summary>
	[HarmonyPatch(typeof(PlayerController))]
	internal static class PlayerControllerPatches
	{
		// Input movement, gravity, jumping, sliding, knockback application, tumble follow.
		[HarmonyPrefix, HarmonyPatch("FixedUpdate")]
		private static bool FixedUpdate() => !Puppet.Active;

		// Crouch/sprint toggles, jump buffering, landing, energy, tumble input, and the per-frame
		// "rb.isKinematic = false" that would undo the puppet.
		[HarmonyPrefix, HarmonyPatch("Update")]
		private static bool Update() => !Puppet.Active;

		// Knock-back REPO wants to give the player: Minecraft's own knock-back comes with the hit.
		[HarmonyPrefix, HarmonyPatch(nameof(PlayerController.ForceImpulse))]
		private static bool ForceImpulse() => !Puppet.Active;

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerController.MoveForce))]
		private static bool MoveForce() => !Puppet.Active;

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerController.Kinematic))]
		private static bool Kinematic() => !Puppet.Active;
	}

	/// <summary>No REPO fall damage: Minecraft has its own.</summary>
	[HarmonyPatch(typeof(PlayerCollisionController), "Update")]
	internal static class FallPatch
	{
		private static void Postfix(PlayerCollisionController __instance)
		{
			if (Puppet.Active)
			{
				__instance.ResetFalling();
			}
		}
	}

	/// <summary>
	/// No tumbling (REPO's ragdoll mode) while Minecraft drives: the body is Minecraft's. Enemies that
	/// would grab and carry a tumbling player can't; their damage still arrives through Hurt.
	/// </summary>
	[HarmonyPatch(typeof(PlayerTumble))]
	internal static class TumblePatches
	{
		private static bool Local(PlayerTumble t) => Puppet.Active && t != null && t.playerAvatar != null && t.playerAvatar.isLocal;

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerTumble.TumbleRequest))]
		private static bool TumbleRequest(PlayerTumble __instance, bool _isTumbling) => !(_isTumbling && Local(__instance));

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerTumble.TumbleSet))]
		private static bool TumbleSet(PlayerTumble __instance, bool _isTumbling) => !(_isTumbling && Local(__instance));

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerTumble.TumbleForce))]
		private static bool TumbleForce(PlayerTumble __instance) => !Local(__instance);

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerTumble.TumbleTorque))]
		private static bool TumbleTorque(PlayerTumble __instance) => !Local(__instance);

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerTumble.TumbleOverrideTime))]
		private static bool TumbleOverrideTime(PlayerTumble __instance) => !Local(__instance);

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerTumble.ImpactHurtSet))]
		private static bool ImpactHurtSet(PlayerTumble __instance) => !Local(__instance);

		// A host in multiplayer may still tumble us: get straight back up.
		[HarmonyPostfix, HarmonyPatch(nameof(PlayerTumble.TumbleSetRPC))]
		private static void TumbleSetRPC(PlayerTumble __instance, bool _isTumbling)
		{
			if (_isTumbling && Local(__instance))
			{
				__instance.TumbleRequest(false, true);
			}
		}
	}

	/// <summary>REPO reads gameplay input only through InputManager: while Minecraft drives, it reads our virtual keys.</summary>
	[HarmonyPatch(typeof(InputManager))]
	internal static class InputManagerPatches
	{
		private static bool Swallow(InputKey key) => InputBridge.Routing && !InputBridge.PassThrough(key);

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.KeyDown))]
		private static bool KeyDown(InputKey key, ref bool __result)
		{
			if (!Swallow(key))
			{
				return true;
			}
			var b = InputBridge.Instance;
			__result = key switch
			{
				InputKey.Push => b.Scroll > 0f,
				InputKey.Pull => b.Scroll < 0f,
				_ => b.IsDown(key),
			};
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.KeyUp))]
		private static bool KeyUp(InputKey key, ref bool __result)
		{
			if (!Swallow(key))
			{
				return true;
			}
			__result = InputBridge.Instance.IsUp(key);
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.KeyHold))]
		private static bool KeyHold(InputKey key, ref bool __result)
		{
			if (!Swallow(key))
			{
				return true;
			}
			__result = InputBridge.Instance.IsHeld(key);
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.KeyPullAndPush))]
		private static bool KeyPullAndPush(ref float __result)
		{
			if (!InputBridge.Routing)
			{
				return true;
			}
			__result = InputBridge.Instance.Scroll;
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.GetScrollY))]
		private static bool GetScrollY(ref float __result)
		{
			if (!InputBridge.Routing)
			{
				return true;
			}
			__result = InputBridge.Instance.Scroll;
			return false;
		}

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.GetMovementX))]
		private static bool GetMovementX(ref float __result) => Zero(ref __result);

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.GetMovementY))]
		private static bool GetMovementY(ref float __result) => Zero(ref __result);

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.GetMovement))]
		private static bool GetMovement(ref Vector2 __result)
		{
			if (!InputBridge.Routing)
			{
				return true;
			}
			__result = Vector2.zero;
			return false;
		}

		// Mouse look is Minecraft's, except while REPO turns a carried thing with the right button.
		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.GetMouseX))]
		private static bool GetMouseX(ref float __result) => InputBridge.Instance.Rotating || Zero(ref __result);

		[HarmonyPrefix, HarmonyPatch(nameof(InputManager.GetMouseY))]
		private static bool GetMouseY(ref float __result) => InputBridge.Instance.Rotating || Zero(ref __result);

		private static bool Zero(ref float result)
		{
			if (!InputBridge.Routing)
			{
				return true;
			}
			result = 0f;
			return false;
		}
	}
}
