using UnityEngine;

namespace RepoCraft.Combat
{
	/// <summary>
	/// A Minecraft explosion (TNT, creepers, fireballs) in REPO's physics: loot, carts and props fly
	/// away from it (and break, losing value, like any hard knock in REPO), enemies in reach are
	/// stunned, hurt and thrown. The crater in the level itself is Minecraft's dig (HostDigBlast).
	/// </summary>
	internal static class Explosions
	{
		private static readonly Collider[] Hits = new Collider[256];

		public static void Blast(Vector3 centre, float radius)
		{
			radius = Mathf.Max(radius, 1f);
			float reach = radius * 1.6f;
			int n = Physics.OverlapSphereNonAlloc(centre, reach, Hits, ~0, QueryTriggerInteraction.Ignore);
			var seen = new System.Collections.Generic.HashSet<Object>();
			int pushed = 0, hurt = 0;
			for (int i = 0; i < n; i++)
			{
				var col = Hits[i];
				// An enemy's body colliders sit under its "Rigidbody" object; its Enemy is on a sibling.
				var enemyBody = col.GetComponentInParent<EnemyRigidbody>();
				var enemy = enemyBody != null && enemyBody.enemy != null ? enemyBody.enemy : col.GetComponentInParent<Enemy>();
				if (enemy != null)
				{
					if (!seen.Add(enemy) || !Repo.Game.HasAuthority)
					{
						continue;
					}
					Vector3 d = Combat.BodyBounds(enemy).center - centre;
					float t = 1f - Mathf.Clamp01(d.magnitude / reach);
					// Minecraft TNT does up to ~56 damage at its centre (radius 4): REPO-scaled like a hit.
					int damage = Mathf.RoundToInt(t * 14f * radius * Config.DamageToEnemies);
					Combat.ApplyHit(enemy, damage, d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.up, 25f * t, 3f * t);
					hurt++;
					continue;
				}
				var rb = col.attachedRigidbody;
				if (rb == null || rb.isKinematic || !seen.Add(rb) || !Repo.Game.HasAuthority)
				{
					continue;
				}
				if (Player.Puppet.Active && PlayerController.instance != null && rb == PlayerController.instance.rb)
				{
					continue; // Minecraft throws its own player
				}
				rb.AddExplosionForce(radius * 6f * Mathf.Max(1f, rb.mass), centre, reach, 0.6f, ForceMode.Impulse);
				pushed++;
				var pgo = rb.GetComponent<PhysGrabObject>();
				if (pgo != null)
				{
					float t = 1f - Mathf.Clamp01((rb.worldCenterOfMass - centre).magnitude / reach);
					if (t > 0.66f)
					{
						pgo.heavyBreakImpulse = true;
					}
					else if (t > 0.33f)
					{
						pgo.mediumBreakImpulse = true;
					}
					else
					{
						pgo.lightBreakImpulse = true;
					}
				}
			}
			SemiFunc.CameraShakeImpactDistance(centre, 6f, 0.25f, 4f, 20f);
			Log.Info($"Minecraft explosion (radius {radius:0.0} m): {pushed} physics objects thrown, {hurt} enemies hit");
		}
	}
}
