using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class ResidentSceneMembership
    {
        internal static Scene[] LoadedScenes()
        {
            var scenes = new List<Scene>();
            for (var i = 0; i < UnitySceneManager.sceneCount; i++) {
                var scene = UnitySceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded) scenes.Add(scene);
            }
            // Unity omits its persistent scene from GetSceneAt. The loader's
            // existing persistent owner supplies the actual handle without
            // creating an object or walking the engine's asset registry.
            var manager = GameManager.UnsafeInstance;
            if (!ResidentObjectDirectory.IsLive(manager)) throw new InvalidDataException("Resident scene loader is absent.");
            var persistent = manager.gameObject.scene;
            if (!persistent.IsValid() || !persistent.isLoaded || persistent.name != "DontDestroyOnLoad")
                throw new InvalidDataException("Scene loader has no live persistent scene.");
            if (!scenes.Contains(persistent)) scenes.Add(persistent);
            return scenes.ToArray();
        }

        internal static GameObject[] LoadedGameObjects()
        {
            var owners = new List<GameObject>();
            foreach (var scene in LoadedScenes())
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                        owners.Add(transform.gameObject);
            return owners.ToArray();
        }
    }
}
