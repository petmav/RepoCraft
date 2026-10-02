using HarmonyLib;
using RepoCraft.Player;

namespace RepoCraft.Patches
{
	/// <summary>
	/// Minecraft owns the player's health: every point REPO would take off the local player
	/// (PlayerHealth.Hurt catches all of it, host or client) is cancelled and sent to Minecraft.
	/// REPO's attack logic still runs (cooldowns, the "hit a player" events enemy AI reacts to).
	/// </summary>
	[HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
	internal static class PlayerHurtPatch
	{
		private static bool Prefix(PlayerHealth __instance, int damage, int enemyIndex, bool hurtByHeal)
		{
			if (!Puppet.Active || Combat.Combat.Bypass || PlayerAvatar.instance == null || __instance != PlayerAvatar.instance.playerHealth)
			{
				return true;
			}
			if (hurtByHeal || damage <= 0)
			{
				return true; // giving health away to another player (a REPO action, not a hit)
			}
			Combat.Combat.Instance.PlayerHurt(damage, enemyIndex);
			return false;
		}
	}

	/// <summary>Which enemy's attack is landing (HurtCollider.PlayerHurt): the attacker Minecraft is told about.</summary>
	[HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
	internal static class HurtColliderPatch
	{
		private static void Prefix(HurtCollider __instance, PlayerAvatar _player)
		{
			if (_player != null && _player.isLocal)
			{
				Combat.Combat.HurtSource = __instance.enemyHost;
				Combat.Combat.HurtFrom = __instance.transform.position;
			}
		}

		private static void Postfix()
		{
			Combat.Combat.HurtSource = null;
		}
	}

	/// <summary>A direct kill (the arena's): Minecraft's player takes it instead, and REPO follows Minecraft's death.</summary>
	[HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.PlayerDeath))]
	internal static class PlayerDeathPatch
	{
		private static bool Prefix(PlayerAvatar __instance)
		{
			if (!Puppet.Active || Combat.Combat.Bypass || !__instance.isLocal)
			{
				return true;
			}
			Link.SharedLink.Instance.PushInput(Link.Proto.InHurt, Link.Proto.HurtOther, 1000000, 0, 0);
			return false;
		}
	}
}
