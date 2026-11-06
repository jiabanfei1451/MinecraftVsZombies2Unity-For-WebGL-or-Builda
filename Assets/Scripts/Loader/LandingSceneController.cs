using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MVZ2.Level;
using MVZ2.Modding;
using MVZ2.OldSave;
using MVZ2.Saves;
using MVZ2.Vanilla;
using MVZ2Logic;
using MVZ2Logic.Games;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace MVZ2
{
    public class LandingSceneController : MonoBehaviour
    {
        void Start()
        {
            // ===== Builda 宿主契约：ready 必须是游戏最开始的第一件事 =====
            // 页面加载 30 秒内宿主没收到 ready 会强制关闭游戏（表现为"无法试玩"）。
            // 不能等资源加载完再调；非 Builda 宿主下 SDK 不存在，静默忽略。
            try
            {
                Builda.BuildaSDK.RuntimeReady(r =>
                {
                    if (!r.Ok)
                        Debug.LogWarning("Builda RuntimeReady failed: " + (r.Error != null ? r.Error.Code + " " + r.Error.Message : "unknown"));
                });
            }
            catch (System.Exception e)
            {
                Debug.Log("Builda SDK not available: " + e.Message);
            }

            OldSaveDataManager.SetSaveDataImporter(new OldSaveDataImporter());
            ModManager.OnRegisterModInfos += OnRegisterModInfos;
            Addressables.LoadSceneAsync("Main", LoadSceneMode.Single);
        }
        private async Task OnRegisterModInfos(IGlobalGame game, List<ModInfo> results)
        {
            var logic = new VanillaMod();
            var assemblies = new Assembly[] { Assembly.GetAssembly(typeof(VanillaMod)), Assembly.GetAssembly(typeof(LogicMain)) };
            var locator = await Addressables.InitializeAsync().Task;
            results.Add(new ModInfo(logic.Namespace, locator)
            {
                LevelDataVersion = LevelManager.CURRENT_DATA_VERSION,
                DisplayName = "Vanilla",
                CatalogPath = null,
                IsBuiltin = true,
                Logic = logic,
                Assemblies = assemblies
            });
        }
    }
}
