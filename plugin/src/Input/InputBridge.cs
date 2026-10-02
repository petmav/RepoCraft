using System.Collections.Generic;
using RepoCraft.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace RepoCraft.Input
{
	/// <summary>
	/// The keyboard and mouse while Minecraft drives the player. REPO's window has the focus; its
	/// gameplay input is swallowed (Patches.InputManagerPatches) and Minecraft gets the raw keys and
	/// buttons through the input ring, as if its hidden window had the focus.
	///
	/// REPO keeps: Esc (its menu; closes a Minecraft screen first), V push-to-talk, B mute, Tab map,
	/// T chat in multiplayer (Minecraft's chat opens with /), and G interact. O opens Minecraft's own
	/// pause menu. REPO's grab beam: left-click on something REPO can grab (loot, carts, doors,
	/// buttons, heads) grabs it instead of hitting; while carrying, the right button rotates it, the
	/// wheel pushes and pulls it, E uses it and 1-3 put it in REPO's inventory; Alt+1-3 take things
	/// out. Everything else is Minecraft's.
	/// </summary>
	internal sealed class InputBridge
	{
		public static readonly InputBridge Instance = new InputBridge();

		/// <summary>REPO's gameplay input is replaced by our virtual REPO keys (read by the patches).</summary>
		public static bool Routing;

		/// <summary>A Minecraft screen is open (read by the patches: Esc then closes it).</summary>
		public static bool McScreenOpen;

		// Virtual REPO keys this frame.
		private readonly HashSet<InputKey> down = new HashSet<InputKey>();
		private readonly HashSet<InputKey> held = new HashSet<InputKey>();
		private readonly HashSet<InputKey> up = new HashSet<InputKey>();
		private readonly HashSet<InputKey> heldPrev = new HashSet<InputKey>();
		public float Scroll { get; private set; }
		public bool Rotating { get; private set; }

		private readonly HashSet<ushort> sentKeys = new HashSet<ushort>();
		private readonly HashSet<int> sentButtons = new HashSet<int>();
		private bool grabMode; // the left button belongs to REPO's grab beam until it's let go
		private bool anySent;
		private int frame = -1;
		private readonly List<char> text = new List<char>();
		private bool textHooked;

		// Look (MC degrees), integrated here so the camera has no added latency (Minecraft adopts it).
		public float Yaw, Pitch;
		public float Sensitivity = 0.5f;

		// Virtual cursor while a Minecraft screen is open (overlay pixels).
		public int CursorX, CursorY;
		private bool screenWasOpen;

		public bool IsDown(InputKey k) => down.Contains(k);

		public bool IsHeld(InputKey k) => held.Contains(k);

		public bool IsUp(InputKey k) => up.Contains(k);

		/// <summary>Keys REPO handles itself even while Minecraft has the player: the patches let these through.</summary>
		public static bool PassThrough(InputKey k)
		{
			switch (k)
			{
				case InputKey.Menu:
				case InputKey.Back:
				case InputKey.BackEditor:
				case InputKey.Map:
					return !McScreenOpen; // Esc closes a Minecraft screen first
				case InputKey.PushToTalk:
				case InputKey.ToggleMute:
				case InputKey.MouseInput:
				case InputKey.ChatDelete:
				case InputKey.Confirm:
					return true;
				case InputKey.Chat:
					return !McScreenOpen && Repo.Game.Multiplayer;
				default:
					return false;
			}
		}

		private void HookText()
		{
			if (textHooked || Keyboard.current == null)
			{
				return;
			}
			textHooked = true;
			Keyboard.current.onTextInput += c => text.Add(c);
		}

		/// <summary>
		/// Once a frame, before REPO's scripts read input. route: Minecraft has the player and no REPO
		/// menu or chat wants the keys. screenOpen: a Minecraft screen (inventory, chat...) is open.
		/// </summary>
		public void Frame(bool route, bool screenOpen, int viewportW, int viewportH, float mcSensitivity)
		{
			if (Time.frameCount == frame)
			{
				return;
			}
			frame = Time.frameCount;
			HookText();
			if (mcSensitivity > 0f)
			{
				Sensitivity = mcSensitivity;
			}
			heldPrev.Clear();
			heldPrev.UnionWith(held);
			held.Clear();
			Scroll = 0f;
			Rotating = false;
			Routing = route;
			McScreenOpen = screenOpen;
			var kb = Keyboard.current;
			var mouse = Mouse.current;
			if (!route || kb == null || mouse == null)
			{
				if (anySent)
				{
					ReleaseAll();
				}
				grabMode = false;
				text.Clear();
				Edges();
				return;
			}
			var link = SharedLink.Instance;
			if (screenOpen && !screenWasOpen)
			{
				CursorX = viewportW / 2;
				CursorY = viewportH / 2;
			}
			screenWasOpen = screenOpen;

			bool carrying = PhysGrabber.instance != null && PhysGrabber.instance.grabbed;
			bool alt = kb.leftAltKey.isPressed || kb.rightAltKey.isPressed;
			var left = mouse.leftButton;
			if (left.wasPressedThisFrame && !screenOpen)
			{
				grabMode = carrying || Arbiter.RepoGrabTarget();
			}
			else if (!left.isPressed && !carrying)
			{
				grabMode = false;
			}
			bool grabbing = !screenOpen && (carrying || grabMode);

			// ---- REPO's virtual keys ----
			if (!screenOpen)
			{
				if (grabMode && left.isPressed) held.Add(InputKey.Grab);
				if (grabbing && mouse.rightButton.isPressed)
				{
					held.Add(InputKey.Rotate);
					Rotating = true;
				}
				if (kb.gKey.isPressed || (grabbing && kb.eKey.isPressed)) held.Add(InputKey.Interact);
				if (grabbing || alt)
				{
					if (kb.digit1Key.isPressed) held.Add(InputKey.Inventory1);
					if (kb.digit2Key.isPressed) held.Add(InputKey.Inventory2);
					if (kb.digit3Key.isPressed) held.Add(InputKey.Inventory3);
				}
			}

			held.UnionWith(DebugCommands.RepoHeld);

			// ---- Minecraft's keys ----
			foreach (var kv in KeyMap.ToSdl)
			{
				var control = kb[kv.Key];
				if (control == null)
				{
					continue;
				}
				if (control.wasPressedThisFrame && !sentKeys.Contains(kv.Value) && (screenOpen || !Reserved(kv.Key, grabbing, alt)))
				{
					sentKeys.Add(kv.Value);
					anySent = true;
					link.PushInput(Proto.InKey, kv.Value, 1);
				}
				else if (!control.isPressed && sentKeys.Remove(kv.Value))
				{
					link.PushInput(Proto.InKey, kv.Value, 0);
				}
			}
			if (screenOpen)
			{
				foreach (char c in text)
				{
					link.PushInput(Proto.InText, 0, c);
				}
			}
			text.Clear();
			if (!screenOpen && kb.oKey.wasPressedThisFrame)
			{
				ReleaseAll();
				link.PushInput(Proto.InOpenMenu, 0);
			}

			// ---- mouse ----
			Vector2 delta = mouse.delta.ReadValue();
			if (screenOpen)
			{
				CursorX = Mathf.Clamp(CursorX + Mathf.RoundToInt(delta.x), 0, viewportW - 1);
				CursorY = Mathf.Clamp(CursorY - Mathf.RoundToInt(delta.y), 0, viewportH - 1);
				if (delta.sqrMagnitude > 0f)
				{
					link.PushInput(Proto.InCursor, 0, CursorX, CursorY);
				}
			}
			Button(left, 1, !grabMode || screenOpen);
			Button(mouse.rightButton, 3, !grabbing);
			Button(mouse.middleButton, 2, true);
			Button(mouse.backButton, 4, true);
			Button(mouse.forwardButton, 5, true);
			float wheel = mouse.scroll.ReadValue().y;
			if (wheel != 0f)
			{
				if (grabbing)
				{
					Scroll = wheel; // REPO: push / pull what's carried
				}
				else
				{
					link.PushInput(Proto.InScroll, 0, wheel > 0 ? 120 : -120);
				}
			}

			// Mouse look: Minecraft's own formula on raw mouse counts (frozen while REPO turns a held thing).
			if (!screenOpen && !Rotating)
			{
				float s = Sensitivity * 0.6f + 0.2f;
				float factor = s * s * s * 8f * 0.15f;
				Yaw = Mathf.Repeat(Yaw + delta.x * factor, 360f);
				Pitch = Mathf.Clamp(Pitch - delta.y * factor, -90f, 90f);
			}
			Edges();
		}

		/// <summary>Keys that are REPO's (or ours) instead of Minecraft's while no Minecraft screen is open.</summary>
		private static bool Reserved(Key key, bool grabbing, bool alt)
		{
			switch (key)
			{
				case Key.Escape:
				case Key.V:
				case Key.B:
				case Key.Tab:
				case Key.O:
				case Key.G:
					return true;
				case Key.T:
					return Repo.Game.Multiplayer; // REPO's chat in multiplayer; Minecraft's opens with /
				case Key.E:
					return grabbing;
				case Key.Digit1:
				case Key.Digit2:
				case Key.Digit3:
					return grabbing || alt;
				default:
					return false;
			}
		}

		/// <summary>Turns this frame's held virtual keys into down / up edges.</summary>
		private void Edges()
		{
			down.Clear();
			up.Clear();
			foreach (var k in held)
			{
				if (!heldPrev.Contains(k))
				{
					down.Add(k);
				}
			}
			foreach (var k in heldPrev)
			{
				if (!held.Contains(k))
				{
					up.Add(k);
				}
			}
		}

		private void Button(ButtonControl b, int sdl, bool toMinecraft)
		{
			if (b == null)
			{
				return;
			}
			if (toMinecraft && b.wasPressedThisFrame && sentButtons.Add(sdl))
			{
				anySent = true;
				SharedLink.Instance.PushInput(Proto.InMouseButton, (ushort)sdl, 1);
			}
			if (sentButtons.Contains(sdl) && (!b.isPressed || !toMinecraft))
			{
				sentButtons.Remove(sdl);
				SharedLink.Instance.PushInput(Proto.InMouseButton, (ushort)sdl, 0);
			}
		}

		/// <summary>Minecraft lets go of every key and button (input focus moved to REPO).</summary>
		public void ReleaseAll()
		{
			sentKeys.Clear();
			sentButtons.Clear();
			anySent = false;
			SharedLink.Instance.PushInput(Proto.InReleaseAll, 0);
		}
	}
}
