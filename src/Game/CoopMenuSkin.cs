using System.Collections.Generic;
using UnityEngine;

namespace SummerCoop.Game
{
    // Never use MSC's bitmap font/skin: its glyph atlas does not contain Cyrillic.
    internal sealed class CoopMenuSkin
    {
        internal readonly GUISkin Skin;
        private readonly Font font;
        private readonly List<Texture2D> textures = new List<Texture2D>();
        internal CoopMenuSkin()
        {
            font = Font.CreateDynamicFontFromOSFont(new string[] { "Segoe UI", "Arial", "Tahoma" }, 16);
            font.RequestCharactersInTexture("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя.,:;!?/()—…", 16, FontStyle.Normal);
            Skin = ScriptableObject.CreateInstance<GUISkin>(); Skin.font = font;
            Texture2D panel = Solid(new Color(0.10f, 0.12f, 0.15f, 0.98f));
            Texture2D normal = Solid(new Color(0.21f, 0.26f, 0.32f));
            Texture2D hover = Solid(new Color(0.29f, 0.37f, 0.45f));
            Texture2D active = Solid(new Color(0.12f, 0.48f, 0.39f));
            Texture2D field = Solid(new Color(0.16f, 0.19f, 0.23f));
            Skin.window = Style(panel, 16, TextAnchor.UpperLeft, new RectOffset(14, 14, 14, 14));
            Skin.window.margin = new RectOffset(0, 0, 0, 0);
            Skin.label = Style(null, 16, TextAnchor.UpperLeft, new RectOffset(0, 0, 3, 3)); Skin.label.wordWrap = true;
            Skin.button = Style(normal, 16, TextAnchor.MiddleCenter, new RectOffset(10, 10, 7, 7));
            Skin.button.wordWrap = true;
            Skin.button.hover.background = Skin.button.onHover.background = hover;
            Skin.button.active.background = Skin.button.onActive.background = active;
            Skin.button.onNormal.background = Skin.button.onFocused.background = active;
            Skin.textField = Style(field, 16, TextAnchor.MiddleLeft, new RectOffset(8, 8, 6, 6));
            Skin.textField.focused.background = hover; Skin.textArea = new GUIStyle(Skin.textField) { wordWrap = true };
            Skin.toggle = Style(normal, 16, TextAnchor.MiddleLeft, new RectOffset(10, 10, 7, 7));
            Skin.toggle.onNormal.background = Skin.toggle.onFocused.background = active;
            Skin.toggle.hover.background = Skin.toggle.onHover.background = hover;
            Skin.box = Style(panel, 16, TextAnchor.UpperLeft, new RectOffset(8, 8, 8, 8));
            Skin.horizontalSlider = Style(field, 0, TextAnchor.MiddleCenter, new RectOffset(0, 0, 0, 0)); Skin.horizontalSlider.fixedHeight = 16;
            Skin.horizontalSliderThumb = Style(active, 0, TextAnchor.MiddleCenter, new RectOffset(0, 0, 0, 0)); Skin.horizontalSliderThumb.fixedWidth = 18; Skin.horizontalSliderThumb.fixedHeight = 16;
            Skin.verticalScrollbar = Style(field, 0, TextAnchor.MiddleCenter, new RectOffset(0, 0, 0, 0)); Skin.verticalScrollbar.fixedWidth = 16;
            Skin.verticalScrollbarThumb = Style(hover, 0, TextAnchor.MiddleCenter, new RectOffset(0, 0, 0, 0)); Skin.verticalScrollbarThumb.fixedWidth = 16;
            Skin.verticalScrollbarUpButton = new GUIStyle { fixedHeight = 0 }; Skin.verticalScrollbarDownButton = new GUIStyle { fixedHeight = 0 };
            Skin.horizontalScrollbar = new GUIStyle { fixedHeight = 0 }; Skin.scrollView = new GUIStyle();
            Skin.settings.cursorColor = Color.white; Skin.settings.selectionColor = new Color(0.20f, 0.55f, 0.80f, 0.8f);
        }
        private Texture2D Solid(Color color)
        {
            Texture2D texture = new Texture2D(1, 1); texture.SetPixel(0, 0, color); texture.Apply(); textures.Add(texture); return texture;
        }
        private GUIStyle Style(Texture2D background, int size, TextAnchor alignment, RectOffset padding)
        {
            GUIStyle style = new GUIStyle { font = font, fontSize = size, alignment = alignment, padding = padding, margin = new RectOffset(0, 0, 3, 3) };
            style.normal.background = style.hover.background = style.active.background = style.focused.background = background;
            style.onNormal.background = style.onHover.background = style.onActive.background = style.onFocused.background = background;
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = Color.white;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = Color.white;
            return style;
        }
        internal void Dispose()
        {
            foreach (Texture2D texture in textures) Object.Destroy(texture);
            Object.Destroy(Skin); Object.Destroy(font);
        }
    }
}
