using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DependencyInjection
{
    public enum InjectSource
    {
        Any,
        All,
        Parent,
        Child,
        Children
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class InjectAttribute : PropertyAttribute
    {
        public InjectSource Source { get; }

        public InjectAttribute(InjectSource source = InjectSource.Any)
        {
            Source = source;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ProvideAttribute : PropertyAttribute { }

    public interface IDependencyProvider { }

    [DefaultExecutionOrder(-1000)]
    public class Injector : MonoBehaviour
    {
        const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        readonly Dictionary<Type, List<(object instance, GameObject gameObject)>> registry = new();

        void Awake()
        {
            var monoBehaviours = FindMonoBehaviours();
            RegisterDependencies(monoBehaviours);
            InjectDependencies(monoBehaviours);
        }

        public void ValidateDependencies()
        {
            var monoBehaviours = FindMonoBehaviours();
            var providers = monoBehaviours.OfType<IDependencyProvider>();
            var providedDependencies = GetProvidedDependencies(providers);

            var invalidDependencies = monoBehaviours
                .SelectMany(mb => mb.GetType().GetFields(bindingFlags), (mb, field) => new { mb, field })
                .Where(t => Attribute.IsDefined(t.field, typeof(InjectAttribute)))
                .Where(t => !providedDependencies.Contains(t.field.FieldType) && t.field.GetValue(t.mb) == null)
                .Select(t => $"[Validation] {t.mb.GetType().Name} is missing dependency {t.field.FieldType.Name} on GameObject {t.mb.gameObject.name}");

            var invalidDependencyList = invalidDependencies.ToList();

            if (!invalidDependencyList.Any())
            {
                Debug.Log("[Validation] All dependencies are valid.");
            }
            else
            {
                Debug.LogError($"[Validation] {invalidDependencyList.Count} dependencies are invalid:");
                foreach (var invalidDependency in invalidDependencyList)
                {
                    Debug.LogError(invalidDependency);
                }
            }
        }

        HashSet<Type> GetProvidedDependencies(IEnumerable<IDependencyProvider> providers)
        {
            var providedDependencies = new HashSet<Type>();
            foreach (var provider in providers)
            {
                var methods = provider.GetType().GetMethods(bindingFlags);

                foreach (var method in methods)
                {
                    if (!Attribute.IsDefined(method, typeof(ProvideAttribute))) continue;

                    var returnType = method.ReturnType;
                    providedDependencies.Add(returnType);
                }
            }

            return providedDependencies;
        }

        public void ClearDependencies()
        {
            foreach (var monoBehaviour in FindMonoBehaviours())
            {
                var type = monoBehaviour.GetType();
                var injectableFields = type.GetFields(bindingFlags)
                    .Where(member => Attribute.IsDefined(member, typeof(InjectAttribute)));

                foreach (var injectableField in injectableFields)
                {
                    injectableField.SetValue(monoBehaviour, null);
                }
            }

            Debug.Log("[Injector] All injectable fields cleared.");
        }

        void RegisterDependencies(MonoBehaviour[] monoBehaviours)
        {

            foreach (var provider in monoBehaviours.OfType<IDependencyProvider>())
            {
                foreach (var method in provider.GetType().GetMethods(bindingFlags)
                                                     .Where(m => Attribute.IsDefined(m, typeof(ProvideAttribute))))
                {
                    var returnType = method.ReturnType;
                    var providedInstance = method.Invoke(provider, null);
                    var gameObject = (providedInstance as Component)?.gameObject ?? (providedInstance as GameObject);

                    if (providedInstance == null)
                    {
                        throw new Exception($"Provider method '{method.Name}' in class '{provider.GetType().Name}' returned null when providing type '{returnType.Name}'.");
                    }

                    var instances = registry.TryGetValue(returnType, out var existingInstances)
                                    ? existingInstances
                                    : (registry[returnType] = new List<(object, GameObject)>());

                    instances.Add((providedInstance, gameObject));
                }
            }
        }

        void InjectDependencies(MonoBehaviour[] monoBehaviours)
        {
            foreach (var mb in monoBehaviours)
            {
                InjectDependenciesIntoObject(mb);
            }
        }

        void InjectDependenciesIntoObject(object instance)
        {
            var type = instance.GetType();
            var transform = (instance as Component)?.transform;

            InjectIntoFields(instance, type, transform);
            InjectIntoMethods(instance, type);
            InjectIntoProperties(instance, type, transform);
        }

        void InjectIntoFields(object instance, Type type, Transform transform)
        {
            var injectableFields = type.GetFields(bindingFlags)
                                        .Where(field => Attribute.IsDefined(field, typeof(InjectAttribute)))
                                        .ToList();

            foreach (var field in injectableFields)
            {
                var fieldValue = field.GetValue(instance);
                if (fieldValue != null)
                {
                    Debug.LogWarning($"[Injector] Field '{field.Name}' of class '{type.Name}' is already set.");
                    continue;
                }

                var injectAttribute = (InjectAttribute)Attribute.GetCustomAttribute(field, typeof(InjectAttribute));
                var source = injectAttribute?.Source;

                var fieldType = field.FieldType;
                var resolvedInstance = ResolveDependency(fieldType, source, transform);

                if (resolvedInstance == null)
                {
                    throw new Exception($"Failed to inject dependency into field '{field.Name}' of class '{type.Name}'.");
                }

                field.SetValue(instance, resolvedInstance);
            }
        }


        void InjectIntoMethods(object instance, Type type)
        {
            var injectableMethods = type.GetMethods(bindingFlags)
                                         .Where(method => Attribute.IsDefined(method, typeof(InjectAttribute)));

            foreach (var method in injectableMethods)
            {
                var parameters = method.GetParameters();
                var parameterValues = new object[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    parameterValues[i] = ResolveDependency(parameters[i].ParameterType);

                    if (parameterValues[i] == null)
                    {
                        throw new Exception($"Failed to inject dependencies into method '{method.Name}' of class '{type.Name}'.");
                    }
                }

                method.Invoke(instance, parameterValues);
            }
        }


        void InjectIntoProperties(object instance, Type type, Transform transform)
        {
            var injectableProperties = type.GetProperties(bindingFlags)
                .Where(property => property.GetCustomAttribute<InjectAttribute>() != null);

            foreach (var property in injectableProperties)
            {
                var injectAttribute = property.GetCustomAttribute<InjectAttribute>();
                var source = injectAttribute?.Source;

                var propertyType = property.PropertyType;
                var resolvedInstance = ResolveDependency(propertyType, source, transform);

                if (resolvedInstance == null)
                {
                    throw new Exception($"Failed to inject dependency into property '{property.Name}' of class '{type.Name}'.");
                }

                property.SetValue(instance, resolvedInstance);
            }
        }


        object ResolveDependency(Type type, InjectSource? source = null, Transform transform = null)
        {
            if (transform == null)
            {
                return Resolve(type);
            }

            switch (source)
            {
                case InjectSource.Parent:
                    return ResolveFromParent(type, transform);
                case InjectSource.Child:
                    return ResolveFromChild(type, transform);
                case InjectSource.Children:
                    return ResolveFromChildren(type, transform);
                case InjectSource.All:
                    return ResolveArray(type);
                default:
                    return Resolve(type);
            }
        }

        object Resolve(Type type)
        {
            registry.TryGetValue(type, out var instances);
            return instances?[0].instance;
        }

        object ResolveFromParent(Type type, Transform transform)
        {
            if (!registry.TryGetValue(type, out var instances))
                return null;

            var gameObjectToInstanceMap = instances.ToDictionary(pair => pair.gameObject, pair => pair.instance);

            var currentTransform = transform;
            while (currentTransform != null)
            {
                if (gameObjectToInstanceMap.TryGetValue(currentTransform.gameObject, out var instance))
                {
                    return instance;
                }

                currentTransform = currentTransform.parent;
            }
            return null;
        }


        object ResolveFromChild(Type type, Transform transform)
        {
            if (!registry.TryGetValue(type, out var instances))
                return null;

            var queue = new Queue<Transform>();
            queue.Enqueue(transform);

            while (queue.Count > 0)
            {
                var currentTransform = queue.Dequeue();

                foreach (var (instance, gameObject) in instances)
                {
                    if (gameObject == currentTransform.gameObject)
                    {
                        return instance;
                    }
                }

                for (int i = 0; i < currentTransform.childCount; i++)
                {
                    queue.Enqueue(currentTransform.GetChild(i));
                }
            }

            return null;
        }


        object ResolveFromChildren(Type type, Transform transform)
        {
            if (!type.IsArray)
            {
                throw new Exception($"Failed to inject dependencies '{type.Name}' is not an Array.");
            }

            if (!registry.TryGetValue(type.GetElementType(), out var instances))
            {
                return null;
            }

            var childrenInstances = new List<object>();
            var queue = new Queue<Transform>();
            queue.Enqueue(transform);

            while (queue.Count > 0)
            {
                var currentTransform = queue.Dequeue();

                foreach (var (instance, gameObject) in instances)
                {
                    if (gameObject == currentTransform.gameObject)
                    {
                        childrenInstances.Add(instance);
                    }
                }

                var childCount = currentTransform.childCount;
                for (int i = 0; i < childCount; i++)
                {
                    queue.Enqueue(currentTransform.GetChild(i));
                }
            }

            return CreateArrayInstance(type.GetElementType(), childrenInstances);
        }

        object ResolveArray(Type arrayType)
        {
            if (!arrayType.IsArray)
            {
                throw new Exception($"Failed to inject dependencies '{arrayType.Name}' is not an Array.");
            }

            var elementType = arrayType.GetElementType();
            var resolvedInstances = ResolveAll(elementType);

            if (resolvedInstances == null)
            {
                throw new Exception($"Failed to inject dependencies into array type '{arrayType.Name}'.");
            }

            return CreateArrayInstance(elementType, resolvedInstances);
        }

        List<object> ResolveAll(Type type)
        {
            if (!registry.TryGetValue(type, out var resolvedInstances))
            {
                return null;
            }

            return resolvedInstances.Select(instance => instance.instance).ToList();
        }

        object CreateArrayInstance(Type elementType, List<object> resolvedInstances)
        {
            var arrayLength = resolvedInstances.Count;
            var arrayInstance = Array.CreateInstance(elementType, arrayLength);

            for (int i = 0; i < arrayLength; i++)
            {
                arrayInstance.SetValue(resolvedInstances[i], i);
            }

            return arrayInstance;
        }

        static MonoBehaviour[] FindMonoBehaviours()
        {
            return FindObjectsOfType<MonoBehaviour>(true);
        }
    }
}
