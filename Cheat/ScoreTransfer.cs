using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Process;
using SinmaiAssist.Types;

namespace SinmaiAssist.Cheat
{
    /// <summary>
    /// 分数转移核心逻辑：
    /// 1. 复用 <see cref="MusicSelect"/> 定位并切换到目标曲目；
    /// 2. 设置目标难度与谱面类型；
    /// 3. 复用完成度写入模块（<see cref="AchievementSetter"/>）强制写入指定完成度并结束该曲。
    /// </summary>
    public static class ScoreTransfer
    {
        public static List<TransferSongInfo> SongList = new List<TransferSongInfo>();

        public static string State = "Idle";
        public static string Message = "";
        public static int CurrentMusicId;
        public static int CurrentDifficulty;
        public static decimal CurrentAchievement;

        private static readonly object SyncRoot = new object();
        private static bool _hasPending;
        private static int _pendingMusicId;
        private static int _pendingScoreType;
        private static int _pendingDifficulty;
        private static decimal _pendingAchievement;
        private static int _pendingComboStatus = -1;
        private static bool _inProgress;

        public static bool IsBusy
        {
            get { lock (SyncRoot) { return _inProgress; } }
        }

        /// <summary>由 HTTP 线程调用，投递一次转移请求（线程安全）。</summary>
        public static bool Request(int musicId, int scoreType, int difficulty, decimal achievement, out string error)
        {
            return Request(musicId, scoreType, difficulty, achievement, -1, out error);
        }

        /// <summary>带“状态(0=AP+/1=AP/2=FC+/3=FC, -1=不限)”的转移请求。</summary>
        public static bool Request(int musicId, int scoreType, int difficulty, decimal achievement, int comboStatus, out string error)
        {
            if (musicId <= 0)
            {
                error = "无效的歌曲 ID";
                return false;
            }
            if (difficulty < 0 || difficulty > 4)
            {
                error = "难度必须在 0-4 之间 (0=Basic,1=Advanced,2=Expert,3=Master,4=Re:Master)";
                return false;
            }
            if (achievement < 0m || achievement > 101m)
            {
                error = "完成度必须在 0-101 之间";
                return false;
            }

            lock (SyncRoot)
            {
                if (_inProgress || _hasPending)
                {
                    error = "已有转移任务正在进行";
                    return false;
                }
                _pendingMusicId = musicId;
                _pendingScoreType = scoreType;
                _pendingDifficulty = difficulty;
                _pendingAchievement = achievement;
                _pendingComboStatus = comboStatus;
                _hasPending = true;
                State = "Queued";
                Message = "";
            }

            error = null;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "OnUpdate")]
        public static void OnMusicSelectUpdate()
        {
            if (!_hasPending)
            {
                return;
            }

            int musicId;
            int scoreType;
            int difficulty;
            decimal achievement;
            int comboStatus;
            lock (SyncRoot)
            {
                if (!_hasPending)
                {
                    return;
                }
                _hasPending = false;
                _inProgress = true;
                musicId = _pendingMusicId;
                scoreType = _pendingScoreType;
                difficulty = _pendingDifficulty;
                achievement = _pendingAchievement;
                comboStatus = _pendingComboStatus;
            }

            try
            {
                MelonCoroutines.Start(RunTransfer(musicId, scoreType, difficulty, achievement, comboStatus));
            }
            catch (Exception e)
            {
                lock (SyncRoot) { _inProgress = false; }
                State = "Error";
                Message = e.Message;
                MelonLogger.Error(e);
            }
        }

        private static IEnumerator RunTransfer(int musicId, int scoreType, int difficulty, decimal achievement, int comboStatus)
        {
            try
            {
                State = "Selecting";
                ResultAdvancer.Enabled = true;
                if (!MusicSelect.IsReady)
                {
                    Fail("当前不在选歌界面，无法开始转移");
                    yield break;
                }

                string selectResult = MusicSelect.SelectMusic(musicId);
                if (selectResult != "成功")
                {
                    Fail(selectResult);
                    yield break;
                }

                ApplySelection(musicId, scoreType, difficulty);

                // 等待游戏状态稳定
                float wait = 0f;
                while (wait < 0.5f)
                {
                    wait += UnityEngine.Time.deltaTime;
                    yield return null;
                }

                State = "Starting";
                string startResult = MusicSelect.StartGame();
                if (startResult != "成功")
                {
                    Fail(startResult);
                    yield break;
                }

                // 交给完成度写入模块
                AchievementSetter.Target = achievement;
                // 状态列：0=AP+ 1=AP 2=FC+ 3=FC -> 游戏 PlayComboflagID (4/3/2/1)；未指定=-1
                AchievementSetter.DesiredCombo = comboStatus < 0 ? -1 : (4 - comboStatus);
                AchievementSetter.Pending = true;

                CurrentMusicId = musicId;
                CurrentDifficulty = difficulty;
                CurrentAchievement = achievement;
                State = "Playing";
                Message = $"已进入曲目 {musicId} (难度 {difficulty})，正在写入完成度 {achievement:0.0000}%";
            }
            finally
            {
                if (State == "Error")
                {
                    lock (SyncRoot) { _inProgress = false; }
                }
            }
        }

        private static void ApplySelection(int musicId, int scoreType, int difficulty)
        {
            int resolvedScoreType = scoreType >= 0 ? scoreType : (musicId >= 10000 ? 1 : 0);

            int count = Math.Min(2, GameManager.SelectMusicID.Length);
            for (int i = 0; i < count; i++)
            {
                if (!Singleton<UserDataManager>.Instance.GetUserData(i).IsEntry)
                {
                    continue;
                }
                GameManager.SelectMusicID[i] = musicId;
                GameManager.SelectDifficultyID[i] = difficulty;
                GameManager.SelectGhostID[i] = GhostManager.GhostTarget.End;
            }

            GameManager.SelectScoreType = resolvedScoreType;

            try
            {
                if (MusicSelect.Process != null)
                {
                    for (int i = 0; i < MusicSelect.Process.CurrentDifficulty.Length; i++)
                    {
                        MusicSelect.Process.CurrentDifficulty[i] = (MusicDifficultyID)difficulty;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 设置难度失败: {e.Message}");
            }
        }

        private static void Fail(string message)
        {
            State = "Error";
            Message = message;
            lock (SyncRoot) { _inProgress = false; }
            MelonLogger.Warning($"[ScoreTransfer] {message}");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ResultProcess), "OnStart")]
        public static void OnResultProcessStart()
        {
            lock (SyncRoot)
            {
                if (!_inProgress)
                {
                    return;
                }
                _inProgress = false;
            }
            if (State == "Playing" || State == "Starting")
            {
                State = "Completed";
                Message = $"完成度 {CurrentAchievement:0.0000}% 已写入曲目 {CurrentMusicId}";
            }
        }
    }
}
