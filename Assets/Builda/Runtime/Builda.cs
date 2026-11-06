// Builda SDK Unity C# 包装层（Unity 2021.3 LTS+，仅 WebGL 导出生效）。
// API 表面与 web 内核 builda-sdk.d.ts 一一对应；异步 API 传回调（requestId 分发，
// 机制对标 agent/sdk/godot/builda.gd 的 sdk_result 信号），同步 API 直接返回。
// 字节类值（privateKV / mic.read）过桥统一 base64，SDK 内部转 byte[]/float[]。
// 非 WebGL 平台（编辑器/PC/手机原生）所有调用返回 UNSUPPORTED 失败态，不抛异常。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;

namespace Builda
{
    public class BuildaError
    {
        public string Code = "";
        public string Message = "";
    }

    // 通用结果（对齐 d.ts BuildaResult）：Ok=true 时 Data 有效（Dictionary<string,object> /
    // List<object> / string / double / bool / null），Ok=false 时 Error 有效。
    public class BuildaResult
    {
        public bool Ok;
        public object Data;
        public BuildaError Error;
        public string RawJson = "";

        public Dictionary<string, object> DataMap => Data as Dictionary<string, object>;

        public static BuildaResult Fail(string code, string message)
        {
            return new BuildaResult { Ok = false, Error = new BuildaError { Code = code, Message = message } };
        }

        public static BuildaResult Unsupported()
        {
            return Fail("UNSUPPORTED", "Builda SDK requires WebGL build.");
        }

        public static BuildaResult Parse(string json)
        {
            object parsed;
            try
            {
                parsed = BuildaJson.Deserialize(json);
            }
            catch (Exception e)
            {
                return Fail("BAD_RESULT", "Invalid Builda SDK result: " + e.Message);
            }
            if (!(parsed is Dictionary<string, object> map) || !(map.TryGetValue("ok", out var ok) && ok is bool okFlag))
            {
                return Fail("BAD_RESULT", "Invalid Builda SDK result.");
            }
            var result = new BuildaResult { Ok = okFlag, RawJson = json };
            if (okFlag)
            {
                map.TryGetValue("data", out result.Data);
                return result;
            }
            result.Error = new BuildaError();
            if (map.TryGetValue("error", out var err) && err is Dictionary<string, object> errMap)
            {
                result.Error.Code = errMap.TryGetValue("code", out var c) ? c as string ?? "" : "";
                result.Error.Message = errMap.TryGetValue("message", out var m) ? m as string ?? "" : "";
            }
            return result;
        }
    }

    [Serializable]
    public struct BuildaSafeArea
    {
        public float Top, Right, Bottom, Left;
    }

    [Serializable]
    public struct BuildaCapsuleMenuRect
    {
        public float Top, Right, Width, Height;
    }

    // mic.read 同步结果：Ok=false 时 Error 有效；State 为 "running" | "interrupted"。
    public class BuildaMicRead
    {
        public bool Ok;
        public BuildaError Error;
        public float[] Samples = Array.Empty<float>();
        public int SampleRate;
        public int Dropped;
        public string State = "";
    }

    public static class BuildaSDK
    {
        public const string SdkVersion = "0.9.6";

#if UNITY_WEBGL && !UNITY_EDITOR
        delegate void ResultDelegate(int requestId, IntPtr jsonPtr);

        [DllImport("__Internal")] static extern void BuildaInit(ResultDelegate callback);
        [DllImport("__Internal")] static extern void BuildaCall(string path, string argsJson, int requestId);
        [DllImport("__Internal")] static extern IntPtr BuildaCallSync(string path, string argsJson);
        [DllImport("__Internal")] static extern void BuildaFree(IntPtr ptr);
        delegate void EventDelegate(IntPtr namePtr, IntPtr jsonPtr);
        [DllImport("__Internal")] static extern void BuildaEventsInit(EventDelegate callback);
        [DllImport("__Internal")] static extern int BuildaEventsOn(string name);
        [DllImport("__Internal")] static extern void BuildaEventsOff(string name);

        // 静态引用防 GC 回收委托（IL2CPP 函数指针生命周期）
        static readonly ResultDelegate CallbackRef = OnJsResult;
        static readonly EventDelegate EventCallbackRef = OnJsEvent;

        [MonoPInvokeCallback(typeof(EventDelegate))]
        static void OnJsEvent(IntPtr namePtr, IntPtr jsonPtr)
        {
            string name = Marshal.PtrToStringUTF8(namePtr);
            string json = Marshal.PtrToStringUTF8(jsonPtr);
            DispatchEvent(name, json);
        }

        [MonoPInvokeCallback(typeof(ResultDelegate))]
        static void OnJsResult(int requestId, IntPtr jsonPtr)
        {
            string json = Marshal.PtrToStringUTF8(jsonPtr);
            Action<BuildaResult> callback;
            if (!Pending.TryGetValue(requestId, out callback))
            {
                return;
            }
            Pending.Remove(requestId);
            callback?.Invoke(BuildaResult.Parse(json));
        }
#endif

        static readonly Dictionary<int, Action<BuildaResult>> Pending = new Dictionary<int, Action<BuildaResult>>();
        static int _nextRequestId = 1;
        static bool _initialized;

        // ---- 通用调用核心（path 为 window.Builda 下点号路径） ----

        public static int Call(string path, object[] args, Action<BuildaResult> callback)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            EnsureInit();
            int requestId = _nextRequestId++;
            if (callback != null)
            {
                Pending[requestId] = callback;
            }
            BuildaCall(path, BuildaJson.Serialize(args ?? Array.Empty<object>()), requestId);
            return requestId;
#else
            callback?.Invoke(BuildaResult.Unsupported());
            return -1;
#endif
        }

        public static BuildaResult CallSync(string path, object[] args)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            EnsureInit();
            IntPtr ptr = BuildaCallSync(path, BuildaJson.Serialize(args ?? Array.Empty<object>()));
            try
            {
                return BuildaResult.Parse(Marshal.PtrToStringUTF8(ptr));
            }
            finally
            {
                BuildaFree(ptr);
            }
#else
            return BuildaResult.Unsupported();
#endif
        }

        static void EnsureInit()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            BuildaInit(CallbackRef);
            BuildaEventsInit(EventCallbackRef);
#endif
        }

        // ---- events：订阅式回调（对齐 Builda.events.on/off，Godot 侧 sdk_event / app_visibility_changed） ----
        // payload 是事件对象解出的 Dictionary（无 payload 时为空字典）。回调在 JS 事件派发时同步回到
        // Unity 主线程。同一 handler 重复 On 只登记一次；Off 不传 handler 清空该事件全部订阅。
        // 非 WebGL 平台 On/Off 静默无效（不抛异常）。

        public delegate void BuildaEventHandler(Dictionary<string, object> payload);

        static readonly Dictionary<string, List<BuildaEventHandler>> EventHandlers = new Dictionary<string, List<BuildaEventHandler>>();

        public static void EventsOn(string name, BuildaEventHandler handler)
        {
            if (string.IsNullOrEmpty(name) || handler == null)
            {
                return;
            }
            if (!EventHandlers.TryGetValue(name, out var list))
            {
                list = new List<BuildaEventHandler>();
                EventHandlers[name] = list;
            }
            if (!list.Contains(handler))
            {
                list.Add(handler);
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            EnsureInit();
            BuildaEventsOn(name);
#endif
        }

        public static void EventsOff(string name, BuildaEventHandler handler = null)
        {
            if (string.IsNullOrEmpty(name) || !EventHandlers.TryGetValue(name, out var list))
            {
                return;
            }
            if (handler == null)
            {
                list.Clear();
            }
            else
            {
                list.Remove(handler);
            }
            if (list.Count == 0)
            {
                EventHandlers.Remove(name);
#if UNITY_WEBGL && !UNITY_EDITOR
                BuildaEventsOff(name);
#endif
            }
        }

        // appVisibility 便捷事件：visible=false 表示 App 最小化/切后台/锁屏（暂停玩法/计时/BGM），
        // true 表示回前台（恢复）；source 为 "host"（宿主推送，精确）或 "browser"（WebView 兜底）。
        // 只在状态变化时触发（SDK 去重），订阅时不补发当前状态（初始视为前台）。
        static Action<bool, string> _appVisibilityChanged;
        static readonly BuildaEventHandler AppVisibilityBridge = payload =>
        {
            var visible = true;
            if (payload != null && payload.TryGetValue("visible", out var v) && v is bool b)
            {
                visible = b;
            }
            var source = payload != null && payload.TryGetValue("source", out var s) ? s as string ?? "" : "";
            _appVisibilityChanged?.Invoke(visible, source);
        };

        public static event Action<bool, string> AppVisibilityChanged
        {
            add
            {
                _appVisibilityChanged += value;
                EventsOn("appVisibility", AppVisibilityBridge);
            }
            remove
            {
                _appVisibilityChanged -= value;
                if (_appVisibilityChanged == null)
                {
                    EventsOff("appVisibility", AppVisibilityBridge);
                }
            }
        }

        // pausePanel 便捷事件：宿主「已暂停」面板（胶囊 ✕ / RuntimeQuit 弹的同一面板）显示 true /
        // 关闭且未退出 false；真退出不触发。visible=true 暂停玩法与计时（BGM 由宿主自理）。与
        // AppVisibilityChanged 正交：按「任一暂停即暂停、全部恢复才恢复」处理。只在状态变化时触发。
        static Action<bool> _pausePanelChanged;
        static readonly BuildaEventHandler PausePanelBridge = payload =>
        {
            var visible = false;
            if (payload != null && payload.TryGetValue("visible", out var v) && v is bool b)
            {
                visible = b;
            }
            _pausePanelChanged?.Invoke(visible);
        };

        public static event Action<bool> PausePanelChanged
        {
            add
            {
                _pausePanelChanged += value;
                EventsOn("pausePanel", PausePanelBridge);
            }
            remove
            {
                _pausePanelChanged -= value;
                if (_pausePanelChanged == null)
                {
                    EventsOff("pausePanel", PausePanelBridge);
                }
            }
        }

        static void DispatchEvent(string name, string json)
        {
            if (string.IsNullOrEmpty(name) || !EventHandlers.TryGetValue(name, out var list) || list.Count == 0)
            {
                return;
            }
            Dictionary<string, object> payload = null;
            try
            {
                payload = BuildaJson.Deserialize(json) as Dictionary<string, object>;
            }
            catch (Exception)
            {
            }
            payload = payload ?? new Dictionary<string, object>();
            foreach (var handler in list.ToArray())
            {
                try
                {
                    handler(payload);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        // ---- 身份 ----

        public static int Whoami(Action<BuildaResult> callback)
        {
            return Call("whoami", null, callback);
        }

        // 向宿主换取 game-scoped 短期 token（data = {token, expiresAt}，expiresAt 为 Unix 秒）。
        // token 只在内存持有：禁止写入 privateKV、本地文件、URL 或存档；Runtime SDK 临近过期时懒刷新。
        public static int RequestToken(Action<BuildaResult> callback)
        {
            return Call("requestToken", null, callback);
        }

        // 获取服务端当前 Unix 毫秒时间（data = {serverTimeMs}）。
        public static int GetServerTime(Action<BuildaResult> callback)
        {
            return Call("getServerTime", null, callback);
        }

        // 批量查询 Cy 游玩角色公开资料。Data 是 roleId -> {userName,userImg} map；
        // map 中没有该 roleId 表示本次没查到。SDK 内部固定 50ms 聚合，单次最多 32 个。
        public static int GetRoleInfo(string[] roleIds, Action<BuildaResult> callback)
        {
            return Call("getRoleInfo", new object[] { roleIds ?? Array.Empty<string>() }, callback);
        }

        // 本人已存储 Player Data 的非权威观察，可能未含未同步操作；revision/source 不证明最新。
        // 发奖/扣减/偷菜/支付由 Online 游戏服务器动作裁决，不整档覆盖存档；后续旧读不能推翻
        // 正式动作结果或触发重放。失败不是空档/零余额。privateKV 是独立存储。
        // int64/revision 保留 string，bytes 为 base64，schemaVersion 为 uint32 number/null。
        public static int PlayerDataGetMany(string[] keys, Action<BuildaResult> callback)
        {
            return Call("playerData.getMany", new object[] { keys }, callback);
        }

        // 目录在 DataMap["meta"] 的 keys，values 为 {}；同样是可能滞后的非权威观察。
        // revision/source 不证明最新，不裁决发奖/扣减/偷菜/支付、不覆盖存档、不重放服务器动作。
        public static int PlayerDataListKeys(Action<BuildaResult> callback)
        {
            return Call("playerData.listKeys", null, callback);
        }

        // ---- runtime ----

        // 游戏存活握手：必须在游戏最开始调用（首个场景 Start() 第一件事，不要等资源加载完），
        // 并确保任何启动路径都能调到——页面加载 30 秒内宿主没收到 ready 会强制关闭游戏。
        public static int RuntimeReady(Action<BuildaResult> callback)
        {
            return Call("runtime.ready", null, callback);
        }

        public static int RuntimeQuit(Action<BuildaResult> callback)
        {
            return Call("runtime.quit", null, callback);
        }

        // 屏幕常亮开关：true 阻止系统自动息屏，false 恢复系统超时。
        // **最低宿主版本 App 1.84.0**，更早的宿主回 UNSUPPORTED_CMD，按不支持降级即可。回调的 Data 里
        // enabled 是**实际生效状态**（宿主未接该能力/系统拒绝时为 false）。
        // 作用域只到本局游戏：宿主退出游戏页时自行复位，游戏不需要在 OnDestroy 里关掉。
        public static int RuntimeSetKeepScreenOn(bool enabled, Action<BuildaResult> callback = null)
        {
            return Call("runtime.setKeepScreenOn", new object[] { enabled }, callback);
        }

        public static string RuntimeGameId()
        {
            var r = CallSync("runtime.gameId", null);
            return r.Ok ? r.Data as string ?? "" : "";
        }

        public static string RuntimeBundleVersion()
        {
            var r = CallSync("runtime.bundleVersion", null);
            return r.Ok ? r.Data as string ?? "" : "";
        }

        // 当前语言（BCP 47 裸语言码，如 "en"、"zh"；唯一例外西语 "es-419"）。每次调用现读，
        // 非 WebGL 或取值异常兜底 "en"。匹配建议：精确 → 语言前缀（es-419 → es）→ 游戏默认语言。
        public static string RuntimeLanguage()
        {
            var r = CallSync("runtime.language", null);
            var lang = r.Ok ? (r.Data as string ?? "").Trim() : "";
            return lang.Length > 0 ? lang : "en";
        }

        // 宿主 Builda App 版本兜底值：`_appVersion` 自 App 1.84.0 起注入，取不到即说明宿主更老，
        // 统一报最后一个不提供该值的版本，故"老宿主"与"无宿主"（非 WebGL / 编辑器）不必区分。
        public const string AppVersionFallback = "1.83.0";

        // 宿主 Builda App 的版本号（形如 "1.84.0"）。每次调用现读；非 WebGL 或取值异常兜底
        // AppVersionFallback。特性探测请用 RuntimeAppVersionAtLeast，不要直接比字符串。
        public static string RuntimeAppVersion()
        {
            var r = CallSync("runtime.appVersion", null);
            var v = r.Ok ? (r.Data as string ?? "").Trim() : "";
            return v.Length > 0 ? v : AppVersionFallback;
        }

        // 宿主版本是否 >= target（如 "1.84.0"）——做新能力特性探测请用本方法，不要自己拿
        // RuntimeAppVersion() 比字符串（字符串序下 "1.9.0" > "1.10.0" 会判错）。
        // WebGL 下直接委派 SDK 核心的 appVersionAtLeast，保证与 HTML5/Godot 判定完全一致；
        // 非 WebGL（编辑器）没有宿主、版本恒为兜底值，本地按同样规则比一次。
        // 其它关系由它组合：小于用 !AtLeast(x)，区间用 AtLeast(a) && !AtLeast(b)。
        public static bool RuntimeAppVersionAtLeast(string target)
        {
            var want = (target ?? "").Trim();
            var r = CallSync("runtime.appVersionAtLeast", new object[] { want });
            if (r.Ok && r.Data is bool ok) return ok;
            return VersionAtLeast(RuntimeAppVersion(), want);
        }

        // 逐段比数字（缺省段按 0）。仅用于非 WebGL / SDK 未加载的兜底路径；
        // 有宿主时以 SDK 核心的实现为准。
        static bool VersionAtLeast(string cur, string target)
        {
            var a = (cur ?? "").Split('.');
            var b = (target ?? "").Split('.');
            var n = a.Length > b.Length ? a.Length : b.Length;
            for (var i = 0; i < n; i++)
            {
                int x = 0, y = 0;
                if (i < a.Length) int.TryParse(a[i], out x);
                if (i < b.Length) int.TryParse(b[i], out y);
                if (x != y) return x > y;
            }
            return true;
        }

        // 失败（非 WebGL / SDK 未加载）时返回全零。
        public static BuildaSafeArea RuntimeSafeArea()
        {
            var r = CallSync("runtime.safeArea", null);
            var map = r.Ok ? r.DataMap : null;
            return new BuildaSafeArea
            {
                Top = Num(map, "top"), Right = Num(map, "right"),
                Bottom = Num(map, "bottom"), Left = Num(map, "left"),
            };
        }

        public static BuildaCapsuleMenuRect RuntimeCapsuleMenuRect()
        {
            var r = CallSync("runtime.capsuleMenuRect", null);
            var map = r.Ok ? r.DataMap : null;
            return new BuildaCapsuleMenuRect
            {
                Top = Num(map, "top"), Right = Num(map, "right"),
                Width = Num(map, "width"), Height = Num(map, "height"),
            };
        }

        // ---- assets（assets.zip：audio/** 宿主音频 + res/** 游戏自取大资源，规则见 skill 文档） ----
        // res/** 用 AssetsUrl/AssetsBaseUrl 取 URL 后自行加载（UnityWebRequest / Addressables
        // RemoteLoadPath / glTFast）；不要把 CDN URL 写死，assetsBaseUrl 每次重传都会变。

        public static string AssetsBaseUrl()
        {
            var r = CallSync("assets.baseUrl", null);
            return r.Ok ? r.Data as string ?? "" : "";
        }

        public static string AssetsUrl(string path)
        {
            var r = CallSync("assets.url", new object[] { path });
            return r.Ok ? r.Data as string ?? "" : "";
        }

        // ---- audio ----

        public static int AudioPlayBGM(string path, bool loop = true, float volume = 1f, Action<BuildaResult> callback = null)
        {
            return Call("audio.playBGM", new object[] { path, new Dictionary<string, object> { { "loop", loop }, { "volume", volume } } }, callback);
        }

        public static int AudioStopBGM(Action<BuildaResult> callback = null)
        {
            return Call("audio.stopBGM", null, callback);
        }

        public static int AudioPlaySFX(string path, string sessionId = "", float volume = 1f, Action<BuildaResult> callback = null)
        {
            return Call("audio.playSFX", new object[] { path, new Dictionary<string, object> { { "sessionId", sessionId }, { "volume", volume } } }, callback);
        }

        public static int AudioSetMuted(bool muted, string group = "bgm", Action<BuildaResult> callback = null)
        {
            return Call("audio.setMuted", new object[] { muted, group }, callback);
        }

        public static int AudioVibrate(int level = 2, Action<BuildaResult> callback = null)
        {
            return Call("audio.vibrate", new object[] { level }, callback);
        }

        // ---- mic（docs/decisions/0012，仅基础透传） ----
        // 标准流程是**两次手势**：第 1 次点击回调里 AudioRequestMicrophone() 申请权限
        // （data.available == false 表示用户拒绝或无麦，给出阻断提示并允许重试）→ 权限结果
        // 回调**不在手势上下文，不要直接 MicStart()**（AudioContext 会被自动播放策略拦下且
        // 不可恢复），停在"等玩家再点一次"的引导 → 第 2 次点击回调里 MicStart() → 每帧
        // MicRead() 同步拉 PCM → 不用时 MicStop()。重 DSP 放 Web Worker，别抢主线程。
        // PCM 只许本地实时分析，禁止录音留存/上传（审核红线）。

        public static int AudioRequestMicrophone(Action<BuildaResult> callback)
        {
            return Call("audio.requestMicrophone", null, callback);
        }

        // sampleRate 传 0 表示跟随 AudioContext 默认（实际值以回调 data.sampleRate 为准）
        public static int MicStart(int sampleRate = 0, int bufferMs = 1000, Action<BuildaResult> callback = null)
        {
            var opts = new Dictionary<string, object> { { "bufferMs", bufferMs } };
            if (sampleRate > 0)
            {
                opts["sampleRate"] = sampleRate;
            }
            return Call("mic.start", new object[] { opts }, callback);
        }

        // 同步拉取自上次 read 以来的新 PCM 样本（每帧调一次）。maxSamples 传 0 表示全部未读。
        // Dropped > 0 表示拉取过慢被环形缓冲覆盖；State 持续 "interrupted" 时引导玩家点击后
        // 重调 MicStart()（幂等可重入）。
        public static BuildaMicRead MicRead(int maxSamples = 0)
        {
            var opts = new Dictionary<string, object> { { "encoding", "base64" } };
            if (maxSamples > 0)
            {
                opts["maxSamples"] = maxSamples;
            }
            var r = CallSync("mic.read", new object[] { opts });
            if (!r.Ok)
            {
                return new BuildaMicRead { Ok = false, Error = r.Error };
            }
            var map = r.DataMap;
            return new BuildaMicRead
            {
                Ok = true,
                Samples = MicSamples(map != null && map.TryGetValue("samples", out var s) ? s : null),
                SampleRate = (int)Num(map, "sampleRate"),
                Dropped = (int)Num(map, "dropped"),
                State = map != null && map.TryGetValue("state", out var st) ? st as string ?? "" : "",
            };
        }

        public static int MicStop(Action<BuildaResult> callback = null)
        {
            return Call("mic.stop", null, callback);
        }

        // ---- pay / ad / input ----

        // legacy 内购：支付后自动确认发货，无法恢复发货失败的订单。新游戏用 Iap*。
        public static int PayShowPanel(string saleId, Action<BuildaResult> callback)
        {
            return Call("pay.showPayPanel", new object[] { saleId }, callback);
        }

        // In-app purchase without auto-claim: data = {success, orderId}; success only means paid, the
        // order stays Paid. grantBy=client: grant idempotently by orderId, then IapClaim.
        // grantBy=room: hand orderId to Room doOrder; do not claim on the client.
        public static int IapShowPayPanel(string saleId, Action<BuildaResult> callback)
        {
            return Call("iap.showPayPanel", new object[] { saleId }, callback);
        }

        // grantBy ("client" | "room"): declare the grant path this purchase is coded for; the host sends
        // it with the order for diagnostics. It does not override the frozen configuration or guarantee
        // rejection on mismatch. Route fulfillment using the returned order grantBy.
        public static int IapShowPayPanel(string saleId, string grantBy, Action<BuildaResult> callback)
        {
            return Call("iap.showPayPanel", new object[] { saleId, new Dictionary<string, object> { { "grantBy", grantBy } } }, callback);
        }

        // Paid→Delivered, idempotent (replays succeed). Only for grantBy=client orders already granted.
        // data = {orderId, status}.
        public static int IapClaim(string orderId, Action<BuildaResult> callback)
        {
            return Call("iap.claim", new object[] { orderId }, callback);
        }

        // Undelivered order ids only. data = {orderIds: string[]}. No payId/grantBy.
        // Do not infer grantBy from the current catalog; if unknown, do not grant on the client.
        public static int IapGetPendingOrderIds(Action<BuildaResult> callback)
        {
            return Call("iap.getPendingOrderIds", null, callback);
        }

        // posId 是游戏自定义的广告位统计标识（任意命名，如 "revive"），仅用于区分广告触发点；
        // 不是平台分配的广告位 ID，无需申请、无需在 manifest 声明。
        public static int AdShowReward(string posId, Action<BuildaResult> callback)
        {
            return Call("ad.showRewardAd", new object[] { posId }, callback);
        }

        // 拉起平台输入面板。结果 data = {confirmed, text}：确认时 text 为最终文本（平台已
        // 完成审核/截断），取消时 confirmed=false、text=""。面板未关时再调返回 BUSY。
        public static int InputShowPanel(string placeholder = "", string defaultValue = "", int maxLength = 64, Action<BuildaResult> callback = null)
        {
            return Call("input.showInputPanel", new object[]
            {
                new Dictionary<string, object> { { "placeholder", placeholder }, { "defaultValue", defaultValue }, { "maxLength", maxLength } },
            }, callback);
        }

        // ---- rank（榜单先在 builda.publish.json 的 rankBoards 声明并发布生效） ----

        public static int RankSubmitScore(string rankId, long score, Action<BuildaResult> callback = null)
        {
            return Call("rank.submitScore", new object[] { rankId, score }, callback);
        }

        // 结果 data = {rankId, list: [{rank, score, player}], self: {rank, score}|null}
        public static int RankGetList(string rankId, int limit = 50, Action<BuildaResult> callback = null)
        {
            return Call("rank.getRankList", new object[] { rankId, new Dictionary<string, object> { { "limit", limit } } }, callback);
        }

        // ---- privateKV：私域存档（当前游戏×当前玩家），value 是字节流，过桥统一 base64。
        // 限额：单 value ≤ 32KB、单次批量 ≤ 32 key、每玩家每游戏总活 key ≤ 100；无聚合字节配额。
        // SDK 不自动拆批，超过 32 key 返回 BATCH_TOO_LARGE。
        // get 的 data 为 null 表示 key 不存在（空字节是合法值），用 KvBytes() 还原 byte[]。 ----

        public static int KvGet(string key, Action<BuildaResult> callback)
        {
            return Call("privateKV.get", new object[] { key, Base64Opts() }, callback);
        }

        public static int KvSet(string key, byte[] value, Action<BuildaResult> callback = null)
        {
            return Call("privateKV.set", new object[] { key, Convert.ToBase64String(value ?? Array.Empty<byte>()), Base64Opts() }, callback);
        }

        public static int KvRemove(string key, Action<BuildaResult> callback = null)
        {
            return Call("privateKV.remove", new object[] { key }, callback);
        }

        public static int KvGetMany(IEnumerable<string> keys, Action<BuildaResult> callback)
        {
            return Call("privateKV.getMany", new object[] { new List<object>(keys ?? Array.Empty<string>()), Base64Opts() }, callback);
        }

        // 整批校验任一违规整批拒绝，不做部分写入
        public static int KvSetMany(IDictionary<string, byte[]> entries, Action<BuildaResult> callback = null)
        {
            var encoded = new Dictionary<string, object>();
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    encoded[e.Key] = Convert.ToBase64String(e.Value ?? Array.Empty<byte>());
                }
            }
            return Call("privateKV.setMany", new object[] { encoded, Base64Opts() }, callback);
        }

        public static int KvRemoveMany(IEnumerable<string> keys, Action<BuildaResult> callback = null)
        {
            return Call("privateKV.removeMany", new object[] { new List<object>(keys ?? Array.Empty<string>()) }, callback);
        }

        // ---- 字节工具（对齐 builda.gd 的 private_kv_bytes / mic_samples） ----

        // KvGet/KvGetMany 结果里 base64 的 value 还原为 byte[]；null（key 不存在）返回 null。
        public static byte[] KvBytes(object value)
        {
            if (!(value is string s))
            {
                return null;
            }
            try
            {
                return Convert.FromBase64String(s);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // base64 的 float32 little-endian 样本流还原为 float[]
        public static float[] MicSamples(object value)
        {
            var bytes = KvBytes(value);
            if (bytes == null || bytes.Length < 4)
            {
                return Array.Empty<float>();
            }
            var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
            return samples;
        }

        static Dictionary<string, object> Base64Opts()
        {
            return new Dictionary<string, object> { { "encoding", "base64" } };
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
