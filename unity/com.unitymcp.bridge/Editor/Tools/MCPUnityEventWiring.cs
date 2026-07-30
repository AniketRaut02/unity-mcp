using System;
using System.Linq;
using System.Reflection;
using UnityEditor.Events;
using UnityEngine.Events;

namespace UnityMCP.Tools
{
    /// <summary>
    /// Shared persistent-listener-adding logic behind wire_unity_event (ComponentTools.cs) and wire_event_listener
    /// (GameplayTools.cs) -- factored out once a second call site needed the exact same dynamic/static/void-fallback
    /// decision tree, rather than duplicating it (unlike the scaffolded-user-script duplication elsewhere in this
    /// codebase, this is bridge-internal code with no cross-project-compile-coupling concern).
    /// </summary>
    internal static class MCPUnityEventWiring
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>Adds one persistent listener to ueBase calling methodName on targetComponent. Returns null on success, or an error message.</summary>
        public static string AddListener(
            UnityEventBase ueBase, Type eventType, object targetComponent, Type targetType, string methodName,
            bool dynamic, string stringArgument, float floatArgument, int intArgument, bool boolArgument)
        {
            var genericArgs = eventType.IsGenericType ? eventType.GetGenericArguments() : Type.EmptyTypes;

            if (genericArgs.Length == 0)
            {
                var methodInfo = targetType.GetMethod(methodName, PublicInstance, null, Type.EmptyTypes, null);
                if (methodInfo == null) return $"No public parameterless method '{methodName}' found on '{targetType.Name}'.";
                UnityEventTools.AddVoidPersistentListener(ueBase, (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), targetComponent, methodInfo));
                return null;
            }

            if (genericArgs.Length > 1)
                return "UnityEvent types with more than one generic argument aren't supported.";

            var argType = genericArgs[0];
            var typedMethod = targetType.GetMethod(methodName, PublicInstance, null, new[] { argType }, null);

            if (typedMethod != null && dynamic)
            {
                // Forwards the event's real runtime argument -- confirmed via live spike that this requires
                // UnityEventTools' plain generic AddPersistentListener<T> overload, not the type-suffixed
                // AddStringPersistentListener/etc (those bake a fixed constant and ignore the real value).
                var actionType = typeof(UnityAction<>).MakeGenericType(argType);
                var del = Delegate.CreateDelegate(actionType, targetComponent, typedMethod);
                var genericAdd = typeof(UnityEventTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "AddPersistentListener" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
                genericAdd.MakeGenericMethod(argType).Invoke(null, new object[] { ueBase, del });
                return null;
            }

            if (typedMethod != null && !dynamic && (argType == typeof(string) || argType == typeof(float) || argType == typeof(int) || argType == typeof(bool)))
            {
                if (argType == typeof(string))
                    UnityEventTools.AddStringPersistentListener(ueBase, (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), targetComponent, typedMethod), stringArgument ?? "");
                else if (argType == typeof(float))
                    UnityEventTools.AddFloatPersistentListener(ueBase, (UnityAction<float>)Delegate.CreateDelegate(typeof(UnityAction<float>), targetComponent, typedMethod), floatArgument);
                else if (argType == typeof(int))
                    UnityEventTools.AddIntPersistentListener(ueBase, (UnityAction<int>)Delegate.CreateDelegate(typeof(UnityAction<int>), targetComponent, typedMethod), intArgument);
                else
                    UnityEventTools.AddBoolPersistentListener(ueBase, (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), targetComponent, typedMethod), boolArgument);
                return null;
            }

            // No overload matching the event's own argument type at all (e.g. a UnityEvent<Collider> like
            // MCPTriggerRelay's onTriggerEnter), or a non-primitive argument type with dynamic:false requested --
            // fall back to a "static" listener that ignores the runtime argument and calls a parameterless
            // method instead, a real first-class UnityEventTools capability (the same Static/Dynamic argument
            // mode the Inspector's own UI exposes), not a workaround.
            var voidMethod = targetType.GetMethod(methodName, PublicInstance, null, Type.EmptyTypes, null);
            if (voidMethod == null)
                return $"No public method '{methodName}({argType.Name})' or parameterless '{methodName}()' found on '{targetType.Name}'.";
            UnityEventTools.AddVoidPersistentListener(ueBase, (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), targetComponent, voidMethod));
            return null;
        }
    }
}
