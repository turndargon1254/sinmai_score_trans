using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Process;
using Type = System.Type;

namespace SinmaiAssist.Cheat
{
    /// <summary>
    /// 结算界面自动确认。
    /// 游戏自身的 ResultProcess 在 Update 阶段会通过 LogicUpdate -> ToNextCheck -> ToNextProcess
    /// 进入下一曲（NextTrackProcess）或选曲（MusicSelectProcess）。这里仅在“传分流程进行中”
    /// 调用游戏原生的 ToNextCheck()，等价于玩家按「次へ」，让流程走正常生命周期；
    /// 不直接创建/销毁 Process，也不伪造状态。
    /// </summary>
    internal class ResultAdvancer
    {
        public static volatile bool Enabled;
        private static bool _done;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ResultProcess), "OnStart")]
        public static void OnStart()
        {
            _done = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ResultProcess), "OnUpdate")]
        public static void OnUpdate(ResultProcess __instance)
        {
            if (!Enabled || _done)
            {
                return;
            }

            try
            {
                FieldInfo sequenceField = typeof(ResultProcess).GetField("_sequence", BindingFlags.NonPublic | BindingFlags.Instance);
                if (sequenceField == null)
                {
                    return;
                }
                int sequence = Convert.ToInt32(sequenceField.GetValue(__instance));

                // 0=Init 1=Start 2=Staging 3=Convention 4=Update 5=GhostResult 6=Release
                // 只在 Update 阶段触发，避免打断前置动画。
                if (sequence != 4)
                {
                    return;
                }

                _done = true;
                MethodInfo toNextCheck = typeof(ResultProcess).GetMethod("ToNextCheck", BindingFlags.NonPublic | BindingFlags.Instance);
                if (toNextCheck == null)
                {
                    MelonLogger.Warning("[ScoreTransfer] 找不到 ResultProcess.ToNextCheck");
                    return;
                }
                toNextCheck.Invoke(__instance, null);
                MelonLogger.Msg("[ScoreTransfer] 已自动确认结算界面 (ToNextCheck)");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[ScoreTransfer] 自动确认结算失败: {e}");
            }
        }
    }
}
