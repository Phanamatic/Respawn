using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

namespace UI.Scripts
{
    /// <summary>
    /// Persistent singleton that manages cursor appearance across all scenes.
    /// Automatically detects sliders and changes cursor to grab sprite when interacting.
    /// </summary>
    public class CursorManager : MonoBehaviour
    {
        [Header("Cursor Sprites")]
        [SerializeField] private Texture2D defaultCursor;
        [SerializeField] private Texture2D grabCursor;

        [Header("Cursor Hotspots")]
        [SerializeField] private Vector2 defaultCursorHotspot = Vector2.zero;
        [SerializeField] private Vector2 grabCursorHotspot = Vector2.zero;

        private static CursorManager _instance;
        public static CursorManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("CursorManager");
                    _instance = go.AddComponent<CursorManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private bool isGrabbing = false;
        private HashSet<Slider> registeredSliders = new HashSet<Slider>();

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            // Subscribe to scene loaded event
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            // Set initial cursor
            SetDefaultCursor();

            // Register sliders in current scene
            RegisterAllSliders();
        }

        private void OnDestroy()
        {
            // Unsubscribe from scene events
            SceneManager.sceneLoaded -= OnSceneLoaded;

            // Clean up slider event triggers
            UnregisterAllSliders();

            // Reset cursor to default
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Re-register sliders when a new scene loads
            RegisterAllSliders();
        }

        private void RegisterAllSliders()
        {
            // Find all sliders in the scene (including inactive ones)
            Slider[] allSliders = Object.FindObjectsByType<Slider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
// Swap deprecated FindObjectsOfType(...) with FindObjectsByType(...).

            foreach (Slider slider in allSliders)
            {
                if (slider != null && !registeredSliders.Contains(slider))
                {
                    RegisterSlider(slider);
                }
            }
        }

        private void RegisterSlider(Slider slider)
        {
            if (slider == null || registeredSliders.Contains(slider))
                return;

            // Add to tracked sliders
            registeredSliders.Add(slider);

            // Get the handle (the draggable part)
            RectTransform handle = slider.handleRect;
            if (handle == null)
                return;

            // Add EventTrigger component if it doesn't exist
            EventTrigger eventTrigger = handle.GetComponent<EventTrigger>();
            if (eventTrigger == null)
            {
                eventTrigger = handle.gameObject.AddComponent<EventTrigger>();
            }

            // Add PointerEnter event
            EventTrigger.Entry pointerEnter = new EventTrigger.Entry();
            pointerEnter.eventID = EventTriggerType.PointerEnter;
            pointerEnter.callback.AddListener((data) => OnSliderHandleEnter());
            eventTrigger.triggers.Add(pointerEnter);

            // Add PointerExit event
            EventTrigger.Entry pointerExit = new EventTrigger.Entry();
            pointerExit.eventID = EventTriggerType.PointerExit;
            pointerExit.callback.AddListener((data) => OnSliderHandleExit());
            eventTrigger.triggers.Add(pointerExit);

            // Add BeginDrag event
            EventTrigger.Entry beginDrag = new EventTrigger.Entry();
            beginDrag.eventID = EventTriggerType.BeginDrag;
            beginDrag.callback.AddListener((data) => OnSliderBeginDrag());
            eventTrigger.triggers.Add(beginDrag);

            // Add EndDrag event
            EventTrigger.Entry endDrag = new EventTrigger.Entry();
            endDrag.eventID = EventTriggerType.EndDrag;
            endDrag.callback.AddListener((data) => OnSliderEndDrag());
            eventTrigger.triggers.Add(endDrag);

            // Also add PointerDown/Up for clicking (not just dragging)
            EventTrigger.Entry pointerDown = new EventTrigger.Entry();
            pointerDown.eventID = EventTriggerType.PointerDown;
            pointerDown.callback.AddListener((data) => OnSliderBeginDrag());
            eventTrigger.triggers.Add(pointerDown);

            EventTrigger.Entry pointerUp = new EventTrigger.Entry();
            pointerUp.eventID = EventTriggerType.PointerUp;
            pointerUp.callback.AddListener((data) => OnSliderEndDrag());
            eventTrigger.triggers.Add(pointerUp);
        }

        private void UnregisterAllSliders()
        {
            // Clean up event triggers from all registered sliders
            foreach (Slider slider in registeredSliders)
            {
                if (slider != null && slider.handleRect != null)
                {
                    EventTrigger eventTrigger = slider.handleRect.GetComponent<EventTrigger>();
                    if (eventTrigger != null)
                    {
                        eventTrigger.triggers.Clear();
                    }
                }
            }

            registeredSliders.Clear();
        }

        private void OnSliderHandleEnter()
        {
            if (!isGrabbing)
            {
                SetGrabCursor();
            }
        }

        private void OnSliderHandleExit()
        {
            if (!isGrabbing)
            {
                SetDefaultCursor();
            }
        }

        private void OnSliderBeginDrag()
        {
            isGrabbing = true;
            SetGrabCursor();
        }

        private void OnSliderEndDrag()
        {
            isGrabbing = false;

            // Check if mouse is still over a slider handle
            // If not, return to default cursor
            PointerEventData pointerData = new PointerEventData(EventSystem.current)
            {
                position = Input.mousePosition
            };

            List<RaycastResult> results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(pointerData, results);

            bool overSliderHandle = false;
            foreach (RaycastResult result in results)
            {
                // Check if any of the hit objects are slider handles
                foreach (Slider slider in registeredSliders)
                {
                    if (slider != null && slider.handleRect != null &&
                        result.gameObject == slider.handleRect.gameObject)
                    {
                        overSliderHandle = true;
                        break;
                    }
                }
                if (overSliderHandle) break;
            }

            if (overSliderHandle)
            {
                SetGrabCursor();
            }
            else
            {
                SetDefaultCursor();
            }
        }

        private void SetDefaultCursor()
        {
            if (defaultCursor != null)
            {
                Cursor.SetCursor(defaultCursor, defaultCursorHotspot, CursorMode.Auto);
            }
            else
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
        }

        private void SetGrabCursor()
        {
            if (grabCursor != null)
            {
                Cursor.SetCursor(grabCursor, grabCursorHotspot, CursorMode.Auto);
            }
        }

        /// <summary>
        /// Manually register a specific slider (useful for dynamically created sliders)
        /// </summary>
        public void RegisterSliderManually(Slider slider)
        {
            if (slider != null && !registeredSliders.Contains(slider))
            {
                RegisterSlider(slider);
            }
        }

        /// <summary>
        /// Refresh slider registration (call this after instantiating new sliders)
        /// </summary>
        public void RefreshSliders()
        {
            RegisterAllSliders();
        }
    }
}
