// Yorimashi Built-in Tools — M3
// 已有 tool：
//   echo                        - 回显 params，冒烟测试用
//   scene/create_cube           - 在当前场景创建一个 Cube (可选 name/position)
//   scene/list_root_gameobjects - 列当前场景所有 root GameObject 名字
//   unity/read_project_context  - 一次性汇报环境（Unity 版本/项目/场景/包/avatar 列表）
//
// M3-T2 新增 hierarchy 只读三件套（零风险，不改场景）：
//   hierarchy/get_children      - 列指定 path 下的直接子 GO
//   hierarchy/find_by_name      - 按名字模糊/精确查找 GO
//   hierarchy/get_active        - 单个 GO 详情 + 组件类型名单
//
// 全部在主线程执行（YorimashiToolRegistry.Pump 已保证）。
//
// Sentinel: "M3-TOOL2 TOOLS"
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
// C# alias 避开 UnityEditor.PackageInfo (老 AssetStore API) 跟
// UnityEditor.PackageManager.PackageInfo (UPM API) 的重名冲突。
// error CS0104: 'PackageInfo' is an ambiguous reference — 2026-07-14
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Yorimashi.Modder.Editor
{
    internal static class YorimashiTools
    {
        public const string SentinelTag = "M3-TOOL1 TOOLS";

        public static void RegisterAll()
        {
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "echo",
                    description = "Echo the params back. Sanity check.",
                    inputSchemaJson = "{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}}}",
                },
                Echo);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "scene/create_cube",
                    description = "Create a Cube primitive in the active scene at optional position.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"properties\":{" +
                        "\"name\":{\"type\":\"string\",\"default\":\"YorimashiCube\"}," +
                        "\"x\":{\"type\":\"number\",\"default\":0}," +
                        "\"y\":{\"type\":\"number\",\"default\":0}," +
                        "\"z\":{\"type\":\"number\",\"default\":0}" +
                        "}}",
                },
                CreateCube);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "scene/list_root_gameobjects",
                    description = "List names of all root GameObjects in the active scene.",
                    inputSchemaJson = "{\"type\":\"object\"}",
                },
                ListRootGameObjects);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "unity/read_project_context",
                    description = "Return a full snapshot of the current environment: Unity version, project path, active scene, installed VRChat-related packages, avatars (GameObjects carrying VRCAvatarDescriptor), and asset roots. Read-only. Call this on session start.",
                    inputSchemaJson = "{\"type\":\"object\"}",
                },
                ReadProjectContext);

            // ---- M3-T2 hierarchy 只读三件套 -----------------------------------

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "hierarchy/get_children",
                    description = "List direct children of a GameObject specified by hierarchy path (e.g. 'Shinano/Armature/Hips'). Read-only. Returns names + activeInHierarchy + hasChildren + component type name list.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\",\"description\":\"slash-delimited hierarchy path from scene root\"}" +
                        "}}",
                },
                HierarchyGetChildren);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "hierarchy/find_by_name",
                    description = "Search active scene for GameObjects whose name matches the query. Read-only. Supports 'exact' (default) or 'contains' mode. Returns array of full paths. Capped at maxResults (default 200; complex avatars like Ramune-class routinely have 20+ Armature matches).",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"query\"],\"properties\":{" +
                        "\"query\":{\"type\":\"string\"}," +
                        "\"mode\":{\"type\":\"string\",\"enum\":[\"exact\",\"contains\"],\"default\":\"exact\"}," +
                        "\"maxResults\":{\"type\":\"integer\",\"default\":200}," +
                        "\"includeInactive\":{\"type\":\"boolean\",\"default\":true}" +
                        "}}",
                },
                HierarchyFindByName);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "hierarchy/get_active",
                    description = "Full details for a single GameObject specified by path: active/activeInHierarchy/tag/layer/transform (pos/rot/scale, local + world) + list of Component type names (enabled state where applicable). Read-only.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}" +
                        "}}",
                },
                HierarchyGetActive);

            // ---- M3-T3 更细粒度 tool (component / blendshape / transform) --------

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "hierarchy/get_transform",
                    description = "Return only the transform values (local + world position/rotation/scale) for a GameObject at path. Read-only. Cheaper than hierarchy/get_active when only spatial info is needed.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}" +
                        "}}",
                },
                HierarchyGetTransform);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "component/list",
                    description = "List every Component on a GameObject: full type name, enabled state (null for Transform / non-Behaviour), and component index. Read-only. Includes MissingScript entries so agents can detect broken references.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}" +
                        "}}",
                },
                ComponentList);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "blendshape/list",
                    description = "List all blendshapes on a SkinnedMeshRenderer at path: name + current weight (0-100). Optionally filter by nameContains substring. Read-only. Returns empty array with reason='no_smr' if path has no SkinnedMeshRenderer.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"nameContains\":{\"type\":\"string\"}," +
                        "\"maxResults\":{\"type\":\"integer\",\"default\":500}" +
                        "}}",
                },
                BlendshapeList);

            // ---- M3-B1 asset/find (只读, VRChat 改模最常用) --------
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "asset/find",
                    description = "Search Project assets by name pattern and optional type filter. Uses AssetDatabase.FindAssets under the hood. Returns list of {guid, path, type, name}. Read-only. Common types: Prefab, Material, AnimationClip, AnimatorController, Texture2D, ScriptableObject, SkinnedMeshRenderer's Mesh via AvatarMask/Mesh.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"query\"],\"properties\":{" +
                        "\"query\":{\"type\":\"string\",\"description\":\"AssetDatabase.FindAssets filter (e.g. 'Ramune', 't:Prefab shirt', 'l:LabelName')\"}," +
                        "\"typeFilter\":{\"type\":\"string\",\"description\":\"Optional Unity type name to further filter results (e.g. 'Prefab', 'Material', 'AnimationClip')\"}," +
                        "\"maxResults\":{\"type\":\"integer\",\"default\":50}," +
                        "\"searchInFolders\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Optional list of asset folders to restrict search (e.g. ['Assets/Ramune'])\"}" +
                        "}}",
                },
                AssetFind);

            // ---- M3-W1-D 假改动 tool（跑通 dry_run + oplog 全链路） --------
            var debugTool = new DebugSetTestValueTool();
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = debugTool.ToolName,
                    description = "M3-W1-D scaffold write tool: sets EditorPrefs['" + DebugSetTestValueTool.PrefKey + "'] to a new integer. Nothing else changes. Use to smoke-test dry_run + oplog before real write tools ship. Requires 'value' (int); defaults to dry_run=true.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"value\"],\"properties\":{" +
                        "\"value\":{\"type\":\"integer\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                        "}}",
                },
                debugTool.Execute);

            // ---- M3-W1-E 第一个真改动 tool: blendshape/set --------
            var bsTool = new BlendshapeSetTool();
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = bsTool.ToolName,
                    description = "Set a blendshape weight (0-100) on a SkinnedMeshRenderer at path. Real write tool: dry_run=true (default) returns preview only; dry_run=false persists via Undo.RecordObject + SetBlendShapeWeight + full backup snapshot of all weights on the SMR. Scope guard requires path to start with Ramune_test/, WriteTest/, or Yorimashi_WriteTest/ for apply. dry_run allowed anywhere.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\",\"blendshape\",\"value\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"blendshape\":{\"type\":\"string\"}," +
                        "\"value\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                        "}}",
                },
                bsTool.Execute);

            // ---- M3-B2 hierarchy/set_active (真改动 tool) --------
            var setActiveTool = new HierarchySetActiveTool();
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = setActiveTool.ToolName,
                    description = "Set GameObject.SetActive(true/false) at path. Real write tool: dry_run=true (default) returns preview; dry_run=false persists via Undo.RecordObject + SetActive. Scope guard requires path to start with Ramune_test/, WriteTest/, or Yorimashi_WriteTest/ for apply.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\",\"active\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"active\":{\"type\":\"boolean\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                        "}}",
                },
                setActiveTool.Execute);

            // ---- M3-B3 blendshape/set_batch (真改动 tool, 批量) --------
            var batchTool = new BlendshapeSetBatchTool();
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = batchTool.ToolName,
                    description = "Set multiple blendshape weights in a single call. Batches reduce wss round-trips and share a single Undo group (one Ctrl+Z reverts the whole batch). Each item in 'changes' must independently pass scope guard (path prefix Ramune_test/ etc.). Max 100 items per batch.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"changes\"],\"properties\":{" +
                        "\"changes\":{\"type\":\"array\",\"maxItems\":100,\"items\":{" +
                        "\"type\":\"object\",\"required\":[\"path\",\"blendshape\",\"value\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"blendshape\":{\"type\":\"string\"}," +
                        "\"value\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}" +
                        "}}}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                        "}}",
                },
                batchTool.Execute);

            // ---- M3-C 只读 tool (component 深读 + material lilToon 参数) --------
            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "component/get_properties",
                    description = "Read serialized properties of one Component on a GameObject. Read-only. Uses SerializedObject to enumerate top-level property paths and their values (int/float/bool/string/enum/color/vector/object reference name). Choose the target Component either by 0-based `index` (matches component/list order) or by `typeName` (short name like 'SkinnedMeshRenderer' or fully qualified 'UnityEngine.SkinnedMeshRenderer'). Skips deep nested arrays past `maxProperties` (default 100) to keep payload small.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"index\":{\"type\":\"integer\",\"description\":\"0-based component index; omit if typeName given\"}," +
                        "\"typeName\":{\"type\":\"string\",\"description\":\"short or fully-qualified type name of the component; omit if index given\"}," +
                        "\"maxProperties\":{\"type\":\"integer\",\"default\":100}" +
                        "}}",
                },
                ComponentGetProperties);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "liltoon/read_material",
                    description = "Read lilToon material properties on a SkinnedMeshRenderer / Renderer at path. Read-only. Returns sharedMaterials list with each material's assetPath, shader name, and common lilToon properties: _Color, _MainTex, _Cutoff, _LightMinLimit, _LightMaxLimit, _AsUnlit, _UseDissolve, _DissolveMask, _Alpha and a small set of dissolve-related fields. Materials with non-lilToon shaders return shader name only. Use materialIndex to inspect a single slot; omit to enumerate all.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"materialIndex\":{\"type\":\"integer\",\"description\":\"0-based index into renderer.sharedMaterials; omit to return all slots\"}" +
                        "}}",
                },
                LiltoonReadMaterial);

            // ---- M3-E 只读扩容 (console/read find_in_file unity_reflect menu/execute packages/list) ----

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "console/read",
                    description = "Read recent Unity Editor console messages (Log/Warning/Error). Preserves multi-line body. Read-only. Use limit (default 20, max 200) to cap results, level to filter ('log'|'warning'|'error'|'all'), and contains for substring match on message.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"properties\":{" +
                        "\"limit\":{\"type\":\"integer\",\"default\":20,\"maximum\":200}," +
                        "\"level\":{\"type\":\"string\",\"enum\":[\"log\",\"warning\",\"error\",\"all\"],\"default\":\"all\"}," +
                        "\"contains\":{\"type\":\"string\"}" +
                        "}}",
                },
                ConsoleRead);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "console/clear",
                    description = "Clear Unity Editor console. Returns previous entry count. Idempotent.",
                    inputSchemaJson = "{\"type\":\"object\"}",
                },
                ConsoleClear);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "script/find_in_file",
                    description = "Search inside a project text file by regex or plain substring. Returns matching line numbers + context snippets. Read-only, no side effects. Path must be under Assets/ or Packages/. Max 200 matches, max 5MB file.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"path\",\"pattern\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\",\"description\":\"project-relative path, e.g. 'Assets/Foo/Bar.cs'\"}," +
                        "\"pattern\":{\"type\":\"string\",\"description\":\"regex or plain substring per isRegex\"}," +
                        "\"isRegex\":{\"type\":\"boolean\",\"default\":false}," +
                        "\"ignoreCase\":{\"type\":\"boolean\",\"default\":false}," +
                        "\"maxMatches\":{\"type\":\"integer\",\"default\":50,\"maximum\":200}," +
                        "\"context\":{\"type\":\"integer\",\"default\":0,\"maximum\":5,\"description\":\"lines of context around each match\"}" +
                        "}}",
                },
                ScriptFindInFile);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "unity/reflect_type",
                    description = "Reflect on a .NET type available in the Editor: list its public fields, properties, methods with signatures. Useful to answer 'does XX component have YY field'. Read-only. Type lookup via short name (search loaded assemblies) or fully qualified name. Max 200 members.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"typeName\"],\"properties\":{" +
                        "\"typeName\":{\"type\":\"string\",\"description\":\"short (e.g. 'SkinnedMeshRenderer') or fully qualified (e.g. 'nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator')\"}," +
                        "\"members\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"fields\",\"properties\",\"methods\"]},\"description\":\"which member kinds to include; default all three\"}," +
                        "\"includeInherited\":{\"type\":\"boolean\",\"default\":true}," +
                        "\"includeNonPublic\":{\"type\":\"boolean\",\"default\":false}" +
                        "}}",
                },
                UnityReflectType);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "menu/execute",
                    description = "Execute a whitelisted Unity Editor menu item by full path. Only safe non-destructive menu items are allowed (see whitelist prefixes). Read-only actions like 'Assets/Refresh' and repaint operations. Blocked: File/*, Edit/Preferences, GameObject/Delete, anything that opens dialogs.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"required\":[\"menuPath\"],\"properties\":{" +
                        "\"menuPath\":{\"type\":\"string\",\"description\":\"e.g. 'Assets/Refresh' or 'Tools/Something'\"}" +
                        "}}",
                },
                MenuExecute);

            YorimashiToolRegistry.Register(
                new ToolInfo
                {
                    name = "packages/list",
                    description = "List UPM packages currently installed in the project: name, version, source (Registry/Git/Local/Embedded/Builtin), displayName, and a `relevance` flag indicating avatar-relevant packages (VRChat SDK / ModularAvatar / lilToon / etc.). Read-only. Uses UnityEditor.PackageManager.Client.List which is async, so this call may take 1-3s the first time.",
                    inputSchemaJson =
                        "{\"type\":\"object\",\"properties\":{" +
                        "\"onlyRelevant\":{\"type\":\"boolean\",\"default\":false,\"description\":\"if true, filter out non-avatar-related packages (Unity built-ins etc.)\"}" +
                        "}}",
                },
                PackagesList);
        }

        // ---- handlers -------------------------------------------------------

        private static string Echo(string paramsJson)
        {
            // Just wrap params into result.
            var msg = ExtractString(paramsJson, "message") ?? "";
            var sb = new StringBuilder();
            sb.Append("{\"echoed\":").Append(YorimashiEnvelope.EncodeString(msg));
            sb.Append(",\"receivedAt\":").Append(YorimashiEnvelope.EncodeString(System.DateTime.Now.ToString("HH:mm:ss.fff")));
            sb.Append("}");
            return sb.ToString();
        }

        private static string CreateCube(string paramsJson)
        {
            var name = ExtractString(paramsJson, "name") ?? "YorimashiCube";
            var x = (float)(ExtractNumber(paramsJson, "x") ?? 0.0);
            var y = (float)(ExtractNumber(paramsJson, "y") ?? 0.0);
            var z = (float)(ExtractNumber(paramsJson, "z") ?? 0.0);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = new Vector3(x, y, z);

            // Mark scene dirty so it can be saved.
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);

            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(cube.name));
            sb.Append(",\"instanceID\":").Append(cube.GetInstanceID());
            sb.Append(",\"position\":{\"x\":").Append(x).Append(",\"y\":").Append(y).Append(",\"z\":").Append(z).Append("}");
            sb.Append(",\"scene\":").Append(YorimashiEnvelope.EncodeString(scene.name));
            sb.Append("}");
            return sb.ToString();
        }

        private static string ListRootGameObjects(string paramsJson)
        {
            var scene = EditorSceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var sb = new StringBuilder();
            sb.Append("{\"scene\":").Append(YorimashiEnvelope.EncodeString(scene.name));
            sb.Append(",\"count\":").Append(roots.Length);
            sb.Append(",\"names\":[");
            for (int i = 0; i < roots.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(YorimashiEnvelope.EncodeString(roots[i].name));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- unity/read_project_context -----------------------------------

        // Names of "modding-relevant" package prefixes we surface separately so
        // Hermes can route decisions (e.g. "user has NDMF, safe to call ndmf/*").
        // Any package matching these prefixes shows up in the packagesRelevant list.
        private static readonly string[] RelevantPackagePrefixes = new[]
        {
            "com.vrchat.",             // VRChat SDK Avatars / Base / Worlds
            "nadena.dev.modular-avatar", // Modular Avatar
            "nadena.dev.ndmf",           // Non-Destructive Modular Framework
            "com.anatawa12.avatar-optimizer", // AAO
            "jp.lilxyzw.liltoon",        // lilToon
            "com.coplaydev.unity-mcp",   // CoplayDev MCP (competing/parallel tool)
            "com.suzuryg.face-emo",      // FaceEmo
            "com.vrcfury.",              // VRCFury
        };

        private static string ReadProjectContext(string paramsJson)
        {
            var sb = new StringBuilder();
            sb.Append('{');

            // --- Unity + project ------------------------------------------------
            sb.Append("\"unity\":{");
            sb.Append("\"version\":").Append(YorimashiEnvelope.EncodeString(Application.unityVersion));
            sb.Append(",\"platform\":").Append(YorimashiEnvelope.EncodeString(Application.platform.ToString()));
            sb.Append(",\"projectPath\":").Append(YorimashiEnvelope.EncodeString(
                System.IO.Path.GetDirectoryName(Application.dataPath)));
            sb.Append(",\"projectName\":").Append(YorimashiEnvelope.EncodeString(
                Application.productName));
            sb.Append(",\"isPlaying\":").Append(Application.isPlaying ? "true" : "false");
            sb.Append(",\"assetPath\":").Append(YorimashiEnvelope.EncodeString(Application.dataPath));
            sb.Append('}');

            // --- Active scene ---------------------------------------------------
            var scene = EditorSceneManager.GetActiveScene();
            sb.Append(",\"activeScene\":{");
            sb.Append("\"name\":").Append(YorimashiEnvelope.EncodeString(scene.name));
            sb.Append(",\"path\":").Append(YorimashiEnvelope.EncodeString(scene.path));
            sb.Append(",\"isLoaded\":").Append(scene.isLoaded ? "true" : "false");
            sb.Append(",\"isDirty\":").Append(scene.isDirty ? "true" : "false");
            sb.Append(",\"rootCount\":").Append(scene.rootCount);
            sb.Append(",\"rootGameObjects\":[");
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(YorimashiEnvelope.EncodeString(roots[i].name));
            }
            sb.Append("]");
            sb.Append('}');

            // --- Loaded assemblies (probe for VRC SDK/NDMF/etc without hard-linking) ---
            var loadedAsms = AppDomain.CurrentDomain.GetAssemblies();
            var relevantAsms = new List<(string name, string version)>();
            foreach (var a in loadedAsms)
            {
                var n = a.GetName();
                var nm = n.Name;
                if (nm.StartsWith("VRC.", StringComparison.OrdinalIgnoreCase)
                    || nm.StartsWith("VRCSDK", StringComparison.OrdinalIgnoreCase)
                    || nm.StartsWith("nadena.", StringComparison.OrdinalIgnoreCase)
                    || nm.StartsWith("lilToon", StringComparison.OrdinalIgnoreCase)
                    || nm.StartsWith("Yorimashi", StringComparison.OrdinalIgnoreCase)
                    || nm.StartsWith("Anatawa12", StringComparison.OrdinalIgnoreCase))
                {
                    relevantAsms.Add((nm, n.Version?.ToString() ?? "?"));
                }
            }
            sb.Append(",\"loadedAssembliesRelevant\":[");
            for (int i = 0; i < relevantAsms.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(relevantAsms[i].name));
                sb.Append(",\"version\":").Append(YorimashiEnvelope.EncodeString(relevantAsms[i].version));
                sb.Append('}');
            }
            sb.Append("]");

            // --- Installed UPM packages ---------------------------------------
            //
            // UpmPackageInfo.GetAllRegisteredPackages() is synchronous, safe to call
            // from Editor thread. May return null if package manager isn't ready
            // yet (rare).
            sb.Append(",\"packagesRelevant\":[");
            string packagesError = null;
            try
            {
                var packages = UpmPackageInfo.GetAllRegisteredPackages();
                var written = 0;
                foreach (var pkg in packages)
                {
                    var name = pkg.name;
                    bool relevant = false;
                    foreach (var pref in RelevantPackagePrefixes)
                    {
                        if (name.StartsWith(pref, StringComparison.OrdinalIgnoreCase))
                        {
                            relevant = true;
                            break;
                        }
                    }
                    if (!relevant) continue;
                    if (written > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(name));
                    sb.Append(",\"version\":").Append(YorimashiEnvelope.EncodeString(pkg.version ?? ""));
                    sb.Append(",\"source\":").Append(YorimashiEnvelope.EncodeString(pkg.source.ToString()));
                    sb.Append(",\"displayName\":").Append(YorimashiEnvelope.EncodeString(pkg.displayName ?? ""));
                    sb.Append('}');
                    written++;
                }
            }
            catch (Exception e)
            {
                packagesError = e.GetType().Name + ": " + e.Message;
            }
            sb.Append("]");
            if (packagesError != null)
            {
                sb.Append(",\"packagesRelevantError\":").Append(YorimashiEnvelope.EncodeString(packagesError));
            }

            // --- Avatars (via VRCAvatarDescriptor reflection) ------------------
            //
            // We don't hard-link the VRC SDK assembly (packages/plugins install
            // order in UPM can compile Yorimashi.Modder.Editor before VRC SDK is
            // available). Look up the type by name from loaded assemblies.
            sb.Append(",\"avatars\":[");
            Type descriptorType = null;
            foreach (var a in loadedAsms)
            {
                var t = a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false);
                if (t != null) { descriptorType = t; break; }
            }
            if (descriptorType != null)
            {
                // Unity 2022.3 API: FindObjectsOfType(Type, bool includeInactive)
                var found = UnityEngine.Object.FindObjectsOfType(descriptorType, true);
                for (int i = 0; i < found.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var comp = found[i] as Component;
                    var go = comp != null ? comp.gameObject : null;
                    if (go == null) { sb.Append("null"); continue; }
                    var path = GetTransformPath(go.transform);
                    var smrCount = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                    var childCount = go.GetComponentsInChildren<Transform>(true).Length;
                    sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(go.name));
                    sb.Append(",\"path\":").Append(YorimashiEnvelope.EncodeString(path));
                    sb.Append(",\"active\":").Append(go.activeInHierarchy ? "true" : "false");
                    sb.Append(",\"instanceID\":").Append(go.GetInstanceID());
                    sb.Append(",\"skinnedMeshRendererCount\":").Append(smrCount);
                    sb.Append(",\"transformCount\":").Append(childCount);
                    sb.Append('}');
                }
            }
            sb.Append("]");
            sb.Append(",\"vrchatSdkPresent\":").Append(descriptorType != null ? "true" : "false");

            // --- Timestamp -----------------------------------------------------
            sb.Append(",\"snapshotAt\":").Append(YorimashiEnvelope.EncodeString(
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")));

            sb.Append('}');
            return sb.ToString();
        }

        // ---- M3-T2 hierarchy handlers -------------------------------------

        /// <summary>
        /// Resolve a slash-delimited hierarchy path to a GameObject in the active scene.
        /// Returns null if not found. The first segment matches a scene root by name.
        /// </summary>
        private static GameObject ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segments = path.Split('/');
            if (segments.Length == 0) return null;

            var scene = EditorSceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            GameObject current = null;
            foreach (var r in roots)
            {
                if (r.name == segments[0]) { current = r; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < segments.Length; i++)
            {
                Transform child = null;
                for (int c = 0; c < current.transform.childCount; c++)
                {
                    var t = current.transform.GetChild(c);
                    if (t.name == segments[i]) { child = t; break; }
                }
                if (child == null) return null;
                current = child.gameObject;
            }
            return current;
        }

        private static void AppendComponentTypeNames(StringBuilder sb, GameObject go)
        {
            var comps = go.GetComponents<Component>();
            sb.Append('[');
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var c = comps[i];
                if (c == null)
                {
                    // Missing script (broken reference) — surface it explicitly for agent.
                    sb.Append("{\"type\":\"MissingScript\",\"enabled\":null}");
                    continue;
                }
                var typeName = c.GetType().FullName;
                // Behaviour has enabled; Renderer has enabled; Transform has neither.
                object enabledValue = null;
                if (c is Behaviour bh) enabledValue = bh.enabled;
                else if (c is Renderer rd) enabledValue = rd.enabled;
                else if (c is Collider cl) enabledValue = cl.enabled;
                sb.Append("{\"type\":").Append(YorimashiEnvelope.EncodeString(typeName));
                sb.Append(",\"enabled\":");
                if (enabledValue == null) sb.Append("null");
                else sb.Append(((bool)enabledValue) ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static string HierarchyGetChildren(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));
            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false,\"children\":[]}");
                return sb.ToString();
            }
            sb.Append(",\"found\":true,\"children\":[");
            var tr = go.transform;
            for (int i = 0; i < tr.childCount; i++)
            {
                if (i > 0) sb.Append(',');
                var childGo = tr.GetChild(i).gameObject;
                sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(childGo.name));
                sb.Append(",\"activeInHierarchy\":").Append(childGo.activeInHierarchy ? "true" : "false");
                sb.Append(",\"activeSelf\":").Append(childGo.activeSelf ? "true" : "false");
                sb.Append(",\"hasChildren\":").Append(childGo.transform.childCount > 0 ? "true" : "false");
                sb.Append(",\"components\":");
                AppendComponentTypeNames(sb, childGo);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string HierarchyFindByName(string paramsJson)
        {
            var query = ExtractString(paramsJson, "query");
            var mode = ExtractString(paramsJson, "mode") ?? "exact";
            var maxResults = (int)(ExtractNumber(paramsJson, "maxResults") ?? 200.0);
            var includeInactive = ExtractBool(paramsJson, "includeInactive") ?? true;

            var sb = new StringBuilder();
            sb.Append("{\"query\":").Append(YorimashiEnvelope.EncodeString(query ?? ""));
            sb.Append(",\"mode\":").Append(YorimashiEnvelope.EncodeString(mode));

            if (string.IsNullOrEmpty(query))
            {
                sb.Append(",\"error\":\"empty query\",\"results\":[]}");
                return sb.ToString();
            }

            var scene = EditorSceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var results = new List<string>();
            bool truncated = false;
            foreach (var root in roots)
            {
                var allTs = root.GetComponentsInChildren<Transform>(includeInactive);
                foreach (var t in allTs)
                {
                    bool hit;
                    if (mode == "contains") hit = t.name.IndexOf(query, StringComparison.Ordinal) >= 0;
                    else hit = t.name == query;
                    if (!hit) continue;
                    if (results.Count >= maxResults) { truncated = true; break; }
                    results.Add(GetTransformPath(t));
                }
                if (truncated) break;
            }

            sb.Append(",\"count\":").Append(results.Count);
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(YorimashiEnvelope.EncodeString(results[i]));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string HierarchyGetActive(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));
            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false}");
                return sb.ToString();
            }
            sb.Append(",\"found\":true");
            sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(go.name));
            sb.Append(",\"activeSelf\":").Append(go.activeSelf ? "true" : "false");
            sb.Append(",\"activeInHierarchy\":").Append(go.activeInHierarchy ? "true" : "false");
            sb.Append(",\"tag\":").Append(YorimashiEnvelope.EncodeString(go.tag));
            sb.Append(",\"layer\":").Append(go.layer);
            sb.Append(",\"layerName\":").Append(YorimashiEnvelope.EncodeString(LayerMask.LayerToName(go.layer)));
            sb.Append(",\"instanceID\":").Append(go.GetInstanceID());
            sb.Append(",\"childCount\":").Append(go.transform.childCount);

            // transform local + world
            var lt = go.transform.localPosition;
            var lr = go.transform.localEulerAngles;
            var ls = go.transform.localScale;
            var wt = go.transform.position;
            var wr = go.transform.eulerAngles;
            sb.Append(",\"transform\":{\"local\":{");
            sb.Append("\"position\":{\"x\":").Append(lt.x).Append(",\"y\":").Append(lt.y).Append(",\"z\":").Append(lt.z).Append('}');
            sb.Append(",\"eulerAngles\":{\"x\":").Append(lr.x).Append(",\"y\":").Append(lr.y).Append(",\"z\":").Append(lr.z).Append('}');
            sb.Append(",\"scale\":{\"x\":").Append(ls.x).Append(",\"y\":").Append(ls.y).Append(",\"z\":").Append(ls.z).Append('}');
            sb.Append("},\"world\":{");
            sb.Append("\"position\":{\"x\":").Append(wt.x).Append(",\"y\":").Append(wt.y).Append(",\"z\":").Append(wt.z).Append('}');
            sb.Append(",\"eulerAngles\":{\"x\":").Append(wr.x).Append(",\"y\":").Append(wr.y).Append(",\"z\":").Append(wr.z).Append('}');
            sb.Append("}}");

            sb.Append(",\"components\":");
            AppendComponentTypeNames(sb, go);
            sb.Append('}');
            return sb.ToString();
        }

        // ---- M3-T3 handlers -----------------------------------------------

        private static void AppendTransformBlock(StringBuilder sb, Transform t)
        {
            var lt = t.localPosition;
            var lr = t.localEulerAngles;
            var ls = t.localScale;
            var wt = t.position;
            var wr = t.eulerAngles;
            sb.Append("\"local\":{");
            sb.Append("\"position\":{\"x\":").Append(lt.x).Append(",\"y\":").Append(lt.y).Append(",\"z\":").Append(lt.z).Append('}');
            sb.Append(",\"eulerAngles\":{\"x\":").Append(lr.x).Append(",\"y\":").Append(lr.y).Append(",\"z\":").Append(lr.z).Append('}');
            sb.Append(",\"scale\":{\"x\":").Append(ls.x).Append(",\"y\":").Append(ls.y).Append(",\"z\":").Append(ls.z).Append('}');
            sb.Append("},\"world\":{");
            sb.Append("\"position\":{\"x\":").Append(wt.x).Append(",\"y\":").Append(wt.y).Append(",\"z\":").Append(wt.z).Append('}');
            sb.Append(",\"eulerAngles\":{\"x\":").Append(wr.x).Append(",\"y\":").Append(wr.y).Append(",\"z\":").Append(wr.z).Append('}');
            sb.Append("}");
        }

        private static string HierarchyGetTransform(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));
            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false}");
                return sb.ToString();
            }
            sb.Append(",\"found\":true,\"transform\":{");
            AppendTransformBlock(sb, go.transform);
            sb.Append("}}");
            return sb.ToString();
        }

        private static string ComponentList(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));
            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false,\"components\":[]}");
                return sb.ToString();
            }
            sb.Append(",\"found\":true,\"components\":[");
            var comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var c = comps[i];
                if (c == null)
                {
                    sb.Append("{\"index\":").Append(i).Append(",\"type\":\"MissingScript\",\"enabled\":null}");
                    continue;
                }
                object enabledValue = null;
                if (c is Behaviour bh) enabledValue = bh.enabled;
                else if (c is Renderer rd) enabledValue = rd.enabled;
                else if (c is Collider cl) enabledValue = cl.enabled;
                sb.Append("{\"index\":").Append(i);
                sb.Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(c.GetType().FullName));
                sb.Append(",\"enabled\":");
                if (enabledValue == null) sb.Append("null");
                else sb.Append(((bool)enabledValue) ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],\"count\":").Append(comps.Length).Append('}');
            return sb.ToString();
        }

        private static string BlendshapeList(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var nameFilter = ExtractString(paramsJson, "nameContains");
            var maxResults = (int)(ExtractNumber(paramsJson, "maxResults") ?? 500.0);

            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));

            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false,\"blendshapes\":[]}");
                return sb.ToString();
            }
            sb.Append(",\"found\":true");

            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null)
            {
                sb.Append(",\"reason\":\"no_smr\",\"blendshapes\":[],\"count\":0}");
                return sb.ToString();
            }

            var mesh = smr.sharedMesh;
            int total = mesh.blendShapeCount;
            sb.Append(",\"totalOnMesh\":").Append(total);

            var results = new List<(string name, float weight, int index)>();
            bool truncated = false;
            for (int i = 0; i < total; i++)
            {
                var bname = mesh.GetBlendShapeName(i);
                if (!string.IsNullOrEmpty(nameFilter) && bname.IndexOf(nameFilter, StringComparison.Ordinal) < 0)
                    continue;
                if (results.Count >= maxResults) { truncated = true; break; }
                results.Add((bname, smr.GetBlendShapeWeight(i), i));
            }
            sb.Append(",\"count\":").Append(results.Count);
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"blendshapes\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(results[i].index);
                sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(results[i].name));
                sb.Append(",\"weight\":").Append(results[i].weight);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- M3-B1 asset/find handler ------------------------------------
        private static string AssetFind(string paramsJson)
        {
            var query = ExtractString(paramsJson, "query");
            var typeFilter = ExtractString(paramsJson, "typeFilter");
            var maxResults = (int)(ExtractNumber(paramsJson, "maxResults") ?? 50.0);
            var foldersJson = ExtractRawArray(paramsJson, "searchInFolders");

            if (string.IsNullOrEmpty(query))
            {
                return "{\"error\":\"missing required 'query' (string)\"}";
            }

            // typeFilter 走 Unity 原生 `t:Type` 语法拼到 query 前面
            // (AssetDatabase 的 filter 语法比 C# 侧 post-filter 更准确, e.g. Prefab 的主 type
            //  在 GetMainAssetTypeAtPath 里返回 GameObject, 用 C# 名字比对匹不上; 官方 t:Prefab 才行)
            string effectiveQuery = query;
            if (!string.IsNullOrEmpty(typeFilter) && !query.Contains("t:"))
            {
                effectiveQuery = "t:" + typeFilter + " " + query;
            }

            string[] searchFolders = null;
            if (!string.IsNullOrEmpty(foldersJson))
            {
                // 粗糙抽 JSON 数组里的字符串
                var folders = new List<string>();
                var re = new System.Text.RegularExpressions.Regex("\"([^\"]+)\"");
                foreach (System.Text.RegularExpressions.Match m in re.Matches(foldersJson))
                {
                    folders.Add(m.Groups[1].Value);
                }
                if (folders.Count > 0) searchFolders = folders.ToArray();
            }

            string[] guids;
            try
            {
                guids = searchFolders != null
                    ? AssetDatabase.FindAssets(effectiveQuery, searchFolders)
                    : AssetDatabase.FindAssets(effectiveQuery);
            }
            catch (Exception e)
            {
                return "{\"error\":" + YorimashiEnvelope.EncodeString("AssetDatabase.FindAssets failed: " + e.Message) + "}";
            }

            var sb = new StringBuilder();
            sb.Append("{\"query\":").Append(YorimashiEnvelope.EncodeString(query));
            sb.Append(",\"effectiveQuery\":").Append(YorimashiEnvelope.EncodeString(effectiveQuery));
            if (!string.IsNullOrEmpty(typeFilter))
                sb.Append(",\"typeFilter\":").Append(YorimashiEnvelope.EncodeString(typeFilter));
            sb.Append(",\"totalFound\":").Append(guids.Length);

            var results = new List<(string guid, string path, string typeName, string name)>();
            bool truncated = false;
            for (int i = 0; i < guids.Length; i++)
            {
                if (results.Count >= maxResults) { truncated = true; break; }
                var guid = guids[i];
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                var t = AssetDatabase.GetMainAssetTypeAtPath(path);
                var typeName = t != null ? t.Name : "Unknown";
                var assetName = System.IO.Path.GetFileNameWithoutExtension(path);
                results.Add((guid, path, typeName, assetName));
            }
            sb.Append(",\"count\":").Append(results.Count);
            sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            sb.Append(",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"guid\":").Append(YorimashiEnvelope.EncodeString(results[i].guid));
                sb.Append(",\"path\":").Append(YorimashiEnvelope.EncodeString(results[i].path));
                sb.Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(results[i].typeName));
                sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(results[i].name));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string GetTransformPath(Transform t)
        {
            var stack = new Stack<string>();
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", stack);
        }

        // ---- M3-C read-only handlers --------------------------------------

        /// <summary>
        /// component/get_properties — 用 SerializedObject 遍历一个 Component 的顶层属性。
        /// 只读；不 Apply()；不改任何东西。
        /// </summary>
        private static string ComponentGetProperties(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var indexOpt = ExtractNumber(paramsJson, "index");
            var typeName = ExtractString(paramsJson, "typeName");
            var maxProps = (int)(ExtractNumber(paramsJson, "maxProperties") ?? 100.0);

            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));

            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false,\"reason\":\"gameobject_not_found\"}");
                return sb.ToString();
            }
            var comps = go.GetComponents<Component>();
            Component target = null;
            int chosenIndex = -1;
            if (indexOpt.HasValue)
            {
                int idx = (int)indexOpt.Value;
                if (idx < 0 || idx >= comps.Length)
                {
                    sb.Append(",\"found\":false,\"reason\":\"index_out_of_range\",\"componentCount\":").Append(comps.Length).Append('}');
                    return sb.ToString();
                }
                target = comps[idx];
                chosenIndex = idx;
            }
            else if (!string.IsNullOrEmpty(typeName))
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) continue;
                    var full = comps[i].GetType().FullName;
                    var shortName = comps[i].GetType().Name;
                    if (full == typeName || shortName == typeName)
                    {
                        target = comps[i];
                        chosenIndex = i;
                        break;
                    }
                }
                if (target == null)
                {
                    sb.Append(",\"found\":false,\"reason\":\"typename_not_found\",\"typeName\":").Append(YorimashiEnvelope.EncodeString(typeName)).Append('}');
                    return sb.ToString();
                }
            }
            else
            {
                sb.Append(",\"found\":false,\"reason\":\"missing_index_or_typename\"}");
                return sb.ToString();
            }

            if (target == null)
            {
                sb.Append(",\"found\":true,\"componentIndex\":").Append(chosenIndex).Append(",\"type\":\"MissingScript\",\"properties\":[]}");
                return sb.ToString();
            }

            sb.Append(",\"found\":true,\"componentIndex\":").Append(chosenIndex);
            sb.Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(target.GetType().FullName));

            SerializedObject so = null;
            try { so = new SerializedObject(target); }
            catch (Exception e)
            {
                sb.Append(",\"error\":").Append(YorimashiEnvelope.EncodeString("SerializedObject failed: " + e.Message));
                sb.Append(",\"properties\":[]}");
                return sb.ToString();
            }

            sb.Append(",\"properties\":[");
            var it = so.GetIterator();
            bool enterChildren = true;
            int emitted = 0;
            bool first = true;
            while (it.NextVisible(enterChildren) && emitted < maxProps)
            {
                enterChildren = false; // only top level
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append("\"name\":").Append(YorimashiEnvelope.EncodeString(it.name));
                sb.Append(",\"path\":").Append(YorimashiEnvelope.EncodeString(it.propertyPath));
                sb.Append(",\"kind\":").Append(YorimashiEnvelope.EncodeString(it.propertyType.ToString()));
                sb.Append(",\"value\":");
                AppendSerializedPropertyValue(sb, it);
                sb.Append('}');
                emitted++;
            }
            sb.Append("],\"emitted\":").Append(emitted);
            sb.Append(",\"maxProperties\":").Append(maxProps);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendSerializedPropertyValue(StringBuilder sb, SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    sb.Append(p.intValue); return;
                case SerializedPropertyType.Boolean:
                    sb.Append(p.boolValue ? "true" : "false"); return;
                case SerializedPropertyType.Float:
                    sb.Append(p.floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture)); return;
                case SerializedPropertyType.String:
                    sb.Append(YorimashiEnvelope.EncodeString(p.stringValue ?? "")); return;
                case SerializedPropertyType.Color:
                    var c = p.colorValue;
                    sb.Append("{\"r\":").Append(c.r).Append(",\"g\":").Append(c.g)
                      .Append(",\"b\":").Append(c.b).Append(",\"a\":").Append(c.a).Append('}');
                    return;
                case SerializedPropertyType.ObjectReference:
                    var o = p.objectReferenceValue;
                    if (o == null) { sb.Append("null"); return; }
                    sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(o.name));
                    sb.Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(o.GetType().Name));
                    sb.Append(",\"instanceID\":").Append(o.GetInstanceID()).Append('}');
                    return;
                case SerializedPropertyType.Enum:
                    sb.Append('{');
                    sb.Append("\"enumIndex\":").Append(p.enumValueIndex);
                    if (p.enumDisplayNames != null && p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length)
                        sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(p.enumDisplayNames[p.enumValueIndex]));
                    sb.Append('}');
                    return;
                case SerializedPropertyType.Vector2:
                    var v2 = p.vector2Value;
                    sb.Append("{\"x\":").Append(v2.x).Append(",\"y\":").Append(v2.y).Append('}'); return;
                case SerializedPropertyType.Vector3:
                    var v3 = p.vector3Value;
                    sb.Append("{\"x\":").Append(v3.x).Append(",\"y\":").Append(v3.y).Append(",\"z\":").Append(v3.z).Append('}'); return;
                case SerializedPropertyType.Vector4:
                    var v4 = p.vector4Value;
                    sb.Append("{\"x\":").Append(v4.x).Append(",\"y\":").Append(v4.y).Append(",\"z\":").Append(v4.z).Append(",\"w\":").Append(v4.w).Append('}'); return;
                case SerializedPropertyType.Quaternion:
                    var q = p.quaternionValue;
                    sb.Append("{\"x\":").Append(q.x).Append(",\"y\":").Append(q.y).Append(",\"z\":").Append(q.z).Append(",\"w\":").Append(q.w).Append('}'); return;
                case SerializedPropertyType.Bounds:
                    sb.Append("\"<bounds>\""); return;
                case SerializedPropertyType.Rect:
                    sb.Append("\"<rect>\""); return;
                case SerializedPropertyType.LayerMask:
                    sb.Append(p.intValue); return;
                default:
                    sb.Append("\"<").Append(p.propertyType.ToString()).Append(">\"");
                    return;
            }
        }

        /// <summary>
        /// liltoon/read_material — 读 renderer.sharedMaterials 里每个 material 的关键 lilToon 参数。
        /// 只读。非 lilToon shader 材质只返回 shader 名。
        /// </summary>
        private static string LiltoonReadMaterial(string paramsJson)
        {
            var path = ExtractString(paramsJson, "path");
            var indexOpt = ExtractNumber(paramsJson, "materialIndex");

            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path ?? ""));

            var go = ResolvePath(path);
            if (go == null)
            {
                sb.Append(",\"found\":false,\"reason\":\"gameobject_not_found\"}");
                return sb.ToString();
            }
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                sb.Append(",\"found\":false,\"reason\":\"no_renderer\"}");
                return sb.ToString();
            }
            var mats = renderer.sharedMaterials;
            sb.Append(",\"found\":true,\"rendererType\":").Append(YorimashiEnvelope.EncodeString(renderer.GetType().Name));
            sb.Append(",\"materialCount\":").Append(mats.Length);
            sb.Append(",\"materials\":[");

            int startIdx = 0, endIdx = mats.Length;
            if (indexOpt.HasValue)
            {
                int mi = (int)indexOpt.Value;
                if (mi < 0 || mi >= mats.Length)
                {
                    sb.Append("],\"error\":\"materialIndex out of range\"}");
                    return sb.ToString();
                }
                startIdx = mi;
                endIdx = mi + 1;
            }

            bool first = true;
            for (int i = startIdx; i < endIdx; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                var m = mats[i];
                sb.Append('{').Append("\"slot\":").Append(i);
                if (m == null)
                {
                    sb.Append(",\"material\":null}");
                    continue;
                }
                sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(m.name));
                var assetPath = AssetDatabase.GetAssetPath(m);
                sb.Append(",\"assetPath\":").Append(YorimashiEnvelope.EncodeString(assetPath ?? ""));
                var shaderName = m.shader != null ? m.shader.name : "";
                sb.Append(",\"shader\":").Append(YorimashiEnvelope.EncodeString(shaderName));
                bool isLilToon = shaderName.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0
                              || shaderName.IndexOf("_lil/", StringComparison.OrdinalIgnoreCase) >= 0
                              || shaderName.StartsWith("Hidden/lil", StringComparison.OrdinalIgnoreCase);
                sb.Append(",\"isLilToon\":").Append(isLilToon ? "true" : "false");
                if (isLilToon)
                {
                    sb.Append(",\"lilToon\":{");
                    AppendLilFloat(sb, m, "_Cutoff", true);
                    AppendLilFloat(sb, m, "_LightMinLimit", false);
                    AppendLilFloat(sb, m, "_LightMaxLimit", false);
                    AppendLilFloat(sb, m, "_AsUnlit", false);
                    AppendLilFloat(sb, m, "_UseDissolve", false);
                    AppendLilFloat(sb, m, "_DissolveNoise", false);
                    AppendLilFloat(sb, m, "_DissolveBorderWidth", false);
                    AppendLilFloat(sb, m, "_DissolveBorderColorPower", false);
                    AppendLilVector(sb, m, "_DissolveParams");
                    AppendLilVector(sb, m, "_DissolvePos");
                    AppendLilColor(sb, m, "_Color");
                    AppendLilColor(sb, m, "_MainColor");
                    AppendLilColor(sb, m, "_DissolveColor");
                    AppendLilTexRef(sb, m, "_MainTex");
                    AppendLilTexRef(sb, m, "_DissolveMask");
                    AppendLilTexRef(sb, m, "_DissolveNoiseMask");
                    sb.Append("\"__end__\":true}");
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendLilFloat(StringBuilder sb, Material m, string prop, bool _first)
        {
            if (!m.HasProperty(prop)) return;
            sb.Append('"').Append(prop).Append("\":").Append(m.GetFloat(prop).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        }

        private static void AppendLilVector(StringBuilder sb, Material m, string prop)
        {
            if (!m.HasProperty(prop)) return;
            var v = m.GetVector(prop);
            sb.Append('"').Append(prop).Append("\":{\"x\":").Append(v.x).Append(",\"y\":").Append(v.y).Append(",\"z\":").Append(v.z).Append(",\"w\":").Append(v.w).Append("},");
        }

        private static void AppendLilColor(StringBuilder sb, Material m, string prop)
        {
            if (!m.HasProperty(prop)) return;
            var c = m.GetColor(prop);
            sb.Append('"').Append(prop).Append("\":{\"r\":").Append(c.r).Append(",\"g\":").Append(c.g).Append(",\"b\":").Append(c.b).Append(",\"a\":").Append(c.a).Append("},");
        }

        private static void AppendLilTexRef(StringBuilder sb, Material m, string prop)
        {
            if (!m.HasProperty(prop)) return;
            var t = m.GetTexture(prop);
            if (t == null)
            {
                sb.Append('"').Append(prop).Append("\":null,");
                return;
            }
            var ap = AssetDatabase.GetAssetPath(t);
            sb.Append('"').Append(prop).Append("\":{\"name\":").Append(YorimashiEnvelope.EncodeString(t.name))
              .Append(",\"assetPath\":").Append(YorimashiEnvelope.EncodeString(ap ?? "")).Append("},");
        }

        // ---- tiny param helpers (use MiniJsonParser) -----------------------

        private static string ExtractString(string paramsJson, string key)
        {
            if (string.IsNullOrEmpty(paramsJson) || paramsJson == "null") return null;
            try
            {
                var parser = new MiniJsonParser(paramsJson);
                var obj = parser.ParseObject();
                if (obj.TryGetValue(key, out var v) && v is string s) return s;
            }
            catch { }
            return null;
        }

        private static double? ExtractNumber(string paramsJson, string key)
        {
            if (string.IsNullOrEmpty(paramsJson) || paramsJson == "null") return null;
            try
            {
                var parser = new MiniJsonParser(paramsJson);
                var obj = parser.ParseObject();
                if (obj.TryGetValue(key, out var v) && v is double d) return d;
            }
            catch { }
            return null;
        }

        private static bool? ExtractBool(string paramsJson, string key)
        {
            if (string.IsNullOrEmpty(paramsJson) || paramsJson == "null") return null;
            try
            {
                var parser = new MiniJsonParser(paramsJson);
                var obj = parser.ParseObject();
                if (obj.TryGetValue(key, out var v) && v is bool b) return b;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 取一个 array 值的原始 JSON 表示（用 List&lt;object&gt; ToString 粗糙化）。
        /// 上层拿到后自己 regex 抽字符串元素——只用于简单 string[] 场景，别拿它解嵌套。
        /// </summary>
        private static string ExtractRawArray(string paramsJson, string key)
        {
            if (string.IsNullOrEmpty(paramsJson) || paramsJson == "null") return null;
            try
            {
                var parser = new MiniJsonParser(paramsJson);
                var obj = parser.ParseObject();
                if (obj.TryGetValue(key, out var v) && v is List<object> list)
                {
                    var sb = new StringBuilder();
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (list[i] is string s) sb.Append('"').Append(s.Replace("\"", "\\\"")).Append('"');
                        else sb.Append(list[i]?.ToString() ?? "null");
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
            }
            catch { }
            return null;
        }

        // ======================================================================
        // M3-E: console/read + console/clear + script/find_in_file
        //       + unity/reflect_type + menu/execute + packages/list
        // ======================================================================

        // ---- console/read + console/clear via internal UnityEditor.LogEntries ----

        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _mStartGetting;
        private static MethodInfo _mEndGetting;
        private static MethodInfo _mGetEntryInternal;
        private static MethodInfo _mConsoleClear;
        private static FieldInfo _fEntryMessage;
        private static FieldInfo _fEntryFile;
        private static FieldInfo _fEntryLine;
        private static FieldInfo _fEntryMode;

        private static void EnsureLogEntriesReflection()
        {
            if (_logEntriesType != null) return;
            var editorAsm = typeof(UnityEditor.Editor).Assembly;
            _logEntriesType = editorAsm.GetType("UnityEditor.LogEntries") ??
                              editorAsm.GetType("UnityEditorInternal.LogEntries");
            _logEntryType   = editorAsm.GetType("UnityEditor.LogEntry")   ??
                              editorAsm.GetType("UnityEditorInternal.LogEntry");
            if (_logEntriesType == null || _logEntryType == null)
                throw new InvalidOperationException("Cannot find UnityEditor.LogEntries via reflection (Unity version mismatch).");
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _mStartGetting     = _logEntriesType.GetMethod("StartGettingEntries", flags);
            _mEndGetting       = _logEntriesType.GetMethod("EndGettingEntries", flags);
            _mGetEntryInternal = _logEntriesType.GetMethod("GetEntryInternal", flags);
            _mConsoleClear     = _logEntriesType.GetMethod("Clear", flags);
            var iflags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _fEntryMessage = _logEntryType.GetField("message", iflags);
            _fEntryFile    = _logEntryType.GetField("file",    iflags);
            _fEntryLine    = _logEntryType.GetField("line",    iflags);
            _fEntryMode    = _logEntryType.GetField("mode",    iflags);
        }

        // LogEntry.mode bits (Unity Editor internal, best-effort — enough for level bucketization)
        // 0x1   ScriptingError    | 0x2 ScriptingAssertion | 0x4 ScriptingWarning
        // 0x100 Log               | 0x200 Fatal            | 0x800  AssetImportError
        // 0x0080 ScriptingLog     | 0x0040 ScriptingException
        private static string ModeToLevel(int mode)
        {
            const int kError   = 0x0001 | 0x00020000 | 0x00010000 | 0x00000200 | 0x00000080; // rough error family
            const int kWarning = 0x0004 | 0x00000010 | 0x00000020;
            if ((mode & kError)   != 0) return "error";
            if ((mode & kWarning) != 0) return "warning";
            return "log";
        }

        private static string ConsoleRead(string paramsJson)
        {
            EnsureLogEntriesReflection();
            var limit = (int)(ExtractNumber(paramsJson, "limit") ?? 20.0);
            if (limit < 1) limit = 1;
            if (limit > 200) limit = 200;
            var levelFilter = (ExtractString(paramsJson, "level") ?? "all").ToLowerInvariant();
            var contains = ExtractString(paramsJson, "contains");
            var hasContains = !string.IsNullOrEmpty(contains);

            int total = (int)_mStartGetting.Invoke(null, null);
            var sb = new StringBuilder();
            sb.Append("{\"total\":").Append(total).Append(",\"returned\":0,\"level\":\"").Append(levelFilter).Append("\",\"entries\":[");
            try
            {
                if (total == 0)
                {
                    sb.Append("]}");
                    return sb.ToString().Replace("\"returned\":0", "\"returned\":0");
                }
                var entryInstance = Activator.CreateInstance(_logEntryType);
                int returned = 0;
                bool first = true;
                // 从最新的往回读；LogEntries index 0 是最老的
                for (int i = total - 1; i >= 0 && returned < limit; i--)
                {
                    _mGetEntryInternal.Invoke(null, new object[] { i, entryInstance });
                    int mode = (int)(_fEntryMode?.GetValue(entryInstance) ?? 0);
                    var level = ModeToLevel(mode);
                    if (levelFilter != "all" && level != levelFilter) continue;
                    var msg = (string)(_fEntryMessage?.GetValue(entryInstance) ?? "");
                    if (hasContains && msg.IndexOf(contains, StringComparison.Ordinal) < 0) continue;

                    var file = (string)(_fEntryFile?.GetValue(entryInstance) ?? "");
                    int line = (int)(_fEntryLine?.GetValue(entryInstance) ?? 0);

                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('{');
                    sb.Append("\"index\":").Append(i).Append(',');
                    sb.Append("\"level\":\"").Append(level).Append("\",");
                    sb.Append("\"mode\":").Append(mode).Append(',');
                    sb.Append("\"message\":").Append(YorimashiEnvelope.EncodeString(msg)).Append(',');
                    sb.Append("\"file\":").Append(YorimashiEnvelope.EncodeString(file)).Append(',');
                    sb.Append("\"line\":").Append(line);
                    sb.Append('}');
                    returned++;
                }
                sb.Append("]}");
                // 回填 returned
                var s = sb.ToString();
                return s.Replace("\"returned\":0", "\"returned\":" + returned);
            }
            finally
            {
                _mEndGetting.Invoke(null, null);
            }
        }

        private static string ConsoleClear(string paramsJson)
        {
            EnsureLogEntriesReflection();
            int total = (int)_mStartGetting.Invoke(null, null);
            _mEndGetting.Invoke(null, null);
            _mConsoleClear.Invoke(null, null);
            return "{\"cleared\":" + total + "}";
        }

        // ---- script/find_in_file ---------------------------------------------

        private const int FindInFileMaxBytes = 5 * 1024 * 1024;
        private const int FindInFileHardMax = 200;

        private static string ScriptFindInFile(string paramsJson)
        {
            var relPath = ExtractString(paramsJson, "path");
            var pattern = ExtractString(paramsJson, "pattern");
            var isRegex = ExtractBool(paramsJson, "isRegex") ?? false;
            var ignoreCase = ExtractBool(paramsJson, "ignoreCase") ?? false;
            int maxMatches = (int)(ExtractNumber(paramsJson, "maxMatches") ?? 50.0);
            if (maxMatches < 1) maxMatches = 1;
            if (maxMatches > FindInFileHardMax) maxMatches = FindInFileHardMax;
            int context = (int)(ExtractNumber(paramsJson, "context") ?? 0.0);
            if (context < 0) context = 0;
            if (context > 5) context = 5;

            if (string.IsNullOrEmpty(relPath) || string.IsNullOrEmpty(pattern))
                return "{\"error\":\"path and pattern required\"}";

            // scope guard: must be under Assets/ or Packages/
            var normalized = relPath.Replace('\\', '/');
            if (normalized.Contains(".."))
                return "{\"error\":\"path contains '..'\"}";
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) &&
                !normalized.StartsWith("Packages/", StringComparison.Ordinal))
                return "{\"error\":\"path must start with Assets/ or Packages/\"}";

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var full = Path.Combine(projectRoot, normalized);
            if (!File.Exists(full))
                return "{\"error\":\"file not found: " + normalized.Replace("\"", "\\\"") + "\"}";
            var fi = new FileInfo(full);
            if (fi.Length > FindInFileMaxBytes)
                return "{\"error\":\"file too large: " + fi.Length + " bytes\"}";

            System.Text.RegularExpressions.Regex rx = null;
            if (isRegex)
            {
                try
                {
                    var opts = ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                                          : System.Text.RegularExpressions.RegexOptions.None;
                    rx = new System.Text.RegularExpressions.Regex(pattern, opts, TimeSpan.FromSeconds(2));
                }
                catch (Exception e)
                {
                    return "{\"error\":\"invalid regex: " + e.Message.Replace("\"", "\\\"") + "\"}";
                }
            }
            var lines = File.ReadAllLines(full);
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(normalized));
            sb.Append(",\"totalLines\":").Append(lines.Length);
            sb.Append(",\"matches\":[");
            int matched = 0;
            bool first = true;
            for (int i = 0; i < lines.Length && matched < maxMatches; i++)
            {
                var line = lines[i];
                bool hit = rx != null ? rx.IsMatch(line) : line.IndexOf(pattern, comparison) >= 0;
                if (!hit) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append("\"lineNumber\":").Append(i + 1).Append(',');
                sb.Append("\"text\":").Append(YorimashiEnvelope.EncodeString(line));
                if (context > 0)
                {
                    sb.Append(",\"context\":[");
                    int start = Math.Max(0, i - context);
                    int end = Math.Min(lines.Length - 1, i + context);
                    bool cf = true;
                    for (int k = start; k <= end; k++)
                    {
                        if (!cf) sb.Append(',');
                        cf = false;
                        sb.Append('{').Append("\"lineNumber\":").Append(k + 1)
                          .Append(",\"text\":").Append(YorimashiEnvelope.EncodeString(lines[k])).Append('}');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
                matched++;
            }
            sb.Append("],\"matchCount\":").Append(matched);
            sb.Append(",\"truncated\":").Append(matched >= maxMatches ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        // ---- unity/reflect_type ----------------------------------------------

        private static string UnityReflectType(string paramsJson)
        {
            var typeName = ExtractString(paramsJson, "typeName");
            if (string.IsNullOrEmpty(typeName))
                return "{\"error\":\"typeName required\"}";
            var includeInherited = ExtractBool(paramsJson, "includeInherited") ?? true;
            var includeNonPublic = ExtractBool(paramsJson, "includeNonPublic") ?? false;
            var membersRaw = ExtractRawArray(paramsJson, "members");
            bool wantFields = membersRaw == null || membersRaw.Contains("\"fields\"");
            bool wantProps  = membersRaw == null || membersRaw.Contains("\"properties\"");
            bool wantMethods= membersRaw == null || membersRaw.Contains("\"methods\"");

            var t = ResolveTypeByName(typeName);
            if (t == null)
                return "{\"error\":\"type not found: " + typeName.Replace("\"", "\\\"") + "\"}";

            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;
            if (!includeInherited) flags |= BindingFlags.DeclaredOnly;

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"typeName\":").Append(YorimashiEnvelope.EncodeString(t.FullName ?? t.Name)).Append(',');
            sb.Append("\"assembly\":").Append(YorimashiEnvelope.EncodeString(t.Assembly.GetName().Name)).Append(',');
            sb.Append("\"baseType\":").Append(YorimashiEnvelope.EncodeString(t.BaseType?.FullName ?? "")).Append(',');
            sb.Append("\"isValueType\":").Append(t.IsValueType ? "true" : "false").Append(',');
            sb.Append("\"isEnum\":").Append(t.IsEnum ? "true" : "false");

            int cap = 200;
            int emitted = 0;
            if (wantFields)
            {
                sb.Append(",\"fields\":[");
                bool first = true;
                foreach (var f in t.GetFields(flags))
                {
                    if (emitted >= cap) break;
                    if (!first) sb.Append(','); first = false;
                    sb.Append('{').Append("\"name\":").Append(YorimashiEnvelope.EncodeString(f.Name))
                      .Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(f.FieldType.FullName ?? f.FieldType.Name))
                      .Append(",\"static\":").Append(f.IsStatic ? "true" : "false")
                      .Append(",\"public\":").Append(f.IsPublic ? "true" : "false").Append('}');
                    emitted++;
                }
                sb.Append(']');
            }
            if (wantProps)
            {
                sb.Append(",\"properties\":[");
                bool first = true;
                foreach (var p in t.GetProperties(flags))
                {
                    if (emitted >= cap) break;
                    if (!first) sb.Append(','); first = false;
                    sb.Append('{').Append("\"name\":").Append(YorimashiEnvelope.EncodeString(p.Name))
                      .Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(p.PropertyType.FullName ?? p.PropertyType.Name))
                      .Append(",\"canRead\":").Append(p.CanRead ? "true" : "false")
                      .Append(",\"canWrite\":").Append(p.CanWrite ? "true" : "false").Append('}');
                    emitted++;
                }
                sb.Append(']');
            }
            if (wantMethods)
            {
                sb.Append(",\"methods\":[");
                bool first = true;
                foreach (var m in t.GetMethods(flags))
                {
                    if (emitted >= cap) break;
                    // 排除 property getter/setter 减少噪音
                    if (m.IsSpecialName) continue;
                    if (!first) sb.Append(','); first = false;
                    var ps = m.GetParameters();
                    var psb = new StringBuilder();
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) psb.Append(',');
                        psb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                    }
                    sb.Append('{').Append("\"name\":").Append(YorimashiEnvelope.EncodeString(m.Name))
                      .Append(",\"returnType\":").Append(YorimashiEnvelope.EncodeString(m.ReturnType.FullName ?? m.ReturnType.Name))
                      .Append(",\"params\":").Append(YorimashiEnvelope.EncodeString(psb.ToString()))
                      .Append(",\"static\":").Append(m.IsStatic ? "true" : "false").Append('}');
                    emitted++;
                }
                sb.Append(']');
            }
            sb.Append(",\"truncated\":").Append(emitted >= cap ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        private static Type ResolveTypeByName(string typeName)
        {
            // 直接 Type.GetType（限当前 asm）
            var t = Type.GetType(typeName, throwOnError: false);
            if (t != null) return t;
            // 扫所有已加载 assembly
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // fully qualified 优先
                    t = asm.GetType(typeName, throwOnError: false);
                    if (t != null) return t;
                    // short name 匹配 —— 用 Name 而非 FullName（省 namespace）
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == typeName) return type;
                        if (type.FullName == typeName) return type;
                    }
                }
                catch (ReflectionTypeLoadException) { /* skip */ }
                catch { /* skip malformed asm */ }
            }
            return null;
        }

        // ---- menu/execute (whitelisted) --------------------------------------

        // 白名单前缀：只允许 clearly non-destructive 的 menu path
        // 显式排除任何会开对话框 / File 操作 / GameObject 删除的
        private static readonly string[] MenuWhitelistPrefixes = new[]
        {
            "Assets/Refresh",
            "Assets/Reimport",              // 只允许 Reimport（不含 Reimport All，那个会卡）
            "Window/",                       // 打开窗口安全
            "Yorimashi/",                    // 我们自己的 menu 我们控制
            "Tools/",                        // 三方 tool 用 Tools/ 前缀是惯例；用户可用
            "Modular Avatar/",               // MA 面板
            "NDMF/",
        };

        private static readonly string[] MenuBlacklist = new[]
        {
            "Assets/Reimport All",           // 卡 Editor 几分钟
            "File/",
            "Edit/Preferences",
            "Edit/Project Settings",
            "GameObject/Delete",
        };

        private static string MenuExecute(string paramsJson)
        {
            var menuPath = ExtractString(paramsJson, "menuPath");
            if (string.IsNullOrEmpty(menuPath))
                return "{\"error\":\"menuPath required\"}";
            // 黑名单先查
            foreach (var b in MenuBlacklist)
            {
                if (menuPath.StartsWith(b, StringComparison.Ordinal) || menuPath == b.TrimEnd('/'))
                    return "{\"error\":\"menu path in blacklist\",\"menuPath\":" + YorimashiEnvelope.EncodeString(menuPath) + "}";
            }
            bool allowed = false;
            foreach (var w in MenuWhitelistPrefixes)
            {
                if (menuPath.StartsWith(w, StringComparison.Ordinal) || menuPath == w.TrimEnd('/'))
                {
                    allowed = true; break;
                }
            }
            if (!allowed)
            {
                return "{\"error\":\"menu path not in whitelist\",\"menuPath\":" + YorimashiEnvelope.EncodeString(menuPath) +
                       ",\"allowedPrefixes\":[" + BuildStringArray(MenuWhitelistPrefixes) + "]}";
            }

            bool executed = EditorApplication.ExecuteMenuItem(menuPath);
            return "{\"menuPath\":" + YorimashiEnvelope.EncodeString(menuPath) +
                   ",\"executed\":" + (executed ? "true" : "false") + "}";
        }

        private static string BuildStringArray(string[] items)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(YorimashiEnvelope.EncodeString(items[i]));
            }
            return sb.ToString();
        }

        // ---- packages/list ---------------------------------------------------

        private static string PackagesList(string paramsJson)
        {
            var onlyRelevant = ExtractBool(paramsJson, "onlyRelevant") ?? false;
            // Client.List 是 async；这里同步等最多 10s
            var req = Client.List(offlineMode: true, includeIndirectDependencies: false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!req.IsCompleted && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(50);
            }
            if (!req.IsCompleted)
                return "{\"error\":\"Client.List timeout after 10s\"}";
            if (req.Status != StatusCode.Success)
                return "{\"error\":\"" + (req.Error?.message?.Replace("\"", "\\\"") ?? "unknown") + "\"}";

            var sb = new StringBuilder();
            sb.Append("{\"packages\":[");
            bool first = true;
            int total = 0, kept = 0;
            foreach (var p in req.Result)
            {
                total++;
                bool relevant = IsAvatarRelevantPackage(p.name);
                if (onlyRelevant && !relevant) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append("\"name\":").Append(YorimashiEnvelope.EncodeString(p.name)).Append(',');
                sb.Append("\"version\":").Append(YorimashiEnvelope.EncodeString(p.version)).Append(',');
                sb.Append("\"displayName\":").Append(YorimashiEnvelope.EncodeString(p.displayName ?? "")).Append(',');
                sb.Append("\"source\":").Append(YorimashiEnvelope.EncodeString(p.source.ToString())).Append(',');
                sb.Append("\"isDirect\":").Append(p.isDirectDependency ? "true" : "false").Append(',');
                sb.Append("\"relevant\":").Append(relevant ? "true" : "false");
                sb.Append('}');
                kept++;
            }
            sb.Append("],\"total\":").Append(total).Append(",\"returned\":").Append(kept).Append('}');
            return sb.ToString();
        }

        private static bool IsAvatarRelevantPackage(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var pref in RelevantPackagePrefixes)
            {
                if (name.StartsWith(pref, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ---- ExtractBool / ExtractNumber 已在文件前部（旧位置）定义，此处不重复 ---
    }
}
