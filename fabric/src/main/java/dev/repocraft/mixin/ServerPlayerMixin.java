package dev.repocraft.mixin;

import dev.repocraft.RepoCraft;
import dev.repocraft.combat.HostCombat;
import dev.repocraft.combat.HostActorEntity;
import dev.repocraft.link.Proto;
import dev.repocraft.link.HostLink;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.server.level.ServerPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(ServerPlayer.class)
public abstract class ServerPlayerMixin {
	/** Critical hits on a REPO actor are flagged so REPO can play them up. */
	@Inject(method = "crit", at = @At("HEAD"))
	private void repocraft$critHost(Entity entity, CallbackInfo ci) {
		if (entity instanceof HostActorEntity proxy) {
			proxy.markCritical();
		}
	}

	/** Dying in Minecraft is dying in REPO: the host's through the link, a guest's through theirs. */
	@Inject(method = "die", at = @At("HEAD"))
	private void repocraft$diesInHost(DamageSource source, CallbackInfo ci) {
		ServerPlayer self = (ServerPlayer) (Object) this;
		int attacker = HostCombat.attackerFormId(source);
		if (!dev.repocraft.net.RepoNet.isHost(self)) {
			if (net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking.canSend(self, dev.repocraft.net.RepoNet.Died.TYPE)) {
				net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking.send(self, new dev.repocraft.net.RepoNet.Died(attacker));
			}
			RepoCraft.LOG.info("RepoCraft: guest {} died ({}); telling their REPO", self.getPlainTextName(), source.getMsgId());
			return;
		}
		if (HostLink.active()) {
			HostLink.pushEvent(Proto.EV_PLAYER_DIED, attacker, 0, 0, 0, 0, 0);
			RepoCraft.LOG.info("RepoCraft: player died ({}); telling REPO", source.getMsgId());
		}
	}
}
