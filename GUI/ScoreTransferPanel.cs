using System.Collections.Generic;
using SinmaiAssist.Cheat;
using SinmaiAssist.Types;
using SinmaiAssist.Utils;
using UnityEngine;

namespace SinmaiAssist.GUI
{
    /// <summary>分数转移状态面板（游戏中按 F8 开关）。</summary>
    public class ScoreTransferPanel
    {
        private static GUIStyle _title;
        private static GUIStyle _text;
        private static GUIStyle _error;
        private static Rect _rect = new Rect(20f, 20f, 460f, 520f);

        public static void OnGUI()
        {
            if (_title == null)
            {
                _title = new GUIStyle(UnityEngine.GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
                _title.normal.textColor = Color.white;
                _text = new GUIStyle(UnityEngine.GUI.skin.label) { fontSize = 12, wordWrap = true };
                _text.normal.textColor = Color.white;
                _error = new GUIStyle(UnityEngine.GUI.skin.label) { fontSize = 12, wordWrap = true };
                _error.normal.textColor = new Color(1f, 0.45f, 0.45f);
            }

            _rect = GUILayout.Window(10086, _rect, Draw, "Sinmai 分数转移");
        }

        private static void Draw(int id)
        {
            ScoreTransferConfig cfg = SinmaiAssist.config?.ScoreTransfer;

            GUILayout.Label($"待转移: {BatchTransfer.CompletedCount} / {BatchTransfer.Items.Count}", _title);
            GUILayout.Label($"批次: {BatchTransfer.CurrentBatch} / {BatchTransfer.TotalBatches}", _text);
            GUILayout.Label($"状态: {BatchTransfer.State}", _text);
            if (!string.IsNullOrEmpty(BatchTransfer.Message))
            {
                GUILayout.Label(BatchTransfer.Message, _text);
            }

            GUILayout.Space(6f);
            GUILayout.Label($"当前曲目: {ScoreTransfer.CurrentMusicId}", _text);
            GUILayout.Label($"难度: {ScoreTransfer.CurrentDifficulty}    目标完成度: {ScoreTransfer.CurrentAchievement:0.0000}%", _text);
            if (!string.IsNullOrEmpty(ScoreTransfer.Message))
            {
                GUILayout.Label(ScoreTransfer.Message, _text);
            }

            GUILayout.Space(6f);
            if (cfg != null)
            {
                GUILayout.Label($"网页: http://<本机IP>:{cfg.Port}/", _text);
            }

            GUILayout.Space(6f);
            foreach (TransferItem item in BatchTransfer.Items)
            {
                string line = $"[{item.status}] {item.name} {item.musicId} d{item.difficulty} " +
                              $"{item.originalAchievement / 10000m:0.0000}% -> {item.targetAchievement:0.0000}%" +
                              (string.IsNullOrEmpty(item.error) ? "" : $" ({item.error})");
                GUILayout.Label(line, item.status == "Failed" ? _error : _text);
            }

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重试失败项"))
            {
                List<TransferItem> failed = new List<TransferItem>();
                foreach (TransferItem item in BatchTransfer.Items)
                {
                    if (item.status == "Failed")
                    {
                        item.status = "Pending";
                        item.error = null;
                        failed.Add(item);
                    }
                }
                if (failed.Count > 0)
                {
                    string error;
                    BatchTransfer.RequestStart(failed, out error);
                }
            }
            if (GUILayout.Button("停止"))
            {
                BatchTransfer.RequestStop();
            }
            if (GUILayout.Button("刷新网页") && cfg != null)
            {
                ScoreTransferHttpServer.Init(cfg.Port);
            }
            GUILayout.EndHorizontal();

            UnityEngine.GUI.DragWindow();
        }
    }
}
