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
    /// 批次处理器（以“游戏 Session”为准，不硬编码 Track 数）：
    ///   Login → 逐首转移 → 每首等待 Result → 当本局结束(结算)时：
    ///   WaitingSettlement → 观测 UpsertUserAll 成功 → LoggingOut → 观测 UserLogout 成功 →
    ///   LoggedOut(等待回到可登录界面) → 下一批。
    /// 所有判断基于游戏 Process/网络状态，不使用固定 Sleep，不伪造任何 API 返回值。
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
        private static bool _sessionEnded;
        private static bool _sessionPlayedAnyTrack;
        private static TransferItem _lastPlayedItem;
        private static float _loginRealtime;
        private static float _lastLogTime;

        private static void LogSnapshot(string tag)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastLogTime < 1f)
            {
                return;
            }
            _lastLogTime = now;
            MelonLogger.Msg($"[ScoreTransfer] {tag} · {GameState.Snapshot()} · upsert={BatchSignals.Upsert} logout={BatchSignals.Logout}");
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
            State = "Idle";
            Message = "";
            _stopRequested = false;

            int batchSize = Math.Max(1, SinmaiAssist.config.ScoreTransfer.BatchSize);
            float enterTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.EnterTimeoutSeconds);
            float trackTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.TrackTimeoutSeconds);
            float uploadTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.UploadTimeoutSeconds);
            float logoutTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.LogoutTimeoutSeconds);
            float loginTimeout = Math.Max(5f, SinmaiAssist.config.ScoreTransfer.LoginTimeoutSeconds);

            TotalBatches = (items.Count + batchSize - 1) / batchSize;
            MelonLogger.Msg($"[ScoreTransfer] 任务开始：共 {items.Count} 首 / 预计 {TotalBatches} 批 (每批最多 {batchSize})");

            int session = 0;
            bool sessionOpen = false;
            int songsInSession = 0;
            bool aborted = false;

            for (int i = 0; i < items.Count; i++)
            {
                if (_stopRequested)
                {
                    items[i].MarkSkipped("已手动停止");
                    continue;
                }

                TransferItem item = items[i];

                if (!sessionOpen)
                {
                    session++;
                    yield return OpenSession(session, enterTimeout);
                    if (!_lastWaitOk)
                    {
                        item.MarkFailed("无法进入选歌界面（登录/进入游戏失败，超时）");
                        UpdateCounters();
                        aborted = true;
                        break;
                    }
                    sessionOpen = true;
                    songsInSession = 0;
                }

                songsInSession++;
                yield return ProcessItem(item, session, songsInSession, batchSize, enterTimeout, trackTimeout);
                UpdateCounters();
                if (item.status == "Done")
                {
                    _sessionPlayedAnyTrack = true;
                    _lastPlayedItem = item;
                }

                if (_sessionEnded)
                {
                    bool isLast = i >= items.Count - 1;
                    yield return HandleSessionEnd(session, isLast, uploadTimeout, logoutTimeout, loginTimeout);
                    sessionOpen = false;
                }
            }

            // 本局还剩 Track：重复最后一首成功开始的曲目直到结算（不硬编码 Track 数）
            if (!aborted && sessionOpen && !_stopRequested && _sessionPlayedAnyTrack && _lastPlayedItem != null &&
                SinmaiAssist.config.ScoreTransfer.FillRemainingTracks)
            {
                int guard = 0;
                while (!_sessionEnded && !_stopRequested && guard < 10)
                {
                    songsInSession++;
                    TransferItem repeat = Clone(_lastPlayedItem, "补");
                    yield return ProcessItem(repeat, session, songsInSession, batchSize, enterTimeout, trackTimeout);
                    if (repeat.status != "Done")
                    {
                        // 连已成功开始的曲目都无法再次进入，说明状态异常，停止补曲，避免死循环
                        break;
                    }
                    guard++;
                }
                if (_sessionEnded)
                {
                    yield return HandleSessionEnd(session, true, uploadTimeout, logoutTimeout, loginTimeout);
                    sessionOpen = false;
                }
            }

            State = "Finished";
            Message = _stopRequested ? "已停止" : (aborted ? "因错误中止" : "全部完成");
            ResultAdvancer.Enabled = false;
            Running = false;
            MelonLogger.Msg($"[ScoreTransfer] 任务结束：成功 {CompletedCount}，失败 {FailedCount}");
        }

        private static IEnumerator OpenSession(int session, float enterTimeout)
        {
            CurrentBatch = session;
            State = "LoggingIn";
            Message = "LoggingIn";
            _sessionPlayedAnyTrack = false;
            _lastPlayedItem = null;
            _loginRealtime = Time.realtimeSinceStartup;
            MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Login（等待进入选歌/登录界面）");

            yield return EnsureMusicSelect(enterTimeout);

            if (_lastWaitOk)
            {
                State = "LoggedIn";
                Message = "LoggedIn";
                MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Login success");
                yield return WarmUpSession(session);
            }
        }

        /// <summary>
        /// 保证从登录到登出(结算/上传)至少持续 MinSessionSeconds。
        /// 服务器只在 UserLogoutApi 时才接收上传，session 太短会导致成绩被静默丢弃。
        /// </summary>
        private static IEnumerator WarmUpSession(int session)
        {
            float min = Math.Max(0f, SinmaiAssist.config.ScoreTransfer.MinSessionSeconds);
            if (min <= 0f)
            {
                yield break;
            }

            float elapsed = Time.realtimeSinceStartup - _loginRealtime;
            float remaining = min - elapsed;
            if (remaining <= 0f)
            {
                yield break;
            }

            State = "SessionWarmup";
            Message = "SessionWarmup";
            MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] 登录后停留 {remaining:0}s（保证登录→登出 ≥ {min:0}s）");

            float t = 0f;
            float nextLog = 15f;
            while (t < remaining && !_stopRequested)
            {
                if (t >= nextLog)
                {
                    MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] 登录等待中… 剩余 {remaining - t:0}s");
                    nextLog += 15f;
                }
                t += Time.deltaTime;
                yield return null;
            }
        }

        /// <summary>
        /// 每首开始前的等待间隔。官方服务器需要在两首之间留出一定间隔，否则成绩可能被丢弃。
        /// </summary>
        private static IEnumerator WaitBeforeSong(int session, TransferItem item)
        {
            float seconds = Math.Max(0f, SinmaiAssist.config.ScoreTransfer.SongWaitSeconds);
            if (seconds <= 0f)
            {
                yield break;
            }

            State = "WaitingSong";
            Message = $"WaitingSong {item.musicId}";
            MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] 开始前等待 {seconds:0}s（曲目 {item.musicId}）");

            float t = 0f;
            float nextLog = 10f;
            while (t < seconds && !_stopRequested)
            {
                if (t >= nextLog)
                {
                    MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] 等待中… 剩余 {seconds - t:0}s");
                    nextLog += 10f;
                }
                t += Time.deltaTime;
                yield return null;
            }
        }

        private static IEnumerator ProcessItem(TransferItem item, int session, int indexInSession, int batchSize, float enterTimeout, float trackTimeout)
        {
            yield return WaitBeforeSong(session, item);

            State = "EnteringPlay";
            Message = $"EnteringPlay {item.musicId}";
            MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] [{indexInSession}/{batchSize}] Enter song {item.musicId} (difficulty {item.difficulty}, target {item.targetAchievement:0.0000}%)");

            item.originalAchievement = GetAchievement(item.musicId, item.difficulty);
            if (string.IsNullOrEmpty(item.name))
            {
                item.name = LookupName(item.musicId);
            }

            item.MarkRunning();
            yield return RunSingle(item, enterTimeout, trackTimeout);
            UpdateCounters();

            if (item.status == "Failed")
            {
                MelonLogger.Warning($"[ScoreTransfer] [Batch {session}] [{indexInSession}/{batchSize}] FAILED {item.name} / {item.musicId} / " +
                                    $"difficulty {item.difficulty} / target {item.targetAchievement:0.0000}% : {item.error}");
            }
            else if (item.status == "Done")
            {
                State = "WaitingResult";
                Message = "WaitingResult";
                MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] [{indexInSession}/{batchSize}] Result done · " +
                                $"{item.originalAchievement / 10000m:0.0000}% -> {item.targetAchievement:0.0000}%");
            }
        }

        private static IEnumerator HandleSessionEnd(int session, bool isLast, float uploadTimeout, float logoutTimeout, float loginTimeout)
        {
            State = "WaitingSettlement";
            Message = "WaitingSettlement";
            MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Waiting for settlement");

            // 1. 观测成绩提交 (UpsertUserAll)
            State = "UploadingResult";
            yield return WaitSignal(() => BatchSignals.Upsert != 0, uploadTimeout);
            if (BatchSignals.Upsert > 0)
            {
                MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Result upload success ({BatchSignals.LastUpsertMessage})");
            }
            else
            {
                MelonLogger.Warning($"[ScoreTransfer] [Batch {session}] Result upload NOT confirmed ({BatchSignals.LastUpsertMessage})");
            }

            // 2. 观测登出 (UserLogout)
            State = "LoggingOut";
            Message = "LoggingOut";
            MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Logging out");
            yield return WaitSignal(() => BatchSignals.Logout != 0, logoutTimeout);
            if (BatchSignals.Logout > 0)
            {
                MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] UserLogoutApi success ({BatchSignals.LastLogoutMessage})");
            }
            else
            {
                MelonLogger.Warning($"[ScoreTransfer] [Batch {session}] UserLogout NOT confirmed ({BatchSignals.LastLogoutMessage})");
            }

            // 3. 确认客户端已回到可重新登录状态（Entry 界面）
            State = "LoggedOut";
            Message = "LoggedOut";
            if (isLast)
            {
                MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Logged out（最后一批，结算/登出确认完成）");
            }
            else
            {
                MelonLogger.Msg($"[ScoreTransfer] [Batch {session}] Logged out，等待可登录界面");
                yield return WaitEntry(loginTimeout);
            }

            BatchSignals.Reset();
            State = isLast ? "Finished" : "StartingNextBatch";
            Message = State;
        }

        private static IEnumerator RunSingle(TransferItem item, float enterTimeout, float trackTimeout)
        {
            _sessionEnded = false;

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
                t += Time.deltaTime;
                yield return null;
            }

            if (!item.IsFinished)
            {
                item.MarkFailed("等待曲目完成超时");
                yield break;
            }

            yield return WaitAfterTrack(trackTimeout);
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

        private static IEnumerator WaitAfterTrack(float timeout)
        {
            float t = 0f;
            while (t < timeout)
            {
                if (GameState.IsSessionEnding)
                {
                    _lastWaitOk = true;
                    _sessionEnded = true;
                    yield break;
                }
                if (GameState.IsMusicSelect)
                {
                    _lastWaitOk = true;
                    _sessionEnded = false;
                    yield break;
                }
                LogSnapshot("等待 Track 结束");
                t += Time.deltaTime;
                yield return null;
            }
            _lastWaitOk = false;
        }

        private static IEnumerator WaitSignal(Func<bool> predicate, float timeout)
        {
            float t = 0f;
            while (t < timeout)
            {
                if (predicate())
                {
                    yield break;
                }
                LogSnapshot("等待网络确认");
                t += Time.deltaTime;
                yield return null;
            }
        }

        private static IEnumerator WaitEntry(float timeout)
        {
            float t = 0f;
            while (t < timeout)
            {
                if (GameState.HasProcess("Process.EntryProcess") ||
                    GameState.HasProcess("Process.Entry.EntryProcess"))
                {
                    yield break;
                }
                LogSnapshot("等待可登录界面");
                t += Time.deltaTime;
                yield return null;
            }
        }

        private static TransferItem Clone(TransferItem src, string suffix)
        {
            return new TransferItem
            {
                musicId = src.musicId,
                scoreType = src.scoreType,
                difficulty = src.difficulty,
                targetAchievement = src.targetAchievement,
                name = string.IsNullOrEmpty(src.name) ? "" : src.name + "(" + suffix + ")",
                batchIndex = src.batchIndex
            };
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
