using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RepoCraft.Link
{
	/// <summary>Plain copy of the MC -> host state (see McState in the protocol header).</summary>
	internal sealed class McState
	{
		public uint Flags;
		public double X, Y, Z;
		public float Yaw, Pitch, EyeHeight, Sensitivity;
		public uint TeleportAck, GuiScale;
		public ulong FrameCounter;
		public float FovDeg, BobPhase, BobAmount;
		public double EyeX, EyeY, EyeZ;
		public long TickQpc;
		public double PrevX, PrevY, PrevZ, CurX, CurY, CurZ;
		public float TickEyeO, TickEye, WalkDistO, WalkDist, BobO, Bob, TickMs;
		public uint CameraMode;
		public float CameraDistance;

		public bool Has(uint flag) => (Flags & flag) != 0;

		public void CopyFrom(McState o)
		{
			Flags = o.Flags; X = o.X; Y = o.Y; Z = o.Z; Yaw = o.Yaw; Pitch = o.Pitch; EyeHeight = o.EyeHeight; Sensitivity = o.Sensitivity;
			TeleportAck = o.TeleportAck; GuiScale = o.GuiScale; FrameCounter = o.FrameCounter; FovDeg = o.FovDeg; BobPhase = o.BobPhase;
			BobAmount = o.BobAmount; EyeX = o.EyeX; EyeY = o.EyeY; EyeZ = o.EyeZ; TickQpc = o.TickQpc; PrevX = o.PrevX; PrevY = o.PrevY;
			PrevZ = o.PrevZ; CurX = o.CurX; CurY = o.CurY; CurZ = o.CurZ; TickEyeO = o.TickEyeO; TickEye = o.TickEye; WalkDistO = o.WalkDistO;
			WalkDist = o.WalkDist; BobO = o.BobO; Bob = o.Bob; TickMs = o.TickMs; CameraMode = o.CameraMode; CameraDistance = o.CameraDistance;
		}
	}

	/// <summary>Host -> MC state (see HostState in the protocol header).</summary>
	internal struct HostState
	{
		public uint Flags, WorldId, CollisionEpoch;
		public double PosX, PosY, PosZ;
		public float Yaw, Pitch;
		public uint TeleportSeq, ViewportW, ViewportH;
		public float GameHour;
	}

	internal struct McEvent
	{
		public uint Type, FormId;
		public float A, B, C, D;
		public uint Flags, Weapon;
	}

	internal struct ActorRecord
	{
		public uint FormId, Flags;
		public float X, Y, Z, Yaw, Width, Height, HealthFrac;
		public ushort Level;
		public string Name;
	}

	/// <summary>
	/// Owner of the shared-memory mapping. The host (REPO) creates it; Minecraft opens it.
	/// Port of SkyCraft's Link.cpp: same rings, seqlocks and triple buffer, byte for byte.
	/// </summary>
	internal sealed unsafe class SharedLink
	{
		public static readonly SharedLink Instance = new SharedLink();

		private const ulong McTimeoutMs = 3000;
		private IntPtr mapping;
		private byte* basePtr;
		private int overlayFront = 2;
		private readonly object collisionLock = new object();

		public bool Valid => basePtr != null;
		public byte* Base => basePtr;

		public bool Create()
		{
			if (basePtr != null)
			{
				return true;
			}
			ulong size = (ulong)Proto.MappingBytes;
			IntPtr descriptor = SharedWithThisUser();
			var attrs = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor, bInheritHandle = 0 };
			mapping = descriptor != IntPtr.Zero
				? CreateFileMappingW(new IntPtr(-1), ref attrs, PAGE_READWRITE, (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), Proto.MappingName)
				: CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, (uint)(size >> 32), (uint)(size & 0xFFFFFFFF), Proto.MappingName);
			int created = Marshal.GetLastWin32Error();
			if (descriptor != IntPtr.Zero)
			{
				LocalFree(descriptor);
			}
			if (mapping == IntPtr.Zero)
			{
				Log.Error($"CreateFileMapping failed ({created})");
				return false;
			}
			bool existed = created == ERROR_ALREADY_EXISTS;
			basePtr = (byte*)MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
			if (basePtr == null)
			{
				Log.Error($"MapViewOfFile failed ({Marshal.GetLastWin32Error()})");
				CloseHandle(mapping);
				mapping = IntPtr.Zero;
				return false;
			}

			// A stale mapping survives if Minecraft still has it open from a previous REPO run:
			// reset everything the host owns so the rings and the overlay swap start clean.
			Zero(Proto.OffHeader + Proto.HMpFlags, Proto.OffHostState - Proto.HMpFlags);
			Zero(Proto.OffHostState, Proto.HostStateBytes);
			Zero(Proto.OffOverlayCtl, 0x100);
			Zero(Proto.OffInputRing, Proto.IrData);
			Zero(Proto.OffCollisionRing, Proto.CrData);
			Zero(Proto.OffActorTable, Proto.AtRecords + Proto.ActorRecordBytes * Proto.MaxActors);
			Zero(Proto.OffEventRing, Proto.ErData);
			Zero(Proto.OffWorldEntities, Proto.WeRecords + Proto.WorldEntityBytes * Proto.MaxWorldEntities);
			Zero(Proto.OffRenderRing, Proto.RrData);
			*(uint*)(basePtr + Proto.OffHeader + Proto.HVersion) = Proto.Version;
			*(uint*)(basePtr + Proto.OffHeader + Proto.HHostPid) = (uint)GetCurrentProcessId();
			Volatile.Write(ref *(long*)(basePtr + Proto.OffHeader + Proto.HHostHeartbeat), (long)GetTickCount64());
			Thread.MemoryBarrier();
			Volatile.Write(ref *(uint*)(basePtr + Proto.OffHeader + Proto.HMagic), Proto.Magic);

			Log.Info($"shared memory {Proto.MappingName} ({size >> 20} MB, {(existed ? "reused" : "created")})");
			return true;
		}

		private void Zero(long offset, long bytes)
		{
			byte* p = basePtr + offset;
			for (long i = 0; i < bytes; i++)
			{
				p[i] = 0;
			}
		}

		public static ulong TickCount() => GetTickCount64();

		public static long Qpc()
		{
			QueryPerformanceCounter(out long t);
			return t;
		}

		public static long QpcFrequency()
		{
			QueryPerformanceFrequency(out long f);
			return f;
		}

		public bool McAlive()
		{
			if (basePtr == null)
			{
				return false;
			}
			ulong last = (ulong)Volatile.Read(ref *(long*)(basePtr + Proto.OffHeader + Proto.HMcHeartbeat));
			return last != 0 && GetTickCount64() - last < McTimeoutMs;
		}

		public uint McPid() => basePtr == null ? 0u : Volatile.Read(ref *(uint*)(basePtr + Proto.OffHeader + Proto.HMcPid));

		public void Heartbeat()
		{
			if (basePtr != null)
			{
				Volatile.Write(ref *(long*)(basePtr + Proto.OffHeader + Proto.HHostHeartbeat), (long)GetTickCount64());
			}
		}

		// ---- multiplayer (header) --------------------------------------------------------------

		/// <summary>What the REPO lobby wants of this Minecraft: Proto.Mp* flags, and the world to join.</summary>
		public void WriteMpRequest(uint flags, string join)
		{
			if (basePtr == null)
			{
				return;
			}
			WriteAscii(Proto.OffHeader + Proto.HMpJoin, join);
			Thread.MemoryBarrier();
			Volatile.Write(ref *(uint*)(basePtr + Proto.OffHeader + Proto.HMpFlags), flags);
		}

		/// <summary>Where Minecraft is: Proto.Mps*, our world's address while shared, the friend's world it's in.</summary>
		public uint ReadMpState(out string shareLink, out string friendWorld)
		{
			if (basePtr == null)
			{
				shareLink = friendWorld = "";
				return Proto.MpsOwn;
			}
			uint state = Volatile.Read(ref *(uint*)(basePtr + Proto.OffHeader + Proto.HMcMpState));
			shareLink = ReadAscii(Proto.OffHeader + Proto.HMcShareLink);
			friendWorld = ReadAscii(Proto.OffHeader + Proto.HMcFriendWorld);
			return state;
		}

		private void WriteAscii(long offset, string value)
		{
			byte* p = basePtr + offset;
			int n = 0;
			if (value != null)
			{
				for (; n < value.Length && n < Proto.MpStringBytes - 1; n++)
				{
					char c = value[n];
					p[n] = c < 128 ? (byte)c : (byte)'?';
				}
			}
			for (; n < Proto.MpStringBytes; n++)
			{
				p[n] = 0;
			}
		}

		private string ReadAscii(long offset)
		{
			byte* p = basePtr + offset;
			int n = 0;
			while (n < Proto.MpStringBytes && p[n] != 0)
			{
				n++;
			}
			return n == 0 ? "" : new string((sbyte*)p, 0, n, System.Text.Encoding.ASCII);
		}

		// ---- host state / water grid (seqlock writers) -----------------------------------------

		public void WriteHostState(in HostState s)
		{
			if (basePtr == null)
			{
				return;
			}
			byte* b = basePtr + Proto.OffHostState;
			uint* seq = (uint*)(b + Proto.HsSeq);
			uint v = *seq;
			Volatile.Write(ref *seq, v + 1);
			Thread.MemoryBarrier();
			*(uint*)(b + Proto.HsFlags) = s.Flags;
			*(uint*)(b + Proto.HsWorldId) = s.WorldId;
			*(uint*)(b + Proto.HsCollisionEpoch) = s.CollisionEpoch;
			*(double*)(b + Proto.HsPosX) = s.PosX;
			*(double*)(b + Proto.HsPosY) = s.PosY;
			*(double*)(b + Proto.HsPosZ) = s.PosZ;
			*(float*)(b + Proto.HsYaw) = s.Yaw;
			*(float*)(b + Proto.HsPitch) = s.Pitch;
			*(uint*)(b + Proto.HsTeleportSeq) = s.TeleportSeq;
			*(uint*)(b + Proto.HsViewportW) = s.ViewportW;
			*(uint*)(b + Proto.HsViewportH) = s.ViewportH;
			*(float*)(b + Proto.HsGameHour) = s.GameHour;
			Thread.MemoryBarrier();
			Volatile.Write(ref *seq, v + 2);
		}

		/// <summary>Writes the water grid (surface[z * size + x] = MC y, or Proto.NoWater).</summary>
		public void WriteWaterGrid(int originX, int originZ, uint worldId, float[] surface)
		{
			if (basePtr == null)
			{
				return;
			}
			byte* b = basePtr + Proto.OffWaterGrid;
			uint* seq = (uint*)(b + Proto.WgSeq);
			uint v = *seq;
			Volatile.Write(ref *seq, v + 1);
			Thread.MemoryBarrier();
			*(int*)(b + Proto.WgOriginX) = originX;
			*(int*)(b + Proto.WgOriginZ) = originZ;
			*(uint*)(b + Proto.WgWorldId) = worldId;
			float* dst = (float*)(b + Proto.WgSurface);
			for (int i = 0; i < Proto.WaterGridSize * Proto.WaterGridSize; i++)
			{
				dst[i] = surface[i];
			}
			Thread.MemoryBarrier();
			Volatile.Write(ref *seq, v + 2);
		}

		// ---- MC state (seqlock reader) ---------------------------------------------------------

		public bool ReadMcState(McState o)
		{
			if (basePtr == null)
			{
				return false;
			}
			byte* b = basePtr + Proto.OffMcState;
			uint* seq = (uint*)(b + Proto.MsSeq);
			for (int attempt = 0; attempt < 64; attempt++)
			{
				uint s1 = Volatile.Read(ref *seq);
				if ((s1 & 1) != 0)
				{
					Thread.SpinWait(4);
					continue;
				}
				Thread.MemoryBarrier();
				o.Flags = *(uint*)(b + Proto.MsFlags);
				o.X = *(double*)(b + Proto.MsX);
				o.Y = *(double*)(b + Proto.MsY);
				o.Z = *(double*)(b + Proto.MsZ);
				o.Yaw = *(float*)(b + Proto.MsYaw);
				o.Pitch = *(float*)(b + Proto.MsPitch);
				o.EyeHeight = *(float*)(b + Proto.MsEyeHeight);
				o.Sensitivity = *(float*)(b + Proto.MsSensitivity);
				o.TeleportAck = *(uint*)(b + Proto.MsTeleportAck);
				o.GuiScale = *(uint*)(b + Proto.MsGuiScale);
				o.FrameCounter = *(ulong*)(b + Proto.MsFrameCounter);
				o.FovDeg = *(float*)(b + Proto.MsFov);
				o.BobPhase = *(float*)(b + Proto.MsBobPhase);
				o.BobAmount = *(float*)(b + Proto.MsBobAmount);
				o.EyeX = *(double*)(b + Proto.MsEyeX);
				o.EyeY = *(double*)(b + Proto.MsEyeY);
				o.EyeZ = *(double*)(b + Proto.MsEyeZ);
				o.TickQpc = *(long*)(b + Proto.MsTickQpc);
				o.PrevX = *(double*)(b + Proto.MsPrevX);
				o.PrevY = *(double*)(b + Proto.MsPrevX + 8);
				o.PrevZ = *(double*)(b + Proto.MsPrevX + 16);
				o.CurX = *(double*)(b + Proto.MsCurX);
				o.CurY = *(double*)(b + Proto.MsCurX + 8);
				o.CurZ = *(double*)(b + Proto.MsCurX + 16);
				o.TickEyeO = *(float*)(b + Proto.MsTickEyeO);
				o.TickEye = *(float*)(b + Proto.MsTickEye);
				o.WalkDistO = *(float*)(b + Proto.MsWalkO);
				o.WalkDist = *(float*)(b + Proto.MsWalk);
				o.BobO = *(float*)(b + Proto.MsBobO);
				o.Bob = *(float*)(b + Proto.MsBob);
				o.TickMs = *(float*)(b + Proto.MsTickMs);
				o.CameraMode = *(uint*)(b + Proto.MsCameraMode);
				o.CameraDistance = *(float*)(b + Proto.MsCameraDistance);
				Thread.MemoryBarrier();
				if (Volatile.Read(ref *seq) == s1)
				{
					return true;
				}
			}
			return false;
		}

		// ---- input ring (producer) -------------------------------------------------------------

		public void PushInput(ushort type, ushort code, int a = 0, int b = 0, int c = 0)
		{
			if (basePtr == null)
			{
				return;
			}
			byte* ring = basePtr + Proto.OffInputRing;
			long head = Volatile.Read(ref *(long*)(ring + Proto.IrHead));
			long tail = Volatile.Read(ref *(long*)(ring + Proto.IrTail));
			if (head - tail >= Proto.InputRingEntries)
			{
				return;
			}
			byte* e = ring + Proto.IrData + (head & (Proto.InputRingEntries - 1)) * 16;
			*(ushort*)e = type;
			*(ushort*)(e + 2) = code;
			*(int*)(e + 4) = a;
			*(int*)(e + 8) = b;
			*(int*)(e + 12) = c;
			Thread.MemoryBarrier();
			Volatile.Write(ref *(long*)(ring + Proto.IrHead), head + 1);
		}

		// ---- collision ring (producer, any one thread at a time) -------------------------------

		/// <summary>Writes one collision message; false if the ring is full (try again later).</summary>
		public bool WriteCollision(uint type, byte[] payload, int bytes)
		{
			if (basePtr == null)
			{
				return false;
			}
			lock (collisionLock)
			{
				byte* ring = basePtr + Proto.OffCollisionRing;
				byte* data = ring + Proto.CrData;
				long size = Proto.CrDataBytes;
				long msgBytes = (8 + bytes + 7) & ~7L;
				if (msgBytes > size / 2)
				{
					Log.Error($"collision message too large ({msgBytes} bytes)");
					return false;
				}
				long head = Volatile.Read(ref *(long*)(ring + Proto.CrHead));
				long tail = Volatile.Read(ref *(long*)(ring + Proto.CrTail));
				long pos = head % size;
				long pad = pos + msgBytes > size ? size - pos : 0;
				if (size - (head - tail) < msgBytes + pad)
				{
					return false;
				}
				if (pad > 0)
				{
					*(uint*)(data + pos) = Proto.ColPad;
					*(uint*)(data + pos + 4) = 0;
					head += pad;
					pos = 0;
				}
				*(uint*)(data + pos) = type;
				*(uint*)(data + pos + 4) = (uint)bytes;
				if (bytes > 0)
				{
					Marshal.Copy(payload, 0, (IntPtr)(data + pos + 8), bytes);
				}
				Thread.MemoryBarrier();
				Volatile.Write(ref *(long*)(ring + Proto.CrHead), head + msgBytes);
				return true;
			}
		}

		// ---- actor table (seqlock writer) ------------------------------------------------------

		public void WriteActors(ActorRecord[] records, int count)
		{
			if (basePtr == null)
			{
				return;
			}
			byte* t = basePtr + Proto.OffActorTable;
			uint* seq = (uint*)(t + Proto.AtSeq);
			uint v = *seq;
			Volatile.Write(ref *seq, v + 1);
			Thread.MemoryBarrier();
			count = Math.Min(count, Proto.MaxActors);
			*(uint*)(t + Proto.AtCount) = (uint)count;
			for (int i = 0; i < count; i++)
			{
				byte* r = t + Proto.AtRecords + i * Proto.ActorRecordBytes;
				ref ActorRecord a = ref records[i];
				*(uint*)r = a.FormId;
				*(uint*)(r + 4) = a.Flags;
				*(float*)(r + 8) = a.X;
				*(float*)(r + 12) = a.Y;
				*(float*)(r + 16) = a.Z;
				*(float*)(r + 20) = a.Yaw;
				*(float*)(r + 24) = a.Width;
				*(float*)(r + 28) = a.Height;
				*(float*)(r + 32) = a.HealthFrac;
				*(ushort*)(r + 36) = a.Level;
				*(ushort*)(r + 38) = 0;
				WriteName(r + 40, a.Name);
			}
			Thread.MemoryBarrier();
			Volatile.Write(ref *seq, v + 2);
		}

		private static void WriteName(byte* dst, string name)
		{
			int n = 0;
			if (!string.IsNullOrEmpty(name))
			{
				byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
				n = Math.Min(utf8.Length, Proto.ActorNameBytes - 1);
				for (int i = 0; i < n; i++)
				{
					dst[i] = utf8[i];
				}
			}
			for (int i = n; i < Proto.ActorNameBytes; i++)
			{
				dst[i] = 0;
			}
		}

		// ---- event ring (consumer) -------------------------------------------------------------

		public bool PopEvent(out McEvent ev)
		{
			ev = default;
			if (basePtr == null)
			{
				return false;
			}
			byte* ring = basePtr + Proto.OffEventRing;
			long head = Volatile.Read(ref *(long*)(ring + Proto.ErHead));
			long tail = Volatile.Read(ref *(long*)(ring + Proto.ErTail));
			if (tail >= head)
			{
				return false;
			}
			if (head - tail > Proto.EventRingEntries)
			{
				tail = head - Proto.EventRingEntries;
			}
			Thread.MemoryBarrier();
			byte* e = ring + Proto.ErData + (tail & (Proto.EventRingEntries - 1)) * Proto.EventBytes;
			ev.Type = *(uint*)e;
			ev.FormId = *(uint*)(e + 4);
			ev.A = *(float*)(e + 8);
			ev.B = *(float*)(e + 12);
			ev.C = *(float*)(e + 16);
			ev.D = *(float*)(e + 20);
			ev.Flags = *(uint*)(e + 24);
			ev.Weapon = *(uint*)(e + 28);
			Thread.MemoryBarrier();
			Volatile.Write(ref *(long*)(ring + Proto.ErTail), tail + 1);
			return true;
		}

		// ---- world entities (seqlock reader) ---------------------------------------------------

		/// <summary>
		/// Copies the world-entity table (raw bytes, header + records) into <paramref name="dst"/>.
		/// Returns the entity count, or -1 on a torn read.
		/// </summary>
		public int ReadWorldEntities(byte[] dst)
		{
			if (basePtr == null)
			{
				return -1;
			}
			byte* src = basePtr + Proto.OffWorldEntities;
			uint* seq = (uint*)(src + Proto.WeSeq);
			for (int attempt = 0; attempt < 16; attempt++)
			{
				uint s1 = Volatile.Read(ref *seq);
				if ((s1 & 1) != 0)
				{
					Thread.SpinWait(4);
					continue;
				}
				Thread.MemoryBarrier();
				int count = (int)Math.Min(*(uint*)(src + Proto.WeCount), (uint)Proto.MaxWorldEntities);
				int bytes = (int)(Proto.WeRecords + Proto.WorldEntityBytes * count);
				Marshal.Copy((IntPtr)src, dst, 0, bytes);
				Thread.MemoryBarrier();
				if (Volatile.Read(ref *seq) == s1)
				{
					return count;
				}
			}
			return -1;
		}

		// ---- render ring (consumer) ------------------------------------------------------------

		public delegate void RenderSink(uint type, byte* payload, uint bytes);

		/// <summary>Calls <paramref name="sink"/> for each pending render message, up to about maxBytes.</summary>
		public void DrainRender(RenderSink sink, long maxBytes)
		{
			if (basePtr == null)
			{
				return;
			}
			byte* ring = basePtr + Proto.OffRenderRing;
			byte* data = ring + Proto.RrData;
			long size = Proto.RrDataBytes;
			long head = Volatile.Read(ref *(long*)(ring + Proto.RrHead));
			long tail = Volatile.Read(ref *(long*)(ring + Proto.RrTail));
			Thread.MemoryBarrier();
			long done = 0;
			while (tail < head && done < maxBytes)
			{
				long pos = tail % size;
				uint type = *(uint*)(data + pos);
				if (type == Proto.RenPad)
				{
					tail += size - pos;
					continue;
				}
				uint payloadBytes = *(uint*)(data + pos + 4);
				try
				{
					sink(type, data + pos + 8, payloadBytes);
				}
				catch (Exception e)
				{
					Log.Error($"render message {type} ({payloadBytes} bytes): {e}");
				}
				long msgBytes = (8 + payloadBytes + 7) & ~7L;
				tail += msgBytes;
				done += msgBytes;
			}
			Thread.MemoryBarrier();
			Volatile.Write(ref *(long*)(ring + Proto.RrTail), tail);
		}

		// ---- overlay triple buffer (consumer) --------------------------------------------------

		public bool AcquireOverlayFrame()
		{
			if (basePtr == null)
			{
				return false;
			}
			int* state = (int*)(basePtr + Proto.OffOverlayCtl + Proto.OcState);
			if ((Volatile.Read(ref *state) & Proto.OverlayDirty) == 0)
			{
				return false;
			}
			int old = Interlocked.Exchange(ref *state, overlayFront);
			overlayFront = old & 3;
			return true;
		}

		public void ResetOverlay()
		{
			if (basePtr == null)
			{
				return;
			}
			Volatile.Write(ref *(int*)(basePtr + Proto.OffOverlayCtl + Proto.OcState), 0);
			overlayFront = 2;
		}

		public byte* FrontPixels => basePtr + Proto.OffOverlayPixels + Proto.OverlaySlotBytes * overlayFront;

		public void FrontHeader(out int width, out int height, out bool bottomUp, out long frameId)
		{
			byte* h = basePtr + Proto.OffOverlaySlotHdr + Proto.SlotHdrBytes * overlayFront;
			width = *(int*)(h + Proto.ShWidth);
			height = *(int*)(h + Proto.ShHeight);
			bottomUp = (*(uint*)(h + Proto.ShFlags) & 1) != 0;
			frameId = *(long*)(h + Proto.ShFrameId);
		}

		// ---- Win32 -----------------------------------------------------------------------------

		private const uint PAGE_READWRITE = 0x04;
		private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
		private const int ERROR_ALREADY_EXISTS = 183;

		[StructLayout(LayoutKind.Sequential)]
		private struct SECURITY_ATTRIBUTES
		{
			public int nLength;
			public IntPtr lpSecurityDescriptor;
			public int bInheritHandle;
		}

		/// <summary>
		/// Who may open the mapping: signed-in users, SYSTEM and administrators, at medium integrity.
		/// Explicit, because a REPO run as administrator would otherwise make it administrators-only
		/// and the (unelevated) Minecraft could never connect. "Local\" names are per logon session
		/// already, so no other user's processes can see it. LocalFree the result.
		/// </summary>
		private static IntPtr SharedWithThisUser()
		{
			const string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)S:(ML;;NW;;;ME)";
			if (ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr descriptor, IntPtr.Zero))
			{
				return descriptor;
			}
			Log.Warn($"shared memory: couldn't build its access rules ({Marshal.GetLastWin32Error()}); using the defaults");
			return IntPtr.Zero;
		}

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES attrs, uint protect, uint maxHigh, uint maxLow, string name);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attrs, uint protect, uint maxHigh, uint maxLow, string name);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll")]
		private static extern IntPtr LocalFree(IntPtr mem);

		[DllImport("kernel32.dll")]
		private static extern ulong GetTickCount64();

		[DllImport("kernel32.dll")]
		private static extern int GetCurrentProcessId();

		[DllImport("kernel32.dll")]
		private static extern bool QueryPerformanceCounter(out long count);

		[DllImport("kernel32.dll")]
		private static extern bool QueryPerformanceFrequency(out long freq);

		[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);
	}
}
