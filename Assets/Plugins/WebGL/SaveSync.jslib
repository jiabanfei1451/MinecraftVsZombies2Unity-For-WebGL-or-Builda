// Builda/WebGL：IndexedDB 持久化同步
// Unity WebGL 把 Application.persistentDataPath 映射到 Emscripten 的 IDBFS（内存文件系统），
// 需要显式调用 FS.syncfs 才会真正写入浏览器 IndexedDB，否则刷新/关闭页面就丢失。
//
// 注意：mergeInto 只收录对象里的属性。写在文件作用域的独立 function 不会被打包，
// 在产物里会变成未定义符号（曾导致 ReferenceError: SafariBridgeSyncAgain is not defined）。
// 所有辅助逻辑必须写成对象成员或成员内部的嵌套函数。
mergeInto(LibraryManager.library, {
  SafariFileSystemSync__Sync: function () {
    try {
      if (typeof FS === 'undefined' || !FS.syncfs) return;

      // 防抖：同一时刻只做一次同步，并发请求合并到下一次。
      // 并发调用会触发 "2 FS.syncfs operations in flight at once" 警告。
      var st = Module.__saveSyncState || (Module.__saveSyncState = { busy: false, pending: false });
      if (st.busy) {
        st.pending = true;
        return;
      }

      // 嵌套函数：写在成员内部，保证会被 mergeInto 一起打包。
      var doSync = function () {
        st.busy = true;
        FS.syncfs(false, function (err) {
          st.busy = false;
          if (err) {
            console.warn('[SaveSync] FS.syncfs failed:', err);
          }
          if (st.pending) {
            st.pending = false;
            doSync();
          }
        });
      };

      doSync();
    } catch (e) {
      console.warn('[SaveSync] sync error:', e);
    }
  }
});
