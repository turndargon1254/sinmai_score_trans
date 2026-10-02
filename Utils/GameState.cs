using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MAI2.Util;
using Main;
using Manager;
using MelonLoader;
using Process;
using Type = System.Type;

namespace SinmaiAssist.Utils
{
    /// <summary>
    /// 游戏状态探测与流程推进。
    /// 通过 ProcessDataContainer.processManager._processList 判断当前处于哪个游戏流程，
    /// 并在登录后的各类选择界面自动推进到选曲界面（逻辑参考 AquaMai 的 OneKeyEntryEnd）。
    /// </summary>
    public static class GameState
    {
        public static ProcessDataContainer Container { get; private set; }
        public static GameMainObject MainObject { get; private set; }

        private static FieldInfo _processListField;
        private static readonly Dictionary<Type, MemberInfo> _processMemberCache = new Dictionary<Type, MemberInfo>();
        private static int _advanceLockFrames;

        /// <summary>当前游戏状态快照，便于诊断。</summary>
        public static string Snapshot()
        {
            int track = 0;
            int scoreCount = 0;
            try { track = (int)GameManager.MusicTrackNumber; } catch { }
            try { scoreCount = Singleton<GamePlayManager>.Instance.GetScoreListCount(); } catch { }
            return $"MusicSelect={IsMusicSelect} TrackNo={track} ScoreListCount={scoreCount} Processes=[{string.Join(",", GetProcessNames())}]";
        }

        // 登录完成后需要自动跳过的选择类流程
        private static readonly HashSet<string> PostLoginProcesses = new HashSet<string>
        {
            "Process.ModeSelect.ModeSelectProcess",
            "Process.LoginBonus.LoginBonusProcess",
            "Process.RegionalSelectProcess",
            "Process.CharacterSelectProcess",
            "Process.CharacterSelectProces", // Assembly-CSharp 中的拼写错误
            "Process.TicketSelect.TicketSelectProcess",
            "Process.Information.InformationProcess",
            "Process.InformationProcess"
        };

        // 表示本局结束/结算相关的流程
        private static readonly HashSet<string> SessionEndProcesses = new HashSet<string>
        {
            "Process.ContinueProcess",
            "Process.GameOverProcess",
            "Process.PhotoEditProcess",
            "Process.DataSaveProcess",
            "Process.Entry.EntryProcess",
            "Process.ModeSelect.ModeSelectProcess",
            "Process.LoginBonus.LoginBonusProcess",
            "Process.RegionalSelectProcess",
            "Process.CharacterSelectProcess",
            "Process.CharacterSelectProces",
            "Process.TicketSelect.TicketSelectProcess",
            "Process.Information.InformationProcess",
            "Process.InformationProcess"
        };

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProcessDataContainer), MethodType.Constructor)]
        public static void OnCreateProcessDataContainer(ProcessDataContainer __instance)
        {
            Container = __instance;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameMainObject), "Awake")]
        public static void OnCreateGameMainObject(GameMainObject __instance)
        {
            MainObject = __instance;
        }

        public static bool IsMusicSelect
        {
            get { return HasProcess("Process.MusicSelectProcess"); }
        }

        public static bool IsSessionEnding
        {
            get
            {
                foreach (string name in GetProcessNames())
                {
                    if (SessionEndProcesses.Contains(name))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public static bool HasProcess(string name)
        {
            foreach (string current in GetProcessNames())
            {
                if (current == name)
                {
                    return true;
                }
            }
            return false;
        }

        public static List<string> GetProcessNames()
        {
            List<string> result = new List<string>();
            IEnumerable list = GetProcessList();
            if (list == null)
            {
                return result;
            }
            foreach (object controle in list)
            {
                ProcessBase process = GetProcess(controle);
                if (process != null)
                {
                    result.Add(process.ToString());
                }
            }
            return result;
        }

        /// <summary>
        /// 如果当前停留在登录后的选择界面，则自动推进到选曲界面。返回是否执行了推进。
        /// </summary>
        public static bool TryAutoAdvance()
        {
            if (_advanceLockFrames > 0)
            {
                _advanceLockFrames--;
                return false;
            }

            ProcessManager manager = Container?.processManager;
            if (manager == null)
            {
                return false;
            }

            IEnumerable list = GetProcessList();
            if (list == null)
            {
                return false;
            }

            ProcessBase processToRelease = null;
            foreach (object controle in list)
            {
                ProcessBase process = GetProcess(controle);
                if (process == null)
                {
                    continue;
                }
                string name = process.ToString();
                if (PostLoginProcesses.Contains(name))
                {
                    processToRelease = process;
                    break;
                }
            }

            if (processToRelease == null)
            {
                return false;
            }

            try
            {
                SetNormalMode(true);
                SetMaxTrack();
                try
                {
                    manager.SendMessage(new Message(ProcessType.CommonProcess, CommonProcess.MessageID_CreditSub));
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[ScoreTransfer] CreditSub message failed: {e.Message}");
                }
                manager.AddProcess(new FadeProcess(Container, processToRelease, new MusicSelectProcess(Container)));
                _advanceLockFrames = 120;
                MelonLogger.Msg($"[ScoreTransfer] 自动推进到选曲界面 (from {processToRelease})");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 自动推进失败: {e}");
                return false;
            }
        }

        public static void SetNormalMode(bool value)
        {
            try
            {
                PropertyInfo prop = typeof(Manager.GameManager).GetProperty(
                    "IsNormalMode", BindingFlags.Public | BindingFlags.Static);
                prop?.SetValue(null, value);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 设置 NormalMode 失败: {e.Message}");
            }
        }

        public static void SetMaxTrack()
        {
            try
            {
                MethodInfo method = typeof(Manager.GameManager).GetMethod(
                    "SetMaxTrack", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    return;
                }
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                {
                    method.Invoke(null, null);
                }
                else if (parameters.Length == 3)
                {
                    method.Invoke(null, new object[] { false, false, false });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] SetMaxTrack 失败: {e.Message}");
            }
        }

        private static IEnumerable GetProcessList()
        {
            ProcessManager manager = Container?.processManager;
            if (manager == null)
            {
                return null;
            }
            if (_processListField == null)
            {
                _processListField = typeof(ProcessManager).GetField(
                    "_processList", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            return _processListField?.GetValue(manager) as IEnumerable;
        }

        private static ProcessBase GetProcess(object controle)
        {
            if (controle == null)
            {
                return null;
            }
            Type type = controle.GetType();
            MemberInfo member;
            if (!_processMemberCache.TryGetValue(type, out member))
            {
                member = (MemberInfo)type.GetProperty("Process", BindingFlags.Public | BindingFlags.Instance)
                         ?? type.GetField("Process", BindingFlags.Public | BindingFlags.Instance);
                _processMemberCache[type] = member;
            }

            try
            {
                object value;
                if (member is PropertyInfo prop)
                {
                    value = prop.GetValue(controle, null);
                }
                else if (member is FieldInfo field)
                {
                    value = field.GetValue(controle);
                }
                else
                {
                    return null;
                }
                return value as ProcessBase;
            }
            catch
            {
                return null;
            }
        }
    }
}
