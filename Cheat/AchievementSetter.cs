using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Process;
using System;
using System.Reflection;
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

                if (sequence < GameSequence.Play || sequence >= GameSequence.Release || GameManager.IsNoteCheckMode || !Pending)
                {
                    return;
                }

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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameScoreList), "SetForceAchivement")]
        public static bool SetForceAchivement(int achivement, int dxscore, GameScoreList __instance)
        {
            decimal num1 = Target;
            long num2;
            long num3;
            if (num1 > 100.0m)
            {
                num2 = (long)((decimal)__instance.ScoreTotal._allPerfectScore * (num1 - 1.0m) * 0.01m);
                num3 = __instance.ScoreTotal._breakBonusScore;
            }
            else
            {
                num2 = (long)((decimal)__instance.ScoreTotal._allPerfectScore * (num1 * 0.99m * 0.01m));
                num3 = (long)((decimal)__instance.ScoreTotal._breakBonusScore * num1 * 0.01m);
            }

            NoteJudge.ETiming[] noteArray = new NoteJudge.ETiming[7]
            {
                NoteJudge.ETiming.Critical,
                NoteJudge.ETiming.FastGreat,
                NoteJudge.ETiming.FastGreat2nd,
                NoteJudge.ETiming.LateGreat,
                NoteJudge.ETiming.LateGreat2nd,
                NoteJudge.ETiming.LateGreat3rd,
                NoteJudge.ETiming.LateGood
            };

            int monitorIndex = (int)typeof(GameScoreList)
                .GetField("_monitorIndex", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(__instance);
            NoteDataList noteList = NotesManager.Instance(monitorIndex).getReader().GetNoteList();

            foreach (NoteData item in noteList)
            {
                if (!item.type.isBreakScore())
                {
                    continue;
                }
                bool flag = false;
                foreach (NoteJudge.ETiming timing in noteArray)
                {
                    NoteScore.EScoreType scoreType = GamePlayManager.NoteType2ScoreType(item.type.getEnum());
                    if (0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing, NoteScore.EScoreType.Break)) &&
                        0m <= (decimal)(num3 - NoteScore.GetJudgeScore(timing, NoteScore.EScoreType.BreakBonus)))
                    {
                        num2 -= NoteScore.GetJudgeScore(timing, scoreType);
                        num3 -= NoteScore.GetJudgeScore(timing, NoteScore.EScoreType.BreakBonus);
                        __instance.SetResult(item.indexNote, scoreType, timing);
                        flag = true;
                        break;
                    }
                }
                if (!flag)
                {
                    __instance.SetResult(item.indexNote, NoteScore.EScoreType.Break, NoteJudge.ETiming.TooFast);

                }
            }

            int num4 = 0;
            int num5 = 0;
            long num6 = 0L;
            for (int j = 0; j < noteArray.Length; j++)
            {
                long num7 = num2;
                long num8 = 0L;
                num8 += __instance.ScoreTotal.GetTapNum() * NoteScore.GetJudgeScore(noteArray[j]);
                num8 += __instance.ScoreTotal.GetHoldNum() * NoteScore.GetJudgeScore(noteArray[j], NoteScore.EScoreType.Hold);
                num8 += __instance.ScoreTotal.GetSlideNum() * NoteScore.GetJudgeScore(noteArray[j], NoteScore.EScoreType.Slide);
                num8 += __instance.ScoreTotal.GetTouchNum() * NoteScore.GetJudgeScore(noteArray[j], NoteScore.EScoreType.Touch);
                if (num8 <= num7)
                {
                    num6 = num7 - num8;
                    num5 = num4 != 0 ? num4 - 1 : 0;
                    break;
                }
                num4++;
            }
            if (num4 >= noteArray.Length)
            {
                num4 = noteArray.Length - 1;
            }

            foreach (NoteData item2 in noteList)
            {
                if (!item2.type.isSlideScore())
                {
                    continue;
                }
                NoteScore.EScoreType scoreType = GamePlayManager.NoteType2ScoreType(item2.type.getEnum());
                NoteJudge.ETiming timing = noteArray[num4];
                NoteJudge.ETiming timing2 = noteArray[num5];
                if (0m <= (decimal)num6 && 0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing2, scoreType)))
                {
                    num6 -= NoteScore.GetJudgeScore(timing2, scoreType) - NoteScore.GetJudgeScore(timing, scoreType);
                    num2 -= NoteScore.GetJudgeScore(timing2, scoreType);
                    __instance.SetResult(item2.indexNote, scoreType, timing2);
                }
                else if (0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing, scoreType)))
                {
                    num2 -= NoteScore.GetJudgeScore(timing, scoreType);
                    __instance.SetResult(item2.indexNote, scoreType, timing);
                }
                else
                {
                    __instance.SetResult(item2.indexNote, scoreType, NoteJudge.ETiming.TooFast);

                }
            }

            foreach (NoteData item3 in noteList)
            {
                if (!item3.type.isHoldScore())
                {
                    continue;
                }
                NoteScore.EScoreType scoreType = GamePlayManager.NoteType2ScoreType(item3.type.getEnum());
                NoteJudge.ETiming timing = noteArray[num4];
                NoteJudge.ETiming timing2 = noteArray[num5];
                if (0m <= (decimal)num6 && 0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing2, scoreType)))
                {
                    num6 -= NoteScore.GetJudgeScore(timing2, scoreType) - NoteScore.GetJudgeScore(timing, scoreType);
                    num2 -= NoteScore.GetJudgeScore(timing2, scoreType);
                    __instance.SetResult(item3.indexNote, scoreType, timing2);
                }
                else if (0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing, scoreType)))
                {
                    num2 -= NoteScore.GetJudgeScore(timing, scoreType);
                    __instance.SetResult(item3.indexNote, scoreType, timing);
                }
                else
                {
                    __instance.SetResult(item3.indexNote, scoreType, NoteJudge.ETiming.TooFast);

                }
            }

            foreach (NoteData item4 in noteList)
            {
                if (!item4.type.isTapScore())
                {
                    continue;
                }
                NoteScore.EScoreType scoreType = GamePlayManager.NoteType2ScoreType(item4.type.getEnum());
                NoteJudge.ETiming timing = noteArray[num4];
                NoteJudge.ETiming timing2 = noteArray[num5];
                if (0m < (decimal)num6 && 0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing2, scoreType)))
                {
                    num6 -= NoteScore.GetJudgeScore(timing2, scoreType) - NoteScore.GetJudgeScore(timing, scoreType);
                    num2 -= NoteScore.GetJudgeScore(timing2, scoreType);
                    __instance.SetResult(item4.indexNote, scoreType, timing2);
                }
                else if (0m <= (decimal)(num2 - NoteScore.GetJudgeScore(timing, scoreType)))
                {
                    num2 -= NoteScore.GetJudgeScore(timing, scoreType);
                    __instance.SetResult(item4.indexNote, scoreType, timing);
                }
                else if (0m < (decimal)num2)
                {
                    num2 -= NoteScore.GetJudgeScore(timing, scoreType);
                    __instance.SetResult(item4.indexNote, scoreType, timing);
                }
                else
                {
                    __instance.SetResult(item4.indexNote, scoreType, NoteJudge.ETiming.TooFast);

                }
            }

            return false;
        }
    }
}
