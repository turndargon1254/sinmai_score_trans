using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AMDaemon;
using ChimeLib.NET;
using HarmonyLib;
using Mai2.Mai2Cue;
using Main;
using Manager;
using MelonLoader;
using Process;
using SinmaiAssist.Utils;
using UnityEngine;

namespace SinmaiAssist.Cheat
{
    /// <summary>自动登录（刷卡）所需的共享状态、刷卡动作与网页侧登录入口。</summary>
    public static class DummyLoginState
    {
        public static string DummyLoginCode = "";
        public static string DummyUserId = "1";
        public static bool CodeLoginFlag = false;
        public static bool UserIdLoginFlag = false;

        /// <summary>由 Main 设置：当前游戏是否为 SDGB（Chime 版）。</summary>
        public static bool IsChime = false;

        public static string LastMessage = "";

        private static volatile bool _pending;
        private static volatile int _pendingMode; // 0 = code, 1 = userId
        private static volatile string _pendingValue;

        /// <summary>由 HTTP 线程调用：用二维码内容（Aime/Chime Code）登录。</summary>
        public static void RequestCodeLogin(string code)
        {
            _pendingValue = code;
            _pendingMode = 0;
            _pending = true;
        }

        /// <summary>由 HTTP 线程调用：用 UserID 登录。</summary>
        public static void RequestUserIdLogin(string userId)
        {
            _pendingValue = userId;
            _pendingMode = 1;
            _pending = true;
        }

        /// <summary>在主线程每帧处理待处理的登录请求。</summary>
        public static void ProcessPending()
        {
            if (!_pending)
            {
                return;
            }
            _pending = false;
            string value = _pendingValue;
            if (_pendingMode == 0)
            {
                LoginWithCode(value);
            }
            else
            {
                LoginWithUserId(value);
            }
        }

        public static void LoginWithCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                LastMessage = "二维码内容为空";
                return;
            }
            DummyLoginCode = code.Trim();
            CodeLoginFlag = true;
            if (!IsChime)
            {
                ReadCard(DummyLoginCode);
            }
            LastMessage = $"已提交二维码登录 ({DummyLoginCode.Length} 字符)";
            MelonLogger.Msg($"[ScoreTransfer] {LastMessage}");
        }

        public static void LoginWithUserId(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                LastMessage = "UserID 为空";
                return;
            }
            DummyUserId = userId.Trim();
            UserIdLoginFlag = true;
            if (!IsChime)
            {
                ReadCard("12312312312312312312", DummyLoginCode);
            }
            LastMessage = $"已提交 UserID 登录 ({DummyUserId})";
            MelonLogger.Msg($"[ScoreTransfer] {LastMessage}");
        }

        public static void ReadCard(string accesscode = null, string oldCode = null)
        {
            accesscode ??= DummyLoginCode.Trim();
            if (!Directory.Exists("DEVICE"))
            {
                Directory.CreateDirectory("DEVICE");
            }
            if (accesscode.Length == 20 && Regex.IsMatch(accesscode, @"^\d+$"))
            {
                File.WriteAllText("DEVICE/aime.txt", accesscode);
                Keyboard.LongPressKey(Keys.Enter, 300);
                if (oldCode is { Length: 20 } && Regex.IsMatch(oldCode, @"^\d+$"))
                {
                    File.WriteAllText("DEVICE/aime.txt", oldCode);
                }
            }
            else
            {
                GameMessageManager.SendMessage(0, "<color=\"red\">Failed to read Aime!");
                GameMessageManager.SendMessage(1, "<color=\"red\">Failed to read Aime!");
                SoundManager.PlaySE(Cue.SE_ENTRY_AIME_ERROR, 1);
            }
        }
    }

    /// <summary>在主线程驱动待处理的登录请求。</summary>
    public class DummyLoginTicker
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameMainObject), "Update")]
        public static void OnTick()
        {
            DummyLoginState.ProcessPending();
        }
    }

    /// <summary>Aime 版自动登录（非 SDGB）。</summary>
    public class DummyAimeLogin
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AimeId), "Value", MethodType.Getter)]
        public static bool GetAimeId(ref uint __result)
        {
            if (DummyLoginState.UserIdLoginFlag)
            {
                __result = Convert.ToUInt32(DummyLoginState.DummyUserId);
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Process.Entry.TryAime), "Execute")]
        public static void ClearFlag()
        {
            DummyLoginState.UserIdLoginFlag = false;
        }
    }

    /// <summary>Chime 版自动登录（SDGB / 舞萌）。</summary>
    public class DummyChimeLogin
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "get_IsAvailableCamera")]
        public static bool IsAvailableCamera(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "get_IsAvailableChimeCamera")]
        public static bool IsAvailableChimeCamera(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "get_IsAvailableCameras")]
        public static bool IsAvailableCameras(ref bool[] __result)
        {
            __result = new bool[2] { true, true };
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "GetTexture")]
        public static bool GetTexture(ref WebCamTexture __result)
        {
            __result = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "IsPlayingPhotoCamera")]
        public static bool IsPlayingPhotoCamera(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "PlayPhotoCamera")]
        public static bool PlayPhotoCamera() { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "PlayPhotoOnly")]
        public static bool PlayPhotoOnly() { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "PausePhoto")]
        public static bool PausePhoto() { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "StopPhoto")]
        public static bool StopPhoto() { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "GetColor32")]
        public static bool GetColor32(ref Color32[] __result)
        {
            __result = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "CameraInitialize")]
        public static bool CameraInitialize(CameraManager __instance, ref IEnumerator __result)
        {
            __result = CameraInitialize(__instance);
            return false;
        }

        public static IEnumerator CameraInitialize(CameraManager __instance)
        {
            CameraManager.IsReady = true;
            yield break;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeDevice), MethodType.Constructor, new[] { typeof(WebCamTexture) })]
        public static bool ChimeDeviceCtor(WebCamTexture texture) { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeDevice), "HasError")]
        public static bool HasError(ref bool __result)
        {
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeDevice), "IsReady")]
        public static bool IsReady(ref bool __result)
        {
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeDevice), "BeginScan")]
        public static bool BeginScan() { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeDevice), "EndScan")]
        public static bool EndScan() { return false; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeDevice), "GetDecodeStrings")]
        public static bool GetDecodeStrings(ref string[] __result)
        {
            if (DummyLoginState.CodeLoginFlag)
            {
                DummyLoginState.CodeLoginFlag = false;
                if (DummyLoginState.DummyLoginCode == null)
                {
                    __result = null;
                    return false;
                }
                __result = new string[1] { DummyLoginState.DummyLoginCode };
                return false;
            }
            __result = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeReaderManager), "Execute")]
        public static bool Execute(ChimeReaderManager __instance)
        {
            var result = AccessTools.Field(typeof(ChimeReaderManager), "_result");
            var aimeId = AccessTools.Field(typeof(ChimeReaderManager), "_aimeId");
            var currentState = AccessTools.Field(typeof(ChimeReaderManager), "currentState");
            if (DummyLoginState.UserIdLoginFlag)
            {
                Type chimeIdType = Type.GetType("ChimeLib.NET.ChimeId, ChimeLib.NET");
                MethodInfo makeMethod = chimeIdType.GetMethod("Make", BindingFlags.NonPublic | BindingFlags.Static);
                ChimeId id = (ChimeId)makeMethod.Invoke(null, new object[] { uint.Parse(DummyLoginState.DummyUserId) });
                result.SetValue(__instance, ChimeReaderManager.Result.Done);
                aimeId.SetValue(__instance, id);
                currentState.SetValue(__instance, 9);
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChimeReaderManager), "AdvCheck")]
        public static bool AdvCheck(ref bool __result)
        {
            if (DummyLoginState.UserIdLoginFlag)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Process.Entry.TryAime), "Execute")]
        public static void ClearFlag()
        {
            DummyLoginState.UserIdLoginFlag = false;
        }
    }
}
