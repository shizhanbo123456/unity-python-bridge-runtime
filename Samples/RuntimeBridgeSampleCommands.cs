// RuntimeBridgeSampleCommands.cs —— 示例命令，演示怎么把游戏自己的函数暴露给 Python。
//
// 这个文件只是示例，确认跑通后可以直接删掉——桥本身不依赖它。
// 真实项目里把命令写在自己的模块下即可，不需要改桥的任何代码。

using System;
using System.Collections.Generic;
using UnityRuntimeBridge;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityRuntimeBridge.Samples
{
    /// <summary>示例命令集（命名空间和命令名都带 sample. 前缀，方便与真实命令区分）。</summary>
    public static class RuntimeBridgeSampleCommands
    {
        private const int MaxWalkDepth = 32;

        /// <summary>读取游戏状态：不需要参数的命令长这样。</summary>
        [RuntimeCommand("sample.scene_info", "读取当前场景与运行状态（示例）", ReadOnly = true)]
        public static object SceneInfo(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            Scene scene = SceneManager.GetActiveScene();
            return new
            {
                scene = scene.name,
                buildIndex = scene.buildIndex,
                rootCount = scene.rootCount,
                frameCount = Time.frameCount,
                time = Time.time,
                timeScale = Time.timeScale,
                screen = new { width = Screen.width, height = Screen.height },
                isPlaying = Application.isPlaying
            };
        }

        /// <summary>带参数且带过滤条件的只读命令。</summary>
        [RuntimeCommand("sample.list_objects", "列出场景物体路径（args.nameFilter 子串过滤，args.limit 限制条数）", ReadOnly = true)]
        public static object ListObjects(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            string filter = args.GetString("nameFilter", "");
            int limit = Mathf.Clamp(args.GetInt("limit", 50), 1, 500);

            var found = new List<object>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (found.Count >= limit) break;
                Walk(root.transform, "", filter, 0, limit, found);
            }

            return new
            {
                filter = filter.Length > 0 ? filter : null,
                count = found.Count,
                objects = found
            };
        }

        /// <summary>修改游戏状态：直接写场景里的东西。</summary>
        [RuntimeCommand("sample.set_position", "按名字把场景物体移到指定位置（args.name，args.position）")]
        public static object SetPosition(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            string name = args.GetString("name");
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("缺少必填参数 name");
            }

            Vector3 position = args.GetVector3("position", Vector3.zero);
            GameObject target = FindByName(name);
            if (target == null)
            {
                throw new ArgumentException($"场景里找不到名为 \"{name}\" 的物体");
            }

            target.transform.position = position;
            return new
            {
                name = target.name,
                position = target.transform.position
            };
        }

        /// <summary>创建物体：演示颜色这类 Unity 值的读取（支持 "#RRGGBB" 与 [r,g,b,a]）。</summary>
        [RuntimeCommand("sample.spawn_cube", "生成一个立方体（args.name，args.position，args.color）")]
        public static object SpawnCube(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            string name = args.GetString("name", "Cube");
            Vector3 position = args.GetVector3("position", Vector3.zero);
            Color color = args.GetColor("color", Color.white);

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;

            Renderer renderer = cube.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }

            ctx.Log($"已生成 {name} @ {position}");
            return new
            {
                spawned = true,
                name = cube.name,
                position = cube.transform.position,
                color = color
            };
        }

        /// <summary>返回多个相机：演示返回数组/集合。</summary>
        [RuntimeCommand("sample.list_cameras", "列出场景里的所有相机（示例）", ReadOnly = true)]
        public static object ListCameras(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            var cameras = new List<object>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                CollectCameras(root.transform, "", cameras);
            }

            return new
            {
                count = cameras.Count,
                cameras = cameras
            };
        }

        private static void Walk(Transform current, string parentPath, string filter, int depth, int limit, List<object> found)
        {
            if (depth > MaxWalkDepth || found.Count >= limit) return;

            string path = parentPath.Length == 0 ? current.name : parentPath + "/" + current.name;
            if (filter.Length == 0 || path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found.Add(new
                {
                    path = path,
                    active = current.gameObject.activeSelf,
                    position = current.position
                });
            }

            for (int i = 0; i < current.childCount; i++)
            {
                Walk(current.GetChild(i), path, filter, depth + 1, limit, found);
            }
        }

        private static void CollectCameras(Transform current, string parentPath, List<object> result)
        {
            string path = parentPath.Length == 0 ? current.name : parentPath + "/" + current.name;

            var camera = current.GetComponent<Camera>();
            if (camera != null)
            {
                result.Add(new
                {
                    path = path,
                    enabled = camera.enabled,
                    fieldOfView = camera.fieldOfView,
                    position = current.position,
                    depth = camera.depth
                });
            }

            for (int i = 0; i < current.childCount; i++)
            {
                CollectCameras(current.GetChild(i), path, result);
            }
        }

        private static GameObject FindByName(string name)
        {
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                GameObject found = FindByName(root.transform, name, 0);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindByName(Transform current, string name, int depth)
        {
            if (depth > MaxWalkDepth) return null;
            if (current.name == name) return current.gameObject;

            for (int i = 0; i < current.childCount; i++)
            {
                GameObject found = FindByName(current.GetChild(i), name, depth + 1);
                if (found != null) return found;
            }
            return null;
        }
    }
}
