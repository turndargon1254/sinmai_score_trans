using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Process;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Type = System.Type;

namespace SinmaiAssist.Cheat
{
    /// <summary>
    /// 分数转移专用的完成度写入：在 Play 阶段强制把结算完成度设置为指定值并结束本曲。
    /// 仅保留“设置指定完成度”所需逻辑（含小数支持）。
    /// </summary>
    internal class AchievementSetter
    {
        private enum GameSequence
        {
            Init,
            Sync,
            Start,
            StartWait,
            Play,
            PlayEnd,
            Result,
            ResultEnd,
            FinalWait,
            Release
        }

        public static bool Pending = false;
        public static decimal Target = 0m;

        // 进入谱面后先正常游玩多久(秒)再强制结算。太短服务器会判定不合法而丢弃成绩。
        private static float _playStartTime = -1f;

        // fullPlay 模式：让游戏自动完整演奏整首(真实判定/时长)，我们只逐键改写判定，
        // 这样上传的是一条“真实打过”的 playlog，服务器更容易接受。
        private static bool _planActive = false;
        private static readonly Dictionary<int, NoteJudge.ETiming> _plan = new Dictionary<int, NoteJudge.ETiming>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameProcess), "OnUpdate")]
        public static void Skip(GameProcess __instance)
        {
            try
            {
                Type processBaseType = typeof(GameProcess).BaseType;
                GameSequence sequence = (GameSequence)typeof(GameProcess)
                    .GetField("_sequence", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(__instance);

                // 新的一首进入 Init~StartWait 阶段时清除上一首的达成率覆盖标记。
                if (sequence < GameSequence.Play)
                {
                    _planActive = false;
                    _plan.Clear();
                    // 非转移目标曲目时关闭自动演奏，避免影响正常游玩。
                    if (SinmaiAssist.config.ScoreTransfer.FullPlay && !Pending)
                    {
                        GameManager.AutoPlay = GameManager.AutoPlayMode.None;
                    }
                }

                if (sequence >= GameSequence.Release)
                {
                    _playStartTime = -1f;
                    if (_planActive && !_resultLogged)
                    {
                        _resultLogged = true;
                        try
                        {
                            GameScoreList sc = Singleton<GamePlayManager>.Instance.GetGameScore(0);
                            if (sc != null)
                            {
                                MelonLogger.Msg($"[ScoreTransfer] 本曲实际达成率={sc.GetAchivement()} (目标 {Target * 10m})");
                            }
                        }
                        catch { }
                    }
                    return;
                }

                bool fullPlay = SinmaiAssist.config.ScoreTransfer.FullPlay;

                // ---- fullPlay：开启自动演奏，整首真实打完，只逐键改写判定 ----
                if (fullPlay && sequence == GameSequence.Play)
                {
                    if (Pending)
                    {
                        GameManager.AutoPlay = GameManager.AutoPlayMode.Critical;
                        BuildPlan(__instance);
                        _planActive = true;
                        Pending = false;
                        // 注意：fullPlay 下不覆盖 GetAchivement，让达成率由真实判定算出，
                        // 避免出现“全 Critical 却报 100.5x%”这种前后不一致而被服务器丢弃。
                        MelonLogger.Msg($"[ScoreTransfer] fullPlay: 自动完整演奏，plan={_plan.Count} 目标 {Target}%");
                    }
                    return;
                }

                if (fullPlay)
                {
                    return;
                }

                // ---- 非 fullPlay：旧的高速强制结算 ----
                if (sequence < GameSequence.Play || GameManager.IsNoteCheckMode || !Pending)
                {
                    return;
                }

                // 进入谱面后先正常游玩 PlayWaitSeconds 秒，再强制结算（时长太短服务器会丢弃成绩）。
                if (_playStartTime < 0f)
                {
                    _playStartTime = Time.realtimeSinceStartup;
                }
                float playWait = Math.Max(0f, SinmaiAssist.config.ScoreTransfer.PlayWaitSeconds);
                if (playWait > 0f && Time.realtimeSinceStartup - _playStartTime < playWait)
                {
                    return;
                }

                _playStartTime = -1f;
                Pending = false;

                var updateSubbMonitorData = typeof(GameProcess).GetMethod("UpdateSubbMonitorData", BindingFlags.NonPublic | BindingFlags.Instance);
                var setRelease = typeof(GameProcess).GetMethod("SetRelease", BindingFlags.NonPublic | BindingFlags.Instance);
                var isPartyPlay = typeof(GameProcess).GetMethod("IsPartyPlay", BindingFlags.NonPublic | BindingFlags.Instance);
                var containerField = processBaseType.GetField("container", BindingFlags.NonPublic | BindingFlags.Instance);
                ProcessDataContainer container = (ProcessDataContainer)containerField.GetValue(__instance);
                GameMonitor[] monitors = (GameMonitor[])typeof(GameProcess)
                    .GetField("_monitors", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(__instance);
                Message[] messages = (Message[])typeof(GameProcess)
                    .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(__instance);

                for (int i = 0; i < monitors.Length; i++)
                {
                    monitors[i].Seek(0);
                }
                NotesManager.StartPlay(0);
                NotesManager.Pause(true);

                bool partyPlay = (bool)isPartyPlay.Invoke(__instance, null);
                Singleton<GamePlayManager>.Instance.Initialize(partyPlay);

                uint maxCombo = 0u;
                for (int i = 0; i < monitors.Length; i++)
                {
                    if (Singleton<UserDataManager>.Instance.GetUserData(i).IsEntry)
                    {
                        monitors[i].ForceAchivement((int)Target, 0);
                        maxCombo += Singleton<GamePlayManager>.Instance.GetGameScore(i).MaxCombo;
                    }
                }
                for (int i = 0; i < monitors.Length; i++)
                {
                    if (Singleton<UserDataManager>.Instance.GetUserData(i).IsEntry)
                    {
                        Singleton<GamePlayManager>.Instance.GetGameScore(i).SetChain(maxCombo);
                    }
                }
                for (int i = 0; i < monitors.Length; i++)
                {
                    if (Singleton<UserDataManager>.Instance.GetUserData(i).IsEntry)
                    {
                        updateSubbMonitorData.Invoke(__instance, new object[] { i });
                        container.processManager.SendMessage(messages[i]);
                        Singleton<GamePlayManager>.Instance.SetSyncResult(i);
                    }
                }
                setRelease.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                MelonLogger.Error(e);
            }
        }

        /// <summary>
        /// fullPlay 模式下，游戏自动演奏时会为每个音符调用 SetResult。
        /// 这里按预先算好的计划改写该音符的判定，从而精确命中目标达成率，
        /// 同时保持整首真实时长/Note 进度（playlog 看起来就是一把正常游玩）。
        /// </summary>
        private static int _overrideCount;
        private static bool _resultLogged;

        private static NoteJudge.ETiming PlannedFor(int noteIndex)
        {
            if (_planActive && _plan.TryGetValue(noteIndex, out NoteJudge.ETiming t))
            {
                return t;
            }
            return NoteJudge.ETiming.Critical;
        }

        // AutoPlay 的判定来源：Tap/Touch 走 NoteBase.SetAutoPlayJudge，Hold 走 JudgeTotalResult，
        // Slide 走 SlideRoot.Judge。这里在游戏自动判定之后，用我们预计算的档位覆盖 JudgeResult。
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NoteBase), "SetAutoPlayJudge")]
        public static void P_TapAutoJudge(NoteBase __instance)
        {
            if (_planActive)
            {
                ApplyPlanToNote(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HoldNote), "JudgeTotalResult")]
        public static void P_HoldJudge(HoldNote __instance)
        {
            if (_planActive)
            {
                ApplyPlanToNote(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BreakHoldNote), "JudgeTotalResult")]
        public static void P_BreakHoldJudge(BreakHoldNote __instance)
        {
            if (_planActive)
            {
                ApplyPlanToNote(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TouchHoldC), "JudgeTotalResult")]
        public static void P_TouchHoldJudge(TouchHoldC __instance)
        {
            if (_planActive)
            {
                ApplyPlanToNote(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlideRoot), "Judge")]
        public static void P_SlideJudge(SlideRoot __instance)
        {
            if (_planActive)
            {
                ApplyPlanToNote(__instance);
            }
        }

        private static void ApplyPlanToNote(object note)
        {
            if (!_planActive || note == null)
            {
                return;
            }
            Traverse tr = Traverse.Create(note);
            int idx = tr.Field<int>("NoteIndex").Value;
            NoteJudge.ETiming planned = PlannedFor(idx);
            tr.Field<NoteJudge.ETiming>("JudgeResult").Value = planned;
            _overrideCount++;
            if (_overrideCount == 1)
            {
                MelonLogger.Msg($"[ScoreTransfer] AutoJudge 覆盖生效: index={idx} {planned} (plan={_plan.Count})");
            }
        }

        /// <summary>
        /// 依据目标达成率，为当前谱面每个 Note 预计算判定（默认全 Critical，部分改 Great/Perfect）。
        /// </summary>
        private static void BuildPlan(GameProcess process)
        {
            try
            {
                _plan.Clear();
                _overrideCount = 0;
                _resultLogged = false;
                int monitorIndex = -1;
                for (int i = 0; i < 2; i++)
                {
                    if (Singleton<UserDataManager>.Instance.GetUserData(i).IsEntry)
                    {
                        monitorIndex = i;
                        break;
                    }
                }
                if (monitorIndex < 0)
                {
                    return;
                }
                GameScoreList score = Singleton<GamePlayManager>.Instance.GetGameScore(monitorIndex);
                if (score == null)
                {
                    return;
                }

                NoteDataList rawList = NotesManager.Instance(monitorIndex).getReader().GetNoteList();
                List<NoteData> notes = new List<NoteData>();
                foreach (NoteData n in rawList)
                {
                    notes.Add(n);
                }

                long A = score.ScoreTotal._allPerfectScore;
                long B = score.ScoreTotal._breakBonusScore;

                List<int> tapTouchIdx = new List<int>();
                List<int> holdIdx = new List<int>();
                List<int> slideIdx = new List<int>();
                List<int> breakIdx = new List<int>();
                for (int i = 0; i < notes.Count; i++)
                {
                    NoteScore.EScoreType st = GamePlayManager.NoteType2ScoreType(notes[i].type.getEnum());
                    int idx = notes[i].indexNote;
                    switch (st)
                    {
                        case NoteScore.EScoreType.Hold: holdIdx.Add(idx); break;
                        case NoteScore.EScoreType.Slide: slideIdx.Add(idx); break;
                        case NoteScore.EScoreType.Break: breakIdx.Add(idx); break;
                        default: tapTouchIdx.Add(idx); break;
                    }
                }

                long R = (long)Math.Round((double)Target * 10000.0, MidpointRounding.AwayFromZero);
                long maxStored = (B > 0) ? 1010000L : 1000000L;
                if (R > maxStored) R = maxStored;
                if (R < 0) R = 0;

                long scoreUnits;
                int breakPerfect;
                if (!Solve(R, A, B, breakIdx.Count, tapTouchIdx.Count, holdIdx.Count, slideIdx.Count,
                           out scoreUnits, out breakPerfect))
                {
                    MelonLogger.Warning($"[ScoreTransfer] fullPlay 无法精确凑出 {Target}%，本曲退化为全 Perfect");
                    return;
                }

                for (int k = 0; k < breakPerfect && k < breakIdx.Count; k++)
                {
                    _plan[breakIdx[k]] = NoteJudge.ETiming.FastPerfect;
                }

                long q = scoreUnits / 2;
                int s3 = (int)Math.Min(slideIdx.Count, q / 3);
                long rem = q - 3L * s3;
                int s2 = (int)Math.Min(holdIdx.Count, rem / 2);
                long s1 = rem - 2L * s2;
                for (int i = 0; i < s3; i++) _plan[slideIdx[i]] = NoteJudge.ETiming.FastGreat;
                for (int i = 0; i < s2; i++) _plan[holdIdx[i]] = NoteJudge.ETiming.FastGreat;
                long placed = 0;
                for (int i = 0; i < tapTouchIdx.Count && placed < s1; i++, placed++) _plan[tapTouchIdx[i]] = NoteJudge.ETiming.FastGreat;

                int minIdx = int.MaxValue, maxIdx = int.MinValue;
                foreach (int key in _plan.Keys)
                {
                    if (key < minIdx) minIdx = key;
                    if (key > maxIdx) maxIdx = key;
                }
                MelonLogger.Msg($"[ScoreTransfer] plan 构建: notes={notes.Count} overrides={_plan.Count} idx=[{minIdx},{maxIdx}] A={A} B={B} R={R}");
            }
            catch (Exception e)
            {
                MelonLogger.Error(e);
            }
        }

        // 游戏达成率（内部值，显示=该值/10）：
        //   Achivement = score/A*1000 + bonus/B*10
        // 其中 A=_allPerfectScore(每键 Critical 分之和)，B=_breakBonusScore(断键数*100)，
        //   score = A - Δs，bonus = B - Δb。
        // 上传/结算读取的整数 stored = (int)(Achivement*1000) = (int)(1e6*score/A + 1e4*bonus/B)
        //   = (int)(1010000 - 1e6*Δs/A - 1e4*Δb/B)   (有断键时)
        // 非断键：Critical 与 Perfect 得分完全相同，只有 Great 才会扣分：
        //   Tap/Touch Great 扣100，Hold Great 扣200，Slide Great 扣300（均为50的倍数）
        // 断键：Critical bonus=100，Perfect bonus=75（Δb=25/个），据此微调小数。
        // 因此用「断键 Perfect 微调 + 非断键 Great 粗调」可精确凑出目标。
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameScoreList), "SetForceAchivement")]
        public static bool SetForceAchivement(int achivement, int dxscore, GameScoreList __instance)
        {
            try
            {
                int monitorIndex = (int)typeof(GameScoreList)
                    .GetField("_monitorIndex", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(__instance);
                NoteDataList rawList = NotesManager.Instance(monitorIndex).getReader().GetNoteList();
                List<NoteData> notes = new List<NoteData>();
                foreach (NoteData n in rawList)
                {
                    notes.Add(n);
                }

                long A = __instance.ScoreTotal._allPerfectScore;
                long B = __instance.ScoreTotal._breakBonusScore;

                List<int> tapIdx = new List<int>();
                List<int> touchIdx = new List<int>();
                List<int> holdIdx = new List<int>();
                List<int> slideIdx = new List<int>();
                List<int> breakIdx = new List<int>();
                for (int i = 0; i < notes.Count; i++)
                {
                    NoteScore.EScoreType st = GamePlayManager.NoteType2ScoreType(notes[i].type.getEnum());
                    switch (st)
                    {
                        case NoteScore.EScoreType.Hold: holdIdx.Add(i); break;
                        case NoteScore.EScoreType.Slide: slideIdx.Add(i); break;
                        case NoteScore.EScoreType.Break: breakIdx.Add(i); break;
                        case NoteScore.EScoreType.Touch: touchIdx.Add(i); break;
                        default: tapIdx.Add(i); break;
                    }
                }

                long R = (long)Math.Round((double)Target * 10000.0, MidpointRounding.AwayFromZero);
                long maxStored = (B > 0) ? 1010000L : 1000000L;
                if (R > maxStored) R = maxStored;
                if (R < 0) R = 0;

                NoteJudge.ETiming[] timing = new NoteJudge.ETiming[notes.Count];
                for (int i = 0; i < timing.Length; i++)
                {
                    timing[i] = NoteJudge.ETiming.Critical;
                }

                long scoreUnits;   // Δs / 50
                int breakPerfect;  // 需要设为 Perfect(而非 Critical) 的断键数量，Δb = 25 * 该值
                if (Solve(R, A, B, breakIdx.Count, tapIdx.Count + touchIdx.Count, holdIdx.Count, slideIdx.Count,
                          out scoreUnits, out breakPerfect))
                {
                    for (int k = 0; k < breakPerfect && k < breakIdx.Count; k++)
                    {
                        timing[breakIdx[k]] = NoteJudge.ETiming.FastPerfect;
                    }
                    AssignScorePenalty(timing, slideIdx, holdIdx, tapIdx, touchIdx, scoreUnits);
                }
                else
                {
                    MelonLogger.Warning($"[ScoreTransfer] 无法精确凑出达成率 {Target}，本曲退化为全 Perfect");
                }

                for (int i = 0; i < notes.Count; i++)
                {
                    NoteScore.EScoreType st = GamePlayManager.NoteType2ScoreType(notes[i].type.getEnum());
                    __instance.SetResult(notes[i].indexNote, st, timing[i]);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error(e);
            }
            return false;
        }

        /// <summary>
        /// 求 (Δs/50, 断键Perfect数) 使结果 stored 精确等于 R。
        /// 只用偶数 scoreUnits（即 Δs 为 100 的倍数），可由 Great 组合精确实现。
        /// </summary>
        private static bool Solve(long R, long A, long B, int nBreak, int nTapTouch, int nHold, int nSlide,
                                  out long scoreUnits, out int breakPerfect)
        {
            scoreUnits = 0;
            breakPerfect = 0;
            if (A <= 0)
            {
                return false;
            }

            if (B <= 0)
            {
                // 无断键：stored = 1e6 - 1e6*Δs/A
                double step = 1e6 * 50.0 / A;
                double need = 1000000.0 - R;
                long p0 = (long)Math.Round(need / step);
                double nbBestWin = double.MaxValue, nbBestAny = double.MaxValue;
                long nbPWin = 0, nbPAny = 0;
                bool nbOkWin = false;
                for (long p = Math.Max(0, p0 - 300); p <= p0 + 300; p++)
                {
                    if ((p & 1L) != 0) continue;
                    if (!CanRealizeGreats(p, nTapTouch, nHold, nSlide)) continue;
                    double stored = 1000000.0 - step * p;
                    double err = Math.Abs(stored - (R + 0.25));
                    if (err < nbBestAny)
                    {
                        nbBestAny = err;
                        nbPAny = p;
                    }
                    if (stored >= R && stored < R + 1 && err < nbBestWin)
                    {
                        nbBestWin = err;
                        nbPWin = p;
                        nbOkWin = true;
                    }
                }
                scoreUnits = nbOkWin ? nbPWin : nbPAny;
                return scoreUnits > 0;
            }

            double sStep = 1e6 * 50.0 / A;   // 每 scoreUnit(50分)
            double bStep = 1e4 * 25.0 / B;   // 每个断键 Perfect(Δb=25)
            double D = 1010000.0 - R;
            double bestWin = double.MaxValue, bestAny = double.MaxValue;
            long pWin = 0, pAny = 0;
            int jWin = 0, jAny = 0;
            bool okWin = false;
            for (int j = 0; j <= nBreak; j++)
            {
                double rem = D - bStep * j;
                if (rem < -0.5)
                {
                    break;
                }
                long p0 = (long)Math.Round(rem / sStep);
                for (long p = Math.Max(0, p0 - 64); p <= p0 + 64; p++)
                {
                    if ((p & 1L) != 0) continue;
                    if (!CanRealizeGreats(p, nTapTouch, nHold, nSlide)) continue;
                    double stored = 1010000.0 - (sStep * p + bStep * j);
                    double err = Math.Abs(stored - (R + 0.25));
                    if (err < bestAny)
                    {
                        bestAny = err;
                        pAny = p;
                        jAny = j;
                    }
                    if (stored >= R && stored < R + 1 && err < bestWin)
                    {
                        bestWin = err;
                        pWin = p;
                        jWin = j;
                        okWin = true;
                    }
                }
            }
            if (okWin)
            {
                scoreUnits = pWin;
                breakPerfect = jWin;
                return true;
            }
            scoreUnits = pAny;
            breakPerfect = jAny;
            return bestAny < double.MaxValue;
        }

        /// <summary>
        /// 判断 Δs = 50*p 能否由各类型 Great 精确组成。
        /// Great 扣分单位为(50分): Tap/Touch=2, Hold=4, Slide=6。p 为偶数时转为 100 分单位 q=p/2，
        /// 币值 1/2/3；贪心先用大币再补 1 币，若 1 币不够则无解。
        /// </summary>
        private static bool CanRealizeGreats(long p, int c1, int c2, int c3)
        {
            if ((p & 1L) != 0)
            {
                return false;
            }
            long q = p / 2;
            if (q == 0)
            {
                return true;
            }
            int s3 = (int)Math.Min(c3, q / 3);
            long rem = q - 3L * s3;
            int s2 = (int)Math.Min(c2, rem / 2);
            long s1 = rem - 2L * s2;
            return s1 <= c1;
        }

        private static void AssignScorePenalty(NoteJudge.ETiming[] timing, List<int> slideIdx, List<int> holdIdx,
                                               List<int> tapIdx, List<int> touchIdx, long p)
        {
            if (p <= 0)
            {
                return;
            }
            long q = p / 2;
            int s3 = (int)Math.Min(slideIdx.Count, q / 3);
            long rem = q - 3L * s3;
            int s2 = (int)Math.Min(holdIdx.Count, rem / 2);
            long s1 = rem - 2L * s2;

            for (int i = 0; i < s3; i++) timing[slideIdx[i]] = NoteJudge.ETiming.FastGreat;
            for (int i = 0; i < s2; i++) timing[holdIdx[i]] = NoteJudge.ETiming.FastGreat;
            int placed = 0;
            for (int i = 0; i < tapIdx.Count && placed < s1; i++, placed++) timing[tapIdx[i]] = NoteJudge.ETiming.FastGreat;
            for (int i = 0; i < touchIdx.Count && placed < s1; i++, placed++) timing[touchIdx[i]] = NoteJudge.ETiming.FastGreat;
        }
    }
}
