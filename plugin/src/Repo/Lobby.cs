using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using RepoCraft.Link;
using UnityEngine;

namespace RepoCraft.Repo
{
	/// <summary>
	/// REPO multiplayer with one Minecraft world for the whole lobby: the host's. The host's Minecraft
	/// opens its world to friends (e4mc gives it an address), and the address goes in the Photon
	/// room's properties, along with the Minecraft slot of each level the host enters; everyone
	/// else's Minecraft joins that address and maps each level to the host's slot. Then all of them
	/// see the same blocks, holes and Minecraft players. A player whose Minecraft is in the lobby's
	/// world says so in their Photon player properties, and the others draw their Minecraft body
	/// instead of their robot.
	/// </summary>
	internal static class Lobby
	{
		private const string WorldKey = "rcWorld"; // room: the host's Minecraft world address
		private const string ScaleKey = "rcK";     // room: the host's blocks per metre
		private const string LevelKey = "rcLevel"; // room: "entry|level name#levels completed|slot|world id", the host's level
		private const string InKey = "rcIn";       // player: their Minecraft is in the lobby's world
		private const float HostLevelWait = 20f;   // seconds a guest waits for the host to say which slot

		private static string publishedWorld;
		private static float publishedScale;
		private static bool? publishedIn;
		private static int hostEntry = (System.Environment.TickCount & 0xFFFF) * 1000;
		private static string usedEntry;
		private static bool slotFromHost;
		private static uint mcState;
		private static string requested = "";
		private static uint lastNoted = uint.MaxValue;
		private static readonly Dictionary<PlayerAvatar, List<Renderer>> hidden = new Dictionary<PlayerAvatar, List<Renderer>>();
		private static readonly List<PlayerAvatar> gone = new List<PlayerAvatar>();

		/// <summary>In a Photon room with other players (not REPO's single player).</summary>
		private static bool InRoom => Game.Multiplayer && PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null;

		/// <summary>We host the lobby: our Minecraft world is the lobby's.</summary>
		public static bool Hosting => InRoom && PhotonNetwork.IsMasterClient && Config.ShareWorld;

		/// <summary>The lobby host's Minecraft world address (empty: the host has no RepoCraft, or it isn't open yet).</summary>
		public static string HostWorld => InRoom && !PhotonNetwork.IsMasterClient && Config.ShareWorld
			&& PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(WorldKey, out object v) && v is string s ? s : "";

		/// <summary>The host's blocks per metre while we play in their world (0: ours).</summary>
		public static float HostScale => HostWorld.Length > 0 && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(ScaleKey, out object v) && v is float k && k > 0f ? k : 0f;

		/// <summary>Our Minecraft is in the lobby's world: the host sharing it, or a guest in it.</summary>
		public static bool InLobbyWorld { get; private set; }

		/// <summary>HostScale as of this frame (Coords reads it everywhere, the collision worker too).</summary>
		public static float Scale { get; private set; }

		/// <summary>Once a frame: tell Minecraft what the lobby wants, and the lobby where Minecraft is.</summary>
		public static void Frame(SharedLink link)
		{
			bool hosting = Hosting;
			string join = HostWorld;
			Scale = HostScale;
			uint flags = hosting ? Proto.MpShare : join.Length > 0 ? Proto.MpJoin : 0u;
			link.WriteMpRequest(flags, join);
			requested = join;

			mcState = link.ReadMpState(out string shareLink, out string friendWorld);
			InLobbyWorld = hosting ? mcState == Proto.MpsSharing && shareLink.Length > 0
				: join.Length > 0 && mcState == Proto.MpsInFriend && string.Equals(friendWorld, join, System.StringComparison.OrdinalIgnoreCase);

			if (InRoom && PhotonNetwork.IsMasterClient)
			{
				string world = hosting && mcState == Proto.MpsSharing ? shareLink : "";
				if (world != publishedWorld || Config.BlocksPerMeter != publishedScale)
				{
					publishedWorld = world;
					publishedScale = Config.BlocksPerMeter;
					PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { WorldKey, world }, { ScaleKey, Config.BlocksPerMeter } });
					Log.Info(world.Length > 0 ? $"lobby: our Minecraft world is the lobby's ({world})" : "lobby: no shared Minecraft world");
				}
			}
			else
			{
				publishedWorld = null;
			}
			if (InRoom && publishedIn != InLobbyWorld)
			{
				publishedIn = InLobbyWorld;
				PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { InKey, InLobbyWorld } });
			}
			else if (!InRoom)
			{
				publishedIn = null;
			}
			NoteState(hosting, join);
			Robots();
		}

		/// <summary>The host entered a level: tell the lobby which slot it's in.</summary>
		public static void HostEntered()
		{
			if (!InRoom || !PhotonNetwork.IsMasterClient)
			{
				return;
			}
			hostEntry++;
			string value = $"{hostEntry}|{LevelKeyNow}|{LevelSlots.Current}|{LevelSlots.WorldId}";
			PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { LevelKey, value } });
			Log.Info($"lobby: level slot {value}");
		}

		/// <summary>
		/// A new level is up: whether its slot is decided yet. A guest in the host's world takes the
		/// host's slot for it (slot -1: work it out ourselves), and waits a while for the host to say.
		/// </summary>
		public static bool PickSlot(float waited, out int slot, out uint worldId)
		{
			slot = -1;
			worldId = 0;
			slotFromHost = false;
			if (HostWorld.Length == 0)
			{
				return true;
			}
			if (TryHostLevel(out string entry, out slot, out worldId) && entry != usedEntry)
			{
				usedEntry = entry;
				slotFromHost = true;
				return true;
			}
			slot = -1;
			if (waited > HostLevelWait)
			{
				Log.Warn($"lobby: the host never said which Minecraft slot '{Game.LevelName}' is; using our own");
				return true;
			}
			return false;
		}

		/// <summary>We picked this level's slot ourselves, and now the host has said (its Minecraft world came up late).</summary>
		public static bool SlotStale => !slotFromHost && HostWorld.Length > 0 && TryHostLevel(out string entry, out _, out _) && entry != usedEntry;

		/// <summary>Which level this is, the same on every client (REPO syncs the name and the run's level count).</summary>
		private static string LevelKeyNow => Game.LevelName + "#" + (RunManager.instance != null ? RunManager.instance.levelsCompleted : 0);

		private static bool TryHostLevel(out string entry, out int slot, out uint worldId)
		{
			entry = null;
			slot = -1;
			worldId = 0;
			if (!PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(LevelKey, out object v) || !(v is string s))
			{
				return false;
			}
			var parts = s.Split('|');
			if (parts.Length != 4 || parts[1] != LevelKeyNow || !int.TryParse(parts[2], out slot) || !uint.TryParse(parts[3], out worldId))
			{
				slot = -1;
				return false;
			}
			entry = parts[0];
			return true;
		}

		/// <summary>Tell the player when their Minecraft goes into (or can't reach) the lobby's world.</summary>
		private static void NoteState(bool hosting, string join)
		{
			uint key = hosting ? 100u + (InLobbyWorld ? 1u : 0u) : join.Length > 0 ? mcState : uint.MaxValue - 1;
			if (key == lastNoted)
			{
				return;
			}
			lastNoted = key;
			if (hosting && InLobbyWorld)
			{
				Driver.Notify("RepoCraft: your Minecraft world is open to the lobby. Friends with RepoCraft play in it with you.");
			}
			else if (!hosting && join.Length > 0 && mcState == Proto.MpsInFriend && InLobbyWorld)
			{
				Driver.Notify("RepoCraft: you're in the host's Minecraft world.");
			}
			else if (!hosting && join.Length > 0 && mcState == Proto.MpsJoinFailed)
			{
				Driver.Notify("RepoCraft: couldn't reach the host's Minecraft world; trying again shortly.");
			}
		}

		/// <summary>
		/// Other RepoCraft players in the lobby's world: their Minecraft body is drawn (from the shared
		/// world), so their robot isn't. The flashlight's light, nametag, grab beam and death head stay.
		/// </summary>
		private static void Robots()
		{
			var gd = GameDirector.instance;
			gone.Clear();
			foreach (var kv in hidden)
			{
				if (kv.Key == null || gd == null || !gd.PlayerList.Contains(kv.Key) || !ShowsMinecraftBody(kv.Key))
				{
					gone.Add(kv.Key);
				}
			}
			foreach (var avatar in gone)
			{
				foreach (var r in hidden[avatar])
				{
					if (r != null)
					{
						r.enabled = true;
					}
				}
				hidden.Remove(avatar);
			}
			if (gd == null || !InLobbyWorld)
			{
				return;
			}
			foreach (var avatar in gd.PlayerList)
			{
				if (avatar == null || !ShowsMinecraftBody(avatar))
				{
					continue;
				}
				if (!hidden.TryGetValue(avatar, out var renderers))
				{
					renderers = new List<Renderer>();
					foreach (var r in avatar.playerAvatarVisuals.GetComponentsInChildren<Renderer>(true))
					{
						if (r is MeshRenderer || r is SkinnedMeshRenderer)
						{
							renderers.Add(r);
						}
					}
					hidden[avatar] = renderers;
					Log.Info($"lobby: {avatar.playerName} plays in the lobby's Minecraft world; showing their Minecraft body");
				}
				foreach (var r in renderers)
				{
					if (r != null && r.enabled)
					{
						r.enabled = false;
					}
				}
			}
		}

		private static bool ShowsMinecraftBody(PlayerAvatar avatar)
		{
			if (avatar.isLocal || avatar.playerAvatarVisuals == null || avatar.photonView == null || avatar.photonView.Owner == null || !InLobbyWorld)
			{
				return false;
			}
			return avatar.photonView.Owner.CustomProperties.TryGetValue(InKey, out object v) && v is bool b && b;
		}

		/// <summary>Where the other players in the lobby's world are (Minecraft coordinates), for collision around them.</summary>
		public static void OthersInWorld(List<Vector3> into)
		{
			into.Clear();
			var gd = GameDirector.instance;
			if (gd == null || !InLobbyWorld)
			{
				return;
			}
			foreach (var avatar in gd.PlayerList)
			{
				if (avatar != null && ShowsMinecraftBody(avatar))
				{
					into.Add(Coords.ToMcF(avatar.transform.position));
				}
			}
		}

		public static string StateText => $"lobby: hosting={Hosting} inWorld={InLobbyWorld} mc={mcState} join='{requested}' slotFromHost={slotFromHost} robotsHidden={hidden.Count}";
	}
}
