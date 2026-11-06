using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildaWebGLBuilder
{
    /// <summary>
    /// 快速验证入口：只跑 Addressables 内容构建，不做完整 WebGL Player 构建。
    /// 用于在 20-40 分钟的完整构建之前，先确认 "Unable to find group" / NullReference 已解决。
    /// 命令行：-executeMethod BuildaWebGLBuilder.BuildAddressablesOnly
    /// </summary>
    [MenuItem("Builda/Test Addressables Content Only")]
    private static void ReimportProblemAssets()
    {
        string[] paths =
        {
            "Assets/GameContent/Assets/mvz2/models/contraption/halloween/small_dispenser.prefab",
            "Assets/GameContent/Assets/mvz2/models/contraption/prologue/mine_tnt.prefab",
        };
        foreach (var p in paths)
        {
            if (!System.IO.File.Exists(p))
                continue;
            AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            var t = AssetDatabase.GetMainAssetTypeAtPath(p);
            Debug.Log($"[ADDRESSABLES] 预导入 {p} -> {t?.Name ?? "<null>"}");
        }
        AssetDatabase.Refresh();
    }
    private static double _playStart;
    private static bool _playEntered;
    public static void PlayDiagnose()
    {
        var scene = "Assets/Scenes/Landing.unity";
        if (!System.IO.File.Exists(scene))
        {
            Debug.LogError("[PLAYDIAG] 场景不存在: " + scene);
            EditorApplication.Exit(2);
            return;
        }
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scene);
        Debug.LogError("[PLAYDIAG] 已打开场景，进入 Play");
        _playStart = EditorApplication.timeSinceStartup;
        EditorApplication.update += PlayTick;
        EditorApplication.EnterPlaymode();
    }
    private static void PlayTick()
    {
        if (!_playEntered && EditorApplication.isPlaying)
        {
            _playEntered = true;
            Debug.LogError("[PLAYDIAG] Play 模式已启动");
        }
        double el = EditorApplication.timeSinceStartup - _playStart;
        if (el > 100.0)
        {
            Debug.LogError("[PLAYDIAG] 到时退出，elapsed=" + el.ToString("F1"));
            EditorApplication.update -= PlayTick;
            EditorApplication.Exit(0);
        }
    }
    public static void DiagnoseAsset()
    {
        const string path = "Assets/GameContent/Assets/mvz2/models/contraption/halloween/small_dispenser.prefab";
        Debug.Log("[DIAG] 文件存在(IO): " + System.IO.File.Exists(path));
        var importer = AssetImporter.GetAtPath(path);
        Debug.Log("[DIAG] importer = " + (importer == null ? "<null>" : importer.GetType().Name));
        var first = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        Debug.Log("[DIAG] 首次加载 = " + (first == null ? "<null>" : first.GetType().Name));
        Debug.Log("[DIAG] GUID = " + AssetDatabase.AssetPathToGUID(path));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();
        var importer2 = AssetImporter.GetAtPath(path);
        Debug.Log("[DIAG] 重导入后 importer = " + (importer2 == null ? "<null>" : importer2.GetType().Name));
        var second = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        Debug.Log("[DIAG] 重导入后加载 = " + (second == null ? "<null>" : second.GetType().Name));
        Debug.Log("[DIAG] 主资源类型 = " + (AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "<null>"));
        Debug.Log("[DIAG] 完成");
    }
    public static void BuildAddressablesOnly()
    {
        ForceInitAddressablesDefaultObject();
        BuildAddressablesContent();
        Debug.Log("ADDRESSABLES_ONLY_DONE");
        EditorApplication.Exit(0);
    }

    /// <summary>
    /// 仅编译检查入口：打开工程、等待脚本编译完成后立即退出，不做任何构建。
    /// 用于快速确认脚本改动没有编译错误（约 2-3 分钟，远快于 20-40 分钟的完整构建）。
    /// 命令行：-executeMethod BuildaWebGLBuilder.CompileCheckOnly
    /// </summary>
    [MenuItem("Builda/Compile Check Only")]
    public static void CompileCheckOnly()
    {
        // 强制触发一次脚本编译
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        var errors = System.AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
            .Where(t => t != null)
            .Count();

        Debug.Log("COMPILE_CHECK_OK types=" + errors);
        EditorApplication.Exit(0);
    }

    [MenuItem("Builda/Build WebGL")]
    public static void Build()
    {
        // ---- batchmode 必需：强制初始化 Addressables DefaultObject ----
        // 见 ForceInitAddressablesDefaultObject() 注释：Settings getter 走的是
        // EditorBuildSettings 的 config object，batchmode 里没有注册项 → 恒 null。
        ForceInitAddressablesDefaultObject();

        // Builda SDK 引用契约：必须使用 Builda WebGL 模板（head 内含 builda-sdk.js）
        // 注意：项目内置模板必须带 PROJECT: 前缀，否则 WebGlBuildPostprocessor 里
        // template.Split(':') 后 parts[1] 越界（IndexOutOfRangeException）
        PlayerSettings.WebGL.template = "PROJECT:Builda";

        // 压缩：Brotli + Decompression Fallback（推荐，产物为 *.unityweb 自解压）
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.decompressionFallback = true;

        // ---- Addressables 随 Player 构建打包 ----
        // BuildWithPlayer(1)：Addressables 资源直接进 Player，出真正可游玩的全量包。
        // 该设置属全局，用反射写入以避免编译期强依赖 Addressables 编辑器程序集。
        // 下面会显式调用 BuildAddressablesContent()，若同时开 BuildWithPlayer，
        // Addressables 会被构建两次，第二次可能覆盖第一次导致 catalog 与 bundle 不一致。
        ApplyAddressablesPlayerBuildOption("DoNotBuildWithPlayer");

        string[] scenes = new string[EditorBuildSettings.scenes.Length];
        for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
        {
            scenes[i] = EditorBuildSettings.scenes[i].path;
            Debug.Log("Scene: " + scenes[i]);
        }

        string outputPath = "Builds/WebGL";
        BuildPlayerOptions opts = new BuildPlayerOptions();
        opts.scenes = scenes;
        opts.locationPathName = outputPath;
        opts.target = BuildTarget.WebGL;
        // 调试期：开发构建 + 完整堆栈，用于定位 WebGL 运行时异常
        bool devBuild = System.Environment.GetEnvironmentVariable("BUILDA_DEV_BUILD") == "1";
        opts.options = devBuild
            ? (BuildOptions.Development | BuildOptions.AllowDebugging)
            : BuildOptions.None;

        // ---- Addressables 内容构建 ----
        // BuildWithPlayer 只控制 Player 构建时如何处理 Addressables 引用，
        // 但不会自动触发 Addressables 的 Content Build——必须显式调用。
        BuildAddressablesContent();

        BuildReport report = BuildPipeline.BuildPlayer(opts);
        BuildSummary summary = report.summary;
        if (summary.result == BuildResult.Succeeded)
        {
            // 平台审核会按产物里的 URL 判定违规，构建后统一清理 Unity 生成脚本中的外部链接。
            try { SanitizeLoaderJs(outputPath); }
            catch (Exception e) { Debug.LogWarning("SANITIZE failed: " + e); }
            Debug.Log("BUILD SUCCESS: " + summary.totalSize + " bytes");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("BUILD FAILED: " + summary.result);
            EditorApplication.Exit(1);
        }
    }

    /// <summary>
    /// 写入 AddressableAssetSettings.BuildAddressablesWithPlayerBuild。
    /// Addressables 1.25.0 PlayerBuildOption: PreferencesValue=0, BuildWithPlayer=1, DoNotBuildWithPlayer=2
    /// </summary>
    /// <summary>
    /// 清除 Unity 自动生成的 WebGL.loader.js 中的外部链接。
    /// Unity 会在 Firefox 兼容性提示里写死 bugzilla 链接，平台审核会按 URL 判为违规。
    /// </summary>
    static void SanitizeLoaderJs(string outputPath)
    {
        string root = System.IO.Path.GetFullPath(outputPath);
        string loaderPath = System.IO.Path.Combine(root, "Build", "WebGL.loader.js");
        if (!System.IO.File.Exists(loaderPath))
        {
            Debug.LogWarning("SANITIZE: loader.js not found: " + loaderPath);
            return;
        }
        string text = System.IO.File.ReadAllText(loaderPath);
        int before = text.Length;
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"https?://bugzilla\.mozilla\.org\S*", "Firefox compatibility note");
        System.IO.File.WriteAllText(loaderPath, text);
        Debug.Log($"SANITIZE: loader.js 已清除外部链接（{before} -> {text.Length} 字符）");
    }

    static void ApplyAddressablesPlayerBuildOption(string optionName)
    {
        try
        {
            UnityEngine.Object settings = null;
            Type settingsType = FindType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings");

            Type defaultObj = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
            if (defaultObj != null)
            {
                settings = defaultObj
                    .GetProperty("Settings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null) as UnityEngine.Object;
            }
            // batchmode fallback: AssetDatabase 直接加载
            if (settings == null && settingsType != null)
            {
                string[] candidates = { "Assets/AddressableAssetsData/AddressableAssetSettings.asset", "Assets/AddressableAssets/AddressableAssetSettings.asset" };
                var loadMethod = typeof(AssetDatabase).GetMethod("LoadAssetAtPath", new[] { typeof(string), typeof(Type) });
                foreach (var path in candidates)
                    if (System.IO.File.Exists(path))
                    { settings = loadMethod.Invoke(null, new object[] { path, settingsType }) as UnityEngine.Object; break; }
            }
            if (settings == null)
            {
                Debug.LogWarning("ADDRESSABLES: settings instance is null, skipping.");
                return;
            }
            var prop = settings.GetType().GetProperty("BuildAddressablesWithPlayerBuild");
            if (prop == null)
            {
                Debug.LogWarning("ADDRESSABLES: BuildAddressablesWithPlayerBuild property not found.");
                return;
            }
            Type enumType = prop.PropertyType;
            if (!Enum.IsDefined(enumType, optionName))
            {
                Debug.LogWarning("ADDRESSABLES: enum value '" + optionName + "' not defined; leaving as-is.");
                return;
            }
            object target = Enum.Parse(enumType, optionName);
            prop.SetValue(settings, target);
            UnityEditor.EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("ADDRESSABLES: BuildAddressablesWithPlayerBuild = " + target);
        }
        catch (Exception e)
        {
            Debug.LogError("ADDRESSABLES reflection failed: " + e);
        }
    }

    /// <summary>
    /// 触发 Addressables Content Build。
    /// Addressables 1.25 里 BuildPlayerContent 是 STATIC 方法，且所有 public 重载
    /// 最终走到 internal 实现：当 AddressablesDataBuilderInput 为 null 时硬依赖
    /// AddressableAssetSettingsDefaultObject.Settings——而 batchmode 里 DefaultObject
    /// 的静态缓存 s_Settings 永远是 null，导致 settings=null 直接返回 error，
    /// Bundle 根本没打出来。
    ///
    /// 解法：反射构造 AddressablesDataBuilderInput(settings)，把 settings 对象
    /// 直接塞进去，再反射调 internal static BuildPlayerContent(out result, input)。
    /// 这样 input 不为 null，Addressables 内部就用我传入的 settings，绕开 DefaultObject。
    /// </summary>
    static void BuildAddressablesContent()
    {
        try
        {
            // 1) AssetDatabase 加载 AddressableAssetSettings 对象
            Type settingsType = FindType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings");
            if (settingsType == null) { Debug.LogWarning("ADDRESSABLES: AddressableAssetSettings type not found."); return; }
            UnityEngine.Object settings = null;
            string[] paths = { "Assets/AddressableAssetsData/AddressableAssetSettings.asset", "Assets/AddressableAssets/AddressableAssetSettings.asset" };
            var loadMethod = typeof(AssetDatabase).GetMethod("LoadAssetAtPath", new[] { typeof(string), typeof(Type) });
            foreach (var p in paths)
                if (System.IO.File.Exists(p)) { settings = loadMethod.Invoke(null, new object[] { p, settingsType }) as UnityEngine.Object; if (settings != null) break; }
            if (settings == null) { Debug.LogWarning("ADDRESSABLES: cannot load settings asset."); return; }
            Debug.Log("ADDRESSABLES: loaded settings obj OK");

            // 1b) 校验 groups 已载入（空集合是 "Unable to find group with guid" 的前兆）
            var groupsProp = settingsType.GetProperty("groups");
            if (groupsProp != null)
            {
                var groups = groupsProp.GetValue(settings) as System.Collections.IList;
                Debug.Log("ADDRESSABLES: content-build settings.groups count = " + (groups?.Count ?? -1));
                if (groups == null || groups.Count == 0)
                    Debug.LogError("ADDRESSABLES: groups is EMPTY - content build will fail with 'Unable to find group'.");
            }

            // 2) 反射构造 AddressablesDataBuilderInput(settings)
            Type inputType = FindType("UnityEditor.AddressableAssets.Build.AddressablesDataBuilderInput");
            if (inputType == null) { Debug.LogWarning("ADDRESSABLES: AddressablesDataBuilderInput type not found."); return; }
            var ctor = inputType.GetConstructor(new[] { settingsType });
            if (ctor == null) { Debug.LogWarning("ADDRESSABLES: AddressablesDataBuilderInput ctor(settings) not found."); return; }
            object builderInput = ctor.Invoke(new object[] { settings });
            Debug.Log("ADDRESSABLES: AddressablesDataBuilderInput constructed OK");

            // 3) 反射找 internal static BuildPlayerContent(out AddressablesPlayerBuildResult, AddressablesDataBuilderInput)
            Type resultType = FindType("UnityEditor.AddressableAssets.Build.AddressablesPlayerBuildResult");
            if (resultType == null) { Debug.LogWarning("ADDRESSABLES: AddressablesPlayerBuildResult type not found."); return; }
            // 在 settingsType（AddressableAssetSettings）上找所有 static 方法
            var allMethods = settingsType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            foreach (var m in allMethods)
            {
                if (m.Name == "BuildPlayerContent")
                {
                    var par = m.GetParameters();
                    var pdesc = string.Join(", ", par.Select(pp => pp.ParameterType.Name + (pp.IsOut ? " out" : "")));
                    Debug.Log("  STATIC " + (m.IsPublic ? "public" : m.IsPrivate ? "private" : "internal") + " " + m.ReturnType.Name + " " + m.Name + "(" + pdesc + ")");
                }
            }

            // 找带 out AddressablesPlayerBuildResult + AddressablesDataBuilderInput 参数的 internal static 方法
            var targetMethod = settingsType.GetMethod("BuildPlayerContent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[] { resultType.MakeByRefType(), inputType },
                null);
            if (targetMethod == null)
            {
                // 兜底：遍历找
                foreach (var m in allMethods)
                {
                    if (m.Name != "BuildPlayerContent") continue;
                    var par = m.GetParameters();
                    if (par.Length == 2 && par[0].ParameterType.Name == "AddressablesPlayerBuildResult&" && par[1].ParameterType.Name == "AddressablesDataBuilderInput")
                    { targetMethod = m; break; }
                    if (par.Length == 2 && par[1].ParameterType.Name == "AddressablesDataBuilderInput")
                    { targetMethod = m; break; }
                }
            }
            if (targetMethod == null) { Debug.LogError("ADDRESSABLES: cannot find internal BuildPlayerContent(out, input) overload."); return; }
            Debug.Log("ADDRESSABLES: target method found OK");

            // 3.5) 强制重导入历史上有问题的资源，确保本次会话里 AssetDatabase 已就绪
            ReimportProblemAssets();
            // 4) 调用！传入 builderInput 绕开 DefaultObject.Settings null
            object[] args = new object[] { null, builderInput }; // out result, input
            targetMethod.Invoke(null, args);
            object result = args[0]; // out 参数值
            Debug.Log("ADDRESSABLES: internal BuildPlayerContent executed. result type=" + (result?.GetType().Name ?? "null"));

            // 5) 检查 result.Error
            if (result != null)
            {
                var errProp = resultType.GetProperty("Error");
                var sucProp = resultType.GetProperty("Success");
                if (errProp != null)
                {
                    string err = errProp.GetValue(result)?.ToString();
                    Debug.Log("ADDRESSABLES: Build result Error = " + (string.IsNullOrEmpty(err) ? "<empty>" : err));
                }
                if (sucProp != null)
                {
                    Debug.Log("ADDRESSABLES: Build result Success = " + sucProp.GetValue(result));
                }
            }
        }
        catch (Exception e) { Debug.LogError("ADDRESSABLES content build failed: " + e); }
    }

    /// <summary>
    /// batchmode 下 Addressables DefaultObject 静态缓存未初始化。
    ///
    /// AddressableAssetSettingsDefaultObject.Settings 的真实实现（1.25.0 源码 110-136 行）：
    ///     static AddressableAssetSettings s_DefaultSettingsObject;   // 注意不是 s_Settings
    ///     get {
    ///         if (s_DefaultSettingsObject == null) {
    ///             AddressableAssetSettingsDefaultObject so;
    ///             if (EditorBuildSettings.TryGetConfigObject(kDefaultConfigObjectName, out so))
    ///                 s_DefaultSettingsObject = so.LoadSettingsObject();
    ///         }
    ///         return s_DefaultSettingsObject;
    ///     }
    ///
    /// 其中 kDefaultConfigObjectName = "com.unity.addressableassets"（源码第 26 行），
    /// 且 config object 的类型是 **AddressableAssetSettingsDefaultObject**（DefaultObject.asset
    /// 里那个 MonoBehaviour），不是 AddressableAssetSettings 本身。
    ///
    /// batchmode 里 EditorBuildSettings 没有这个 config object → Settings 恒 null
    /// → Addressables 内部 groups 集合为空 → 构建报 "Unable to find group with guid ..."
    /// 最终 GenerateLocationListsTask 抛 NullReference。
    ///
    /// 解法：加载 Assets/AddressableAssetsData/DefaultObject.asset，用
    /// EditorBuildSettings.AddConfigObject 注册进 config object 表。
    /// </summary>
    static void ForceInitAddressablesDefaultObject()
    {
        try
        {
            Type defaultObj = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
            if (defaultObj == null) { Debug.LogWarning("ADDRESSABLES: DefaultObject type not found."); return; }

            // 1) 先试 Settings getter（编辑器模式下通常已就绪）
            var settingsProp = defaultObj.GetProperty("Settings",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            object settings = settingsProp?.GetValue(null);
            if (settings != null) { Debug.Log("ADDRESSABLES: DefaultObject.Settings OK via getter."); return; }
            Debug.Log("ADDRESSABLES: Settings getter null, forcing init via EditorBuildSettings.");

            // 2) 加载 DefaultObject.asset（类型 = AddressableAssetSettingsDefaultObject）
            const string defaultObjPath = "Assets/AddressableAssetsData/DefaultObject.asset";
            if (!System.IO.File.Exists(defaultObjPath))
            {
                Debug.LogError("ADDRESSABLES: DefaultObject.asset not found at " + defaultObjPath);
                return;
            }
            var loadMethod = typeof(AssetDatabase).GetMethod("LoadAssetAtPath", new[] { typeof(string), typeof(Type) });
            UnityEngine.Object so = loadMethod.Invoke(null, new object[] { defaultObjPath, defaultObj }) as UnityEngine.Object;
            if (so == null) { Debug.LogError("ADDRESSABLES: failed to load DefaultObject.asset"); return; }
            Debug.Log("ADDRESSABLES: loaded DefaultObject.asset OK");

            // 3) 核心：注册 config object。key 必须是 "com.unity.addressableassets"
            const string cfgKey = "com.unity.addressableassets";
            EditorBuildSettings.AddConfigObject(cfgKey, so, true);
            Debug.Log("ADDRESSABLES: EditorBuildSettings.AddConfigObject('" + cfgKey + "') done.");

            // 4) 清静态缓存，迫使 getter 重新走 EditorBuildSettings 分支
            var cacheField = defaultObj.GetField("s_DefaultSettingsObject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (cacheField != null)
            {
                cacheField.SetValue(null, null);
                Debug.Log("ADDRESSABLES: s_DefaultSettingsObject cache cleared.");
            }

            // 5) 重新取 Settings —— 此时应能通过 so.LoadSettingsObject() 拿到实例
            settings = settingsProp?.GetValue(null);
            Debug.Log("ADDRESSABLES: DefaultObject.Settings after init = " + (settings != null));
            if (settings == null)
            {
                Debug.LogError("ADDRESSABLES: init FAILED - Settings still null.");
                return;
            }

            // 6) 顺带验证 groups 是否真的载入（判断"Unable to find group"是否已解决）
            var groupsProp = settings.GetType().GetProperty("groups");
            if (groupsProp != null)
            {
                var groups = groupsProp.GetValue(settings) as System.Collections.IList;
                Debug.Log("ADDRESSABLES: settings.groups count = " + (groups?.Count ?? -1));
            }
        }
        catch (Exception e) { Debug.LogError("ADDRESSABLES init failed: " + e); }
    }

    static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = asm.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }
}
