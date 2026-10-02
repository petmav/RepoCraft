using UnityEngine;

namespace RepoCraft.Input
{
	/// <summary>
	/// A left-click is REPO's grab when the crosshair is on something REPO's beam can take (within its
	/// range, nearer than the block Minecraft has targeted), and Minecraft's attack/mine otherwise.
	/// Enemies are always Minecraft's to hit.
	/// </summary>
	internal static class Arbiter
	{
		private static int mask = -1;

		public static bool RepoGrabTarget()
		{
			var grabber = PhysGrabber.instance;
			var cam = Repo.Game.MainCamera;
			if (grabber == null || !grabber.enabled || cam == null)
			{
				return false;
			}
			if (mask == -1)
			{
				// The beam's own mask: Default + PhysGrabObject + Cart + Hinge + StaticGrabObject.
				mask = SemiFunc.LayerMaskGetVisionObstruct() & ~LayerMask.GetMask("Player");
			}
			Vector3 origin = Player.Puppet.Active ? Player.Puppet.Instance.Eye : cam.transform.position;
			Vector3 dir = cam.transform.forward;
			if (!Physics.Raycast(origin, dir, out RaycastHit hit, 10f, mask, QueryTriggerInteraction.Ignore))
			{
				return false;
			}
			if (!hit.collider.CompareTag("Phys Grab Object") || hit.distance > grabber.grabRange)
			{
				return false;
			}
			if (hit.collider.GetComponentInParent<EnemyRigidbody>() != null || hit.collider.GetComponentInParent<Enemy>() != null)
			{
				return false;
			}
			float block = Render.EntityRenderer.Instance.SelectionDistance(origin);
			return !(block < hit.distance - 0.05f);
		}
	}
}
