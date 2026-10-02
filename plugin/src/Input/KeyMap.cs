using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace RepoCraft.Input
{
	/// <summary>Unity Input System keys -> SDL scancodes (USB HID usages), which Minecraft 26.x reads.</summary>
	internal static class KeyMap
	{
		public static readonly Dictionary<Key, ushort> ToSdl = Build();

		private static Dictionary<Key, ushort> Build()
		{
			var t = new Dictionary<Key, ushort>();
			for (int i = 0; i < 26; i++)
			{
				t[Key.A + i] = (ushort)(4 + i);
			}
			t[Key.Digit1] = 30; t[Key.Digit2] = 31; t[Key.Digit3] = 32; t[Key.Digit4] = 33; t[Key.Digit5] = 34;
			t[Key.Digit6] = 35; t[Key.Digit7] = 36; t[Key.Digit8] = 37; t[Key.Digit9] = 38; t[Key.Digit0] = 39;
			t[Key.Enter] = 40; t[Key.Escape] = 41; t[Key.Backspace] = 42; t[Key.Tab] = 43; t[Key.Space] = 44;
			t[Key.Minus] = 45; t[Key.Equals] = 46; t[Key.LeftBracket] = 47; t[Key.RightBracket] = 48; t[Key.Backslash] = 49;
			t[Key.Semicolon] = 51; t[Key.Quote] = 52; t[Key.Backquote] = 53; t[Key.Comma] = 54; t[Key.Period] = 55; t[Key.Slash] = 56;
			t[Key.CapsLock] = 57;
			t[Key.F1] = 58; t[Key.F2] = 59; t[Key.F3] = 60; t[Key.F4] = 61; t[Key.F5] = 62; t[Key.F6] = 63;
			t[Key.F7] = 64; t[Key.F8] = 65; t[Key.F9] = 66; t[Key.F10] = 67; t[Key.F11] = 68; t[Key.F12] = 69;
			t[Key.PrintScreen] = 70; t[Key.ScrollLock] = 71; t[Key.Pause] = 72; t[Key.Insert] = 73; t[Key.Home] = 74; t[Key.PageUp] = 75;
			t[Key.Delete] = 76; t[Key.End] = 77; t[Key.PageDown] = 78;
			t[Key.RightArrow] = 79; t[Key.LeftArrow] = 80; t[Key.DownArrow] = 81; t[Key.UpArrow] = 82;
			t[Key.NumLock] = 83; t[Key.NumpadDivide] = 84; t[Key.NumpadMultiply] = 85; t[Key.NumpadMinus] = 86; t[Key.NumpadPlus] = 87;
			t[Key.NumpadEnter] = 88;
			t[Key.Numpad1] = 89; t[Key.Numpad2] = 90; t[Key.Numpad3] = 91; t[Key.Numpad4] = 92; t[Key.Numpad5] = 93;
			t[Key.Numpad6] = 94; t[Key.Numpad7] = 95; t[Key.Numpad8] = 96; t[Key.Numpad9] = 97; t[Key.Numpad0] = 98; t[Key.NumpadPeriod] = 99;
			t[Key.ContextMenu] = 101;
			t[Key.LeftCtrl] = 224; t[Key.LeftShift] = 225; t[Key.LeftAlt] = 226; t[Key.LeftMeta] = 227;
			t[Key.RightCtrl] = 228; t[Key.RightShift] = 229; t[Key.RightAlt] = 230; t[Key.RightMeta] = 231;
			return t;
		}
	}
}
