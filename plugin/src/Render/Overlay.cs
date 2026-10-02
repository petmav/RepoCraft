using System;
using RepoCraft.Link;
using UnityEngine;

namespace RepoCraft.Render
{
	/// <summary>
	/// Minecraft's own picture on top of REPO's: its first-person hand and held item, hotbar,
	/// hearts, hunger, crosshair and every open Minecraft screen (inventory, crafting, chat),
	/// rendered by the hidden Minecraft at REPO's resolution on a transparent background
	/// (premultiplied alpha) and shipped through the overlay triple buffer.
	/// </summary>
	internal sealed unsafe class Overlay : MonoBehaviour
	{
		public static Overlay Instance { get; private set; }

		private Texture2D frame;
		private Texture2D cursor;
		private Material material;
		private long lastFrameId;
		public bool Show { get; set; }
		public bool ShowCursor { get; set; }
		public Vector2Int Cursor { get; set; }
		public long FramesShown { get; private set; }

		private void Awake()
		{
			Instance = this;
			cursor = MakeCursor();
		}

		/// <summary>Picks up the newest frame Minecraft published (call once per frame).</summary>
		public void Pull()
		{
			var link = SharedLink.Instance;
			if (!link.Valid || !link.AcquireOverlayFrame())
			{
				return;
			}
			link.FrontHeader(out int w, out int h, out bool bottomUp, out long frameId);
			if (w <= 0 || h <= 0 || w > Proto.MaxOverlayW || h > Proto.MaxOverlayH || frameId == lastFrameId)
			{
				return;
			}
			lastFrameId = frameId;
			if (frame == null || frame.width != w || frame.height != h)
			{
				if (frame != null)
				{
					Destroy(frame);
				}
				frame = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{
					name = "Minecraft overlay",
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.DontSave,
				};
				Log.Info($"overlay {w}x{h} ({(bottomUp ? "bottom-up" : "top-down")})");
			}
			// Bottom-up rows are exactly Unity's texture order.
			frame.LoadRawTextureData((IntPtr)link.FrontPixels, w * h * 4);
			frame.Apply(false, false);
			FramesShown++;
		}

		public void Clear()
		{
			lastFrameId = 0;
			Show = false;
		}

		private void OnGUI()
		{
			if (Event.current.type != EventType.Repaint || !Show || frame == null)
			{
				return;
			}
			material ??= Materials.Get(Materials.Kind.Overlay, null);
			GUI.depth = -10000;
			var screen = new Rect(0, 0, Screen.width, Screen.height);
			// The particle shader multiplies by the vertex colour: white keeps Minecraft's own colours.
			Graphics.DrawTexture(screen, frame, new Rect(0, 0, 1, 1), 0, 0, 0, 0, Color.white, material);
			if (ShowCursor)
			{
				// The cursor lives in overlay pixels; scale if the overlay and screen differ.
				float sx = (float)Screen.width / frame.width, sy = (float)Screen.height / frame.height;
				GUI.DrawTexture(new Rect(Cursor.x * sx, Cursor.y * sy, cursor.width, cursor.height), cursor);
			}
		}

		/// <summary>An arrow pointer, white with a black edge (Minecraft's window cursor isn't in its frame).</summary>
		private static Texture2D MakeCursor()
		{
			const int w = 12, h = 19;
			var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
			var px = new Color32[w * h];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					// Image row y counts from the top; texture rows count from the bottom.
					int i = (h - 1 - y) * w + x;
					bool inside = y < 18 && x <= y * 0.6f;
					bool edge = inside && (x < 1.5f || x > y * 0.6f - 1.5f || y > 16.5f);
					px[i] = inside ? (edge ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255)) : new Color32(0, 0, 0, 0);
				}
			}
			tex.SetPixels32(px);
			tex.Apply();
			return tex;
		}
	}
}
