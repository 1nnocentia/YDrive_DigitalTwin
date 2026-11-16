using UnityEngine;
using System.Collections.Generic;

public class OnScreenLogger : MonoBehaviour
{
    [Header("Logger Settings")]
    public int maxLines = 12;
    public bool showLog = true;

    private Queue<string> logQueue = new Queue<string>();
    private Vector2 scrollPosition;
    private GUIStyle logStyle;

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        string color = "white";
        if (type == LogType.Error || type == LogType.Exception) color = "#FF5555";
        else if (type == LogType.Warning) color = "#FFFF55";

        string timestamp = System.DateTime.Now.ToString("HH:mm:ss");
        string formattedLog = $"<color={color}>[{timestamp}] {logString}</color>";

        logQueue.Enqueue(formattedLog);

        while (logQueue.Count > maxLines)
        {
            logQueue.Dequeue();
        }
    }

    void OnGUI()
    {
        if (!showLog) return;

        if (logStyle == null)
        {
            logStyle = new GUIStyle(GUI.skin.label);
            logStyle.fontSize = Screen.width > 1000 ? 20 : 14;
            logStyle.richText = true;
            logStyle.wordWrap = true;
        }

        float boxWidth = Screen.width * 0.95f;
        float boxHeight = Screen.height * 0.35f;

        GUILayout.BeginArea(new Rect(10, 10, boxWidth, boxHeight), GUI.skin.box);
        scrollPosition = GUILayout.BeginScrollView(scrollPosition);

        foreach (string log in logQueue)
        {
            GUILayout.Label(log, logStyle);
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}