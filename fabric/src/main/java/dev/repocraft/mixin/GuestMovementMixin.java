package dev.repocraft.mixin;

import dev.repocraft.link.HostLink;
import net.minecraft.server.network.ServerGamePacketListenerImpl;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * A REPO lobby in the host's world: each guest's own REPO moves them (a new level is a jump of
 * thousands of blocks, a door or a respawn a few dozen), so their client is the authority on where
 * they are, as the host's own is. Without this the server calls those jumps "moved too quickly"
 * and puts them back.
 */
@Mixin(ServerGamePacketListenerImpl.class)
public abstract class GuestMovementMixin {
	@Inject(method = "shouldCheckPlayerMovement", at = @At("HEAD"), cancellable = true)
	private void repocraft$guestsFollowTheirRepo(boolean fallFlying, CallbackInfoReturnable<Boolean> cir) {
		if (HostLink.active()) {
			cir.setReturnValue(false);
		}
	}

	/**
	 * The server checks each move by sweeping the player from where it last had them, against
	 * blocks only (REPO's level isn't blocks, and a guest's is only on their own PC). If it ends up
	 * elsewhere ("moved wrongly"), or the new spot touches anything new, vanilla sends the player
	 * back. For RepoCraft players that check knows less than their own client: trust the client.
	 */
	@com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation(method = "handlePlayerPositionChange", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/server/level/ServerLevel;noCollision(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;)Z"))
	private boolean repocraft$noMovedWronglyPushBack(net.minecraft.server.level.ServerLevel level, net.minecraft.world.entity.Entity entity,
		net.minecraft.world.phys.AABB box, com.llamalad7.mixinextras.injector.wrapoperation.Operation<Boolean> original) {
		return !HostLink.active() && original.call(level, entity, box);
	}

	@com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation(method = "handlePlayerPositionChange", at = @At(value = "INVOKE",
		target = "Lnet/minecraft/server/network/ServerGamePacketListenerImpl;isEntityCollidingWithAnythingNew(Lnet/minecraft/world/level/LevelReader;Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/phys/AABB;DDD)Z"))
	private boolean repocraft$noCollisionPushBack(ServerGamePacketListenerImpl self, net.minecraft.world.level.LevelReader level, net.minecraft.world.entity.Entity entity,
		net.minecraft.world.phys.AABB box, double x, double y, double z, com.llamalad7.mixinextras.injector.wrapoperation.Operation<Boolean> original) {
		return !HostLink.active() && original.call(self, level, entity, box, x, y, z);
	}

	/** Guests stand on their REPO's ground, which isn't blocks: not "standing on air" (logged every 10 s each). */
	@Inject(method = "forceSendPlayerSupportBlocks", at = @At("HEAD"), cancellable = true)
	private void repocraft$groundIsRepos(org.spongepowered.asm.mixin.injection.callback.CallbackInfo ci) {
		if (HostLink.active()) {
			ci.cancel();
		}
	}
}
