namespace RepoCraft.Link
{
	/// <summary>
	/// Byte layout of the shared-memory link. Mirror of protocol/repocraft_protocol.h and the Fabric
	/// mod's dev.repocraft.link.Proto; keep all three in sync and bump Version together.
	/// Everything is little-endian, in Minecraft coordinates (blocks, Y up, Z south) unless noted.
	/// </summary>
	internal static class Proto
	{
		public const uint Magic = 0x43504552; // "REPC"
		public const uint Version = 12;
		// A stand-in second REPO on the same PC (tools/FakeRepo --guest) talks to its own Minecraft:
		// REPOCRAFT_LINK=Local\RepoCraft_guest for both.
		public static readonly string MappingName = System.Environment.GetEnvironmentVariable("REPOCRAFT_LINK") is string link && link.Length > 0 ? link : "Local\\RepoCraft_v1";

		// ---- regions ---------------------------------------------------------------------------
		public const long OffHeader = 0x0;
		public const long OffHostState = 0x100;
		public const long OffMcState = 0x200;
		public const long OffOverlayCtl = 0x300;
		public const long OffOverlaySlotHdr = 0x340; // 3 x 0x40
		public const long OffWaterGrid = 0x400;
		public const long OffInputRing = 0x1000;
		public const long OffActorTable = 0x12000;
		public const long OffEventRing = 0x17000;
		public const long OffWorldEntities = 0x1C000;
		public const long OffCollisionRing = 0x20000;
		public const long CollisionRingBytes = 32L << 20;
		public const long OffOverlayPixels = OffCollisionRing + CollisionRingBytes;
		public const int MaxOverlayW = 3840;
		public const int MaxOverlayH = 2160;
		public const long OverlaySlotBytes = (long)MaxOverlayW * MaxOverlayH * 4;
		public const int OverlaySlots = 3;
		public const long OffRenderRing = OffOverlayPixels + OverlaySlotBytes * OverlaySlots;
		public const long RenderRingBytes = 64L << 20;
		public const long MappingBytes = OffRenderRing + RenderRingBytes;

		// ---- header ----------------------------------------------------------------------------
		public const long HMagic = 0x00, HVersion = 0x04, HHostPid = 0x08, HMcPid = 0x0C, HHostHeartbeat = 0x10, HMcHeartbeat = 0x18;

		// ---- multiplayer (header, v12): a REPO lobby plays in one Minecraft world, the host's ----
		// REPO -> MC: what the lobby wants of this Minecraft; MC -> REPO: where it is. ASCII, NUL-padded.
		public const long HMpFlags = 0x20, HMpJoin = 0x28, HMcMpState = 0x68, HMcShareLink = 0x70, HMcFriendWorld = 0xB0;
		public const int MpStringBytes = 64;
		public const uint MpShare = 1, MpJoin = 1u << 1;
		public const uint MpsOwn = 0, MpsSharing = 1, MpsJoining = 2, MpsInFriend = 3, MpsJoinFailed = 4;

		// ---- host state (seqlock) --------------------------------------------------------------
		public const long HsSeq = 0x00, HsFlags = 0x04, HsWorldId = 0x08, HsCollisionEpoch = 0x0C;
		public const long HsPosX = 0x10, HsPosY = 0x18, HsPosZ = 0x20, HsYaw = 0x28, HsPitch = 0x2C;
		public const long HsTeleportSeq = 0x30, HsViewportW = 0x34, HsViewportH = 0x38, HsGameHour = 0x3C;
		public const long HostStateBytes = 0x40;
		public const uint HostInGame = 1u << 0;   // a level is loaded and the player exists
		public const uint HostMenuOpen = 1u << 1; // a REPO menu owns input; MC drops held keys
		public const uint HostLoading = 1u << 2;  // level generating / loading

		// ---- water grid (seqlock) --------------------------------------------------------------
		public const int WaterGridSize = 16;
		public const float NoWater = -1.0e30f;
		public const long WgSeq = 0x0, WgOriginX = 0x4, WgOriginZ = 0x8, WgWorldId = 0xC, WgSurface = 0x10;

		// ---- MC state (seqlock) ----------------------------------------------------------------
		public const long MsSeq = 0x00, MsFlags = 0x04, MsX = 0x08, MsY = 0x10, MsZ = 0x18, MsYaw = 0x20, MsPitch = 0x24;
		public const long MsEyeHeight = 0x28, MsSensitivity = 0x2C, MsTeleportAck = 0x30, MsGuiScale = 0x34, MsFrameCounter = 0x38;
		public const long MsFov = 0x40, MsBobPhase = 0x44, MsBobAmount = 0x48, MsEyeX = 0x50, MsEyeY = 0x58, MsEyeZ = 0x60;
		public const long MsTickQpc = 0x68, MsPrevX = 0x70, MsCurX = 0x88, MsTickEyeO = 0xA0, MsTickEye = 0xA4;
		public const long MsWalkO = 0xA8, MsWalk = 0xAC, MsBobO = 0xB0, MsBob = 0xB4, MsTickMs = 0xB8, MsCameraMode = 0xC0, MsCameraDistance = 0xC4;
		public const long McStateBytes = 0xC8;
		public const uint McInWorld = 1u << 0, McScreenOpen = 1u << 1, McOnGround = 1u << 2, McSneaking = 1u << 3;
		public const uint McSprinting = 1u << 4, McDead = 1u << 5, McSwimming = 1u << 6, McFlying = 1u << 7;

		// ---- overlay triple buffer -------------------------------------------------------------
		public const long OcState = 0x00, OcFramesPublished = 0x08;
		public const int OverlayDirty = 1 << 2;
		public const long SlotHdrBytes = 0x40;
		public const long ShWidth = 0x00, ShHeight = 0x04, ShFlags = 0x08, ShFrameId = 0x10;

		// ---- input ring (host produces) --------------------------------------------------------
		public const int InputRingEntries = 4096;
		public const long IrHead = 0x00, IrTail = 0x40, IrData = 0x80;
		public const ushort InKey = 1;          // code = SDL scancode, a = 1 press / 0 release
		public const ushort InMouseButton = 2;  // code = SDL button (1 L, 2 M, 3 R, 4 X1, 5 X2)
		public const ushort InScroll = 3;       // a = notches * 120, positive = up
		public const ushort InCursor = 4;       // a, b = cursor in overlay pixels
		public const ushort InText = 5;         // a = unicode code point
		public const ushort InReleaseAll = 6;
		public const ushort InHurt = 7;         // code = HurtKind, a = host damage * 100, b = attacker id, c = HurtFlags
		public const ushort InOpenMenu = 8;     // open Minecraft's pause menu
		public const ushort HurtMelee = 0, HurtProjectile = 1, HurtMagic = 2, HurtOther = 3;
		public const int HurtBlockedInHost = 1, HurtPowerAttack = 2;

		// ---- actor table (host -> MC, seqlock) -------------------------------------------------
		public const int MaxActors = 256;
		public const long AtSeq = 0x00, AtCount = 0x04, AtRecords = 0x40, ActorRecordBytes = 64;
		public const uint ActorHostile = 1u << 0, ActorDead = 1u << 1, ActorEssential = 1u << 2, ActorInCombat = 1u << 3;
		public const int ActorNameBytes = 24;

		// ---- event ring (MC -> host) -----------------------------------------------------------
		public const int EventRingEntries = 512;
		public const long ErHead = 0x00, ErTail = 0x40, ErData = 0x80, EventBytes = 32;
		public const uint EvHitActor = 1, EvPlayerDied = 2, EvExplosion = 3, EvArrowStuck = 4, EvSkillUse = 5;
		public const uint HitCritical = 1u << 0, HitProjectile = 1u << 1, HitSweep = 1u << 2, HitFire = 1u << 3;
		public const uint WeaponUnarmed = 0, WeaponBlade = 1, WeaponAxe = 2, WeaponBlunt = 3, WeaponPierce = 4, WeaponArrow = 5;

		// ---- world entities (MC -> host, seqlock) ----------------------------------------------
		public const int MaxWorldEntities = 160;
		public const long WeSeq = 0x00, WeCount = 0x04, WeHasSelection = 0x08, WeSelMin = 0x0C, WeSelMax = 0x18, WeRecords = 0x40;
		public const long WorldEntityBytes = 96;
		public const uint WeArrow = 1, WeItem = 2, WeTrident = 3, WeBlock = 4, WeCrack = 5, WeShadow = 6;

		// ---- render ring (MC -> host) ----------------------------------------------------------
		public const long RrHead = 0x00, RrTail = 0x40, RrData = 0x80;
		public const long RrDataBytes = RenderRingBytes - RrData;
		public const uint RenPad = 0, RenAtlas = 1, RenSection = 2, RenClearAll = 3, RenTexture = 4, RenAvatar = 5, RenScene = 6;
		public const uint RenAtlasRegion = 7, RenLights = 8, RenRagdoll = 9, RenSolids = 10, RenDug = 11;
		public const int RenVertexBytes = 32;
		public const int RenBatchBytes = 16;
		public const uint LightSteady = 0, LightFlame = 1, LightLava = 2;
		public const uint HazardNone = 0, HazardFire = 1, HazardLava = 2, HazardMagma = 3;

		// ---- collision ring (host produces) ----------------------------------------------------
		public const long CrHead = 0x00, CrTail = 0x40, CrData = 0x80;
		public const long CrDataBytes = CollisionRingBytes - CrData;
		public const uint ColPad = 0, ColClear = 1, ColRegion = 2, ColTris = 3;
		public const int ColRegionBytes = 32, ColBlockBytes = 80, ColTriBytes = 40;
		public const uint TriStairHelper = 1u << 0, TriDiggable = 1u << 1, TriGhost = 1u << 2, TriTerrain = 1u << 3;
		public const int TriMaterialShift = 8;

		// What diggable host geometry digs into (ColTri flags bits 8-15).
		public const byte DigNone = 0, DigGrass = 1, DigDirt = 2, DigStone = 3, DigCobble = 4, DigSnow = 5, DigIce = 6, DigSand = 7;
		public const byte DigGravel = 8, DigMud = 9, DigOakLog = 10, DigSpruceLog = 11, DigBirchLog = 12, DigPlanks = 13, DigMetal = 14;
		public const byte DigGlass = 15, DigOrganic = 16, DigCloth = 17, DigBone = 18, DigWeb = 19, DigAsh = 20, DigBedrock = 21;
	}
}
