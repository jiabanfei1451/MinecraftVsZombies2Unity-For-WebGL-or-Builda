// Builda SDK 用法示例：挂到任意场景对象上即可（仅演示，正式接入按需取用）。
// 完整 API 见 Runtime/Builda.cs 与 .builda-agent/sdk/web/builda-sdk.d.ts。
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Builda.Examples
{
    public class BuildaExample : MonoBehaviour
    {
        // App 前后台与宿主「已暂停」面板是两组正交状态，统一维护、合并判定：
        // 任一为暂停即暂停、全部恢复才恢复。不要在两个回调里各自直接暂停/恢复——
        // 「打开暂停面板 → 锁屏 → 解锁」后面板仍开着，前台事件不能把游戏恢复。
        bool _appVisible = true;          // 初始视为前台
        bool _pausePanelVisible = false;  // 初始视为未显示

        void ApplyPause()
        {
            bool paused = !_appVisible || _pausePanelVisible;
            Time.timeScale = paused ? 0f : 1f;  // 这里同时暂停/恢复玩法、计时器（BGM 面板期间由宿主自理）
        }

        // Optional stored preview only; Online server actions decide rewards/deductions/payments/stealing.
        // Never overwrite saves or replay successful actions from a later stale read.
        public void RefreshPlayerDataPreview()
        {
            BuildaSDK.PlayerDataGetMany(new[] { "coins" }, r => Debug.Log("Player Data preview available=" + r.Ok));
            BuildaSDK.PlayerDataListKeys(r => Debug.Log("Player Data directory available=" + r.Ok));
        }

        void Start()
        {
            // ready 必须是游戏最开始的第一件事（页面加载 30 秒内宿主没收到会强制关闭游戏）
            BuildaSDK.RuntimeReady(r => Debug.Log("ready ok=" + r.Ok));
            Debug.Log("Builda SDK version: " + BuildaSDK.SdkVersion);
            Debug.Log("Builda language: " + BuildaSDK.RuntimeLanguage());
            // 宿主版本探测：判定一律用 RuntimeAppVersionAtLeast，别自己比版本字符串。
            // 1.84.0 起才有真实版本号，更老的宿主与无宿主都按 1.83.0 处理 → false。
            if (BuildaSDK.RuntimeAppVersionAtLeast("1.84.0")) Debug.Log("host supports 1.84.0 features");

            // 身份：回调在 JS Promise 完成后回到主线程
            BuildaSDK.Whoami(result =>
            {
                if (!result.Ok)
                {
                    Debug.LogWarning("whoami failed: " + result.Error.Code + " " + result.Error.Message);
                    return;
                }
                var player = result.DataMap;
                Debug.Log("player id=" + player["id"] + " name=" + player["name"]);
            });

            // 私域存档：value 是字节流，SDK 内部 base64 过桥
            BuildaSDK.KvSet("save_slot1", Encoding.UTF8.GetBytes("{\"level\":3}"), setResult =>
            {
                if (!setResult.Ok)
                {
                    Debug.LogWarning("kv set failed: " + setResult.Error.Code);
                    return;
                }
                BuildaSDK.KvGet("save_slot1", getResult =>
                {
                    // data 为 null 表示 key 不存在；KvBytes 还原字节
                    var bytes = BuildaSDK.KvBytes(getResult.Data);
                    Debug.Log("kv get: " + (bytes == null ? "<missing>" : Encoding.UTF8.GetString(bytes)));
                });
            });

            // 排行榜（榜单先在 builda.publish.json 的 rankBoards 声明）
            BuildaSDK.RankSubmitScore("weekly_score", 1200, r => Debug.Log("submit ok=" + r.Ok));

            // 安全区适配（刘海/胶囊）
            var safeArea = BuildaSDK.RuntimeSafeArea();
            Debug.Log("safeArea top=" + safeArea.Top + " bottom=" + safeArea.Bottom);

            // App 切后台/回前台（主线程回调）与宿主「已暂停」面板弹出/关闭：只更新各自状态，
            // 暂停与否交给 ApplyPause 合并判定（见类顶部说明）
            BuildaSDK.AppVisibilityChanged += (visible, source) =>
            {
                Debug.Log("app visibility: " + visible + " (" + source + ")");
                _appVisible = visible;
                ApplyPause();
            };
            BuildaSDK.PausePanelChanged += visible =>
            {
                Debug.Log("pause panel: " + visible);
                _pausePanelVisible = visible;
                ApplyPause();
            };
        }
    }
}
