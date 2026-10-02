using System;
using System.Collections.Generic;
using RepoCraft.Link;

namespace RepoCraft.Player
{
	/// <summary>
	/// Interpolates Minecraft's 20 Hz physics ticks on REPO's own frame clock, the way Minecraft's
	/// renderer does with partial ticks. Sampling Minecraft's per-frame position instead judders,
	/// because the two games' frames aren't phase-locked. Keeps a short history and renders slightly
	/// in the past so the next tick has always arrived: pure interpolation, never extrapolation.
	/// Port of the interpolation in SkyCraft's Game.cpp.
	/// </summary>
	internal sealed class TickInterpolator
	{
		private struct Tick
		{
			public McState S;
			public long At;    // when it happened (QPC), on the locked rhythm
			public int Slots;  // ticks since the one before (2+: we missed one)
		}

		private readonly List<Tick> history = new List<Tick>();
		private readonly double[] tickDue = new double[40]; // last 2 s: how long each tick was already due when first seen, ms
		private int tickDueNext;
		private bool tickDueInit;
		private long lastFrameQpc;
		private int stampOutliers;
		private static readonly long QpcFreq = SharedLink.QpcFrequency();

		public double RenderDelayMs { get; private set; } = 10.0;
		public int LateFrames { get; private set; }

		// Output of the last Sample().
		public double FeetX, FeetY, FeetZ, EyeHeight;
		public float BobPhase, BobAmount;

		private static bool Jump(double dx, double dy, double dz) => dx * dx + dy * dy + dz * dz > 16.0; // 4 blocks in one tick

		public void Reset()
		{
			history.Clear();
			tickDueInit = false;
			lastFrameQpc = 0;
		}

		/// <summary>Feed this frame's McState and get the interpolated feet / eye / bob for now.</summary>
		public void Sample(McState mc)
		{
			FeetX = mc.X;
			FeetY = mc.Y;
			FeetZ = mc.Z;
			EyeHeight = mc.EyeY - mc.Y;
			BobPhase = mc.BobPhase;
			BobAmount = mc.BobAmount;
			if (mc.TickQpc == 0 || mc.TickMs <= 0f)
			{
				return;
			}
			double qpcPerMs = QpcFreq / 1000.0;
			long period = Math.Max(1, (long)Math.Round(mc.TickMs * qpcPerMs));
			long now = SharedLink.Qpc();

			if (history.Count == 0 || history[history.Count - 1].S.TickQpc != mc.TickQpc)
			{
				if (history.Count > 0 && mc.TickQpc < history[history.Count - 1].S.TickQpc)
				{
					history.Clear(); // Minecraft restarted
				}
				var copy = new McState();
				copy.CopyFrom(mc);
				// A teleport: the tick's start is where the player was before it. Never interpolate
				// across that (the body would sweep through the level), and forget the ticks before it.
				if (Jump(copy.PrevX - copy.CurX, copy.PrevY - copy.CurY, copy.PrevZ - copy.CurZ))
				{
					copy.PrevX = copy.CurX;
					copy.PrevY = copy.CurY;
					copy.PrevZ = copy.CurZ;
					copy.WalkDistO = copy.WalkDist;
					copy.TickEyeO = copy.TickEye;
					history.Clear();
				}
				else if (history.Count > 0)
				{
					var lastS = history[history.Count - 1].S;
					if (Jump(lastS.CurX - copy.PrevX, lastS.CurY - copy.PrevY, lastS.CurZ - copy.PrevZ))
					{
						history.Clear();
					}
				}
				var tick = new Tick { S = copy, At = mc.TickQpc, Slots = 1 };
				if (history.Count > 0)
				{
					var last = history[history.Count - 1];
					long n = (long)Math.Round((double)(mc.TickQpc - last.At) / period);
					long err = mc.TickQpc - (last.At + n * period);
					if (n == 0 && last.Slots >= 2)
					{
						// Minecraft ran two ticks in one frame and we saw both: the first one carries
						// the second's stamp. It belongs a tick earlier.
						last.At -= period;
						last.Slots -= 1;
						history[history.Count - 1] = last;
						tick.At = last.At + period;
					}
					else if (n >= 1 && n <= 10 && Math.Abs(err) < period * 3 / 10)
					{
						tick.At = last.At + n * period + err / 16; // the rhythm is exact; the stamps are noisy
						tick.Slots = (int)n;
						stampOutliers = 0;
					}
					else if (n <= 10 && ++stampOutliers < 3)
					{
						tick.Slots = (int)Math.Max(n, 1);
						tick.At = last.At + tick.Slots * period; // one odd stamp (a hitch): keep the rhythm
					}
					else
					{
						stampOutliers = 0; // lost the rhythm (a pause, a new tick rate): start from this stamp
					}
				}
				if (lastFrameQpc != 0)
				{
					if (!tickDueInit)
					{
						for (int i = 0; i < tickDue.Length; i++)
						{
							tickDue[i] = RenderDelayMs - 1.0;
						}
						tickDueInit = true;
					}
					double dueMs = (lastFrameQpc - tick.At) / qpcPerMs;
					if (dueMs < 30.0)
					{
						tickDue[tickDueNext++ % tickDue.Length] = dueMs;
					}
				}
				history.Add(tick);
				if (history.Count > 8)
				{
					history.RemoveAt(0);
				}
			}

			// The render delay follows how late ticks have been over the last 2 s: it grows 2% slower
			// than real time and shrinks 0.2% faster, too little to see either way.
			double frameMs = lastFrameQpc != 0 ? (now - lastFrameQpc) / qpcPerMs : 0.0;
			lastFrameQpc = now;
			if (tickDueInit)
			{
				double max = double.MinValue;
				foreach (double d in tickDue)
				{
					max = Math.Max(max, d);
				}
				double target = Math.Max(4.0, Math.Min(30.0, max + 1.0));
				double dt = Math.Min(frameMs, 100.0) / 1000.0;
				RenderDelayMs = target > RenderDelayMs ? Math.Min(target, RenderDelayMs + 20.0 * dt) : Math.Max(target, RenderDelayMs - 2.0 * dt);
			}
			long renderQpc = now - (long)Math.Round(RenderDelayMs * qpcPerMs);

			int idx = 0;
			for (int k = history.Count - 1; k >= 0; k--)
			{
				if (history[k].At <= renderQpc)
				{
					idx = k;
					break;
				}
			}
			var t0 = history[idx];
			bool hasNext = idx + 1 < history.Count;
			double ticks = (double)(renderQpc - t0.At) / period;
			double t = Math.Max(0.0, Math.Min(1.0, ticks));
			var s = t0.S;
			FeetX = s.PrevX + (s.CurX - s.PrevX) * t;
			FeetY = s.PrevY + (s.CurY - s.PrevY) * t;
			FeetZ = s.PrevZ + (s.CurZ - s.PrevZ) * t;
			EyeHeight = s.TickEyeO + (s.TickEye - s.TickEyeO) * t;
			BobPhase = -(s.WalkDist + (s.WalkDist - s.WalkDistO) * (float)t);
			BobAmount = s.BobO + (s.Bob - s.BobO) * (float)t;
			if (ticks > 1.0 && hasNext)
			{
				// Past this tick's end, and the next tick we have starts later: Minecraft ran one we
				// never saw. Carry on from this tick's end to the next one's start.
				var nx = history[idx + 1];
				var n = nx.S;
				double gap = nx.At - (t0.At + period);
				double u = gap > 0.0 ? Math.Max(0.0, Math.Min(1.0, (renderQpc - (t0.At + period)) / gap)) : 1.0;
				FeetX = s.CurX + (n.PrevX - s.CurX) * u;
				FeetY = s.CurY + (n.PrevY - s.CurY) * u;
				FeetZ = s.CurZ + (n.PrevZ - s.CurZ) * u;
				EyeHeight = s.TickEye + (n.TickEyeO - s.TickEye) * u;
				float endPhase = -(s.WalkDist + (s.WalkDist - s.WalkDistO));
				BobPhase = endPhase + (-n.WalkDist - endPhase) * (float)u;
				BobAmount = s.Bob + (n.BobO - s.Bob) * (float)u;
			}
			else if (ticks > 1.0)
			{
				LateFrames++; // the next tick hasn't arrived: the player stands still this frame
			}
		}
	}
}
