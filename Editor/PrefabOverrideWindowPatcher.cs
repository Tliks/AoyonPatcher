#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEditor.IMGUI.Controls;
using Object = UnityEngine.Object;

namespace Aoyon.AoyonPatcher.PrefabOverridesWindow
{
    [InitializeOnLoad]
    internal static class PrefabOverrideWindowPatcher
    {
        private static readonly Harmony s_Harmony;

        private static readonly Type s_WindowType;
        private static readonly Type s_TreeViewType;
        private static readonly Type s_TreeViewItemType;
        private static readonly Type s_TreeViewControllerType;

        // PrefabOverridesWindow
        private static readonly MethodInfo s_WindowRefreshStatusMethod;

        // PrefabOverridesTreeView
        private static readonly FieldInfo s_TreeViewControllerField;
        private static readonly FieldInfo s_TreeViewItemSingleModificationField;

        // TreeViewController
        private static readonly PropertyInfo s_TreeViewControllerSelectionChangedProperty;

        // PrefabOverride
        private static readonly MethodInfo s_PrefabOverrideGetObjectMethod;

        // PrefabUtility
        private static readonly MethodInfo s_PrefabUtilityGetApplyTargetsMethod;
        private static readonly MethodInfo s_PrefabUtilityIsObjectOverrideAllDefaultMethod;
        private static readonly MethodInfo s_PrefabUtilityIsAssetANestedPrefabRootMethod;
        private static readonly MethodInfo s_PrefabUtilityGetRootGameObjectMethod;
        private static readonly MethodInfo s_PrefabUtilityIsPartOfPrefabThatCanBeAppliedToMethod;
        private static readonly MethodInfo s_PrefabUtilityHasApplicableObjectOverridesForTargetMethod;
        private static readonly MethodInfo s_PrefabUtilityProcessMultipleOverridesMethod;
        private static readonly object s_OverrideOperationApplyValue;

        // EditorUtility
        private static readonly MethodInfo s_EditorUtilityForceRebuildInspectorsMethod;

        private static PopupWindowContent? s_WindowInstance;
        private static TreeView? s_TreeViewInstance;
        private static object? s_TreeViewControllerInstance;
        private static Action<int[]>? s_SelectionChangedDelegate;
        private static SelectionMode s_ApplyMode;
        private static List<PrefabOverride> s_CurrentOverrides;
        private static List<ApplyTargetOption> s_CurrentTargets;
        
        class ApplyTargetOption
        {
            public GameObject RootGameObject;
            public bool CanApply;
            public GUIContent Content;

            public ApplyTargetOption(GameObject rootGameObject, bool canApply, GUIContent content)
            {
                RootGameObject = rootGameObject;
                CanApply = canApply;
                Content = content;
            }
        }

        struct OverrideApplyInfo
        {
            public Object instanceOrAssetObject;
            public bool isPersistent;
            public List<(Object sourceObject, GameObject root)> targets;
        }

        enum SelectionMode
        {
            All,
            Selected,
            None
        }

        static PrefabOverrideWindowPatcher()
        {
            s_Harmony = new Harmony("aoyon.prefaboverridewindowpatcher");

            s_WindowType = AccessTools.TypeByName("UnityEditor.PrefabOverridesWindow") ?? throw new Exception("Failed to get PrefabOverridesWindow type");
            s_TreeViewType = AccessTools.TypeByName("UnityEditor.PrefabOverridesTreeView") ?? throw new Exception("Failed to get PrefabOverridesTreeView type");
            s_TreeViewItemType = AccessTools.Inner(s_TreeViewType, "PrefabOverridesTreeViewItem") ?? throw new Exception("Failed to get PrefabOverridesTreeViewItem type");
            s_TreeViewControllerType = AccessTools.TypeByName("UnityEditor.IMGUI.Controls.TreeViewController") ?? throw new Exception("Failed to get TreeViewController type");

            s_WindowRefreshStatusMethod = AccessTools.Method(s_WindowType, "RefreshStatus") ?? throw new Exception("Failed to get RefreshStatus method");

            s_TreeViewControllerField = AccessTools.Field(typeof(TreeView), "m_TreeView") ?? throw new Exception("Failed to get m_TreeView field");
            s_TreeViewControllerSelectionChangedProperty = AccessTools.Property(s_TreeViewControllerType, "selectionChangedCallback") ?? throw new Exception("Failed to get selectionChangedCallback property");
            s_TreeViewItemSingleModificationField = AccessTools.Field(s_TreeViewItemType, "singleModification") ?? throw new Exception("Failed to get singleModification field");        
            
            s_PrefabOverrideGetObjectMethod = AccessTools.Method(typeof(PrefabOverride), "GetObject") ?? throw new Exception("Failed to get GetObject method");
                    
            s_PrefabUtilityGetApplyTargetsMethod = AccessTools.Method(typeof(PrefabUtility), "GetApplyTargets", new[] { typeof(UnityEngine.Object), typeof(bool), typeof(bool), typeof(bool) }) ?? throw new Exception("Failed to get GetApplyTargets method");
            s_PrefabUtilityIsObjectOverrideAllDefaultMethod = AccessTools.Method(typeof(PrefabUtility), "IsObjectOverrideAllDefaultOverridesComparedToOriginalSource") ?? throw new Exception("Failed to get IsObjectOverrideAllDefaultOverridesComparedToOriginalSource method");
            s_PrefabUtilityIsAssetANestedPrefabRootMethod = AccessTools.Method(typeof(PrefabUtility), "IsAssetANestedPrefabRoot") ?? throw new Exception("Failed to get IsAssetANestedPrefabRoot method");
            s_PrefabUtilityGetRootGameObjectMethod = AccessTools.Method(typeof(PrefabUtility), "GetRootGameObject", new[] { typeof(UnityEngine.Object) }) ?? throw new Exception("Failed to get GetRootGameObject method");
            s_PrefabUtilityIsPartOfPrefabThatCanBeAppliedToMethod = AccessTools.Method(typeof(PrefabUtility), "IsPartOfPrefabThatCanBeAppliedTo") ?? throw new Exception("Failed to get IsPartOfPrefabThatCanBeAppliedTo method");
            s_PrefabUtilityHasApplicableObjectOverridesForTargetMethod = AccessTools.Method(typeof(PrefabUtility), "HasApplicableObjectOverridesForTarget") ?? throw new Exception("Failed to get HasApplicableObjectOverridesForTarget method");
            s_PrefabUtilityProcessMultipleOverridesMethod = AccessTools.Method(typeof(PrefabUtility), "ProcessMultipleOverrides") ?? throw new Exception("Failed to get ProcessMultipleOverrides method");
            var overrideOperationType = AccessTools.TypeByName("UnityEditor.PrefabUtility+OverrideOperation") ?? throw new Exception("Failed to get OverrideOperation type");
            s_OverrideOperationApplyValue = Enum.Parse(overrideOperationType, "Apply");

            s_EditorUtilityForceRebuildInspectorsMethod = AccessTools.Method(typeof(EditorUtility), "ForceRebuildInspectors") ?? throw new Exception("Failed to get ForceRebuildInspectors method");

            var onOpenMethod = AccessTools.Method("PrefabOverridesWindow:OnOpen") ?? throw new Exception("Failed to get OnOpen method");
            var onOpenPostfix = AccessTools.Method(typeof(PrefabOverrideWindowPatcher), nameof(OnOpenPostfix)) ?? throw new Exception("Failed to get OnOpenPostfix method");
            s_Harmony.Patch(onOpenMethod, postfix: new HarmonyMethod(onOpenPostfix));

            var onCloseMethod = AccessTools.Method("PrefabOverridesWindow:OnClose") ?? throw new Exception("Failed to get OnClose method");
            var onClosePostfix = AccessTools.Method(typeof(PrefabOverrideWindowPatcher), nameof(OnClosePostfix)) ?? throw new Exception("Failed to get OnClosePostfix method");
            s_Harmony.Patch(onCloseMethod, postfix: new HarmonyMethod(onClosePostfix));

            var onGuiMethod = AccessTools.Method("PrefabOverridesWindow:OnGUI") ?? throw new Exception("Failed to get OnGUI method");
            var onGuiTranspiler = AccessTools.Method(typeof(PrefabOverrideWindowPatcher), nameof(OnGUITranspiler)) ?? throw new Exception("Failed to get OnGUITranspiler method");
            s_Harmony.Patch(onGuiMethod, transpiler: new HarmonyMethod(onGuiTranspiler));

            var refreshStatusPostfix = AccessTools.Method(typeof(PrefabOverrideWindowPatcher), nameof(RefreshStatusPostfix)) ?? throw new Exception("Failed to get RefreshStatusPostfix method");
            s_Harmony.Patch(s_WindowRefreshStatusMethod, postfix: new HarmonyMethod(refreshStatusPostfix));

            s_WindowInstance = null;
            s_TreeViewInstance = null;
            s_TreeViewControllerInstance = null;
            s_SelectionChangedDelegate = null;
            s_ApplyMode = SelectionMode.All;
            s_CurrentOverrides = new();
            s_CurrentTargets = new();
        }

        private static void InitializeState()
        {
            s_WindowInstance = null;
            s_TreeViewInstance = null;
            s_TreeViewControllerInstance = null;
            s_SelectionChangedDelegate = null;
            s_ApplyMode = SelectionMode.All;
            s_CurrentOverrides = new();
            s_CurrentTargets = new();
        }

        private static void OnOpenPostfix(PopupWindowContent __instance, TreeView ___m_TreeView)
        {
            s_WindowInstance = __instance;
            s_TreeViewInstance = ___m_TreeView;
            
            s_TreeViewControllerInstance = s_TreeViewControllerField.GetValue(___m_TreeView) ?? throw new Exception("Failed to get TreeViewController instance");
            RegisterSelectionChangedCallback();
            
            OnSelectionChanged(new List<int>());
        }

        private static void OnClosePostfix()
        {
            UnregisterSelectionChangedCallback();
            InitializeState();
        }
        
        private static void RegisterSelectionChangedCallback()
        {
            s_SelectionChangedDelegate = new Action<int[]>(OnSelectionChanged);
            var existingCallback = (Delegate?)s_TreeViewControllerSelectionChangedProperty.GetValue(s_TreeViewControllerInstance);
            var newCallback = existingCallback != null 
                ? Delegate.Combine(existingCallback, s_SelectionChangedDelegate) 
                : s_SelectionChangedDelegate;
            s_TreeViewControllerSelectionChangedProperty.SetValue(s_TreeViewControllerInstance, newCallback);
        }

        private static void UnregisterSelectionChangedCallback()
        {
            if (s_TreeViewControllerInstance != null && s_SelectionChangedDelegate != null)
            {
                var existingCallback = (Delegate?)s_TreeViewControllerSelectionChangedProperty.GetValue(s_TreeViewControllerInstance);
                if (existingCallback != null)
                {
                    var newCallback = Delegate.Remove(existingCallback, s_SelectionChangedDelegate);
                    s_TreeViewControllerSelectionChangedProperty.SetValue(s_TreeViewControllerInstance, newCallback);
                }
            }
        }

        private static void RefreshStatusPostfix()
        {
            InitializeState();
            if (s_TreeViewInstance is null) return;
            var currentSelection = s_TreeViewInstance.GetSelection();
            OnSelectionChanged(currentSelection);
        }

        private static void OnSelectionChanged(IList<int> selectedIds)
        {
            if (s_TreeViewInstance is null) return;
            var rows = s_TreeViewInstance.GetRows();
            if (rows.Count == 0) return;

            // overrideの行のみ抽出
            var overrideRows = rows
                .Where(row => 
                    s_TreeViewItemType.IsInstanceOfType(row) && 
                    s_TreeViewItemSingleModificationField.GetValue(row) != null)
                .ToList();
            
            if (overrideRows.Count == 0) return;

            // 選択状態に応じてモードとオーバーライドを決定
            if (selectedIds.Count == 0)
            {
                // 選択なし：すべてのオーバーライドを対象
                s_ApplyMode = SelectionMode.All;
                s_CurrentOverrides = overrideRows
                    .Select(row => s_TreeViewItemSingleModificationField.GetValue(row))
                    .Cast<PrefabOverride>()
                    .ToList();
            }
            else
            {
                var selectedSet = new HashSet<int>(selectedIds);
                var selectedOverrideRows = overrideRows.Where(row => selectedSet.Contains(row.id)).ToList();
                
                if (selectedOverrideRows.Count == 0)
                {
                    // 選択されているが全てオーバーライド行ではない（親ノードなど）
                    s_ApplyMode = SelectionMode.None;
                    s_CurrentOverrides = new();
                }
                else
                {
                    // 選択されたオーバーライド行がある
                    s_ApplyMode = SelectionMode.Selected;
                    s_CurrentOverrides = selectedOverrideRows
                        .Select(row => s_TreeViewItemSingleModificationField.GetValue(row))
                        .Cast<PrefabOverride>()
                        .ToList();
                }
            }

            var overrides = s_CurrentOverrides;

            s_CurrentTargets.Clear();
            if (overrides.Count == 0) return;

            var overrideInfos = new OverrideApplyInfo[overrides.Count];
            for (int i = 0; i < overrides.Count; i++)
            {
                var @override = overrides[i];
                var instanceOrAssetObject = GetInstanceOrAssetObject(@override);
                var applyTargetObjects = GetApplyTargetObjects(@override, instanceOrAssetObject);
                
                overrideInfos[i] = new OverrideApplyInfo
                {
                    instanceOrAssetObject = instanceOrAssetObject,
                    isPersistent = EditorUtility.IsPersistent(instanceOrAssetObject),
                    targets = applyTargetObjects.Select(obj => (obj, GetRootGameObject(obj))).ToList()
                };
            }

            var commonRootsSet = new HashSet<GameObject>(overrideInfos[0].targets.Select(t => t.root));
            for (int i = 1; i < overrideInfos.Length; i++)
            {
                commonRootsSet.IntersectWith(overrideInfos[i].targets.Select(t => t.root));
                if (commonRootsSet.Count == 0) return;
            }

            var firstTargets = overrideInfos[0].targets;
            for (int i = 0; i < firstTargets.Count; i++)
            {
                var (sourceObject, root) = firstTargets[i];
                if (!commonRootsSet.Contains(root)) continue;

                var canApply = CanApply(root, overrideInfos);

                string format = i == firstTargets.Count - 1 
                    ? "Apply to Prefab '{0}'" 
                    : "Apply as Override in Prefab '{0}'";
                var content = new GUIContent(string.Format(format, root.name));

                s_CurrentTargets.Add(new ApplyTargetOption(root, canApply, content));
            }
        }

        private static Object GetInstanceOrAssetObject(PrefabOverride @override)
        {
            return @override is ObjectOverride 
                ? (Object)s_PrefabOverrideGetObjectMethod.Invoke(@override, null)
                : (Object)@override.GetAssetObject();
        }

        private static List<Object> GetApplyTargetObjects(PrefabOverride @override, Object instanceOrAssetObject)
        {
            var isObjectOverride = @override is ObjectOverride;
            
            var isAllDefaultOverrides = isObjectOverride && 
                ((bool)s_PrefabUtilityIsObjectOverrideAllDefaultMethod.Invoke(null, new[] { instanceOrAssetObject }));
            var isRemovedNestedPrefabRoot = @override is RemovedGameObject && 
                ((bool)s_PrefabUtilityIsAssetANestedPrefabRootMethod.Invoke(null, new[] { instanceOrAssetObject }));
            
            var includeSelfAsTarget = !isObjectOverride;
            var includeOriginalSelfAsTarget = !isRemovedNestedPrefabRoot;
            
            // PrefabUtility.GetApplyTargetsを呼び出し
            var targetObjects = (IList)s_PrefabUtilityGetApplyTargetsMethod.Invoke(null, new object[] 
            { 
                instanceOrAssetObject, 
                isAllDefaultOverrides, 
                includeSelfAsTarget, 
                includeOriginalSelfAsTarget 
            });
            
            if (targetObjects == null) 
                return new List<Object>();
            
            return targetObjects.Cast<Object>().ToList();
        }

        private static GameObject GetRootGameObject(Object obj)
        {
            return (GameObject)s_PrefabUtilityGetRootGameObjectMethod.Invoke(null, new[] { obj });
        }

        private static bool CanApply(GameObject root, OverrideApplyInfo[] overrideInfos)
        {
            var isPartOfPrefabThatCanBeAppliedTo = (bool)s_PrefabUtilityIsPartOfPrefabThatCanBeAppliedToMethod.Invoke(null, new object[] { root });
            if (!isPartOfPrefabThatCanBeAppliedTo) return false;

            for (int j = 0; j < overrideInfos.Length; j++)
            {
                var info = overrideInfos[j];
                if (info.isPersistent) continue;

                var targetSourceObject = info.targets.FirstOrDefault(t => t.root == root).sourceObject;
                if (targetSourceObject == null || 
                    !(bool)s_PrefabUtilityHasApplicableObjectOverridesForTargetMethod.Invoke(null, new object[] { info.instanceOrAssetObject, targetSourceObject, false }))
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<CodeInstruction> OnGUITranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var buttonMethod = AccessTools.Method(typeof(GUILayout), nameof(GUILayout.Button), new[] { typeof(GUIContent), typeof(GUILayoutOption[]) }) ?? throw new Exception();

            var applySelectedContentField = AccessTools.Field(s_WindowType, "m_ApplySelectedContent") ?? throw new Exception();
            var applyAllContentField = AccessTools.Field(s_WindowType, "m_ApplyAllContent") ?? throw new Exception();
            var revertSelectedContentField = AccessTools.Field(s_WindowType, "m_RevertSelectedContent") ?? throw new Exception();

            var drawApplyButtonShim = AccessTools.Method(typeof(PrefabOverrideWindowPatcher), nameof(DrawApplyButtonShim)) ?? throw new Exception();
            var drawRevertButtonShim = AccessTools.Method(typeof(PrefabOverrideWindowPatcher), nameof(DrawRevertButtonShim)) ?? throw new Exception();

            MethodInfo? replaceWith = null;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field)
                {
                    if (field == applySelectedContentField || field == applyAllContentField)
                    {
                        replaceWith = drawApplyButtonShim;
                    }
                    else if (field == revertSelectedContentField)
                    {
                        replaceWith = drawRevertButtonShim;
                    }
                }

                if (instruction.Calls(buttonMethod) && replaceWith != null)
                {
                    yield return new CodeInstruction(OpCodes.Call, replaceWith);
                    replaceWith = null;
                    continue;
                }

                yield return instruction;
            }
        }

        private static bool DrawApplyButtonShim(GUIContent content, GUILayoutOption[] options)
        {
            var newLabel = s_ApplyMode switch
            {
                SelectionMode.All => "Apply All",
                SelectionMode.Selected => "Apply Selected",
                SelectionMode.None => "Apply",
                _ => throw new NotImplementedException(),
            };
            var style = new GUIStyle("MiniPulldown") { alignment = TextAnchor.MiddleCenter };
            var newContent = new GUIContent(newLabel);
            var rect = GUILayoutUtility.GetRect(GUIContent.none, style, GUILayout.Width(120f));
            var prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && s_ApplyMode != SelectionMode.None;
            if (EditorGUI.DropdownButton(rect, newContent, FocusType.Passive, style))
            {
                var genericMenu = new GenericMenu();
                HandleApplyMenu(genericMenu);
                genericMenu.DropDown(rect);
            }
            GUI.enabled = prevEnabled;
            return false;
        }

        private static bool DrawRevertButtonShim(GUIContent content, GUILayoutOption[] options)
        {
            var prevEnabled = GUI.enabled;
            GUI.enabled = prevEnabled && s_ApplyMode != SelectionMode.None;
            var result = GUILayout.Button(content, options);
            GUI.enabled = prevEnabled;
            return result;
        }

        private static void HandleApplyMenu(GenericMenu menu)
        {
            if (s_CurrentTargets.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No applicable targets"));
                return;
            }

            foreach (var target in s_CurrentTargets)
            {
                if (target.CanApply)
                {
                    menu.AddItem(target.Content, false, Apply, target);
                }
                else
                {
                    menu.AddDisabledItem(target.Content);
                }
            }
        }

        private static void Apply(object userData)
        {
            if (userData is not ApplyTargetOption target)
            {
                Debug.LogError("Invalid userData type");
                return;
            }

            var result = (bool)s_PrefabUtilityProcessMultipleOverridesMethod.Invoke(null, new object[] { target.RootGameObject, s_CurrentOverrides, s_OverrideOperationApplyValue, InteractionMode.UserAction });
            if (result)
            {
                s_EditorUtilityForceRebuildInspectorsMethod.Invoke(null, null);   
            }

            if (s_WindowInstance != null)
            {
                s_WindowRefreshStatusMethod.Invoke(s_WindowInstance, new object[] { true });
            }
        }
    }
}