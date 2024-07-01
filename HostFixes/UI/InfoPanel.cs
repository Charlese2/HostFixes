using BepInEx.Logging;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace HostFixes.UI
{
    internal class InfoPanel
    {
        public static GameObject GameObjectInstance;
        public static InfoPanel Instance;
        private Text MyText;
        private Queue logQueue = new(10);
        public InfoPanel()
        {
            Instance ??= this;

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
                Plugin.Log.LogError("test is null");
            }
            Plugin.Log.LogEvent -= Log_LogEvent;
            Plugin.Log.LogEvent += Log_LogEvent;
        }

        private void Log_LogEvent(object sender, LogEventArgs logEvent)
        {
            if (logEvent.Level is not LogLevel.Warning) return;

            Log(logEvent.Data.ToString().Replace("\n", ""));
        }

        public void Log(object data)
        {
            try
            {
                if (logQueue.Count == 10)
                {
                    logQueue.Dequeue();
                }
                logQueue.Enqueue(data.ToString());
                MyText.text = "";
                foreach (string log in logQueue)
                {
                    MyText.text += $"{log}\n";
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
            }
        }
    }
}
