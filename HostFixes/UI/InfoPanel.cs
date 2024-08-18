using BepInEx.Logging;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace HostFixes.UI
{
    internal class InfoPanel
    {
        public static GameObject GameObjectInstance = null!;
        public static InfoPanel Instance = null!;
        private readonly Text MyText = null!;
        private readonly Queue logQueue = new(10);
        private string? LastLogMessage;
        private int TimesRepeated = 1;
        public InfoPanel()
        {
            Instance = this;

            GameObjectInstance = new GameObject("InfoPanel");
            GameObjectInstance.transform.SetParent(GameObject.Find("Systems/UI/Canvas").transform);
            GameObjectInstance.transform.localPosition = Vector3.zero;
            GameObjectInstance.transform.localScale = new Vector3(1, 1, 1);
            var test = GameObjectInstance.AddComponent<RectTransform>();
            test.anchorMin = new Vector2(0.6f, 0.6f);
            test.anchorMax = new Vector2(0.943f, 0.89f);



            var canvasRenderer = GameObjectInstance.AddComponent<CanvasRenderer>();
            canvasRenderer.SetColor(new Color(1.0f, 1.0f, 1.0f, 1.0f));

            var canvasGroup = GameObjectInstance.AddComponent<CanvasGroup>();
            canvasGroup.blocksRaycasts = false;

            Font font = Font.CreateDynamicFontFromOSFont("Noto Sans", 8);



            MyText = GameObjectInstance.AddComponent<Text>();
            if (MyText is not null)
            {
                MyText.font = font;
                MyText.fontSize = 7;
                MyText.text = "Host Fixes";
                MyText.supportRichText = false;
            }
            else
            {
                Plugin.Log.LogError("MyText is null");
            }
            Plugin.Log.LogEvent += Log_LogEvent;
        }

        ~InfoPanel()
        {
            Plugin.Log.LogInfo("InfoPanel Destroy");
        }

        internal void Log_LogEvent(object sender, LogEventArgs logEvent)
        {
            //if (logEvent.Level is not LogLevel.Info) return;

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
                    if (logQueue.Count == 9)
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
    }
}
