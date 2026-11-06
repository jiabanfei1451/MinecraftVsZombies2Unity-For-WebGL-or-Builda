#nullable enable

using System.Runtime.InteropServices;
using UnityEngine;

namespace MVZ2.IO
{
    /// <summary>
    /// Builda/WebGL 存档持久化同步。
    /// WebGL 下 persistentDataPath 是内存文件系统（IDBFS），
    /// 必须调用 FS.syncfs 才会写入浏览器 IndexedDB，否则刷新页面后存档丢失。
    /// 非 WebGL 平台下所有调用是空操作。
    /// </summary>
    public static class SaveSync
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void SafariFileSystemSync__Sync();
#endif

        public static void Sync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                SafariFileSystemSync__Sync();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[SaveSync] sync failed: " + e.Message);
            }
#endif
        }
    }
}
