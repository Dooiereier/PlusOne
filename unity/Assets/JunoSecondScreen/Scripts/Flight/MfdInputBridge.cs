namespace JunoSecondScreen.Flight
{
    using JunoSecondScreen.Util;
    using ModApi.Flight.GameView;
    using UnityEngine;
    using UnityEngine.EventSystems;
    using UnityEngine.UI;

    /// <summary>
    /// Simulates a click on an MFD's world-space Canvas from a normalized
    /// (u, v) position on its rendered image.
    /// </summary>
    /// <remarks>
    /// MFD widgets don't use Unity's standard IPointerClickHandler/Button/
    /// EventTrigger at all - every widget's base class (WidgetScript) instead
    /// implements the game's own ModApi.Flight.GameView.IGameViewPointerEventHandler,
    /// with a single HandleGameViewPointerEvent(GameViewPointerEvent) method
    /// (confirmed by inspecting the game's own assemblies; ExecuteEvents against
    /// the standard interfaces reliably found the right widget but never any
    /// handler, which is what led here). This also sidesteps GraphicRaycaster
    /// entirely (which raycasts through canvas.worldCamera - the player's own,
    /// live, independently moving in-game camera; the console's MFD feed is
    /// always framed on the MFD by its own dedicated capture camera regardless
    /// of where the player is actually looking, so a raycast through the
    /// player's camera may not even hit the canvas). The target widget is
    /// instead found directly from the canvas's own RectTransform corners (no
    /// camera involved) by walking the widget hierarchy for a local-space rect
    /// containment hit. Must only be called from the Unity main thread.
    /// </remarks>
    internal static class MfdInputBridge
    {
        /// <param name="u">Normalized X on the rendered MFD image, 0 = left, 1 = right.</param>
        /// <param name="v">Normalized Y on the rendered MFD image, 0 = top, 1 = bottom.</param>
        public static void Click(Canvas canvas, float u, float v)
        {
            RectTransform rect = canvas != null ? canvas.GetComponent<RectTransform>() : null;
            EventSystem eventSystem = EventSystem.current;

            if (rect == null || eventSystem == null)
            {
                Log.Warn($"MfdInputBridge: cannot simulate a click (rect={rect != null}, eventSystem={eventSystem != null}).");
                return;
            }

            var corners = new Vector3[4]; // bottom-left, top-left, top-right, bottom-right
            rect.GetWorldCorners(corners);

            Vector3 top = Vector3.Lerp(corners[1], corners[2], Mathf.Clamp01(u));
            Vector3 bottom = Vector3.Lerp(corners[0], corners[3], Mathf.Clamp01(u));
            Vector3 worldPoint = Vector3.Lerp(top, bottom, Mathf.Clamp01(v));

            GameObject target = FindTopmostHit(rect, worldPoint);
            IGameViewPointerEventHandler handler = target != null ? target.GetComponentInParent<IGameViewPointerEventHandler>() : null;

            if (handler == null)
            {
                return;
            }

            var pointerData = new PointerEventData(eventSystem)
            {
                pointerId = -1, // mimic a mouse click, not a touch
                button = PointerEventData.InputButton.Left,
            };

            handler.HandleGameViewPointerEvent(new GameViewPointerEvent(GameViewPointerEventType.PointerDown, pointerData));
            handler.HandleGameViewPointerEvent(new GameViewPointerEvent(GameViewPointerEventType.PointerUp, pointerData));
            handler.HandleGameViewPointerEvent(new GameViewPointerEvent(GameViewPointerEventType.PointerClick, pointerData));
        }

        // Walks the RectTransform hierarchy under 'parent' for the frontmost
        // (last-sibling-drawn-on-top, deepest-first) widget whose own rect
        // contains 'worldPoint', skipping any widget whose Graphic explicitly
        // opts out of raycasting. Camera-independent: purely local-space math.
        private static GameObject FindTopmostHit(RectTransform parent, Vector3 worldPoint)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                if (!(parent.GetChild(i) is RectTransform child) || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                GameObject deeper = FindTopmostHit(child, worldPoint);
                if (deeper != null)
                {
                    return deeper;
                }

                Graphic graphic = child.GetComponent<Graphic>();
                if (graphic != null && !graphic.raycastTarget)
                {
                    continue;
                }

                if (child.rect.Contains(child.InverseTransformPoint(worldPoint)))
                {
                    return child.gameObject;
                }
            }

            return null;
        }
    }
}
