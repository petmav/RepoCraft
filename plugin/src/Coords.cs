using UnityEngine;

namespace RepoCraft
{
	/// <summary>
	/// Unity (left-handed: X right, Y up, Z forward; metres) &lt;-&gt; Minecraft (right-handed: X east,
	/// Y up, Z south; blocks). Z is mirrored, so triangle winding flips between the two.
	/// Yaw: Unity 0 faces +Z, which is Minecraft north (yaw 180); both pitches are positive downward.
	///
	/// Every REPO level is generated at the same place (the truck at the origin), so each one gets its
	/// own stretch of the Minecraft world, OffsetX blocks east: what you build in one level stays there
	/// and never turns up inside the walls of the next (see LevelSlots).
	/// </summary>
	internal static class Coords
	{
		/// <summary>Blocks per metre: ours, or the lobby host's while we play in their Minecraft world.</summary>
		public static float K => Repo.Lobby.Scale > 0f ? Repo.Lobby.Scale : Config.BlocksPerMeter;

		/// <summary>Minecraft X of the current level's origin (blocks).</summary>
		public static double OffsetX;

		public static void ToMc(Vector3 u, out double x, out double y, out double z)
		{
			x = u.x * (double)K + OffsetX;
			y = u.y * (double)K;
			z = -u.z * (double)K;
		}

		/// <summary>Single precision is fine for anything relative; positions near a far slot lose ~1 mm.</summary>
		public static Vector3 ToMcF(Vector3 u) => new Vector3((float)(u.x * (double)K + OffsetX), u.y * K, -u.z * K);

		public static Vector3 ToUnity(double x, double y, double z) => new Vector3((float)((x - OffsetX) / K), (float)(y / K), (float)(-z / K));

		public static Vector3 ToUnity(Vector3 mc) => ToUnity(mc.x, mc.y, mc.z);

		/// <summary>A direction or offset (no scale, no offset).</summary>
		public static Vector3 DirToMc(Vector3 u) => new Vector3(u.x, u.y, -u.z);

		public static Vector3 DirToUnity(Vector3 mc) => new Vector3(mc.x, mc.y, -mc.z);

		public static float YawToMc(float unityYaw) => Mathf.Repeat(unityYaw + 180f, 360f);

		public static float YawToUnity(float mcYaw) => mcYaw - 180f;

		/// <summary>The Unity rotation of a Minecraft look (yaw, pitch in MC degrees).</summary>
		public static Quaternion Look(float mcYaw, float mcPitch, float rollDeg = 0f) => Quaternion.Euler(mcPitch, YawToUnity(mcYaw), rollDeg);
	}
}
