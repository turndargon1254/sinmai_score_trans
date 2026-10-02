using HarmonyLib;
using MelonLoader;
using Net.Packet;
using Net.Packet.Mai2;

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
    }
}
