using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AMDaemon.Allnet;
using DB;
using HarmonyLib;
using MAI2System;
using Main;
using Manager;
using Manager.Operation;
using MelonLoader;
using Net;
using Net.Packet;
using Net.VO;
using Net.VO.Mai2;
using Process;
using Process.Entry.State;
using SinmaiAssist.Utils;

namespace SinmaiAssist.Fix
{
    // 这些是“能启动游戏/联网”所必需的底层修复，从原 Sinmai-Assist / AquaMai 移植。
    // 它们与分数转移本身无关，但 1.56 上缺少它们会导致环境检测/加密/版本检查阻断流程。

    public class DisableEnvironmentCheck
    {
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(WarningProcess), "OnStart")]
        public static IEnumerable<CodeInstruction> OnStart(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var onceDispIndex = codes.FindIndex(inst =>
                inst.opcode == OpCodes.Ldsfld &&
                inst.operand is FieldInfo field &&
                field.Name == "OnceDisp");
            if (onceDispIndex == -1)
            {
                MelonLogger.Warning("[Fix] DisableEnvironmentCheck: OnceDisp not found");
                return codes;
            }
            return codes.Skip(onceDispIndex);
        }
    }

    public class DisableReboot
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "IsAutoRebootNeeded")]
        public static bool IsAutoRebootNeeded(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "IsUnderServerMaintenance")]
        public static bool IsUnderServerMaintenance(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "get_RemainingMinutes")]
        public static bool GetRemainingMinutes(ref int __result)
        {
            __result = 600;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "GetAutoRebootSec")]
        public static bool GetAutoRebootSec(ref int __result)
        {
            __result = 60 * 60 * 10;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "GetServerMaintenanceSec")]
        public static bool GetServerMaintenanceSec(ref int __result)
        {
            __result = 60 * 60 * 10;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "Execute")]
        public static bool Execute(MaintenanceTimer __instance) => false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MaintenanceTimer), "UpdateTimes")]
        public static bool UpdateTimes(MaintenanceTimer __instance) => false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ClosingTimer), "IsShowRemainingMinutes")]
        public static bool IsShowRemainingMinutes(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ClosingTimer), "IsClosed")]
        public static bool IsClosed(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    public class FixCheckAuth
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OperationManager), "CheckAuth_Proc")]
        private static void PostCheckAuthProc(ref OperationData ____operationData)
        {
            if (Auth.GameServerUri.StartsWith("http://") || Auth.GameServerUri.StartsWith("https://"))
            {
                ____operationData.ServerUri = Auth.GameServerUri;
            }
        }
    }

    public class SkipCakeHashCheck
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NetHttpClient), MethodType.Constructor)]
        private static void OnNetHttpClientConstructor(NetHttpClient __instance)
        {
            var tInstance = Traverse.Create(__instance).Field("isTrueDll");
            if (tInstance.FieldExists())
            {
                tInstance.SetValue(true);
            }
        }
    }

    public class SkipSpecialNumCheck
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), "CalcSpecialNum")]
        private static void CalcSpecialNum(ref int __result)
        {
            __result = 1024;
        }
    }

    public class SkipVersionCheck
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ConfirmPlay), "IsValidVersion")]
        public static bool IsValidVersion(ref bool __result, ref UserPreviewResponseVO vo)
        {
            __result = true;
            return false;
        }
    }

    public class DisableEncryption
    {
        private static string _apiSuffix = "";

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMain), "LateInitialize")]
        public static void GetApiSuffix()
        {
            try
            {
                var ctor = typeof(NetQuery<VOSerializer, VOSerializer>).GetConstructors().First();
                _apiSuffix = ((INetQuery)ctor.Invoke(
                    ctor.GetParameters().Select((p, i) => i == 0 ? "" : p.DefaultValue).ToArray())).Api;
                MelonLogger.Msg($"[Fix] API suffix: {_apiSuffix}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Fix] Failed to resolve API suffix: {e}");
                _apiSuffix = null;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Packet), "Obfuscator", typeof(string))]
        public static bool PreObfuscator(string srcStr, ref string __result)
        {
            if (string.IsNullOrEmpty(_apiSuffix))
            {
                return false;
            }
            if (srcStr.EndsWith(_apiSuffix))
            {
                __result = srcStr.Substring(0, srcStr.Length - _apiSuffix.Length);
            }
            return false;
        }

        [HarmonyPatch]
        public class EncryptDecrypt
        {
            public static IEnumerable<MethodBase> TargetMethods()
            {
                var methods = AccessTools.TypeByName("Net.CipherAES").GetMethods();
                return new[]
                {
                    methods.FirstOrDefault(it => it.Name == "Encrypt" && it.IsPublic),
                    methods.FirstOrDefault(it => it.Name == "Decrypt" && it.IsPublic)
                };
            }

            public static bool Prefix(object[] __args, ref object __result)
            {
                if (__args.Length == 1)
                {
                    __result = __args[0];
                }
                else if (__args.Length == 2)
                {
                    __args[1] = __args[0];
                    __result = true;
                }
                return false;
            }
        }
    }
}
