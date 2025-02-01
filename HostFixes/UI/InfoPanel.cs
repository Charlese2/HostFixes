using BepInEx.Logging;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace HostFixes.UI
{
    internal class InfoPanel
    {
        public static InfoPanel Instance = null!;
        public readonly InputAction action = null!;
        private static GameObject GameObjectInstance = null!;
        private readonly Text MyText = null!;
        private readonly Queue logQueue = new(10);
        private string? LastLogMessage;
        private int TimesRepeated = 1;
        private bool visible = false;

        public InfoPanel()
        {
            Instance = this;

            GameObjectInstance = new GameObject("InfoPanel");
            GameObjectInstance.transform.SetParent(GameObject.Find("Systems/UI/Canvas").transform);
            GameObjectInstance.transform.localPosition = Vector3.zero;
            GameObjectInstance.transform.localScale = new Vector3(1, 1, 1);
            var rectTransform = GameObjectInstance.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.6f, 0.6f);
            rectTransform.anchorMax = new Vector2(0.943f, 0.89f);



            var canvasRenderer = GameObjectInstance.AddComponent<CanvasRenderer>();
            canvasRenderer.SetColor(new Color(1.0f, 1.0f, 1.0f, 1.0f));

            var canvasGroup = GameObjectInstance.AddComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;

            Font font = Font.CreateDynamicFontFromOSFont("Noto Sans", 8);

            MyText = GameObjectInstance.AddComponent<Text>();
            if (MyText != null)
            {
                MyText.font = font;
                MyText.fontSize = 7;
                MyText.text = "Host Fixes";
                MyText.supportRichText = false;
            }
            else
            {
                Plugin.Log.LogError("MyText is null");
                return;
            }
            Plugin.Log.LogEvent += Log_LogEvent;
            GameObjectInstance.SetActive(visible);
            action = new("ToggleLogGUI", binding: "<Keyboard>/f9");
            action.performed += ToggleVisibility;
            action.Enable();
        }

        internal void Log_LogEvent(object sender, LogEventArgs logEvent)
        {
            Log(logEvent.Data.ToString().Replace("\n", ""));
        }

        public void Log(string data)
        {
            if (LastLogMessage == data)
            {
                TimesRepeated++;
            }
            else
            {
                if (LastLogMessage != null)
                {
                    if (logQueue.Count >= 9)
                    {
                        logQueue.Dequeue();
                    }
                    logQueue.Enqueue($"{LastLogMessage}{(TimesRepeated > 1 ? $" x{TimesRepeated}" : $"")}");
                    TimesRepeated = 1;
                }
                LastLogMessage = data;
            }

            if (MyText == null)
            {
                Plugin.Log.LogError("MyText is null. Can't add to GUI log.");
                return;
            }

            MyText.text = "";
            foreach (string log in logQueue)
            {
                MyText.text += $"{log}\n";
            }
            if (LastLogMessage != null)
            {
                MyText.text += $"{LastLogMessage}{(TimesRepeated > 1 ? $" x{TimesRepeated}" : $"")}";
            }
        }

        public void ToggleVisibility(InputAction.CallbackContext _)
        {
            visible = !visible;
            GameObjectInstance.SetActive(visible);
        }
    }
}
