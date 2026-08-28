#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Aoyon.AoyonPatcher.PrefabOverridesWindow
{
    [InitializeOnLoad]
    internal static class PrefabOverrideApplyPatcher
    {
        private static readonly Harmony s_Harmony;
        private static readonly MethodInfo s_PrefabUtilityProcessMultipleOverridesMethod;
        private static readonly object s_OverrideOperationApplyValue;
        private static string? s_ApplyTargetAssetPath;

        static PrefabOverrideApplyPatcher()
        {
            s_Harmony = new Harmony("aoyon.prefaboverridewindowpatcher.apply");
            s_PrefabUtilityProcessMultipleOverridesMethod =
                AccessTools.Method(typeof(PrefabUtility), "ProcessMultipleOverrides")
                ?? throw new Exception("Failed to get ProcessMultipleOverrides method");

            var overrideOperationType =
                AccessTools.TypeByName("UnityEditor.PrefabUtility+OverrideOperation")
                ?? throw new Exception("Failed to get OverrideOperation type");
            s_OverrideOperationApplyValue = Enum.Parse(overrideOperationType, "Apply");

            var prefabOverrideApplyMethod = AccessTools.Method(
                typeof(PrefabOverride),
                "Apply",
                new[] { typeof(InteractionMode) })
                ?? throw new Exception("Failed to get PrefabOverride.Apply method");
            var prefabOverrideApplyPrefix = AccessTools.Method(
                typeof(PrefabOverrideApplyPatcher),
                nameof(PrefabOverrideApplyPrefix))
                ?? throw new Exception("Failed to get PrefabOverrideApplyPrefix method");
            s_Harmony.Patch(prefabOverrideApplyMethod, prefix: new HarmonyMethod(prefabOverrideApplyPrefix));
        }

        internal static bool ApplyToTarget(GameObject targetRoot, List<PrefabOverride> overrides)
        {
            var targetAssetPath = AssetDatabase.GetAssetPath(targetRoot);
            if (string.IsNullOrEmpty(targetAssetPath))
            {
                Debug.LogError("Failed to get the asset path for the selected Prefab target.");
                return false;
            }

            var previousTargetAssetPath = s_ApplyTargetAssetPath;
            s_ApplyTargetAssetPath = targetAssetPath;
            try
            {
                return (bool)s_PrefabUtilityProcessMultipleOverridesMethod.Invoke(null, new object[]
                {
                    targetRoot,
                    overrides,
                    s_OverrideOperationApplyValue,
                    InteractionMode.UserAction
                });
            }
            finally
            {
                s_ApplyTargetAssetPath = previousTargetAssetPath;
            }
        }

        // ProcessMultipleOverrides has no target path parameter, so redirect only its scoped apply calls.
        private static bool PrefabOverrideApplyPrefix(PrefabOverride __instance, InteractionMode mode)
        {
            var assetPath = s_ApplyTargetAssetPath;
            if (string.IsNullOrEmpty(assetPath))
                return true;

            __instance.Apply(assetPath, mode);
            return false;
        }
    }
}
