using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MAI2.Util;
using Main;
using Manager;
using MelonLoader;
using SinmaiAssist.Types;
using SinmaiAssist.Utils;
using UnityEngine;
using Type = System.Type;

namespace SinmaiAssist.Cheat
{
    /// <summary>
    /// 批次处理器：把待转移成绩按每批最多 4 首（可配置）拆分，
    /// 自动完成登录/进入选曲、逐首转移、批间自动重新进入，直到全部处理完成。
    /// 所有状态判断均基于当前游戏流程（<see cref="GameState"/>），不使用固定 Sleep。
    /// </summary>
    public static class BatchTransfer
    {
        public static List<TransferItem> Items { get; private set; } = new List<TransferItem>();
        public static volatile bool Running;
        public static string State = "Idle";
        public static string Message = "";
        public static int CurrentBatch;
        public static int TotalBatches;
        public static int CompletedCount;
        public static int FailedCount;

        private static volatile bool _startRequested;
        private static volatile bool _stopRequested;
        private static List<TransferItem> _pendingItems;
        private static bool _lastWaitOk;
        private static float _lastLogTime;

        private static void LogSnapshot(string tag)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastLogTime < 1f)
            {
                return;
            }
            _lastLogTime = now;
            MelonLogger.Msg($"[ScoreTransfer] {tag} · {GameState.Snapshot()}");
        }

        public static bool RequestStart(List<TransferItem> items, out string error)
        {
            if (items == null || items.Count == 0)
            {
                error = "没有待转移的曲目";
                return false;
            }
            if (Running || _startRequested)
            {
                error = "已有转移任务正在进行";
                return false;
            }

            _pendingItems = items;
            _stopRequested = false;
            _startRequested = true;
            error = null;
            return true;
        }

        public static void RequestStop()
        {
            _stopRequested = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMainObject), "Update")]
        public static void OnTick()
        {
            if (!_startRequested || Running)
            {
                return;
            }
            _startRequested = false;
            Running = true;
            List<TransferItem> items = _pendingItems;
            _pendingItems = null;
            try
            {
                MelonCoroutines.Start(RunBatch(items));
            }
            catch (Exception e)
            {
                Running = false;
                State = "Error";
                Message = e.Message;
                MelonLogger.Error(e);
            }
        }

        private static IEnumerator RunBatch(List<TransferItem> items)
        {
            try
            {
                yield return RunBatchInternal(items);
            }
            finally
            {
                Running = false;
            }
        }

        private static IEnumerator RunBatchInternal(List<TransferItem> items)
        {
            Items = items;
            CompletedCount = 0;
            FailedCount = 0;
            _stopRequested = false;

            int batchSize = Math.Max(1, SinmaiAssist.config.ScoreTransfer.BatchSize);
            float enterTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.EnterTimeoutSeconds);
            float trackTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.TrackTimeoutSeconds);

            List<List<TransferItem>> batches = Split(items, batchSize);
            TotalBatches = batches.Count;
            MelonLogger.Msg($"[ScoreTransfer] 批次任务开始，共 {items.Count} 首 / {TotalBatches} 批");

            for (int b = 0; b < batches.Count; b++)
            {
                CurrentBatch = b + 1;
                State = $"Batch {CurrentBatch}/{TotalBatches}";
                Message = "";
                List<TransferItem> batch = batches[b];

                for (int i = 0; i < batch.Count; i++)
                {
                    TransferItem item = batch[i];
                    item.batchIndex = CurrentBatch;

                    if (_stopRequested)
                    {
                        item.MarkSkipped("已手动停止");
                        continue;
                    }

                    Message = $"批 {CurrentBatch}/{TotalBatches} · 曲目 {item.musicId}";
                    State = $"Batch {CurrentBatch}/{TotalBatches} - Selecting {i + 1}/{batch.Count}";

                    // 1. 等待进入选曲界面，必要时自动完成登录/进入流程
                    yield return EnsureMusicSelect(enterTimeout);
                    if (!_lastWaitOk)
                    {
                        item.MarkFailed("无法进入选歌界面（超时）");
                        UpdateCounters();
                        continue;
                    }

                    // 2. 记录原完成度
                    item.originalAchievement = GetAchievement(item.musicId, item.difficulty);
                    if (string.IsNullOrEmpty(item.name))
                    {
                        item.name = LookupName(item.musicId);
                    }

                    // 3. 复用单曲转移
                    item.MarkRunning();
                    yield return RunSingle(item, enterTimeout, trackTimeout);
                    UpdateCounters();

                    if (item.status == "Failed")
                    {
                        MelonLogger.Warning($"[ScoreTransfer] [FAILED] {item.name} / {item.musicId} / " +
                                            $"difficulty {item.difficulty} / target {item.targetAchievement:0.0000}% : {item.error}");
                    }
                    else if (item.status == "Done")
                    {
                        MelonLogger.Msg($"[ScoreTransfer] [OK] {item.name} / {item.musicId} / " +
                                        $"difficulty {item.difficulty} / {item.originalAchievement / 10000m:0.0000}% -> {item.targetAchievement:0.0000}%");
                    }
                }

                State = $"Batch {CurrentBatch}/{TotalBatches} 完成";
            }

            // 所有计划曲目处理完后：若本局尚未结束（游戏又回到选曲界面，说明还剩 Track），
            // 就用最后一首重复传分，直到游戏进入结算/本局结束（登出）。
            if (!_stopRequested && items.Count > 0 && SinmaiAssist.config.ScoreTransfer.FillRemainingTracks)
            {
                yield return FillRemainingTracks(items[items.Count - 1], enterTimeout, trackTimeout);
            }

            State = "Completed";
            Message = _stopRequested ? "已停止" : "全部完成";
            ResultAdvancer.Enabled = false;
            Running = false;
            MelonLogger.Msg($"[ScoreTransfer] 批次任务结束：成功 {CompletedCount}，失败 {FailedCount}");
        }

        private static IEnumerator RunSingle(TransferItem item, float enterTimeout, float trackTimeout)
        {
            // 等待上一个任务完全结束
            float waited = 0f;
            while (ScoreTransfer.IsBusy && waited < enterTimeout)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            string error;
            if (!ScoreTransfer.Request(item.musicId, item.scoreType, item.difficulty, item.targetAchievement, out error))
            {
                item.MarkFailed(error);
                yield break;
            }

            float t = 0f;
            while (t < trackTimeout)
            {
                if (ScoreTransfer.State == "Error")
                {
                    item.MarkFailed(ScoreTransfer.Message);
                    yield break;
                }
                if (!ScoreTransfer.IsBusy && ScoreTransfer.State == "Completed")
                {
                    item.MarkDone();
                    break;
                }
                LogSnapshot("等待曲目完成");
                t += Time.deltaTime;
                yield return null;
            }

            if (!item.IsFinished)
            {
                item.MarkFailed("等待曲目完成超时");
            }

            if (item.status == "Done")
            {
                yield return WaitAfterTrack(trackTimeout);
            }
        }

        private static IEnumerator EnsureMusicSelect(float timeout)
        {
            float t = 0f;
            while (t < timeout)
            {
                if (GameState.IsMusicSelect && MusicSelect.IsReady)
                {
                    _lastWaitOk = true;
                    yield break;
                }
                GameState.TryAutoAdvance();
                LogSnapshot("等待选曲界面");
                t += Time.deltaTime;
                yield return null;
            }
            _lastWaitOk = GameState.IsMusicSelect && MusicSelect.IsReady;
        }

        private static IEnumerator FillRemainingTracks(TransferItem last, float enterTimeout, float trackTimeout)
        {
            int safety = 0;
            while (safety < 10)
            {
                if (GameState.IsSessionEnding)
                {
                    MelonLogger.Msg("[ScoreTransfer] 本局已结束（进入结算/登出），停止补曲");
                    yield break;
                }

                State = "补足剩余 Track";
                yield return EnsureMusicSelect(enterTimeout);
                if (!_lastWaitOk)
                {
                    MelonLogger.Warning("[ScoreTransfer] 补曲：无法进入选曲界面");
                    yield break;
                }
                if (GameState.IsSessionEnding)
                {
                    yield break;
                }

                TransferItem repeat = new TransferItem
                {
                    musicId = last.musicId,
                    scoreType = last.scoreType,
                    difficulty = last.difficulty,
                    targetAchievement = last.targetAchievement,
                    name = last.name,
                    batchIndex = last.batchIndex
                };
                repeat.MarkRunning();
                MelonLogger.Msg($"[ScoreTransfer] 补足剩余 Track：重复最后一首 {repeat.musicId} (难度 {repeat.difficulty})");
                yield return RunSingle(repeat, enterTimeout, trackTimeout);
                safety++;
            }
            MelonLogger.Warning("[ScoreTransfer] 补曲达到上限，停止");
        }

        private static IEnumerator WaitAfterTrack(float timeout)
        {
            float t = 0f;
            while (t < timeout)
            {
                if (GameState.IsMusicSelect || GameState.IsSessionEnding)
                {
                    _lastWaitOk = true;
                    yield break;
                }
                LogSnapshot("等待 Track 结束");
                t += Time.deltaTime;
                yield return null;
            }
            _lastWaitOk = false;
        }

        private static List<List<TransferItem>> Split(List<TransferItem> items, int batchSize)
        {
            List<List<TransferItem>> result = new List<List<TransferItem>>();
            for (int i = 0; i < items.Count; i += batchSize)
            {
                List<TransferItem> batch = new List<TransferItem>();
                for (int j = i; j < i + batchSize && j < items.Count; j++)
                {
                    batch.Add(items[j]);
                }
                result.Add(batch);
            }
            return result;
        }

        private static string LookupName(int musicId)
        {
            foreach (TransferSongInfo song in SongDatabase.Songs)
            {
                if (song.id == musicId)
                {
                    return song.name;
                }
            }
            return "";
        }

        private static void UpdateCounters()
        {
            int done = 0;
            int failed = 0;
            foreach (TransferItem item in Items)
            {
                if (item.status == "Done") done++;
                else if (item.status == "Failed") failed++;
            }
            CompletedCount = done;
            FailedCount = failed;
        }

        /// <summary>读取指定曲目/难度的原完成度（原始值，×10000）。</summary>
        public static uint GetAchievement(int musicId, int difficulty)
        {
            try
            {
                var userData = Singleton<UserDataManager>.Instance.GetUserData(0);
                if (userData == null)
                {
                    return 0;
                }

                Type type = userData.GetType();

                PropertyInfo listProp = type.GetProperty("ScoreList", BindingFlags.Public | BindingFlags.Instance);
                if (listProp != null)
                {
                    Array array = listProp.GetValue(userData, null) as Array;
                    if (array != null && difficulty >= 0 && difficulty < array.Length)
                    {
                        IEnumerable list = array.GetValue(difficulty) as IEnumerable;
                        if (list != null)
                        {
                            foreach (object score in list)
                            {
                                if (GetInt(score, "id") == musicId)
                                {
                                    return GetUint(score, "achivement");
                                }
                            }
                        }
                    }
                }

                PropertyInfo dicProp = type.GetProperty("ScoreDic", BindingFlags.Public | BindingFlags.Instance);
                if (dicProp != null)
                {
                    Array array = dicProp.GetValue(userData, null) as Array;
                    if (array != null && difficulty >= 0 && difficulty < array.Length)
                    {
                        IDictionary dictionary = array.GetValue(difficulty) as IDictionary;
                        if (dictionary != null && dictionary.Contains(musicId))
                        {
                            return GetUint(dictionary[musicId], "achivement");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 读取原完成度失败: {e.Message}");
            }
            return 0;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null)
            {
                return null;
            }
            Type type = obj.GetType();
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                return prop.GetValue(obj, null);
            }
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        private static int GetInt(object obj, string name)
        {
            object value = GetMember(obj, name);
            try
            {
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static uint GetUint(object obj, string name)
        {
            object value = GetMember(obj, name);
            try
            {
                return value == null ? 0u : Convert.ToUInt32(value);
            }
            catch
            {
                return 0u;
            }
        }
    }
}
