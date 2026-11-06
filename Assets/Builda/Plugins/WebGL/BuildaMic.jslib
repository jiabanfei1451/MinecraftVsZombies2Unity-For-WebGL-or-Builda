// BuildaMic Unity WebGL 胶水（页面内权限/启动/停止 + rAF 泵 + 特征邮箱），
// 与 Godot 版 builda_mic.gd 的 _BOOTSTRAP_JS 同构（docs/decisions/0012 §4.2）。
// PCM 全程不过 C# 桥——rAF 泵把 Builda.mic.read() 的样本 transfer 给分析 Worker
// （StreamingAssets/builda-mic/builda-mic-worker.js，默认算 RMS 音量 + YIN 音高），
// C# 每帧只经 BuildaMicStatus 拉几十字节的特征 JSON。
// 换算法/加特征只改 worker 文件：postMessage({...}) 的字段自动出现在 BuildaMic.Features 里。
var BuildaMicLib = {
  $BuildaMicGlue: {
    st: null,

    ensure: function () {
      if (!BuildaMicGlue.st) {
        BuildaMicGlue.st = { phase: "idle", error: "", micState: "", features: {}, worker: null, pump: 0 };
      }
      return BuildaMicGlue.st;
    },

    request: function () {
      var st = BuildaMicGlue.ensure();
      if (st.phase === "requesting") {
        return;
      }
      if (!window.Builda) {
        st.phase = "unavailable";
        st.error = "SDK_MISSING";
        return;
      }
      st.phase = "requesting";
      st.error = "";
      window.Builda.audio.requestMicrophone().then(function (r) {
        if (r.ok && r.data && r.data.available) {
          st.phase = "granted";
        } else {
          st.phase = "unavailable";
          st.error = r.ok ? "MIC_UNAVAILABLE" : ((r.error && r.error.code) || "ERROR");
        }
      });
    },

    start: function (workerUrl) {
      var st = BuildaMicGlue.ensure();
      if (st.phase === "starting") {
        return;
      }
      if (st.phase === "running" && st.micState !== "interrupted") {
        return;
      }
      if (!window.Builda) {
        st.phase = "unavailable";
        st.error = "SDK_MISSING";
        return;
      }
      st.phase = "starting";
      st.error = "";
      if (!st.worker) {
        try {
          st.worker = new Worker(workerUrl);
        } catch (e) {
          st.phase = "unavailable";
          st.error = "WORKER_LOAD_FAILED";
          return;
        }
        st.worker.onerror = function () {
          BuildaMicGlue.stopPump();
          st.phase = "unavailable";
          st.error = "WORKER_LOAD_FAILED";
        };
        st.worker.onmessage = function (ev) {
          if (ev.data && typeof ev.data === "object") {
            st.features = ev.data;
          }
        };
      }
      window.Builda.mic.start().then(function (r) {
        if (!r.ok) {
          st.phase = "start_failed";
          st.error = (r.error && r.error.code) || "ERROR";
          return;
        }
        st.phase = "running";
        BuildaMicGlue.pump();
      });
    },

    pump: function () {
      var st = BuildaMicGlue.ensure();
      if (st.pump) {
        return;
      }
      st.pump = requestAnimationFrame(function tick() {
        st.pump = requestAnimationFrame(tick);
        var r = window.Builda.mic.read();
        if (!r.ok) {
          return;
        }
        st.micState = r.data.state;
        if (r.data.samples.length && st.worker) {
          st.worker.postMessage({ samples: r.data.samples, sampleRate: r.data.sampleRate }, [r.data.samples.buffer]);
        }
      });
    },

    stopPump: function () {
      var st = BuildaMicGlue.ensure();
      if (st.pump) {
        cancelAnimationFrame(st.pump);
        st.pump = 0;
      }
    }
  },

  BuildaMicRequest: function () {
    BuildaMicGlue.request();
  },

  BuildaMicStart: function (workerUrlPtr) {
    BuildaMicGlue.start(UTF8ToString(workerUrlPtr));
  },

  BuildaMicStop: function () {
    var st = BuildaMicGlue.ensure();
    BuildaMicGlue.stopPump();
    if (st.worker) {
      st.worker.terminate();
      st.worker = null;
    }
    st.phase = "idle";
    st.error = "";
    st.micState = "";
    st.features = {};
    if (window.Builda) {
      window.Builda.mic.stop();
    }
  },

  // 返回堆上 UTF-8 指针，C# 侧读取后必须调 BuildaFree（Builda.jslib）释放。
  BuildaMicStatus: function () {
    var st = BuildaMicGlue.ensure();
    var json = JSON.stringify({ phase: st.phase, error: st.error, micState: st.micState, features: st.features });
    var len = lengthBytesUTF8(json) + 1;
    var ptr = _malloc(len);
    stringToUTF8(json, ptr, len);
    return ptr;
  }
};

autoAddDeps(BuildaMicLib, "$BuildaMicGlue");
mergeInto(LibraryManager.library, BuildaMicLib);
