namespace RepoCraft.Repo
{
	/// <summary>
	/// REPO's HUD while Minecraft drives: its health and stamina bars and its crosshair go (Minecraft's
	/// hearts, hunger and crosshair are drawn instead); its inventory slots, haul goal, money,
	/// extraction info, map, menus and chat stay. REPO's hide calls last 0.1 s: called every frame.
	/// </summary>
	internal static class Hud
	{
		public static void Frame(bool puppet)
		{
			if (!puppet || !Config.HideRepoHud)
			{
				return;
			}
			SemiFunc.UIHideHealth();
			SemiFunc.UIHideEnergy();
			SemiFunc.UIHideAim();
		}
	}
}
