using UnityEngine;

namespace RepoCraft.Repo
{
	/// <summary>
	/// What REPO is doing right now, from its own singletons. REPO reloads its one gameplay scene
	/// ("Main") for every level, so nothing here is cached across frames.
	/// </summary>
	internal static class Game
	{
		/// <summary>The local player exists, is spawned and alive, in a generated level that isn't a menu.</summary>
		public static bool InPlay
		{
			get
			{
				var lg = LevelGenerator.Instance;
				var gd = GameDirector.instance;
				var av = PlayerAvatar.instance;
				var pc = PlayerController.instance;
				return lg != null && lg.Generated && gd != null && av != null && pc != null && av.spawned && !av.isDisabled && !av.deadSet
					&& !SemiFunc.MenuLevel() && SpectateCamera.instance == null && pc.gameObject.activeInHierarchy
					&& (gd.currentState == GameDirector.gameState.Main || gd.currentState == GameDirector.gameState.Start);
			}
		}

		/// <summary>A level is loaded with a local player in it (dead or alive), not the main menu.</summary>
		public static bool InLevel
		{
			get
			{
				var lg = LevelGenerator.Instance;
				return lg != null && lg.Generated && PlayerAvatar.instance != null && RunManager.instance != null && !SemiFunc.MenuLevel();
			}
		}

		/// <summary>REPO is generating or switching levels (loading screen).</summary>
		public static bool Loading
		{
			get
			{
				var lg = LevelGenerator.Instance;
				var gd = GameDirector.instance;
				var rm = RunManager.instance;
				return lg == null || !lg.Generated || gd == null || gd.currentState == GameDirector.gameState.Load
					|| gd.currentState >= GameDirector.gameState.Outro && gd.currentState != GameDirector.gameState.Death
					|| (rm != null && rm.restarting);
			}
		}

		/// <summary>A REPO page (escape menu, settings...) is open or REPO text input (chat) is active: REPO gets the input.</summary>
		public static bool MenuOpen
		{
			get
			{
				var mm = MenuManager.instance;
				if (mm != null && mm.currentMenuPage != null)
				{
					return true;
				}
				return ChatManager.instance != null && !SemiFunc.NoTextInputsActive();
			}
		}

		public static bool Multiplayer => GameManager.instance != null && GameManager.Multiplayer();

		public static bool HasAuthority => SemiFunc.IsMasterClientOrSingleplayer();

		/// <summary>Which REPO level this is ("Level - Manor", "Level - Lobby", ...).</summary>
		public static string LevelName => RunManager.instance != null && RunManager.instance.levelCurrent != null ? RunManager.instance.levelCurrent.name : "";

		public static bool IsRunLevel => RunManager.instance != null && SemiFunc.RunIsLevel();

		public static Camera MainCamera => GameDirector.instance != null && GameDirector.instance.MainCamera != null ? GameDirector.instance.MainCamera : Camera.main;
	}
}
