using System;
using System.Reflection;
using HarmonyLib;
using Manager;
using MelonLoader;
using Net.Packet;
using Net.Packet.Mai2;
using Net.VO.Mai2;

namespace SinmaiAssist.Utils
{
    /// <summary>
    /// 网络层观测信号（只读观测，绝不伪造返回值）。
    /// UpsertUserAll / UserLogout 的成功与否由游戏自身发送后的 Packet.Proc 结果决定。
    /// </summary>
    public static class BatchSignals
    {
        // 0 = 未观测到, 1 = 成功, -1 = 失败
        public static volatile int Upsert;
        public static volatile int Logout;
        public static string LastUpsertMessage = "";
        public static string LastLogoutMessage = "";

        public static void Reset()
        {
            Upsert = 0;
            Logout = 0;
            LastUpsertMessage = "";
            LastLogoutMessage = "";
        }
    }

    /// <summary>
    /// 观测成绩提交与登出结果（PacketState.Done=2, PacketStatus.Ok=0）。
    /// </summary>
    internal class NetworkObserver
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PacketUpsertUserAll), "Proc")]
        public static void UpsertProc(PacketUpsertUserAll __instance, PacketState __result)
        {
            int state = (int)__result;
            int status = (int)__instance.Status;
            bool ok = state == 2 && status == 0;
            BatchSignals.Upsert = ok ? 1 : -1;
            BatchSignals.LastUpsertMessage = $"state={state} status={status} http={__instance.HttpStatus}";
            MelonLogger.Msg($"[ScoreTransfer] UpsertUserAll {(ok ? "OK" : "FAIL")} ({BatchSignals.LastUpsertMessage})");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PacketUserLogout), "Proc")]
        public static void LogoutProc(PacketUserLogout __instance, PacketState __result)
        {
            int state = (int)__result;
            int status = (int)__instance.Status;
            bool ok = state == 2 && status == 0;
            BatchSignals.Logout = ok ? 1 : -1;
            BatchSignals.LastLogoutMessage = $"state={state} status={status} http={__instance.HttpStatus}";
            MelonLogger.Msg($"[ScoreTransfer] UserLogout {(ok ? "OK" : "FAIL")} ({BatchSignals.LastLogoutMessage})");
        }

        /// <summary>观测实际上传的 playlog 内容（trackNo 是否 1..N 连续）。</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(VOExtensions), "ExportUserPlaylog", new[] { typeof(UserData), typeof(int), typeof(int) })]
        public static void ExportPlaylog(ref UserPlaylog __result)
        {
            try
            {
                MelonLogger.Msg("[ScoreTransfer] ExportUserPlaylog " +
                                $"trackNo={Get(__result, "trackNo")} musicId={Get(__result, "musicId")} level={Get(__result, "level")} " +
                                $"achievement={Get(__result, "achievement")} playDate={Get(__result, "playDate")} playlogId={Get(__result, "playlogId")}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] ExportUserPlaylog log failed: {e.Message}");
            }
        }

        private static object Get(object o, string name)
        {
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                return p.GetValue(o, null);
            }
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(o);
        }
    }
}
