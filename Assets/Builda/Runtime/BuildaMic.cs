// BuildaMic —— 麦克风声控内置件（L2 模板，docs/decisions/0012 §4.2），与 Godot 版
// addons/builda/mic/builda_mic.gd 同构。状态机/桥接（本文件 + BuildaMic.jslib）归 SDK 所有，
// 升级随 sdk install 整体覆盖；分析 Worker（Assets/StreamingAssets/builda-mic/
// builda-mic-worker.js）归项目所有：升级不覆盖，CP 可魔改算法（恢复默认：删除该目录后
// 重跑 builda sdk install）。
//
// 用法（零 JS）：
//   1. 把本组件挂到常驻场景对象上（如 DontDestroyOnLoad 的管理节点）。
//   2. 任意点击回调里调 StartMic()——内建两次手势状态机（0012 §3.2：权限结果回调不是
//      手势上下文）：第 1 次点击申请权限；State 变 NeedsGesture 后引导玩家"再点一次"；
//      第 2 次点击真正开采集。中断恢复失败同样回 NeedsGesture，复用同一引导。
//   3. 每帧读 Volume / Pitch（或 Features）驱动玩法；订阅 StateChanged 更新引导 UI；
//      State == Unavailable 时给出明确的阻断提示（说明需要麦克风、引导重试或去系统设置
//      开启；该态下再次 StartMic() 会重新申请权限），不能黑屏/卡死；无麦替代玩法可选、
//      不是必须。
//   4. 打包：Worker 在 Assets/StreamingAssets/ 下，Unity WebGL 构建会自动原样拷进产物，
//      无需手工处理（builda dev 直接跑构建目录即可）。
//
// 数据流：PCM 全程不过 C# 桥——页面内 rAF 泵（BuildaMic.jslib）把 Builda.mic.read() 的
// 样本 transfer 给 Worker，Worker 回传的特征对象原样并入 Features，C# 每帧只同步拉几十
// 字节的特征 JSON。换算法/加特征只改 worker 文件：postMessage({...}) 的字段自动出现在
// Features 里，本文件不用动。
using System;
using System.Collections.Generic;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace Builda
{
    public class BuildaMic : MonoBehaviour
    {
        public enum MicState
        {
            Idle,
            Requesting,
            NeedsGesture,
            Starting,
            Running,
            Interrupted,
            Unavailable,
        }

        // 中断后 SDK 会自动重建；持续超过该秒数仍未恢复则回 NeedsGesture（引导玩家点击手动重启）
        public const float InterruptedGestureAfter = 5f;
        const string WorkerUrl = "StreamingAssets/builda-mic/builda-mic-worker.js";

        public MicState State { get; private set; } = MicState.Idle;
        public float Volume { get; private set; }
        public float Pitch { get; private set; }
        public Dictionary<string, object> Features { get; private set; } = new Dictionary<string, object>();
        public string LastError { get; private set; } = "";

        public event Action<MicState> StateChanged;

        bool _active;
        float _interruptedFor;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void BuildaMicRequest();
        [DllImport("__Internal")] static extern void BuildaMicStart(string workerUrl);
        [DllImport("__Internal")] static extern void BuildaMicStop();
        [DllImport("__Internal")] static extern IntPtr BuildaMicStatus();
        [DllImport("__Internal")] static extern void BuildaFree(IntPtr ptr);
#endif

        // 必须在用户手势回调（点击/触摸）里调用，按当前状态自动分派两次手势。
        public void StartMic()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            _active = true;
            switch (State)
            {
                case MicState.Idle:
                case MicState.Unavailable:
                case MicState.Requesting:
                    // 第 1 次手势：只申请权限（Unavailable 允许重试——用户可能刚在系统设置里开了权限）
                    BuildaMicRequest();
                    SetState(MicState.Requesting);
                    break;
                case MicState.NeedsGesture:
                case MicState.Interrupted:
                    // 第 2 次手势 / 中断手动重启：真正开采集（SDK 侧 start 在途去重，重复调用安全）
                    BuildaMicStart(WorkerUrl);
                    SetState(MicState.Starting);
                    break;
            }
#else
            SetState(MicState.Unavailable);
#endif
        }

        public void StopMic()
        {
            _active = false;
            Volume = 0f;
            Pitch = 0f;
            Features = new Dictionary<string, object>();
            LastError = "";
            _interruptedFor = 0f;
#if UNITY_WEBGL && !UNITY_EDITOR
            BuildaMicStop();
#endif
            SetState(MicState.Idle);
        }

        void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!_active)
            {
                return;
            }
            IntPtr ptr = BuildaMicStatus();
            string raw;
            try
            {
                raw = Marshal.PtrToStringUTF8(ptr);
            }
            finally
            {
                BuildaFree(ptr);
            }
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }
            object parsed;
            try
            {
                parsed = BuildaJson.Deserialize(raw);
            }
            catch (FormatException)
            {
                return;
            }
            if (!(parsed is Dictionary<string, object> status))
            {
                return;
            }
            Features = status.TryGetValue("features", out var f) && f is Dictionary<string, object> map
                ? map
                : new Dictionary<string, object>();
            Volume = Num(Features, "volume");
            Pitch = Num(Features, "pitch");
            LastError = status.TryGetValue("error", out var e) ? e as string ?? "" : "";
            Advance(
                status.TryGetValue("phase", out var p) ? p as string ?? "" : "",
                status.TryGetValue("micState", out var m) ? m as string ?? "" : "",
                Time.unscaledDeltaTime);
#endif
        }

        void Advance(string phase, string micState, float delta)
        {
            switch (phase)
            {
                case "requesting":
                    SetState(MicState.Requesting);
                    break;
                case "granted":
                    // 权限已通过，但此刻不在手势上下文（结果经轮询取回），必须等下一次点击
                    SetState(MicState.NeedsGesture);
                    break;
                case "unavailable":
                    SetState(MicState.Unavailable);
                    break;
                case "start_failed":
                    // 启动失败可在下一次手势里重试；持久失败由游戏依据 LastError 决定降级
                    SetState(MicState.NeedsGesture);
                    break;
                case "starting":
                    SetState(MicState.Starting);
                    break;
                case "running":
                    if (micState == "interrupted")
                    {
                        _interruptedFor += delta;
                        SetState(_interruptedFor > InterruptedGestureAfter ? MicState.NeedsGesture : MicState.Interrupted);
                    }
                    else
                    {
                        _interruptedFor = 0f;
                        SetState(MicState.Running);
                    }
                    break;
            }
        }

        void SetState(MicState next)
        {
            if (State == next)
            {
                return;
            }
            State = next;
            StateChanged?.Invoke(next);
        }

        static float Num(Dictionary<string, object> map, string key)
        {
            if (map != null && map.TryGetValue(key, out var v) && v is double d)
            {
                return (float)d;
            }
            return 0f;
        }
    }
}
