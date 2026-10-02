package dev.repocraft.client;

import dev.repocraft.RepoCraft;
import dev.repocraft.link.HostLink;
import dev.repocraft.link.Proto;
import net.minecraft.client.Minecraft;
import net.minecraft.server.MinecraftServer;
import org.jspecify.annotations.Nullable;

/**
 * REPO multiplayer: a lobby plays in one Minecraft world, the lobby host's, so everyone sees the
 * same blocks, holes and Minecraft players. REPO says what the lobby wants through the link header:
 * the host's Minecraft opens its world to friends (e4mc gives it an address, which REPO hands the
 * lobby), everyone else's joins that address. /join and /leave still work; the lobby only undoes
 * what it did itself, and leaves alone a world the player chose.
 */
public final class LobbyWorld {
	private static final long RETRY_MS = 20_000;
	private static final boolean E4MC = net.fabricmc.loader.api.FabricLoader.getInstance().isModLoaded("e4mc");

	private static @Nullable String autoJoined;  // the address we're in because the lobby asked
	private static @Nullable String optedOut;    // the lobby's address the player /left or /joined away from
	private static @Nullable String failedJoin;
	private static long failedAt;
	private static boolean autoShared;
	private static String lastLink = "";

	private LobbyWorld() {
	}

	/** Every client tick while linked. */
	static void tick(Minecraft minecraft) {
		if (!HostLink.active()) {
			return;
		}
		HostLink.MpRequest request = HostLink.readMpRequest();
		boolean share = (request.flags() & Proto.MP_SHARE) != 0;
		String join = (request.flags() & Proto.MP_JOIN) != 0 ? MirrorWorld.cleanAddress(request.join()) : "";
		if (join.isEmpty() || !join.equals(optedOut)) {
			optedOut = null;
		}
		long now = System.currentTimeMillis();

		// The lobby's host: our world, open to the others.
		var server = minecraft.getSingleplayerServer();
		if (share && server != null && minecraft.player != null && !server.isPublished()) {
			// Testing with an offline dev client as the guest.
			if (System.getenv("REPOCRAFT_LAN_OFFLINE") != null || Boolean.getBoolean("repocraft.lanOffline")) {
				server.setUsesAuthentication(false);
			}
			int port = net.minecraft.util.HttpUtil.getAvailablePort();
			autoShared = server.publishServer(MinecraftServer.MultiplayerScope.LAN, false, port);
			RepoCraft.LOG.info("RepoCraft: hosting a REPO lobby: world opened to friends on port {} ({})", port, autoShared ? "ok" : "FAILED");
		} else if (!share && autoShared && server != null && server.isPublished()) {
			autoShared = false;
			server.unpublishServer();
			RepoCraft.LOG.info("RepoCraft: no longer hosting a REPO lobby: world closed to friends");
		}

		// Everyone else: the host's world.
		String current = MirrorWorld.sessionAddress();
		if (!join.isEmpty() && !join.equals(current) && !join.equals(optedOut)
			&& !(join.equals(failedJoin) && now - failedAt < RETRY_MS)) {
			autoJoined = join;
			RepoCraft.LOG.info("RepoCraft: REPO lobby plays in {}; joining", join);
			MirrorWorld.joinFriend(minecraft, join);
			current = join;
		} else if (join.isEmpty() && autoJoined != null && autoJoined.equals(current)) {
			autoJoined = null;
			RepoCraft.LOG.info("RepoCraft: left the REPO lobby; back to our own world");
			MirrorWorld.leaveFriend(minecraft);
			current = null;
		}

		// Where we are, for REPO.
		int state;
		String link = "";
		if (current != null) {
			boolean in = minecraft.level != null && minecraft.player != null && !minecraft.isLocalServer();
			state = in ? Proto.MPS_IN_FRIEND : Proto.MPS_JOINING;
		} else if (!join.isEmpty() && join.equals(failedJoin) && now - failedAt < RETRY_MS) {
			state = Proto.MPS_JOIN_FAILED;
		} else if (server != null && server.isPublished()) {
			state = Proto.MPS_SHARING;
			String hostLink = DiscordPresence.hostLink();
			link = hostLink != null ? hostLink : E4MC ? "" : lanAddress(server.getPort());
		} else {
			state = Proto.MPS_OWN;
		}
		if (!link.equals(lastLink)) {
			lastLink = link;
			if (!link.isEmpty()) {
				RepoCraft.LOG.info("RepoCraft: telling the REPO lobby our world is at {}", link);
			}
		}
		HostLink.writeMpState(state, link, current != null ? current : "");
	}

	/**
	 * Without e4mc (your own launcher) the world is only open on this network: its LAN address
	 * (REPOCRAFT_LAN_HOST overrides, e.g. 127.0.0.1 for a second client on this PC).
	 */
	private static String lanAddress(int port) {
		String host = System.getenv("REPOCRAFT_LAN_HOST");
		if (host == null || host.isBlank()) {
			host = "127.0.0.1";
			try (var socket = new java.net.DatagramSocket()) {
				socket.connect(java.net.InetAddress.getByName("192.0.2.1"), 9); // picks the outgoing interface; sends nothing
				host = socket.getLocalAddress().getHostAddress();
			} catch (java.io.IOException | RuntimeException e) {
				// keep loopback
			}
		}
		return host.trim() + ":" + port;
	}

	/** MirrorWorld: this address couldn't be reached, or dropped us. Wait a while before trying it again. */
	static void joinFailed(String address) {
		failedJoin = address;
		failedAt = System.currentTimeMillis();
		if (address.equals(autoJoined)) {
			autoJoined = null;
		}
	}

	/** The player typed /join or /leave: the world they pick beats the lobby's. */
	static void playerChose() {
		HostLink.MpRequest request = HostLink.readMpRequest();
		if ((request.flags() & Proto.MP_JOIN) != 0) {
			optedOut = MirrorWorld.cleanAddress(request.join());
		}
		autoJoined = null;
	}
}
