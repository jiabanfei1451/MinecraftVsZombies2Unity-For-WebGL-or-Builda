// Builda SDK Unity WebGL 桥（C# ↔ window.Builda.*）。
// 通用调用器：4 个导出按点号路径分发，不逐 API 导出——加请求式 API 只改 C# 侧 Builda.cs，
// 本文件保持稳定；订阅式事件（Builda.events.*）另有 3 个导出（BuildaEventsInit/On/Off），
// 加事件同样只改 C# 侧。结果统一 JSON 字符串过桥，形态对齐 builda-sdk.d.ts 的 BuildaResult。
// 异步回调经 makeDynCall 触发 C# 侧 [MonoPInvokeCallback] 静态函数（requestId 分发，
// 机制对标 agent/sdk/godot/builda.gd 的 sdk_result）。
var BuildaBridgeLib = {
  $BuildaBridge: {
    callback: null,
    eventCallback: null,
    eventSubs: {},

    resolve: function (path) {
      var target = window.Builda;
      var parts = path.split(".");
      for (var i = 0; i < parts.length - 1; i++) {
        target = target ? target[parts[i]] : undefined;
      }
      return { parent: target, leaf: parts[parts.length - 1] };
    },

    fail: function (message) {
      return JSON.stringify({ ok: false, error: { code: "JS_ERROR", message: String(message) } });
    },

    // 非 BuildaResult 形态的返回值（同步属性/工具函数）包成 {ok:true,data:v}，
    // 对齐 builda.gd 对 safeArea/assets.url 的包装惯例。
    wrap: function (value) {
      if (value && typeof value === "object" && typeof value.ok === "boolean") {
        return JSON.stringify(value);
      }
      return JSON.stringify({ ok: true, data: value === undefined ? null : value });
    },

    toHeap: function (str) {
      var len = lengthBytesUTF8(str) + 1;
      var ptr = _malloc(len);
      stringToUTF8(str, ptr, len);
      return ptr;
    },

    deliver: function (requestId, json) {
      if (!BuildaBridge.callback) {
        return;
      }
      var ptr = BuildaBridge.toHeap(json);
      {{{ makeDynCall('vii', 'BuildaBridge.callback') }}}(requestId, ptr);
      _free(ptr);
    },

    // 订阅式事件（Builda.events.*）→ C# 侧 [MonoPInvokeCallback] (namePtr, payloadJsonPtr)；
    // 指针回调返回后即释放，C# 侧须在回调内拷贝。
    deliverEvent: function (name, json) {
      if (!BuildaBridge.eventCallback) {
        return;
      }
      var namePtr = BuildaBridge.toHeap(name);
      var jsonPtr = BuildaBridge.toHeap(json);
      {{{ makeDynCall('vii', 'BuildaBridge.eventCallback') }}}(namePtr, jsonPtr);
      _free(jsonPtr);
      _free(namePtr);
    }
  },

  BuildaInit: function (cb) {
    BuildaBridge.callback = cb;
  },

  BuildaEventsInit: function (cb) {
    BuildaBridge.eventCallback = cb;
  },

  // 按事件名向 window.Builda.events 订阅一次（同名幂等）；返回 1 成功、0 表示 SDK 未加载或无 events
  // （旧 SDK），C# 侧可稍后重试。
  BuildaEventsOn: function (namePtr) {
    var name = UTF8ToString(namePtr);
    if (!window.Builda || !window.Builda.events || typeof window.Builda.events.on !== "function") {
      return 0;
    }
    if (BuildaBridge.eventSubs[name]) {
      return 1;
    }
    var handler = function (payload) {
      BuildaBridge.deliverEvent(name, JSON.stringify(payload === undefined ? null : payload));
    };
    BuildaBridge.eventSubs[name] = handler;
    window.Builda.events.on(name, handler);
    return 1;
  },

  BuildaEventsOff: function (namePtr) {
    var name = UTF8ToString(namePtr);
    var handler = BuildaBridge.eventSubs[name];
    if (!handler) {
      return;
    }
    delete BuildaBridge.eventSubs[name];
    if (window.Builda && window.Builda.events && typeof window.Builda.events.off === "function") {
      window.Builda.events.off(name, handler);
    }
  },

  // 异步调用：path 如 "privateKV.get"，argsJson 是 JSON 数组（逐个展开为实参），
  // Promise 结果回调 (requestId, jsonPtr)；jsonPtr 回调返回后即释放，C# 侧须在回调内拷贝。
  BuildaCall: function (pathPtr, argsPtr, requestId) {
    var path = UTF8ToString(pathPtr);
    try {
      if (!window.Builda) {
        throw new Error("Builda SDK not loaded (builda-sdk.js missing)");
      }
      var argsJson = UTF8ToString(argsPtr);
      var args = argsJson ? JSON.parse(argsJson) : [];
      var r = BuildaBridge.resolve(path);
      if (!r.parent || typeof r.parent[r.leaf] !== "function") {
        throw new Error("Unknown Builda API: " + path);
      }
      Promise.resolve(r.parent[r.leaf].apply(r.parent, args)).then(function (res) {
        BuildaBridge.deliver(requestId, BuildaBridge.wrap(res));
      }).catch(function (e) {
        BuildaBridge.deliver(requestId, BuildaBridge.fail(e));
      });
    } catch (e) {
      BuildaBridge.deliver(requestId, BuildaBridge.fail(e));
    }
  },

  // 同步调用：函数则调用、属性则取值（runtime.gameId / assets.baseUrl / mic.read 等）。
  // 返回堆上 UTF-8 指针，C# 侧读取后必须调 BuildaFree 释放。
  BuildaCallSync: function (pathPtr, argsPtr) {
    var out;
    try {
      if (!window.Builda) {
        throw new Error("Builda SDK not loaded (builda-sdk.js missing)");
      }
      var path = UTF8ToString(pathPtr);
      var argsJson = UTF8ToString(argsPtr);
      var args = argsJson ? JSON.parse(argsJson) : [];
      var r = BuildaBridge.resolve(path);
      if (!r.parent || !(r.leaf in r.parent)) {
        throw new Error("Unknown Builda API: " + path);
      }
      var v = typeof r.parent[r.leaf] === "function" ? r.parent[r.leaf].apply(r.parent, args) : r.parent[r.leaf];
      out = BuildaBridge.wrap(v);
    } catch (e) {
      out = BuildaBridge.fail(e);
    }
    return BuildaBridge.toHeap(out);
  },

  BuildaFree: function (ptr) {
    _free(ptr);
  }
};

autoAddDeps(BuildaBridgeLib, "$BuildaBridge");
mergeInto(LibraryManager.library, BuildaBridgeLib);
